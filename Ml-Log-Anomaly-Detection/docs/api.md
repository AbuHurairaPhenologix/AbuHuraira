# API Reference

Interactive documentation: **Swagger UI** at `http://localhost:5080/swagger` (two documents: *Anomaly Detection API v1*
and *Demo workload*). All timestamps are UTC ISO-8601. Errors use RFC 7807 ProblemDetails.

## Authentication

| Caller | Mechanism |
|---|---|
| Dashboard users | `POST /api/v1/auth/token {username, password}` → `{accessToken, expiresIn, roles}`; send `Authorization: Bearer …` |
| Event producers | `X-Api-Key: <INGESTION_API_KEY>` on `POST /api/v1/events` |
| Backend → ML service | short-lived JWT (see architecture.md §8) |

Roles: **Engineer** — read + review. **Administrator** — additionally model lifecycle, retraining, pipeline control,
audit. Denied administrative calls return 401/403 and are written to the audit log.

## Backend (`/api/v1`)

| Method & path | Role | Purpose |
|---|---|---|
| `POST /events` | API key | Ingest up to 5,000 events. Returns `202 {received, accepted, acceptedLate, duplicates, quarantined, items[]}` |
| `GET /events?service&environment&from&to&correlationId&eventType&page&pageSize` | Engineer | Constrained investigation search (OpenSearch; PostgreSQL fallback). Response has `source` |
| `GET /events/{id}` | Engineer | One normalized event |
| `GET /events/quarantine` | Engineer | Quarantined records with reason codes (payloads sanitized) |
| `GET /windows?service&environment&from&to&scoringStatus&page&pageSize` | Engineer | Feature windows (8 features, scoring status) |
| `GET /windows/{id}` | Engineer | Window + all scoring records + anomaly id |
| `GET /anomalies?service&environment&reviewState&modelVersion&from&to&minScore&sort&page&pageSize` | Engineer | Ranked anomalies. `sort` = `created` (default), `window`, `score`, `severity` |
| `GET /anomalies/stats?days=7` | Engineer | Dashboard counts, review-state breakdown, trend, active model |
| `GET /anomalies/{id}` | Engineer | Window, score/threshold, model, feature deviations, correlation IDs, reviews |
| `GET /anomalies/{id}/events?eventType&correlationId&page&pageSize` | Engineer | Related raw events for the anomaly window |
| `GET /anomalies/{id}/reviews` | Engineer | Append-only review history |
| `POST /anomalies/{id}/reviews {outcome, note}` | Engineer | `ConfirmedIssue`, `BenignChange`, `FalsePositive`, `DuplicateAlert`, `InsufficientEvidence` (or `Unreviewed` to reopen) |
| `GET /models`, `GET /models/{id}` | Engineer | Registered model versions with metadata and usage counts |
| `GET /models/registry` | Admin | Versions present in the ML artifact registry |
| `POST /models {modelVersion}` | Admin | Register a registry version (label only — paths rejected) |
| `POST /models/{id}/activate` | Admin | Verify artifact/hash/schema/eligibility, activate, deactivate previous |
| `POST /models/{id}/deactivate` | Admin | Deactivate (scoring is deferred until another model is active) |
| `POST /models/retrain {algorithm, source, trainingPeriodStartUtc?, trainingPeriodEndUtc?}` | Admin | Explicit training job; new versions are registered inactive |
| `GET /models/training-jobs/{jobId}` | Admin | Training job status |
| `POST /pipeline/run` | Admin | Index + aggregate + score now (workers do this periodically) |
| `POST /pipeline/requeue` | Admin | Re-queue deferred/rejected windows |
| `GET /audit?action&page` | Admin | Audit trail |
| `GET /system/status` | Engineer | Component health, workers, queue, pipeline settings, stats |
| `GET /health` | anonymous | `{status, components{postgresql, opensearch, ml-service}}` (503 only if Unhealthy) |
| `GET /auth/me` | any user | Current identity and roles |

Container probes: `GET /health/live`, `GET /health/ready` (PostgreSQL).

### Event schema (POST /api/v1/events)

```json
{ "events": [ {
  "eventId": "optional-unique-id",          "eventTimestamp": "2022-02-15T14:31:02Z",
  "serviceName": "identity-api",            "environment": "production",
  "eventType": "http_request",              "endpointGroup": "/login",
  "statusCode": 200,                        "durationMs": 182.4,
  "errorFlag": false,                       "authenticationResult": "success",
  "dependencyName": null,                   "retryCount": 0,
  "correlationId": "4b1c…",                 "attributes": { "build": "1.4.2" }
} ] }
```

`eventType` ∈ `http_request | authentication | dependency_call | background_job` (synonyms are mapped).
`authenticationResult` ∈ `none | success | failure`. Unknown top-level fields and `attributes` are sanitized (secret-like
keys removed, values redacted, e-mail/IP masked) and never used as features. See `data/samples/events-sample.json`.

### Demo workload (`/demo`, anonymous)

`GET /demo/products`, `GET /demo/orders/{id}`, `POST /demo/login`, `GET /demo/dependency`, `POST /demo/job`.
DEV-only scenario switches: `GET /demo/scenarios`, `POST /demo/scenarios/{name}?durationMinutes=10`,
`DELETE /demo/scenarios[/{name}]` — names `latency_spike`, `error_burst`, `auth_failure_burst`, `dependency_failure`,
`retry_storm`, `traffic_surge`. Returns 404 unless scenarios are enabled (Development).

## ML service (internal, port 8000 — not published)

| Method & path | Scope | Purpose |
|---|---|---|
| `POST /internal/anomaly/score` | `anomaly.score` | Score one window |
| `POST /internal/anomaly/score/batch` | `anomaly.score` | Score ≤ 500 windows with one model |
| `GET /internal/models`, `GET /internal/models/{version}` | `models.read` | Registry metadata |
| `POST /internal/models/{version}/activate` / `deactivate` | `models.admin` | Verify (exists, SHA-256, library version, self-test) and (de)activate |
| `POST /internal/training/jobs`, `GET /internal/training/jobs/{id}` | `training.run` | Explicit retraining |
| `GET /health/live`, `GET /health/ready` | none | Liveness / registry + artifact readiness |

Score request (report Appendix "API Contract"; `featureSchema` is accepted as an alias of `schemaVersion`):

```json
{ "windowId": "5c7151f0-7f65-4d34-bf80-2bfa6507b66c", "service": "identity-api", "environment": "production",
  "windowStartUtc": "2022-02-15T14:30:00Z", "windowEndUtc": "2022-02-15T14:35:00Z",
  "modelVersion": "ocsvm-ops-v1-20261001t124738z", "schemaVersion": "ops-v1",
  "features": { "request_count": 142, "error_rate": 0.031, "avg_duration_ms": 248.4, "p95_duration_ms": 681.7,
                "auth_failure_rate": 0.017, "dependency_failure_count": 1, "retry_count": 0, "endpoint_entropy": 2.31 } }
```

Response: `{windowId, score, threshold, isAnomaly, modelVersion, modelId, algorithm, schemaVersion, reasonSummary, reasons[]}`.

| Condition | Status / code |
|---|---|
| Missing/extra feature, wrong type (string/bool/float count), ratio > 1, negative, wrong schema, malformed JSON, path-like or missing `modelVersion` | 422 `validation_failed` |
| Unknown model version | 404 `model_not_registered` |
| Registered but not active | 409 `model_inactive` |
| Artifact missing / hash mismatch / library mismatch | 503 `model_artifact_unavailable` |
| Missing/invalid/expired token | 401 · wrong scope 403 |
