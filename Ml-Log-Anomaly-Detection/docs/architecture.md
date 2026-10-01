# Architecture

Aligned with the report's Figure 3.2 (high-level architecture), Figure 3.3 (ERD), Figure 3.4 (runtime sequence),
Figure 3.1/4.1 (ML processing pipeline), use-case diagram, and Figure 4.3 (deployment topology).

## 1. High-level architecture (Figure 3.2)

```mermaid
flowchart LR
    APP["ASP.NET Core applications & APIs<br/>(demo workload /demo/*, external producers)"]
    LOG["Structured logging &amp; correlation IDs<br/>OperationalEventMiddleware · POST /api/v1/events"]
    ING["Ingestion · sanitization · normalization<br/>(quarantine for invalid records)"]
    PG[("PostgreSQL<br/>operational store")]
    OS[("OpenSearch<br/>event index ops-events-v1")]
    FE["Feature engineering service<br/>5-min windows · ops-v1 (8 features)"]
    ML["Python ML scoring service<br/>FastAPI · registered models"]
    API["Alert API (/api/v1)"]
    UI["Engineering review dashboard (Angular)"]

    APP --> LOG --> ING --> PG
    PG -- "async indexing worker" --> OS
    PG --> FE -- "batch score (JWT, resilient)" --> ML
    ML -- "score, threshold, reasons" --> FE
    FE -- "scoring_record / anomaly_record" --> PG
    PG --> API
    OS -- "constrained queries" --> API
    API --> UI
```

The ML service is reachable only from the backend, on the internal network. Machine learning is never in the
end-user request path: the demo endpoints only enqueue an event into a bounded in-process channel.

## 2. Event pipeline (Figure 3.1 "Machine-learning processing pipeline")

```mermaid
flowchart LR
    RAW[Raw events] --> SAN[Sanitize<br/>drop secret keys, redact tokens,<br/>mask e-mail / IP, strip query strings]
    SAN --> NORM[Normalize<br/>UTC, categories, endpoint templates,<br/>range checks, derived event IDs]
    NORM -->|invalid| Q[(quarantined_event)]
    NORM -->|valid| DEDUP[De-duplicate by event_id]
    DEDUP --> STORE[(operational_event)]
    STORE --> WIN[Window aggregation<br/>service + environment + 5 min]
    WIN -->|late event, window finalized| LATE[stored, flagged is_late,<br/>excluded from features]
    WIN --> FEAT[8 features ops-v1]
    FEAT --> SCORE[Batch scoring job]
    SCORE -->|ML unavailable| DEFER[Deferred + exponential back-off]
    DEFER --> SCORE
    SCORE --> TH[Threshold decision<br/>score ≥ validation threshold]
    TH --> REC[(scoring_record)]
    TH -->|anomaly| ANOM[(anomaly_record)]
    ANOM --> REVIEW[Engineer review<br/>append-only history]
```

## 3. Runtime scoring sequence (Figure 3.4)

```mermaid
sequenceDiagram
    participant App as Application (demo workload)
    participant Col as Log collector (middleware + queue + ingestion)
    participant FS as Feature service (aggregation + scoring workers)
    participant ML as ML scoring service
    participant Store as Alert store (PostgreSQL)
    participant UI as Dashboard / API

    App->>Col: emit structured event (non-blocking enqueue)
    Col->>Store: sanitize + normalize + persist (quarantine invalid)
    FS->>Store: read events of closed windows
    FS->>FS: compute ops-v1 feature vector
    FS->>ML: POST /internal/anomaly/score/batch (Bearer JWT, scope anomaly.score)
    alt ML available
        ML-->>FS: score, threshold, isAnomaly, reasons
        FS->>Store: persist scoring_record (+ anomaly_record if flagged)
    else timeout / 5xx / circuit open
        FS->>Store: mark window Deferred (retry with back-off)
    end
    UI->>Store: GET /api/v1/anomalies, /{id}, /{id}/events
    UI->>Store: POST /api/v1/anomalies/{id}/reviews
```

## 4. Data model / ERD (Figure 3.3, extended)

