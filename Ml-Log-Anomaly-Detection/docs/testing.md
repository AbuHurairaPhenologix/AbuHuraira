# Testing

| Suite | Location | Runner | What it uses |
|---|---|---|---|
| .NET unit | `src/backend/tests/AnomalyDetection.UnitTests` | `dotnet test` | pure domain/application code, EF Core InMemory, fake ML port |
| .NET integration | `tests/integration/AnomalyDetection.IntegrationTests` | `dotnet test` | real API in-process (WebApplicationFactory), **real PostgreSQL 17 + OpenSearch 2.19 via Testcontainers**, ML HTTP contract stub at the HttpClient boundary |
| Python | `src/ml-service/tests` | `pytest` | real models trained on a reduced benchmark in a temp registry; FastAPI TestClient |
| Frontend | `src/frontend/src/app/app.spec.ts` | `ng test` (Vitest) | pipes, auth interceptor, API client contract, chart |
| End-to-end | `tests/e2e/test_stack_e2e.py` | `pytest` | the running Docker stack — real backend, PostgreSQL, OpenSearch and **real Python model** |

## Commands

```powershell
dotnet test src/backend/AnomalyDetection.sln                 # unit + integration (Docker required for integration)
cd src/ml-service; .venv\Scripts\python -m pytest            # Python
cd src/frontend; npx ng test --watch=false                   # Angular
docker compose up -d --build
src\ml-service\.venv\Scripts\python -m pytest tests/e2e -v   # E2E (add $env:E2E_ALLOW_OUTAGE=1 for the live ML-outage test)
```

## Report test cases (Appendix "Test Cases")

| ID | Scenario | Expected | Automated evidence |
|---|---|---|---|
| TC-01 | Valid normal feature window | Score returned; no alert below threshold; persisted score | Python `test_api.py::test_tc01_normal_window_returns_score_below_threshold`; integration `PipelineEndToEndTests.Full_pipeline_…` (normal window scored, persisted, no anomaly); E2E `test_full_pipeline_with_real_ml_service` |
| TC-02 | Feature schema mismatch | 4xx + log | Python `test_tc02_invalid_contract_is_rejected[*]` (schema, missing/extra feature, string/bool/float types, ranges, extra field), `test_tc02_malformed_json_is_rejected`; structured rejection log in `app/main.py` |
| TC-03 | Missing model version | Rejected; no arbitrary file | Python `test_tc03_missing_model_version_is_rejected`, `test_tc03_paths_are_never_accepted_as_model_versions[*]`, `test_tc03_unknown_model_version_is_404`, `test_registry.py` (path traversal, containment, tampered hash); .NET `Registration_accepts_only_registry_labels_never_paths`, `Model_registration_rejects_paths_and_unknown_versions` |
| TC-04 | ML service timeout | App continues; scoring deferred/retried | .NET unit `TC04_ml_unavailable_defers_scoring_with_backoff…`, `Deferred_windows_are_scored_after_recovery`; integration `ResilienceTests.TC04_ml_timeout_does_not_affect_requests…` (real Polly timeout); E2E `test_tc04_live_ml_outage…` (stops the real ml-service container) |
| TC-05 | High-latency anomaly | Score increases, may exceed threshold | Python `test_tc05_high_latency_increases_score_for_registered_models`, `test_tc05_high_latency_window_scores_higher_and_is_flagged`; E2E latency-spike window flagged |
| TC-06 | Authentication failure burst | auth_failure_rate increases | .NET `TC06_authentication_failure_burst_increases_auth_failure_rate`; `Http_401_without_explicit_outcome_counts_as_auth_failure…` |
| TC-07 | Unauthorized model activation | Denied + audit event | integration `SecurityTests.TC07_unauthorized_model_activation_is_denied_and_audited` (403/401 + `authorization.denied` rows); E2E `test_tc07_engineer_cannot_activate_models` |
| TC-08 | Secret-like input field | Excluded/rejected before training | .NET `EventSanitizerTests` (9 key variants, value redaction, canonical-field quarantine), integration `TC08_secrets_never_reach_storage_or_features`; Python `test_validation.py` (sensitive columns/values reject dataset), `test_retraining_rejects_secret_columns` |
| TC-09 | Duplicate event | Not counted twice | .NET `TC09_duplicate_event_ids_are_counted_once`, `TC09_duplicate_events_are_not_stored_or_counted_twice`, `Event_id_uniqueness_is_enforced_by_the_database`; integration E2E duplicate → `request_count` 151; Python feature parity test |
| TC-10 | Late-arriving event | Policy applied consistently | .NET `TC10_late_arriving_event_is_stored_but_does_not_change_the_finalized_window`, `Open_windows_are_not_finalized_before_allowed_lateness`; integration `TC10_late_event_is_accepted_but_does_not_change_the_scored_window` |

## Additional coverage

p95, Shannon entropy, request count, error rate, auth rate, dependency failures, retries (`FeatureCalculatorTests`,
`test_features.py`); score direction (`test_higher_score_means_more_anomalous`); threshold selection
(`test_candidates_are_quantiles_of_normal_scores_only`, `test_select_threshold_objectives`); model loading and schema
compatibility (`test_registry.py`, `Incompatible_schema_is_rejected`); activation (`ModelLifecycleServiceTests`,
`Benchmark_reference_model_cannot_be_activated…`); review lifecycle (`ReviewLifecycleTests`, E2E review);
repositories / DB constraints (`DatabaseTests`); API contracts (`Swagger_document_describes_the_versioned_api`,
`test_api.py`); service timeout behaviour (integration TC-04); sanitization; correlation IDs (E2E
`test_demo_workload_generates_live_telemetry` round-trips `X-Correlation-ID` to OpenSearch); OpenSearch query
construction/injection (`OpenSearchQueryBuilderTests`, `Search_accepts_only_constrained_filters`); reproducibility
(`test_generation_is_deterministic_for_a_seed`, metadata completeness, evaluation `environment.json`).

## End-to-end flow covered automatically

1 send events → 2 normalize → 3 store → 4 aggregate a logical 5-minute window → 5 compute 8 features → 6 request ML
score → 7 persist scoring result → 8 create anomaly → 9 retrieve via API → 10 submit review → 11 verify persistence.
Implemented twice: in-process with real PostgreSQL/OpenSearch (`PipelineEndToEndTests`) and against the Docker stack
with the real Python model (`tests/e2e`). Synthetic historical timestamps are used, so no test waits five minutes.

Latest verified counts: see `final-verification.md`.
