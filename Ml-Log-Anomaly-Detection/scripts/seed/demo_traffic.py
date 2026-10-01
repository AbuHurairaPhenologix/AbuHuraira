"""Live demo traffic generator: calls the demo workload endpoints at a steady rate (~140 requests / 5 minutes).

    python scripts/seed/demo_traffic.py --base-url http://localhost:5080 --minutes 30

Normal mode produces normal telemetry. Anomaly scenarios are switched on explicitly (DEV only), e.g.:
    curl -X POST "http://localhost:5080/demo/scenarios/latency_spike?durationMinutes=6"
While `traffic_surge` is active this generator doubles its request rate. Uses only the standard library.
"""

from __future__ import annotations

import argparse
import json
import random
import threading
import time
import urllib.error
import urllib.request

ENDPOINTS = [("GET", "/demo/products", 0.30), ("GET", "/demo/orders/{id}", 0.25), ("POST", "/demo/login", 0.15), ("GET", "/demo/dependency", 0.15), ("POST", "/demo/job", 0.15)]


def call(base: str, method: str, path: str, stats: dict, lock: threading.Lock) -> None:
    path = path.replace("{id}", str(random.randint(1, 5000)))
    body = json.dumps({"username": "shopper", "password": "demo"}).encode() if path.endswith("login") else (b"{}" if method == "POST" else None)
    req = urllib.request.Request(base + path, data=body, method=method, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            code = r.status
    except urllib.error.HTTPError as e:
        code = e.code
    except Exception:  # noqa: BLE001 - connection problems are counted, not fatal
        code = 0
    with lock:
        stats[code] = stats.get(code, 0) + 1


def surge_active(base: str) -> bool:
    try:
        with urllib.request.urlopen(base + "/demo/scenarios", timeout=5) as r:
            return any(s["name"] == "traffic_surge" for s in json.loads(r.read())["active"])
    except Exception:  # noqa: BLE001
        return False


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--base-url", default="http://localhost:5080")
    p.add_argument("--rate", type=float, default=140 / 300, help="requests per second in normal mode")
    p.add_argument("--minutes", type=float, default=30)
    args = p.parse_args()
    stats: dict[int, int] = {}
    lock = threading.Lock()
    deadline = time.time() + args.minutes * 60
    last_check, surge, last_report = 0.0, False, time.time()
    print(f"Sending demo traffic to {args.base_url} for {args.minutes} min (Ctrl+C to stop)")
    while time.time() < deadline:
        if time.time() - last_check > 10:
            surge, last_check = surge_active(args.base_url), time.time()
        rate = args.rate * (2.0 if surge else 1.0)
        method, path, _ = random.choices(ENDPOINTS, [w for *_, w in ENDPOINTS])[0]
        threading.Thread(target=call, args=(args.base_url, method, path, stats, lock), daemon=True).start()
        time.sleep(random.expovariate(rate))
        if time.time() - last_report > 30:
            with lock:
                print(f"{time.strftime('%H:%M:%S')} surge={surge} responses={dict(sorted(stats.items()))}")
            last_report = time.time()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
