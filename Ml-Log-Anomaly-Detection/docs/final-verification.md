# Final Verification

Date: 2026-10-01 · Machine: Windows 11 Pro, 12 logical CPUs, 15.7 GB RAM · Docker Desktop 28.5.1 (Compose v2.40.3)

## 1. What was implemented

* **Backend** (`src/backend`, .NET 10): Domain / Application / Infrastructure / Api. Ingestion with sanitization,
  normalization, duplicate detection and quarantine; configurable windowing (1/5/15 min) with a late-arrival policy;
  the eight `ops-v1` features; background workers (event queue, aggregation, scoring with deferral and back-off,
  OpenSearch indexing, opt-in model bootstrap); resilient ML client (timeout, transient retry, circuit breaker,
  short-lived scoped JWT); model lifecycle (register / activate / deactivate / retrain) with verification and audit;
  append-only reviews; dashboard statistics; constrained OpenSearch investigation with PostgreSQL fallback; JWT auth
  with Engineer/Administrator roles (OIDC mode available); audited authorization denials; health checks; Swagger;
  a demo workload with DEV-only anomaly scenarios. EF Core migration `InitialCreate` (9 tables, 27 indexes).
* **ML service** (`src/ml-service`, Python 3.12): deterministic synthetic benchmark (report §5.2), chronological
  splits, four model adapters with a uniform score direction, validation-based threshold selection, controlled
  registry (SHA-256, containment, library-version check), FastAPI internal scoring API (single + batch), activation,
  explicit training jobs, evaluation with metrics and plots.
* **Frontend** (`src/frontend`, Angular 21): sign-in, Overview, Anomalies, Anomaly detail with review, Investigate
  events, Models, System health — all backed by real API calls.
* **Docker Compose**: postgres, opensearch (+ optional dashboards), ml-trainer (one-shot), ml-service, backend,
  frontend; health checks, named volumes, internal network, secrets from `.env`.
* **Tooling**: env generator, dev setup / run-local scripts, benchmark runner, historical backfill, live traffic generator.
* **Docs**: plan, traceability, architecture (Mermaid), API, database, ML pipeline, testing, demo guide, README.

## 2. Commands executed and results (final pass, from the current source)

| Command | Result |
|---|---|
| `dotnet --list-sdks` | 9.0.316, **10.0.401** → net10.0 selected |
| `dotnet restore src/backend/AnomalyDetection.sln` | success |
| `dotnet build src/backend/AnomalyDetection.sln -c Release` | **Build succeeded, 0 warnings, 0 errors** |
| `dotnet test src/backend/AnomalyDetection.sln -c Release` | **Unit 105/105 passed; Integration 17/17 passed** (real PostgreSQL 17 + OpenSearch 2.19.2 via Testcontainers) |
| `src/ml-service: python -m pytest` | **80 passed** |
| `src/frontend: npm run build` | bundle generated (initial ~75 kB transfer), no errors |
| `src/frontend: npx ng test --watch=false` | **8 passed** (Vitest) |
| `docker compose config --quiet` | OK |
| `docker compose build` | backend, ml-service, frontend images built |
| `docker compose up -d` | all services healthy; ml-trainer exited 0 (registry present → skipped training) |
| `E2E_ALLOW_OUTAGE=1 python -m pytest tests/e2e -v` | **4 passed** (full pipeline with real model, TC-07, live telemetry/correlation, live ML outage TC-04) |

## 3. Benchmark execution

```
python -m app.training.generate_data --force   # 33,800 windows, seed 20220215
python -m app.training.train --algorithm all   # run 20261001t124738z (~11 s for all four models)
python -m app.evaluation.evaluate              # eval-20261001t124738z-20261001t124752z
```

Dataset: `data/generated/benchmark-ops-v1-seed20220215-ae708426ee.csv` (train 16,000+900, validation 8,000+450,
test 8,000+450; 300 windows per anomaly class). An earlier trial run into a scratch directory produced identical
thresholds and metrics, confirming determinism.

## 4. Generated model artifacts (`artifacts/models`)

| Version | Algorithm | Validation threshold | Validation F1 | Production eligible |
|---|---|---|---|---|
| `if-ops-v1-20261001t124738z` | Isolation Forest | 0.034452 | 0.356 | yes |
| `lof-ops-v1-20261001t124738z` | LOF (novelty) | 0.046132 | 0.643 | yes |
| `ocsvm-ops-v1-20261001t124738z` | One-Class SVM | 2.915572 | 0.645 | yes — **recommended** (best validation F1) |
| `rf-ops-v1-20261001t124738z` | Random Forest | 0.596104 | 0.678 | no (reference only) |

