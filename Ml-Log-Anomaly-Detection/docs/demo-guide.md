# Live Demo Guide

Time needed: ~10 minutes (plus ~2 minutes of image builds on first run).

## 0. Start

```powershell
python scripts/setup/generate_env.py      # once: creates .env with random secrets + demo passwords
docker compose up -d --build              # postgres, opensearch, ml-trainer, ml-service, backend, frontend
docker compose ps                         # all "healthy"; ml-trainer "exited (0)"
```

Open **http://localhost:8081** and sign in as `admin` (password = `DEMO_ADMIN_PASSWORD` in `.env`).
Swagger: **http://localhost:5080/swagger**.

On first start the backend's opt-in bootstrap (`MODEL_BOOTSTRAP_ENABLED=true`) registers the models in the ML registry
and activates the recommended one (best validation F1 among production-eligible models) through the same audited
lifecycle service an administrator uses. If `artifacts/models` is empty, `ml-trainer` generates the benchmark and
trains all four models first (~40 s).

## 1. Show the model governance (Models page)

* Four registered versions, each with seed, training period, validation threshold and validation P/R/F1/FPR.
* Random Forest is marked *reference only* — its Activate button is disabled (and the API returns 422).
* Sign in as `engineer` in a private window and try to activate a model → denied; back as admin, `GET /api/v1/audit`
  (Swagger) shows the `authorization.denied` event (TC-07).

## 2. Fast path: historical telemetry with injected incidents

```powershell
python scripts/seed/backfill_events.py --hours 6
```

Posts ~30,000 raw events for `checkout-api` and `identity-api` (6 h, 144 windows) through `POST /api/v1/events`, with
five injected scenarios on `checkout-api`. Windows are in the past, so workers close and score them within ~15 s
(or press **Run pipeline now** on the Overview page).

Expected (verified run): exactly the 5 injected windows are flagged; normal windows are not.

| Scenario | Reason summary (excerpt) |
|---|---|
| latency_spike | Elevated average response time (808 vs baseline 250), elevated p95 response time … |
| error_burst | Elevated error rate (0.161 vs baseline 0.02) … |
| auth_failure_burst | Elevated authentication-failure rate (0.13 vs baseline 0.015) … |
| dependency_failure | Elevated retry count, elevated dependency failure count … |
| traffic_surge | Elevated request count (289 vs 140), reduced endpoint diversity (entropy) … |

## 3. Investigate and review an anomaly

1. **Anomalies** → filter service `checkout-api` → open one.
2. Point out: score vs the threshold *actually used*, the model version that produced it, the 8 features with z-scores
   against the training baseline, and the non-causal reason summary.
3. **Related events** come from OpenSearch (`source: opensearch`); click a correlation ID to filter the request chain.
4. Record a review (e.g. *Confirmed issue* with a note). Submit a second review → the history keeps both, with the
   previous state.

## 4. Live path: real requests through the demo workload

```powershell
python scripts/seed/demo_traffic.py --minutes 20          # ~140 requests / 5 min to /demo/*
curl.exe -X POST "http://localhost:5080/demo/scenarios/latency_spike?durationMinutes=6"
# others: error_burst, auth_failure_burst, dependency_failure, retry_storm, traffic_surge (DEV-only)
curl.exe -X DELETE http://localhost:5080/demo/scenarios     # back to normal
```

Telemetry is captured by `OperationalEventMiddleware` (service `demo-shop-api`, environment `development`). Each
5-minute window is finalized 2 minutes after it ends and scored by the background worker, so a live anomaly appears
~7 minutes after the scenario starts. Use **Investigate events** meanwhile (filter service `demo-shop-api`).

## 5. Resilience (TC-04)

```powershell
docker compose stop ml-service
curl.exe http://localhost:5080/demo/products        # still 200 — ML is not in the request path
# System health page: ml-service Degraded; windows show as deferred
docker compose start ml-service                     # backend re-activates the model from PostgreSQL and catches up
```

## 6. Reproducibility

```powershell
scripts\benchmark\run_benchmark.ps1        # regenerate data, retrain, evaluate -> artifacts/evaluations/<id>/
```

Same seed + config ⇒ identical dataset hash and identical metrics.

## Reset

`docker compose down -v` removes containers and the PostgreSQL/OpenSearch volumes (artifacts on disk are kept).
