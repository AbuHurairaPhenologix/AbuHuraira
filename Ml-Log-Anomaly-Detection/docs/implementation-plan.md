# Implementation Plan

**Project:** Machine Learning-Based Anomaly Detection for Distributed Web Application Logs
**Primary specification:** [`../project_Report/Abu_Huraira_Project_Report.pdf`](../project_Report/Abu_Huraira_Project_Report.pdf) (32 pages, read in full, including all 12 figures and all tables)
**Plan written:** before any application source code was created. The workspace (`H:\ML`) contained only the report PDF — this is a greenfield implementation.

---

## 1. Project understanding

The report describes a *decision-support* pipeline, not a new algorithm. Its contribution is the integration of established
one-class methods into a traceable software-engineering workflow:

> structured events → deterministic normalization → fixed observation windows → eight versioned features (`ops-v1`)
> → registered model → explicit validated threshold → persisted, reviewable anomaly record → human review.

The machine-learning component is intentionally small and replaceable; the surrounding contracts (event schema,
feature schema, scoring API, anomaly record, model registry) are the core deliverable. The report's conclusion is that
"model scores must be paired with event traceability, threshold governance, versioned artifacts, security controls, and
human review".

## 2. Problem being solved

Distributed web applications produce large volumes of logs; complete incident labels are rarely available. Fixed
threshold rules miss combinations of weak signals (e.g. slightly higher p95 latency + a few dependency failures + some
retries). The system models *normal* window-level behaviour from historical data and **ranks** unusual windows for an
engineer to investigate. An anomaly score is explicitly **not** a root-cause diagnosis or a security verdict (report §1.4).

Research questions to be answered by artifacts (report §1.5):

| RQ | Answering artifact |
|----|--------------------|
| RQ1 – can unsupervised ML detect controlled anomalies at manageable FPR? | `artifacts/evaluations/{run}/metrics.json`, confusion matrices |
| RQ2 – which features distinguish anomalies? | feature-profile plot, per-anomaly-type recall table, reason summaries |
| RQ3 – how does validation threshold selection change P/R/F1/review burden? | threshold-sensitivity tables + curves (validation), default-vs-tuned comparison |
| RQ4 – how to integrate with .NET without entering the request path? | background workers + resilient `HttpClientFactory` client; TC-04 test |

## 3. Scope derived from the PDF

In scope (report §1.7): structured application events from web APIs, authentication flows, background jobs and
dependencies; window-level features; three one-class detectors + Random Forest reference; internal scoring API;
persistence (PostgreSQL) and investigation search (OpenSearch); review workflow; model-version management; reproducible
synthetic benchmark.

