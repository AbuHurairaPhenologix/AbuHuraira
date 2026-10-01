"""End-to-end test against the running Docker stack (real backend, PostgreSQL, OpenSearch and Python ML service).

    docker compose up -d --build
    python -m pytest tests/e2e -v                       # uses .env for credentials
    E2E_ALLOW_OUTAGE=1 python -m pytest tests/e2e -v    # also stops/starts ml-service to verify TC-04 live

Flow: send operational events → normalize → store → aggregate a logical 5-minute window → compute the 8 features →
request an ML score → persist the scoring result → create an anomaly → retrieve it via the API → submit an engineer
review → verify persistence. Timestamps are synthetic (two days ago), so nothing waits five minutes.
"""

from __future__ import annotations

import os
import random
import subprocess
import sys
import time
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

import httpx
import pytest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "seed"))
from eventgen import aligned, window_events  # noqa: E402


def _env() -> dict[str, str]:
    values = dict(os.environ)
    path = ROOT / ".env"
    if path.exists():
        for line in path.read_text(encoding="utf-8").splitlines():
            if "=" in line and not line.startswith("#"):
                k, v = line.split("=", 1)
                values.setdefault(k.strip(), v.strip())
    return values


ENV = _env()
BASE = ENV.get("E2E_BASE_URL", f"http://localhost:{ENV.get('BACKEND_HOST_PORT', '5080')}")


@pytest.fixture(scope="module")
def http() -> httpx.Client:
    client = httpx.Client(base_url=BASE, timeout=60)
    deadline = time.time() + 180
    while True:
        try:
            health = client.get("/api/v1/health").json()
            if health["components"].get("ml-service") == "Healthy" and health["status"] != "Unhealthy":
                break
        except (httpx.HTTPError, KeyError, ValueError):
            pass
        if time.time() > deadline:
            pytest.skip(f"Stack at {BASE} is not ready; start it with `docker compose up -d`.")
        time.sleep(3)
    return client


def token(http: httpx.Client, user: str) -> dict[str, str]:
    password = ENV["DEMO_ADMIN_PASSWORD"] if user == "admin" else ENV["DEMO_ENGINEER_PASSWORD"]
    r = http.post("/api/v1/auth/token", json={"username": user, "password": password})
    r.raise_for_status()
    return {"Authorization": f"Bearer {r.json()['accessToken']}"}


@pytest.fixture(scope="module")
def admin(http) -> dict[str, str]:
    return token(http, "admin")


@pytest.fixture(scope="module")
def engineer(http) -> dict[str, str]:
    return token(http, "engineer")


@pytest.fixture(scope="module")
def active_model(http, admin) -> dict:
    """Uses the active model, or registers and activates the registry's recommended model (audited admin path)."""
    for _ in range(30):
        models = http.get("/api/v1/models", headers=admin).json()
        active = [m for m in models if m["isActive"]]
        if active:
            return active[0]
        registry = http.get("/api/v1/models/registry", headers=admin).json()
        recommended = next(r for r in registry if r["metadata"]["recommended"])
        if not recommended["registeredInBackend"]:
            http.post("/api/v1/models", json={"modelVersion": recommended["metadata"]["modelVersion"]}, headers=admin)
            continue
        r = http.post(f"/api/v1/models/{recommended['backendModelId']}/activate", headers=admin)
        if r.status_code == 200:
            return r.json()
        time.sleep(2)
    pytest.fail("No model could be activated")


def fresh_ids(events: list[dict]) -> list[dict]:
    """Seeded distributions, but unique event IDs per run (re-sending the same IDs would correctly be de-duplicated)."""
    return [{**e, "eventId": uuid.uuid4().hex} for e in events]


def ingest(http, events: list[dict]) -> dict:
    r = http.post("/api/v1/events", json={"events": events}, headers={"X-Api-Key": ENV["INGESTION_API_KEY"]})
    assert r.status_code == 202, r.text
    return r.json()


