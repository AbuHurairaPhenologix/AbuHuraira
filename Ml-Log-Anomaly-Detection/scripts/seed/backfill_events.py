"""Backfill historical synthetic telemetry through the real ingestion API (POST /api/v1/events).

Windows are in the past, so they close immediately and the background workers (or POST /api/v1/pipeline/run)
aggregate and score them without waiting. Anomaly scenarios are injected only into explicitly listed windows.

    python scripts/seed/backfill_events.py --hours 6 --anomalies "latency_spike@60,error_burst@140,auth_failure_burst@220"

The `@N` suffix is the window offset (in minutes) from the start of the backfill period. Uses only the standard library.
"""

from __future__ import annotations

import argparse
import json
import random
import sys
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from eventgen import SCENARIOS, aligned, window_events  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]


def read_env() -> dict[str, str]:
    env = {}
    path = ROOT / ".env"
    if path.exists():
        for line in path.read_text(encoding="utf-8").splitlines():
            if "=" in line and not line.startswith("#"):
                k, v = line.split("=", 1)
                env[k.strip()] = v.strip()
    return env


def post(url: str, body: dict, headers: dict[str, str]) -> dict:
    req = urllib.request.Request(url, data=json.dumps(body).encode(), headers={"Content-Type": "application/json", **headers}, method="POST")
    with urllib.request.urlopen(req, timeout=120) as resp:
        return json.loads(resp.read() or b"{}")


def main() -> int:
    env = read_env()
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--base-url", default="http://localhost:5080")
    p.add_argument("--api-key", default=env.get("INGESTION_API_KEY"))
    p.add_argument("--services", default="checkout-api,identity-api")
    p.add_argument("--environment", default="production")
    p.add_argument("--hours", type=float, default=6)
    p.add_argument("--end-hours-ago", type=float, default=1, help="Backfill ends this many hours before now")
    p.add_argument("--anomalies", default="latency_spike@60,error_burst@135,auth_failure_burst@210,dependency_failure@250,traffic_surge@300", help="scenario@minuteOffset[:service],...")
    p.add_argument("--seed", type=int, default=7)
    args = p.parse_args()
    if not args.api_key:
        print("No ingestion API key (set INGESTION_API_KEY in .env or pass --api-key).", file=sys.stderr)
        return 2

    rng = random.Random(args.seed)
    services = [s.strip() for s in args.services.split(",") if s.strip()]
    end = aligned(datetime.now(timezone.utc) - timedelta(hours=args.end_hours_ago))
    start = end - timedelta(hours=args.hours)
    injected: dict[tuple[str, datetime], str] = {}
    for spec in filter(None, (s.strip() for s in args.anomalies.split(","))):
        scenario, _, rest = spec.partition("@")
        offset, _, svc = rest.partition(":")
        if scenario not in SCENARIOS:
            raise SystemExit(f"Unknown scenario {scenario}; choose from {SCENARIOS}")
        injected[(svc or services[0], aligned(start + timedelta(minutes=int(offset))))] = scenario

    totals = {"windows": 0, "events": 0, "accepted": 0, "duplicates": 0, "quarantined": 0}
    batch: list[dict] = []
    window = start
    while window < end:
        for svc in services:
            scenario = injected.get((svc, window))
            events = window_events(svc, args.environment, window, scenario, rng)
            batch.extend(events)
            totals["windows"] += 1
            if scenario:
                print(f"  injected {scenario:<20} {svc} {window.isoformat()}")
        if len(batch) >= 2000:
            r = post(f"{args.base_url}/api/v1/events", {"events": batch}, {"X-Api-Key": args.api_key})
            for k in ("accepted", "duplicates", "quarantined"):
                totals[k] += r.get(k, 0)
            totals["events"] += len(batch)
            batch = []
        window += timedelta(minutes=5)
    if batch:
        r = post(f"{args.base_url}/api/v1/events", {"events": batch}, {"X-Api-Key": args.api_key})
        for k in ("accepted", "duplicates", "quarantined"):
            totals[k] += r.get(k, 0)
        totals["events"] += len(batch)
    print(json.dumps({"period": [start.isoformat(), end.isoformat()], **totals}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