Out of scope (report §1.7): network packet inspection, endpoint malware detection, source-code vulnerability scanning,
**automated root-cause diagnosis**, and any **irreversible automated action** (report §3.2: "the system recommends
investigation rather than automatically blocking users or restarting services"). Sequence/deep models are future work.

## 4. Functional requirements (report Table "Functional requirements")

| ID | Requirement (verbatim intent) |
|----|------------------------------|
| FR-01 | Ingest structured operational events from one or more application services. |
| FR-02 | Validate and normalize events into a canonical schema. |
| FR-03 | Aggregate events into configurable observation windows. |
| FR-04 | Generate versioned numerical feature vectors. |
| FR-05 | Score windows using a registered machine-learning model. |
| FR-06 | Persist score, threshold, model version, and source context. |
| FR-07 | Retrieve related raw events for engineering investigation. |
| FR-08 | Record review outcome and reviewer notes. |
| FR-09 | Register, activate, and deactivate model versions through an authorized administrative workflow. |

## 5. Non-functional requirements (report Table "Non-functional requirements")

| Quality | Requirement |
|---------|-------------|
| Security | Secrets and raw authentication material must never enter the feature dataset. |
| Performance | Batch scoring must remain outside the critical end-user request path. |
| Traceability | Every alert must record feature window, model version, score, threshold, and source context. |
| Reproducibility | Training configuration, random seed, feature schema, and evaluation procedure must be versioned. |
| Resilience | ML service failure must not interrupt ordinary application processing. |
| Maintainability | Application and model services must communicate through a stable, versioned contract. |

Additional qualities named in the text: extensibility (model-agnostic contract), availability, privacy.

## 6. Security requirements (report §3.3, §4.8)

* S-1 Preprocessing removes secrets, masks identifiers not required for correlation, restricts features to operational quantities.
* S-2 No secret, password, access token or authorization header in the schema or benchmark.
* S-3 Access to model retraining and raw event context is separated from ordinary alert viewing.
* S-4 Internal model API is not publicly callable: service-to-service authentication with a short-lived token,
  dedicated audience and permission scope; administrative operations require stronger authorization than scoring.
* S-5 Tokens/secrets never included in logs or training features; application identity and machine identity have distinct credentials.
* S-6 A model is never loaded from a user-supplied path; only registered versions from controlled storage (report §4.6).
* S-7 Backend constructs constrained OpenSearch queries; no arbitrary client-side index access (report §4.9).

## 7. Proposed architecture

Aligned with Figure 3.2 (high-level), Figure 3.4 (runtime sequence) and Figure 4.3 (deployment):

```
 Demo workload / external services
        │  structured events (middleware or POST /api/v1/events with ingestion key)
        ▼
 ASP.NET Core backend ──────────────────────────────────────────────────────────────┐
   Ingestion + Normalization + Sanitization ──► PostgreSQL (operational store)       │
        │                                       ▲   │                                │
        │ quarantine (invalid records)          │   └─► OpenSearch indexing worker ──► OpenSearch (event index)
        ▼                                       │                                    │
   WindowAggregationWorker (5-min, per service+environment) → FeatureWindow (ops-v1) │
        ▼                                                                            │
   ScoringWorker ── HttpClientFactory + resilience + short-lived JWT ──► Python FastAPI ML scoring service
        ▼                                                                   (registered model artifacts, read-only)
   ScoringRecord + AnomalyRecord (score, threshold, model version, reason summary)
        ▼
   REST API (/api/v1/…) ──► Angular engineering review dashboard
```

ML never runs inside an end-user request: demo endpoints only enqueue an event into a bounded in-process channel.

## 8. Technology decisions

| Concern | Choice | Reason |
|---------|--------|--------|
| Backend | ASP.NET Core **.NET 10 (LTS)**, SDK 10.0.401 installed | latest stable installed SDK |
| ORM | EF Core 10 + Npgsql 10, snake_case naming (`EFCore.NamingConventions`) | report DDL uses snake_case |
| Resilience | `Microsoft.Extensions.Http.Resilience` (Polly v8): timeout, retry (transient only), circuit breaker | report §4.7 |
| API docs | Swashbuckle (Swagger UI) | OpenAPI |
| Auth (users) | JWT bearer; dev/demo token issuer with Engineer/Administrator roles; OIDC authority mode via config | report §4.8 |
| Auth (service) | HS256 short-lived JWT, `aud=ml-scoring`, scopes `anomaly.score`, `models.admin`, `training.run` | report §4.8 / Fig 4.3 "JWT/OIDC-compatible" |
| ML service | Python **3.12**, FastAPI, Uvicorn, Pydantic v2, NumPy, Pandas, scikit-learn, joblib, matplotlib, pytest | prompt + report |
| Search | OpenSearch 2.x accessed through a typed HTTP client built in the backend | report §4.9 |
| DB | PostgreSQL 17 | report |
| Frontend | **Angular 21** (Angular 22 requires Node ≥24.15; installed Node is 24.11.1) | compatibility |
| Tests | xUnit (.NET), Testcontainers PostgreSQL for integration, pytest (Python), pytest E2E against the running stack | |
| Containers | Docker Compose: postgres, opensearch, (opensearch-dashboards profile), ml-trainer (one-shot), ml-service, backend, frontend (nginx) | report: containerizable but not required for correctness |

## 9. Repository structure

```
H:\ML
├── project_Report/Abu_Huraira_Project_Report.pdf   (untouched specification)
├── README.md, docker-compose.yml, .env.example, .gitignore
├── config/                 benchmark.yaml (single reproducibility manifest), feature-schema ops-v1.json
├── docs/                   plan, traceability, architecture, api, database, ml-pipeline, testing, demo-guide, final-verification
├── src/backend/            AnomalyDetection.sln
│     AnomalyDetection.Domain/          entities, enums, feature schema, pure calculators (no EF)
│     AnomalyDetection.Application/     services, abstractions (ports), DTOs, normalization, windowing, scoring orchestration
│     AnomalyDetection.Infrastructure/  EF Core DbContext + migrations, repositories, OpenSearch client, ML HTTP client, workers
│     AnomalyDetection.Api/             controllers (thin), auth, demo workload, middleware, health checks, Swagger
│     tests/AnomalyDetection.UnitTests/
├── src/ml-service/         app/{api,schemas,core,models,registry,scoring,training,datasets,evaluation}, tests/
├── src/frontend/           Angular dashboard
├── tests/integration/      AnomalyDetection.IntegrationTests (WebApplicationFactory + real PostgreSQL)
├── tests/e2e/              pytest end-to-end flow against the running stack
├── scripts/{setup,seed,benchmark}
├── data/{generated,samples}
└── artifacts/{models,evaluations}
```

## 10. Data model

Based on Figure 3.3 (Service → LogEvent → FeatureWindow → Anomaly ← ModelVersion), extended with the prompt's entities:

| Table | Key columns | Notes |
|-------|-------------|-------|
| `service_definition` | id uuid PK, name, environment, unique(name, environment) | "Service" in ERD |
| `operational_event` | id uuid PK, event_id (unique dedupe key), service_definition_id FK, event_timestamp_utc, event_type, endpoint_group, status_code, duration_ms, error_flag, authentication_result, dependency_name, retry_count, correlation_id, schema_version, sanitized attributes jsonb, processing_state, feature_window_id FK null, is_late, indexed_at_utc, received_at_utc | "LogEvent" in ERD |
| `quarantined_event` | id, received_at_utc, reason codes, sanitized raw payload jsonb, source | invalid records never silently dropped |
| `feature_window` | window_id PK, service_definition_id FK, window_start_utc, window_end_utc, window_size_minutes, feature_schema_version, 8 feature columns, event_count, late_event_count, scoring_status, scoring_attempts, next_scoring_attempt_utc, last_scoring_error, created_at_utc; unique(service, start, end, schema) | stores vector with source window |
| `model_version` | model_id uuid PK, model_version unique, algorithm, feature_schema_version, training period, random seed, library versions, parameters jsonb, validation_threshold, artifact path (registry-relative), artifact sha256, production_eligible, is_active, created_at_utc, activated_at/by | immutable identifiers |
| `scoring_record` | id, window_id FK, model_id FK, score, threshold, is_anomaly, reason_summary, reasons jsonb, scored_at_utc; unique(window_id, model_id) | every score (including below threshold) |
| `anomaly_record` | anomaly_id PK, window_id FK, model_id FK, scoring_record_id FK, score, threshold, is_anomaly, review_state, reason_summary, denormalized service/environment/window, created_at_utc, row version | report DDL + reason summary |
| `anomaly_review` | id, anomaly_id FK, outcome, previous_state, note, reviewer, created_at_utc | append-only history |
| `audit_event` | id, action, actor, target_type, target_id, result, metadata jsonb (safe), occurred_at_utc | model lifecycle, reviews, authorization failures |

Indexes follow the report's access patterns (§4.4): service + time range, review state, model version, anomaly score.

## 11. Event-processing pipeline

1. **Capture** — `OperationalEventMiddleware` (report §4.2 code sample) records duration, status, endpoint group,
   correlation ID; dedicated handlers emit authentication and dependency events. Events go into a bounded channel
   (never blocks the request; overflow is counted and logged).
2. **Ingest** — `IngestionService` accepts batches from the channel or `POST /api/v1/events` (ingestion API key).
3. **Sanitize** — `EventSanitizer` drops secret-like attribute keys (password, token, authorization, cookie, secret,
   api key, credential, private key, session…), redacts secret-like values (JWT, bearer, key material), masks e-mail
   and IP addresses in retained attributes. Secret-like content in a *canonical* field quarantines the event.
4. **Normalize** — `EventNormalizer` (deterministic): UTC conversion, invalid/future timestamp → quarantine, categorical
   mapping (`prod`→`production`, `HttpRequest`→`http_request`, `denied`→`failure`, …), endpoint templating
   (numeric/GUID segments → `{id}`, query strings stripped), null optional fields → canonical defaults,
   negative/non-finite/implausible durations → quarantine, invalid status code → quarantine, negative retry → quarantine.
5. **Deduplicate** — by `event_id` (client-supplied, or a deterministic SHA-256 of canonical fields) within the batch and
   against stored events (unique index). Duplicates are counted in the ingestion response, never double counted (TC-09).
6. **Persist** — PostgreSQL; then asynchronously indexed into OpenSearch by `OpenSearchIndexingWorker`
   (OpenSearch outage only delays search indexing).
7. **Window** — `WindowAggregationWorker` closes windows at `window_end + allowed_lateness` (default 2 min).
8. **Late-arriving policy (TC-10)** — an event whose window is already finalized is stored and indexed for
   investigation, flagged `is_late = true`, counted in the window's `late_event_count`, and **not** merged into the
   finalized feature vector (historical scores are never silently changed).

## 12. Feature-engineering design (`ops-v1`)

Feature order is explicit and shared (`config/feature-schema.ops-v1.json`, C# `FeatureSchema`, Python `FEATURE_NAMES`):

| # | Feature | Definition (per service + environment + window) |
|---|---------|-----------------------------------------------|
| 1 | `request_count` | number of `http_request` events |
| 2 | `error_rate` | request events with `error_flag` (explicit flag or status ≥ 500) ÷ request_count (0 if none) |
| 3 | `avg_duration_ms` | mean duration of request events with a duration |
| 4 | `p95_duration_ms` | 95th percentile, linear interpolation between closest ranks (same as NumPy default) |
| 5 | `auth_failure_rate` | request events with `authentication_result = failure` (incl. 401/403) ÷ request_count |
| 6 | `dependency_failure_count` | `dependency_call` events with `error_flag` |
| 7 | `retry_count` | Σ `retry_count` over all events in the window |
| 8 | `endpoint_entropy` | Shannon entropy, base 2, of the endpoint-group distribution of request events: H = −Σ pᵢ log₂ pᵢ |

Calculation is a pure, deterministic function (`FeatureCalculator`) in the Domain layer; Python mirrors the formulas in
`app/datasets/features.py` for parity tests.

## 13. ML architecture

* `ModelAdapter` hierarchy hides native score direction: **higher score = more anomalous** for every model.
  * Isolation Forest: `score = −decision_function(x)` on raw features (report §5.3: no scaling for IF).
  * LOF (novelty=True): `Pipeline(StandardScaler, LOF)`, `score = −decision_function(x)`.
  * One-Class SVM (RBF, nu=0.04, gamma="scale"): `Pipeline(StandardScaler, OCSVM)`, `score = −decision_function(x)`
    — exactly the report §4.6 snippet; trained on a representative subset of normal training windows.
  * Random Forest (supervised **reference only**, `production_eligible=false`): `score = predict_proba[:, 1]`.
* Benchmark (report §5.2): 32,000 normal + 1,800 anomaly windows; Poisson(140) requests; log-normal avg latency ≈250 ms;
  p95 ≈ 2.6×avg + Gaussian noise, constrained > avg; Beta error rate mean ≈0.02; Beta auth-failure mean ≈0.015;
  zero-inflated Poisson dependency failures (mean ≈0.15) and retries (≈0.10); bounded Gaussian entropy ≈2.3.
  Anomaly classes: latency spike (×2.5–4.0), auth failure burst (×6–10), dependency instability (rate ×5),
  retry burst (rate ×5), traffic surge (≈×2), error burst (mean 0.10–0.15). Moderate shifts keep classes overlapping.
* Split (chronological, by synthetic time): train 16,000 normal (+900 anomalies used **only** by Random Forest),
  validation 8,000 + 450, test 8,000 + 450 (the report's IF confusion matrix totals 8,000 normal / 450 anomaly test windows).

## 14. Threshold strategy

Report §3.9: candidate thresholds are **quantiles of the normal validation-score distribution**; for each candidate compute
precision, recall, F1, FPR, confusion matrix, and review burden (alerts per 1,000 windows and per service-day);
select by the configured objective (default `max_f1`; alternative `max_recall_at_fpr` with target FPR). The test set is
untouched until selection completes. Selected threshold persisted in model metadata, in `model_version`, and in every
`scoring_record`/`anomaly_record`. The library default decision (`predict() == -1`) is reported only for comparison.

## 15. Model registry / lifecycle

* Python registry (`artifacts/models/registry.json` + `{version}/model.joblib` + `{version}/metadata.json` with SHA-256)
  is the controlled artifact store; read-only at runtime.
* Backend `model_version` table is the source of truth for which model is **active** (one active per feature schema).
* Register: admin → backend → ML `GET /internal/models/{version}` (metadata) → validate → insert (inactive). Audited.
* Activate: admin only; verifies artifact exists + hash, schema compatible (`ops-v1`), metadata complete, production
  eligible; ML `POST /internal/models/{version}/activate` loads & self-tests the model; DB flip; audited.
* Deactivate: admin only; audited; scoring is deferred while no model is active.
* ML rejects unknown (404), inactive (409), unavailable/corrupt (503) versions. The backend reconciles the ML service's
  in-memory active set with the database after an ML restart.
* Retraining is an explicit admin action (`POST /api/v1/models/retrain` → ML training job) or CLI; never automatic.
  New versions are registered **inactive**.

## 16. Backend API design

Versioned under `/api/v1`, DTOs + validation, pagination (`page`, `pageSize` ≤ 200), filtering:
`/events` (POST ingest, GET search), `/events/{id}`, `/events/quarantine`, `/windows`, `/windows/{id}`,
`/anomalies`, `/anomalies/{id}`, `/anomalies/{id}/events`, `/anomalies/{id}/reviews` (GET/POST), `/anomalies/stats`,
`/models`, `/models/{id}`, `/models/registry`, `/models` (POST register), `/models/{id}/activate`,
`/models/{id}/deactivate`, `/models/retrain`, `/pipeline/run`, `/system/status`, `/health`, `/auth/token`, `/audit`.
Demo workload: `/demo/products`, `/demo/orders/{id}`, `/demo/login`, `/demo/dependency`, `/demo/job`,
`/demo/scenarios` (Development-only toggle).

## 17. OpenSearch strategy

Index `ops-events-v1` with explicit mapping (keyword fields for service/environment/event type/endpoint/correlation ID,
date for timestamp). Backend builds a `bool.filter` query from a typed `EventSearchCriteria` (service, environment,
time range, correlation ID, event type) — the browser never sends Query DSL. Bulk indexing is asynchronous and
retried; if OpenSearch is unavailable, investigation queries fall back to PostgreSQL with the same filters and the
response states which source was used. Health check reports OpenSearch as Degraded, not fatal.

## 18. Review workflow

Review outcomes: `Unreviewed`, `ConfirmedIssue`, `BenignChange`, `FalsePositive`, `DuplicateAlert`,
`InsufficientEvidence`. Each review appends an `anomaly_review` row (previous state, new outcome, note, reviewer,
timestamp) and updates `anomaly_record.review_state`; history is never overwritten. Reviews are audited. Reviews do not
change scores (labels can later support supervised experiments, report §4.9).

## 19. Frontend design

Angular 21 standalone components, real API calls only. Pages: Login, Overview (processed windows, anomalies, anomaly
rate, unreviewed, active model, 7-day anomaly trend, component health), Anomaly list (filters, sort, paging),
Anomaly details (window, score vs threshold, 8 features vs model baseline, reason summary, related events with
correlation IDs from OpenSearch, review history + form), Event investigation (constrained search), Model management
(registry, register, activate, deactivate, retrain — admin), System health. Served by nginx which proxies `/api` to the
backend (same origin).

## 20. Authentication / authorization strategy

* Users: JWT bearer. Development/demo issuer `POST /api/v1/auth/token` checks configured demo users (passwords from
  environment, never in source). Roles: `Engineer` (inspect + review), `Administrator` (model lifecycle, retrain,
  pipeline trigger, audit). Production: `Authentication:Mode=Oidc` validates tokens from an external OIDC authority.
* Ingestion: `X-Api-Key` (service credential, constant-time compare).
* Backend → ML: short-lived (5 min) HS256 JWT, `iss=anomaly-backend`, `aud=ml-scoring`, scopes per operation;
  signing key from environment. Compatible with workload identity/OIDC client-credentials in production.
* Authorization failures on admin endpoints are written to `audit_event` (TC-07).

## 21. Testing strategy

* .NET unit tests: feature calculations (each feature, p95, entropy), normalization, sanitization, dedupe, late policy,
  windowing, review lifecycle, activation validation, scoring deferral on timeout.
* .NET integration tests (real PostgreSQL via Testcontainers): repositories, API contracts, authorization + audit,
  full in-process pipeline E2E with an ML stub at the HTTP boundary, TC-04 timeout behaviour.
* Python pytest: schema validation (TC-02/03), score direction, threshold selection, registry safety, adapters,
  generator statistics, TC-05 latency, TC-08 secret columns, API auth.
* E2E (`tests/e2e`): against the running Docker stack — send events → … → review persisted (synthetic timestamps).
* Documented test cases TC-01…TC-10 mapped in `docs/testing.md`.

## 22. Docker / deployment design

Compose services on an internal network with health checks and named volumes; only frontend (and optionally backend
for Swagger) published to the host. `ml-trainer` one-shot service generates the benchmark and trains models if the
registry is missing; `ml-service` mounts artifacts read-only (report §4.11 "model artifacts are read-only at runtime").
Credentials come from `.env` (template `.env.example`).

## 23. Reproducibility strategy

`config/benchmark.yaml` holds seed, synthetic parameters, split sizes, algorithms, hyperparameters and threshold
settings. Each training run writes metadata (seed, config hash, library versions, training period) and each evaluation
run writes `environment.json` (Python + package versions), the config snapshot, metrics, and plots to
`artifacts/evaluations/{run-id}/`. Python dependencies are pinned.

## 24. Implementation phases

Exactly the 23 phases in the brief: read PDF → inspect → plan → structure → domain → persistence → ingestion →
features → dataset → training/evaluation → registry → FastAPI → integration → background scoring → reviews →
OpenSearch → frontend → security → tests → Docker → benchmark → verification → docs. Each phase is built/tested
before the next.

## 25. Assumptions

* A1 Endpoint entropy uses log base 2 (report does not specify; base 2 places the demo's five endpoint groups near 2.3).
* A2 Rates use request events as the denominator; standalone `authentication` events are stored for traceability but
  only request events with `authentication_result=failure` count toward `auth_failure_rate` (no double counting).
* A3 Timestamps without an offset are interpreted as UTC; timestamps > 5 min in the future are quarantined.
* A4 Empty windows (no events) are not created or scored (documented limitation).
* A5 The report lists five anomaly families; "dependency/retry" is split into two classes (six total) as requested.
* A6 Scoring request accepts both `schemaVersion` (appendix contract) and `featureSchema` (report §4.7 example).

## 26. Risks and limitations

* Synthetic benchmark ≠ production evidence (report §1.8, §5.10); metrics will differ from Table 5.1.
* OCSVM training cost grows super-linearly — trained on a subset.
* Concept drift is not automatically detected (future work); retraining is manual.
* Demo authentication is for development only.
* OpenSearch security plugin disabled in the development compose file (internal network only) — documented.

## 27. Requirement Traceability Matrix

See [`requirement-traceability.md`](requirement-traceability.md) (kept up to date as implementation proceeds).