def test_full_pipeline_with_real_ml_service(http, admin, engineer, active_model):
    service = f"e2e-{uuid.uuid4().hex[:8]}"
    rng = random.Random(2022)
    normal_start = aligned(datetime.now(timezone.utc) - timedelta(days=2))
    spike_start = normal_start + timedelta(minutes=5)

    # 1-3. Send events: a normal window and a latency-spike window, plus a duplicate and an invalid record.
    normal = fresh_ids(window_events(service, "production", normal_start, None, rng))
    spike = fresh_ids(window_events(service, "production", spike_start, "latency_spike", rng))
    invalid = {"eventId": "e2e-invalid", "eventTimestamp": "not-a-time", "serviceName": service, "environment": "production", "eventType": "http_request"}
    result = ingest(http, normal + spike + [spike[0], invalid])
    assert result["accepted"] == len(normal) + len(spike)
    assert result["duplicates"] == 1 and result["quarantined"] == 1

    # 4-8. Aggregate, compute features, score with the real Python model, persist.
    run = http.post("/api/v1/pipeline/run", headers=admin)
    assert run.status_code == 200, run.text

    windows = http.get("/api/v1/windows", params={"service": service}, headers=engineer).json()
    assert windows["total"] == 2
    by_start = {w["windowStartUtc"][:16]: w for w in windows["items"]}
    w_normal = by_start[normal_start.strftime("%Y-%m-%dT%H:%M")]
    w_spike = by_start[spike_start.strftime("%Y-%m-%dT%H:%M")]
    for w in (w_normal, w_spike):
        assert set(w["features"]) == {"request_count", "error_rate", "avg_duration_ms", "p95_duration_ms", "auth_failure_rate", "dependency_failure_count", "retry_count", "endpoint_entropy"}
        assert w["featureSchemaVersion"] == "ops-v1" and w["scoringStatus"] == "Scored"
    assert w_normal["features"]["request_count"] == sum(1 for e in normal if e["eventType"] == "http_request")
    assert w_spike["features"]["avg_duration_ms"] > 2 * w_normal["features"]["avg_duration_ms"]

    # TC-01 with the real model: the normal window is scored and persisted but not flagged.
    detail_normal = http.get(f"/api/v1/windows/{w_normal['windowId']}", headers=engineer).json()
    score = detail_normal["scores"][0]
    assert score["modelVersion"] == active_model["modelVersion"]
    assert score["isAnomaly"] is False and score["score"] < score["threshold"]

    # 9. TC-05: the latency spike exceeded the threshold and produced an anomaly.
    anomalies = http.get("/api/v1/anomalies", params={"service": service}, headers=engineer).json()
    assert anomalies["total"] == 1
    anomaly = anomalies["items"][0]
    assert anomaly["windowId"] == w_spike["windowId"]
    assert anomaly["score"] >= anomaly["threshold"]
    assert anomaly["threshold"] == pytest.approx(active_model["validationThreshold"])
    detail = http.get(f"/api/v1/anomalies/{anomaly['anomalyId']}", headers=engineer).json()
    assert "response time" in detail["anomaly"]["reasonSummary"]
    assert "not a root-cause" in detail["anomaly"]["reasonSummary"]
    assert len(detail["correlationIds"]) > 0

    # FR-07: related raw events from OpenSearch.
    for _ in range(20):
        events = http.get(f"/api/v1/anomalies/{anomaly['anomalyId']}/events", params={"pageSize": 200}, headers=engineer).json()
        if events["total"] == len(spike):
            break
        time.sleep(1)
    assert events["source"] == "opensearch"
    assert events["total"] == len(spike)

    # 10-11. Engineer review persisted with history.
    r = http.post(f"/api/v1/anomalies/{anomaly['anomalyId']}/reviews", json={"outcome": "ConfirmedIssue", "note": "E2E: latency spike confirmed"}, headers=engineer)
    assert r.status_code == 201, r.text
    history = http.get(f"/api/v1/anomalies/{anomaly['anomalyId']}/reviews", headers=engineer).json()
    assert [(h["previousState"], h["outcome"], h["reviewer"]) for h in history] == [("Unreviewed", "ConfirmedIssue", "engineer")]
    assert http.get(f"/api/v1/anomalies/{anomaly['anomalyId']}", headers=engineer).json()["anomaly"]["reviewState"] == "ConfirmedIssue"


def test_tc07_engineer_cannot_activate_models(http, engineer, active_model):
    r = http.post(f"/api/v1/models/{active_model['modelId']}/deactivate", headers=engineer)
    assert r.status_code == 403


def test_demo_workload_generates_live_telemetry(http, engineer):
    correlation = f"e2e-live-{uuid.uuid4().hex[:8]}"
    r = http.get("/demo/products", headers={"X-Correlation-ID": correlation})
    assert r.status_code in (200, 500)
    assert r.headers["X-Correlation-ID"] == correlation
    for _ in range(30):
        found = http.get("/api/v1/events", params={"correlationId": correlation}, headers=engineer).json()
        if found["total"] >= 1:
            break
        time.sleep(1)
    assert found["items"][0]["endpointGroup"] == "/demo/products"


@pytest.mark.skipif(os.environ.get("E2E_ALLOW_OUTAGE") != "1", reason="set E2E_ALLOW_OUTAGE=1 to stop/start ml-service")
def test_tc04_live_ml_outage_defers_scoring_without_affecting_requests(http, admin, engineer, active_model):
    service = f"e2e-outage-{uuid.uuid4().hex[:6]}"
    start = aligned(datetime.now(timezone.utc) - timedelta(days=3))
    ingest(http, fresh_ids(window_events(service, "production", start, None, random.Random(4))))
    subprocess.run(["docker", "compose", "stop", "ml-service"], cwd=ROOT, check=True)
    try:
        statuses = [http.get("/demo/products").status_code for _ in range(5)]
        assert all(s in (200, 500) for s in statuses) and 200 in statuses
        http.post("/api/v1/pipeline/run", headers=admin)
        w = http.get("/api/v1/windows", params={"service": service}, headers=engineer).json()["items"][0]
        assert w["scoringStatus"] == "Deferred" and w["scoringAttempts"] >= 1
        health = http.get("/api/v1/health").json()
        assert health["components"]["ml-service"] == "Degraded" and health["status"] != "Unhealthy"
    finally:
        subprocess.run(["docker", "compose", "start", "ml-service"], cwd=ROOT, check=True)
    for _ in range(60):
        if http.get("/api/v1/health").json()["components"].get("ml-service") == "Healthy":
            break
        time.sleep(2)
    time.sleep(31)  # circuit-breaker break duration
    http.post("/api/v1/pipeline/requeue", headers=admin)
    http.post("/api/v1/pipeline/run", headers=admin)
    w = http.get("/api/v1/windows", params={"service": service}, headers=engineer).json()["items"][0]
    assert w["scoringStatus"] == "Scored"
