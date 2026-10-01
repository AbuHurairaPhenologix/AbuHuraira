"""Raw operational-event generator (stdlib only).

Produces canonical events for one 5-minute window whose aggregate features follow the report's normal baseline
(~140 requests, mean latency ~250 ms with p95 ~2.6x, ~2 % errors, ~1.5 % auth failures, rare dependency failures and
retries, five endpoint groups ≈ 2.3 bits of entropy), or a controlled anomaly scenario. Used by the backfill script,
the live demo and the end-to-end test. Contains no credentials.
"""

from __future__ import annotations

import math
import random
import uuid
from datetime import datetime, timedelta, timezone

ENDPOINTS = [("/demo/products", 0.30), ("/demo/orders/{id}", 0.25), ("/demo/login", 0.15), ("/demo/dependency", 0.15), ("/demo/job", 0.15)]
SCENARIOS = ("latency_spike", "error_burst", "auth_failure_burst", "dependency_failure", "retry_storm", "traffic_surge")

MEAN_LATENCY_MS = 250.0
LATENCY_SIGMA = 0.754  # per-request log-normal sigma giving p95 ≈ 2.6 × mean


def _poisson(rng: random.Random, lam: float) -> int:
    # Knuth for small lambda; normal approximation for large lambda.
    if lam > 60:
        return max(0, int(round(rng.gauss(lam, math.sqrt(lam)))))
    l, k, p = math.exp(-lam), 0, 1.0
    while True:
        p *= rng.random()
        if p <= l:
            return k
        k += 1


def _latency(rng: random.Random, multiplier: float = 1.0) -> float:
    mu = math.log(MEAN_LATENCY_MS) - LATENCY_SIGMA**2 / 2
    return round(min(rng.lognormvariate(mu, LATENCY_SIGMA) * multiplier, 30_000.0), 3)


def _iso(ts: datetime) -> str:
    return ts.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def window_events(service: str, environment: str, start: datetime, scenario: str | None = None, rng: random.Random | None = None, window_minutes: int = 5) -> list[dict]:
    """Events for the window [start, start + window_minutes)."""
    rng = rng or random.Random()
    if scenario is not None and scenario not in SCENARIOS:
        raise ValueError(f"unknown scenario {scenario}")
    span = window_minutes * 60.0
    lam = 140.0 * window_minutes / 5.0 * (2.0 if scenario == "traffic_surge" else 1.0)
    n = _poisson(rng, lam)
    latency_mult = 3.2 if scenario == "latency_spike" else 1.0
    error_p = 0.13 if scenario == "error_burst" else 0.02
    login_fail_p = 0.75 if scenario == "auth_failure_burst" else 0.10
    dep_fail_p = 0.35 if scenario == "dependency_failure" else 0.0075
    weights = [w for _, w in ENDPOINTS]
    if scenario == "traffic_surge":
        weights = [0.55, 0.30, 0.05, 0.05, 0.05]  # a surge concentrated on catalog/order browsing

    events: list[dict] = []
    for _ in range(n):
        ts = start + timedelta(seconds=rng.random() * span)
        corr = uuid.UUID(int=rng.getrandbits(128)).hex
        endpoint = rng.choices([e for e, _ in ENDPOINTS], weights)[0]
        status, auth, error, retries = 200, "none", False, 0
        duration = _latency(rng, latency_mult)
        if endpoint == "/demo/login":
            failed = rng.random() < login_fail_p
            auth = "failure" if failed else "success"
            status = 401 if failed else 200
            events.append(_event(service, environment, ts, "authentication", "/auth/login", 401 if failed else 200, None, False, auth, None, 0, corr, rng))
        elif endpoint == "/demo/dependency":
            failures = 0
            for _attempt in range(3):
                if rng.random() >= dep_fail_p:
                    break
                failures += 1
            failed = failures == 3
            dep_retries = min(failures, 2) if scenario == "dependency_failure" else 0
            events.append(_event(service, environment, ts, "dependency_call", "/dependency/inventory-db", 503 if failed or failures else 200, round(duration * 0.4, 3), failed or failures > 0, "none", "inventory-db", dep_retries, corr, rng))
            if failed:
                status, error = 503, True
        elif endpoint == "/demo/job":
            retries = rng.randint(2, 6) if scenario == "retry_storm" else (1 if rng.random() < 0.005 else 0)
            events.append(_event(service, environment, ts, "background_job", "/jobs/reconcile-inventory", 200, round(duration * 0.5, 3), False, "none", None, retries, corr, rng))
        if not error and rng.random() < error_p:
            status, error = 500, True
        events.append(_event(service, environment, ts, "http_request", endpoint, status, duration, error, auth, None, 0, corr, rng))
    return events


def _event(service, environment, ts, event_type, endpoint, status, duration, error, auth, dependency, retries, corr, rng) -> dict:
    return {
        "eventId": uuid.UUID(int=rng.getrandbits(128)).hex,
        "eventTimestamp": _iso(ts),
        "serviceName": service,
        "environment": environment,
        "eventType": event_type,
        "endpointGroup": endpoint,
        "statusCode": status,
        "durationMs": duration,
        "errorFlag": error,
        "authenticationResult": auth,
        "dependencyName": dependency,
        "retryCount": retries,
        "correlationId": corr,
    }


def aligned(ts: datetime, window_minutes: int = 5) -> datetime:
    ts = ts.astimezone(timezone.utc).replace(second=0, microsecond=0)
    return ts - timedelta(minutes=ts.minute % window_minutes)
