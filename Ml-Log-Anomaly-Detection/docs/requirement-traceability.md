# Requirement Traceability Matrix

Status reflects the verified state recorded in [`final-verification.md`](final-verification.md) (2026-10-01).
Paths are relative to the repository root; `.NET` = `src/backend`, `ML` = `src/ml-service`, `IT` = `tests/integration`.

## Functional requirements (report Table "Functional requirements")

| Req | Component | Class / service / API | Table(s) | Automated test(s) | Status |
|---|---|---|---|---|---|
| FR-01 Ingest structured events from one or more services | Api, Application | `OperationalEventMiddleware`, `ChannelEventQueue` → `EventQueueWorker`; `EventsController.Ingest` (`POST /api/v1/events`, X-Api-Key); `IngestionService` | `operational_event`, `service_definition` | IT `Ingestion_requires_the_service_api_key`, `Full_pipeline_…`; E2E `test_demo_workload_generates_live_telemetry` | ✅ Verified |
| FR-02 Validate & normalize into canonical schema | Application | `EventSanitizer`, `EventNormalizer`, `QuarantineReasons` | `operational_event`, `quarantined_event` | `EventNormalizerTests`, `EventSanitizerTests`, `Invalid_records_are_quarantined_not_dropped` | ✅ Verified |
| FR-03 Configurable observation windows | Domain, Application, Infrastructure | `WindowAligner` (1/5/15 min), `PipelineOptions.WindowSizeMinutes`, `WindowAggregationService`, `WindowAggregationWorker` | `feature_window` | `WindowAlignerTests`, `Windows_are_partitioned_by_service_environment_and_time`, `Open_windows_are_not_finalized…`, `Aggregation_is_idempotent` | ✅ Verified |
| FR-04 Versioned numerical feature vectors | Domain + ML | `FeatureSchema` (`ops-v1`, ordered), `FeatureCalculator`, `FeatureStatistics`; ML `core/feature_schema.py`, `datasets/features.py`; `config/feature-schema.ops-v1.json` | `feature_window` | `FeatureCalculatorTests`, `All_eight_features_are_computed_from_events`, ML `test_features.py` (parity) | ✅ Verified |
| FR-05 Score with a registered model | Infrastructure + ML | `ScoringService`, `MlScoringClient` (batch); FastAPI `/internal/anomaly/score[/batch]`, `ScoringService` (py), `ModelRegistry` | `scoring_record` | `ScoringServiceTests`, ML `test_api.py`, `test_registry.py`, E2E full pipeline (real model) | ✅ Verified |
| FR-06 Persist score, threshold, model version, source context | Application | `ScoringService.PersistResultsAsync`, `ScoringRecord`, `AnomalyRecord` | `scoring_record`, `anomaly_record` | `Scores_windows_persists_threshold…`, IT `Full_pipeline_…`, E2E | ✅ Verified |
| FR-07 Retrieve related raw events | Application, Infrastructure | `EventInvestigationService` (OpenSearch + PostgreSQL fallback), `OpenSearchQueryBuilder`, `OpenSearchEventSearchService`, `IndexingService`; `GET /anomalies/{id}/events`, `GET /events` | `operational_event`, OpenSearch `ops-events-v1` | `OpenSearchQueryBuilderTests`, IT `Full_pipeline_…` (source=opensearch), `Search_accepts_only_constrained_filters`, E2E | ✅ Verified |
| FR-08 Record review outcome and notes | Domain, Application | `AnomalyRecord.Review`, `ReviewService`; `POST/GET /anomalies/{id}/reviews`; Angular detail page | `anomaly_review`, `anomaly_record`, `audit_event` | `ReviewLifecycleTests`, IT `Full_pipeline_…`, E2E review step | ✅ Verified |
| FR-09 Register / activate / deactivate via authorized workflow | Application, Api, ML | `ModelLifecycleService`, `ModelsController` (Administrator policy), `AuditingAuthorizationResultHandler`; ML `/internal/models/*` (scope `models.admin`) | `model_version`, `audit_event` | `ModelLifecycleServiceTests`, IT `TC07_…`, `Benchmark_reference_model_cannot_be_activated…`, `Only_one_model_can_be_active_per_schema`, ML `test_model_listing_and_activation_state` | ✅ Verified |