```mermaid
erDiagram
    SERVICE_DEFINITION ||--o{ OPERATIONAL_EVENT : emits
    SERVICE_DEFINITION ||--o{ FEATURE_WINDOW : "aggregated into"
    FEATURE_WINDOW ||--o{ OPERATIONAL_EVENT : "contains (or late)"
    FEATURE_WINDOW ||--o{ SCORING_RECORD : "scored as"
    MODEL_VERSION ||--o{ SCORING_RECORD : produces
    SCORING_RECORD ||--o| ANOMALY_RECORD : "flags"
    MODEL_VERSION ||--o{ ANOMALY_RECORD : "originally produced"
    FEATURE_WINDOW ||--o{ ANOMALY_RECORD : "source window"
    ANOMALY_RECORD ||--o{ ANOMALY_REVIEW : "review history"

    SERVICE_DEFINITION { uuid id PK; string name; string environment }
    OPERATIONAL_EVENT { uuid id PK; string event_id UK; uuid service_definition_id FK; timestamptz event_timestamp_utc; string event_type; string endpoint_group; int status_code; float duration_ms; bool error_flag; string authentication_result; string dependency_name; int retry_count; string correlation_id; jsonb attributes_json; uuid feature_window_id FK; bool is_late }
    QUARANTINED_EVENT { uuid id PK; string reason_codes; jsonb sanitized_payload_json }
    FEATURE_WINDOW { uuid window_id PK; uuid service_definition_id FK; timestamptz window_start_utc; timestamptz window_end_utc; string feature_schema_version; int request_count; float error_rate; float avg_duration_ms; float p95_duration_ms; float auth_failure_rate; int dependency_failure_count; int retry_count; float endpoint_entropy; int event_count; string scoring_status }
    MODEL_VERSION { uuid model_id PK; string model_version UK; string algorithm; string feature_schema_version; float validation_threshold; string artifact_sha256; bool production_eligible; bool is_active }
    SCORING_RECORD { uuid id PK; uuid window_id FK; uuid model_id FK; float score; float threshold; bool is_anomaly; string reason_summary }
    ANOMALY_RECORD { uuid anomaly_id PK; uuid window_id FK; uuid model_id FK; float score; float threshold; bool is_anomaly; string review_state; string reason_summary; timestamptz created_at_utc }
    ANOMALY_REVIEW { uuid id PK; uuid anomaly_id FK; string previous_state; string outcome; string note; string reviewer }
    AUDIT_EVENT { uuid id PK; string action; string actor; string target_id; string result; jsonb metadata_json }
```

## 5. Deployment topology (Figure 4.3)

```mermaid
flowchart LR
    subgraph host["Host (127.0.0.1 only)"]
      B[Browser]
    end
    subgraph net["Docker network: anomaly-detection-internal"]
      FE["frontend<br/>nginx + Angular :80"]
      BE["backend<br/>ASP.NET Core :8080"]
      MLS["ml-service<br/>FastAPI :8000"]
      TR["ml-trainer (one-shot)"]
      PG[("postgres :5432")]
      OS[("opensearch :9200")]
      ART[["./artifacts/models<br/>(registry + joblib artifacts)"]]
    end
    B -->|":8081"| FE
    B -.->|":5080 Swagger (dev)"| BE
    FE -->|"/api, /demo proxy"| BE
    BE --> PG
    BE --> OS
    BE -->|"JWT (aud=ml-scoring, per-call scope)"| MLS
    MLS --> ART
    TR --> ART
```

*Deviation from Figure 4.3:* the report's deployment sketch draws an arrow from the ML scoring API to OpenSearch.
In this implementation the backend owns all OpenSearch access (indexing and constrained queries) and the ML service
has no search credentials — least privilege, and the ML service stays a pure scoring component. The model artifact
store sits beside the ML service exactly as in the figure.

## 6. Use cases (report "Primary use cases")

| Actor | Use case | Implementation |
|---|---|---|
| Software engineer | Review anomaly | Anomaly list + detail pages, `GET /api/v1/anomalies[/{id}]` |
| Software engineer | Inspect event context | Related events (OpenSearch), correlation IDs, Investigate page |
| Software engineer | Mark false positive | Review form → `POST /api/v1/anomalies/{id}/reviews` (`FalsePositive`, …) |
| Administrator | Retrain / activate model | Models page → register / activate / deactivate / retrain (audited) |

## 7. Layering (backend)

| Project | Responsibility | Depends on |
|---|---|---|
| `AnomalyDetection.Domain` | Entities, enums, `FeatureSchema`, `FeatureCalculator`, `WindowAligner` — no EF Core | – |
| `AnomalyDetection.Application` | Services (ingestion, aggregation, scoring, lifecycle, reviews, queries), ports | Domain, EF Core abstractions |
| `AnomalyDetection.Infrastructure` | PostgreSQL DbContext + migrations, ML HTTP client (resilience, service JWT), OpenSearch client, workers, health checks | Application |
| `AnomalyDetection.Api` | Thin controllers, auth, middleware, demo workload, Swagger | Infrastructure |

## 8. Security architecture

* **Users:** JWT bearer, roles `Engineer` / `Administrator`. Development issuer for demo identities; `Auth:Mode=Oidc`
  validates tokens from an external OpenID Connect authority (SSO) in production.
* **Producers:** `X-Api-Key` service credential on `POST /api/v1/events` (constant-time comparison).
* **Backend → ML:** short-lived (5 min) HS256 JWT, `iss=anomaly-backend`, `aud=ml-scoring`, one scope per call
  (`anomaly.score`, `models.read`, `models.admin`, `training.run`). Compatible with OIDC client-credentials /
  workload identity (swap the issuer, keep audience/scope checks).
* **Data:** secrets never persisted (sanitizer), training data validated (only the eight operational features).
* **Production hardening (not enabled in the dev compose file):** OpenSearch security plugin with TLS + auth, TLS
  termination at the edge, secrets from a vault, OIDC instead of demo users.
