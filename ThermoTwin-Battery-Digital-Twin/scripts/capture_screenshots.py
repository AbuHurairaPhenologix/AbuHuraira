"""Capture README screenshots from the running ThermoTwin.NET application.

Prerequisites: API on http://localhost:5180 and Angular dev server on http://localhost:4200.
Usage:  python scripts/capture_screenshots.py [--pause-at 1200] [--out docs/screenshots] [--only overview,...]
                                              [--wait-experiments 900] [--no-restart]

The script
  1. waits until every experiment kind has a stored result (the API seeds them on first start),
  2. starts a fresh demo run, waits until simulated time reaches --pause-at and pauses the twin,
     so every page shows the same deterministic state (fixed seeds),
  3. screenshots each page (full page) or a page section (element crop), and resumes the run.
Every image is a real capture of the running application with its computed data.
"""
import argparse
import json
import time
import urllib.request
from pathlib import Path

from playwright.sync_api import sync_playwright

API = "http://localhost:5180"
WEB = "http://localhost:4200"

EXPERIMENT_KINDS = [
    "NumericalConvergence", "Regularization", "SensorDensity", "NoiseRobustness", "ForecastAccuracy", "CoolingComparison",
    "FemVerification", "AdjointGradientCheck", "OptimizationBenchmark", "ReducedOrderModel", "ReducedOrderControl",
    "ParameterIdentifiability",
]

# (file name, route, optional (CSS selector, index) for an element crop)
PAGES = [
    ("dashboard-overview", "/overview", None),
    ("live-digital-twin", "/twin", None),
    ("temperature-heatmap", "/twin", (".grid.g-3", 0)),
    ("mathematical-model", "/model", None),
    ("fem-analysis", "/methods", None),
    ("fvm-fem-comparison", "/methods", (".grid.g-3", 0)),
    ("fem-convergence", "/methods", (".grid.g-2", 0)),
    ("inverse-problem", "/inverse", None),
    ("heat-source-reconstruction", "/inverse", (".grid.g-3", 0)),
    ("regularization-analysis", "/inverse", (".grid.g-3", 1)),
    ("regularization-fields", "/inverse", (".grid.g-3", 2)),
    ("parameter-identifiability", "/identifiability", None),
    ("reduced-order-model", "/rom", None),
    ("pod-spectrum", "/rom", (".grid.g-3", 0)),
    ("rom-comparison", "/rom", (".grid.g-3", 1)),
    ("rom-mpc", "/rom", (".grid.g-2", 1)),
    ("pde-constrained-optimization", "/optimization", None),
    ("adjoint-gradient-validation", "/optimization", (".adjoint-section", 0)),
    ("cooling-optimization", "/cooling", None),
    ("cooling-strategy-comparison", "/cooling", (".page > tt-card", 0)),
    ("thermal-analysis", "/thermal", None),
    ("numerical-validation", "/validation", None),
    ("convergence-analysis", "/validation", (".grid.g-2", 0)),
    ("simulation-configuration", "/configure", None),
    ("experiment-results", "/experiments", None),
]


def call(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(API + path, data=data, method=method, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=60) as r:
        raw = r.read()
        return json.loads(raw) if raw else None


def wait_for_experiments(timeout):
    deadline = time.time() + timeout
    while True:
        overview = call("GET", "/api/experiments")
        stored = {e["kind"] for e in overview["experiments"]}
        missing = [k for k in EXPERIMENT_KINDS if k not in stored]
        if not missing:
            print("all experiments stored")
            return
        if time.time() > deadline:
            raise SystemExit(f"experiments still missing after {timeout} s: {missing}")
        failed = [q for q in overview["queue"] if q["state"] == "Failed"]
        if failed:
            raise SystemExit(f"experiment(s) failed: {failed}")
        print("waiting for experiments:", ", ".join(missing))
        time.sleep(10)


def wait_for_time(target):
    while True:
        state = call("GET", "/api/twin/state?includeFields=false")
        if state["time"] >= target or state["status"] != "Running":
            return state
        time.sleep(0.25)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pause-at", type=float, default=1200)
    ap.add_argument("--out", default="docs/screenshots")
    ap.add_argument("--only", default="")
    ap.add_argument("--no-restart", action="store_true")
    ap.add_argument("--wait-experiments", type=float, default=1200)
    args = ap.parse_args()
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    wait_for_experiments(args.wait_experiments)

    if not args.no_restart:
        run = call("POST", "/api/simulations", {"scenarioKey": "rapid-charge-hidden-hotspot"})
        print("started run", run["id"])
        state = wait_for_time(args.pause_at)
        call("POST", "/api/twin/pause")
        print(f"paused at t = {state['time']} s, T_max = {state['truth']['max']:.2f}")
        time.sleep(1.0)

    only = set(filter(None, args.only.split(",")))
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="chrome", headless=True)
        page = browser.new_page(viewport={"width": 1680, "height": 1050}, device_scale_factor=1.5)
        for name, route, section in PAGES:
            if only and name not in only:
                continue
            page.goto(WEB + route, wait_until="networkidle")
            page.wait_for_timeout(2500)
            if section:
                selector, index = section
                element = page.locator(selector).nth(index)
                element.scroll_into_view_if_needed()
                page.wait_for_timeout(600)
                element.screenshot(path=str(out / f"{name}.png"))
            else:
                page.screenshot(path=str(out / f"{name}.png"), full_page=True)
            print("captured", name)
        browser.close()

    if not args.no_restart:
        call("POST", "/api/twin/resume")


if __name__ == "__main__":
    main()