## Non-functional requirements

| Quality | Requirement | Implementation | Test(s) | Status |
|---|---|---|---|---|
| Security | Secrets/raw auth material never enter the feature dataset | `EventSanitizer` (keys removed, values redacted, query strings stripped, secret in canonical field → quarantine); features computed only from canonical operational fields; ML `datasets/validation.py` rejects sensitive/unexpected columns | TC-08 tests (.NET unit + IT + Python) | ✅ |
| Performance | Batch scoring outside the end-user request path | Middleware only enqueues; `ScoringWorker` batches ≤ 100 windows; ML batch endpoint ≤ 500 | IT TC-04 (demo requests unaffected), E2E outage test | ✅ |
| Traceability | Every alert records window, model version, score, threshold, source context | `anomaly_record` (window FK, model FK, scoring FK, score, threshold, reason) + correlation IDs + related events | IT/E2E full pipeline assertions | ✅ |
| Reproducibility | Config, seed, schema, evaluation procedure versioned | `config/benchmark.yaml` (hash in metadata), seed 20220215, pinned requirements & NuGet, metadata (library versions, dataset hash), evaluation `environment.json` + config snapshot | `test_generation_is_deterministic_for_a_seed`, `test_trained_models_persist…metadata`; identical metrics in two independent runs | ✅ |
| Resilience | ML failure must not interrupt ordinary processing | Polly timeout/retry/circuit breaker; never-throwing `MlScoringClient`; `Deferred` status with back-off; activation reconciliation after ML restart; health = Degraded | `TC04_*` unit/IT/E2E, `After_ml_restart_activation_is_reconciled…` | ✅ |
| Maintainability | Stable, versioned contract | `/internal/anomaly/score` + `ops-v1` + `event-v1`; strict Pydantic validation; `/api/v1` | ML TC-02 tests, `Swagger_document_describes_the_versioned_api` | ✅ |
| Availability / observability | Health checks, structured logs | `/api/v1/health`, `/api/v1/system/status`, `/health/live|ready` (backend and ML); JSON console logs with correlation scope | `Health_reports_each_dependency`, ML `test_health_endpoints` | ✅ |

## Security & privacy requirements (report §3.3, §4.8)

| Req | Implementation | Test | Status |
|---|---|---|---|
| Mask identifiers not needed for correlation | e-mail/IP masking, endpoint ID templating | `Secret_like_values_are_redacted_and_identifiers_masked`, `Endpoints_are_grouped…` | ✅ |
| Retraining / raw context separated from alert viewing | Administrator policy for lifecycle, retrain, audit; Engineer for viewing/review | IT `TC07_…` | ✅ |
| Internal model API authenticated (short-lived token, audience, scope) | `ServiceTokenIssuer`; ML `core/security.py` | ML `test_invalid_service_tokens_are_rejected`, `test_scopes_are_not_interchangeable` | ✅ |
| Never load a model by user-supplied path | label regex + registry lookup + containment + SHA-256 | ML `test_registry.py`, `test_tc03_*` | ✅ |
| Backend constructs constrained search queries | `OpenSearchQueryBuilder` (whitelisted term/range filters) | `Injection_attempts_stay_literal_term_values` | ✅ |
| Audit | `audit_event`: register, activate, deactivate, retrain, review, pipeline run/requeue, authorization denials | unit + IT assertions on audit rows | ✅ |

## Report test cases

TC-01 … TC-10 → see the mapping table in [`testing.md`](testing.md). All ten are automated and passing.