Each directory holds `model.joblib`, `metadata.json` (seed, config/dataset hashes, library versions, parameters,
training period, threshold, baseline statistics) and `validation_sensitivity.json`; `registry.json` indexes them.
The Linux container loaded and hash-verified these Windows-trained artifacts (same Python/scikit-learn versions).

## 5. Generated metrics (test set; `artifacts/evaluations/eval-20261001t124738z-20261001t124752z/`)

| Model | Precision | Recall | F1 | FPR | ROC-AUC | PR-AUC | TP | FP | TN | FN |
|---|---|---|---|---|---|---|---|---|---|---|
| Isolation Forest | 0.318 | 0.411 | 0.359 | 0.0495 | 0.825 | 0.312 | 185 | 396 | 7604 | 265 |
| Local Outlier Factor | 0.859 | 0.529 | 0.655 | 0.0049 | 0.827 | 0.642 | 238 | 39 | 7961 | 212 |
| One-Class SVM | 0.830 | 0.573 | 0.678 | 0.0066 | 0.830 | 0.662 | 258 | 53 | 7947 | 192 |
| Random Forest (ref.) | 0.854 | 0.584 | 0.694 | 0.0056 | 0.858 | 0.680 | 263 | 45 | 7955 | 187 |

Library-default `predict()` for comparison: IF F1 0.340 @ FPR 0.118; LOF 0.651 @ 0.0073; OCSVM 0.532 @ 0.048;
RF 0.676 @ 0.0091. Plots: model comparison, four confusion matrices + grid, four score distributions, four
threshold-sensitivity curves, feature profile, recall by anomaly type.

## 6. Docker verification

* `docker compose ps`: postgres, opensearch, ml-service, backend, frontend all `healthy`.
* `GET http://localhost:5080/api/v1/health` and via nginx `http://localhost:8081/api/v1/health` →
  `{"status":"Healthy","components":{"postgresql":"Healthy","opensearch":"Healthy","ml-service":"Healthy"}}`.
* Bootstrap registered all 4 registry models and activated `ocsvm-ops-v1-20261001t124738z` as `system-bootstrap`.
* Live outage (E2E): `docker compose stop ml-service` → demo requests still 200, window `Deferred`, health
  `ml-service: Degraded` (overall not Unhealthy); after `start`, the backend re-activated the model from PostgreSQL
  and scored the deferred window.

## 7. API verification

* Backfill through the real ingestion API: 29,579 events / 144 windows (2 services × 6 h) with 5 injected
  scenarios → **exactly the 5 injected windows flagged** (latency spike, error burst, auth-failure burst, dependency
  failure, traffic surge), 0 of 139 normal windows flagged, all with correct non-causal reason summaries.
* Dashboard checked in headless Chrome (Overview, Anomalies, Anomaly detail with OpenSearch-backed related events,
  Models, System health): pages render live data, no browser console errors.
* Swagger document lists the versioned endpoints (integration test).

## 8. Known limitations

* Synthetic benchmark only; no production logs were available (report §1.8).
* Retry-burst and dependency-instability classes are largely undetectable at the specified rates (see
  `ml-pipeline.md` error analysis); overall recall is therefore lower than the report's Table 5.1.
* Windows with zero events are not created/scored.
* The ML service keeps activation state in memory (the backend database is authoritative and reconciles it).
* One ML service replica and one backend replica assumed (no distributed locking for workers beyond DB unique keys).
* Development security settings: demo users, OpenSearch security plugin disabled on the internal network.
* Live-demo anomalies appear ~7 minutes after a scenario starts (5-minute window + 2-minute lateness).

## 9. Deviations from the original report (documented, intentional)

1. Deployment figure shows ML → OpenSearch; here only the backend accesses OpenSearch (least privilege).
2. Report §4.7 example uses `featureSchema`, appendix uses `schemaVersion`; both accepted.
3. `anomaly_record` DDL extended with `reason_summary`, denormalized window/service fields, row version.
4. Report's five anomaly families split into six classes (dependency instability and retry burst separately);
   dependency instability adds a mild latency increase (report §1.3 motivation).
5. Endpoint entropy uses log base 2 (unspecified in the report); rates use request events as denominator.
6. Benchmark metrics are computed, and differ from Table 5.1 (qualitative ranking matches).
7. The report's DDL `review_state` values extended with `FalsePositive` per the prompt and use-case diagram.
