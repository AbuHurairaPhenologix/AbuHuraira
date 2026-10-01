# Database (PostgreSQL)

Schema is managed by EF Core migrations in
`src/backend/AnomalyDetection.Infrastructure/Persistence/Migrations` (initial migration `InitialCreate`).
Naming is snake_case (like the report's `anomaly_record` DDL). UUID primary keys throughout.

## Applying migrations

* Automatically at start-up when `Database:ApplyMigrationsOnStartup=true` (Development and Docker).
* Manually: `dotnet ef database update -p src/backend/AnomalyDetection.Infrastructure -s src/backend/AnomalyDetection.Api`
  (set `ConnectionStrings__Postgres`).
* New migration: `dotnet ef migrations add <Name> -p src/backend/AnomalyDetection.Infrastructure -s src/backend/AnomalyDetection.Api -o Persistence/Migrations`.

## Tables

| Table | Purpose | Key constraints / indexes |
|---|---|---|
| `service_definition` | service + environment | unique (name, environment) |
| `operational_event` | normalized, sanitized events | **unique event_id** (TC-09); (service, timestamp); (service_name, environment, timestamp); correlation_id; feature_window_id; (processing_state, timestamp); partial index on unindexed events |
| `quarantined_event` | invalid inputs with reason codes and sanitized payload | received_at |
| `feature_window` | ops-v1 vector + scoring job state (`Pending/Scored/Deferred/Rejected`, attempts, next attempt, last error) | **unique (service, start, end, schema)** (idempotent aggregation); (scoring_status, next_attempt); (service_name, environment, start) |
| `model_version` | immutable registered models (threshold, hash, seed, params, library versions, training period) | unique model_version; **partial unique (feature_schema_version) WHERE is_active** — one active model per schema |
| `scoring_record` | every score of every window (incl. below threshold) with the threshold actually used | **unique (window_id, model_id)** |
| `anomaly_record` | reviewable alert (report DDL + reason summary, denormalized service/window, xmin row version) | unique (window_id, model_id); (review_state, created); model_id; score; (service, env, window_start) |
| `anomaly_review` | append-only review history (previous state, outcome, note, reviewer) | (anomaly_id, created) |
| `audit_event` | model lifecycle, reviews, pipeline actions, authorization denials | occurred_at; (action, occurred_at) |

Foreign keys from anomalies to the window **and** the model that produced them use `RESTRICT`, so a historical alert
always keeps its original model (report §3.12). Index choices follow the report's access patterns (§4.4): service and
time range, review state, model version and anomaly score.

## Retention

Raw `operational_event` rows can be pruned by age while `feature_window`, `scoring_record`, `anomaly_record`,
`anomaly_review` and `audit_event` are kept for reproducibility (report §4.4). Late-event policy: see ml-pipeline.md.
