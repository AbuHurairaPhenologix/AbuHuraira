# ML Pipeline

## Feature schema `ops-v1`

Explicit order (model input columns) — `config/feature-schema.ops-v1.json`, C# `FeatureSchema`, Python `FEATURE_NAMES`.
Computed per **service + environment + window** (default 5 min; 1 or 15 configurable via `Pipeline:WindowSizeMinutes`).

| # | Feature | Formula |
|---|---|---|
| 1 | `request_count` | number of `http_request` events |
| 2 | `error_rate` | requests with `error_flag` ÷ `request_count` (`error_flag` defaults to status ≥ 500) |
| 3 | `avg_duration_ms` | mean request duration |
| 4 | `p95_duration_ms` | 95th percentile, linear interpolation (`(n−1)·0.95`, same as NumPy) |
| 5 | `auth_failure_rate` | requests with `authentication_result=failure` (401/403 derive failure) ÷ `request_count` |
| 6 | `dependency_failure_count` | `dependency_call` events with `error_flag` |
| 7 | `retry_count` | Σ `retry_count` over all events |
| 8 | `endpoint_entropy` | Shannon entropy in bits, H = −Σ pᵢ log₂ pᵢ over request endpoint groups |

Deterministic (`FeatureCalculator` is pure; summation order fixed); duplicate event IDs count once. A Python mirror
(`app/datasets/features.py`) is parity-tested against the same example in both test suites.

**Late-arriving events (TC-10):** a window is finalized at `window_end + 120 s` (`Pipeline:AllowedLatenessSeconds`).
Events arriving afterwards are stored and indexed, flagged `is_late`, counted in `feature_window.late_event_count`, and
**not** merged into the finalized vector — scores already produced are never silently changed.

## Synthetic benchmark (`config/benchmark.yaml`, seed 20220215)

32,000 normal + 1,800 anomaly windows (300 per class), parameters from report §5.2: Poisson(140) requests; log-normal
average latency (mean 250 ms); p95 = avg × N(2.6, 0.25) constrained > avg; Beta error rate (mean 0.02) and auth-failure
rate (mean 0.015); zero-inflated Poisson dependency failures (mean 0.15) and retries (mean 0.10); bounded Gaussian
entropy (2.3). Anomalies: latency ×2.5–4.0; auth-failure ×6–10; dependency rate ×5 (+ latency ×1.0–1.3); retry rate ×5;
traffic ×1.8–2.2 (entropy ×0.8–0.95); error-rate mean 0.10–0.15. Classes overlap by design.

Chronological split (validation/test later in time than training):

| Split | Normal | Anomaly | Used for |
|---|---|---|---|
| train | 16,000 | 900 | one-class models fit on normal rows only; anomalies only for the Random Forest reference |
| validation | 8,000 | 450 | threshold selection, recommendation |
| test | 8,000 | 450 | final metrics only (matches the report's test confusion-matrix totals) |

## Models (`app/models/adapters.py`)

Every adapter returns **higher = more anomalous**.

| Model | Pipeline | Score | Params |
|---|---|---|---|
| Isolation Forest | raw features (report §5.3) | `−decision_function` | 300 trees, max_samples 256 |
| LOF (novelty) | StandardScaler → LOF | `−decision_function` | n_neighbors 35 |
| One-Class SVM | StandardScaler → OCSVM | `−decision_function` (report §4.6) | rbf, nu 0.04, gamma scale, 6,000-window subset |
| Random Forest (reference only, `production_eligible=false`) | raw | `predict_proba[:,1]` | 300 trees, balanced |

Scaling is inside the persisted sklearn `Pipeline`, so training and inference transformations are identical.

## Threshold selection (`app/training/thresholds.py`)

Candidates = 240 quantiles (0.80…0.9995) of the **normal validation-score** distribution. For each: precision, recall,
F1, FPR, TP/FP/TN/FN, alerts per 1,000 windows and per service-day (288 windows). Objective `max_f1` (ties → lower FPR);
`max_recall_at_fpr` available. Decision rule everywhere: `score >= threshold`. The selected threshold is stored in
model metadata, `model_version.validation_threshold`, and every `scoring_record`/`anomaly_record`. The full
validation sensitivity table is saved as `artifacts/models/<version>/validation_sensitivity.json`.

## Registry and artifacts (`artifacts/models`)

`registry.json` + `<version>/model.joblib` + `<version>/metadata.json` + `validation_sensitivity.json`. Metadata:
modelId, modelVersion, algorithm, featureSchemaVersion, featureNames, training period, trainingWindows, randomSeed,
libraryVersions, parameters, validationThreshold, thresholdObjective/quantile, productionEligible, validationMetrics,
baseline (per-feature mean/std for reason summaries), configSha256, datasetSha256, trainingRunId, createdAtUtc,
artifactPath (registry-relative), artifactSha256, recommended. Loading verifies: label pattern, registry membership,
path containment inside the store, SHA-256, scikit-learn version. Versions are immutable.

## Commands (from `src/ml-service`, venv active)

```bash
python -m app.training.generate_data [--force]          # dataset -> data/generated/benchmark-ops-v1-seed…csv (+ .meta.json)
python -m app.training.train --algorithm all            # or isolation_forest | lof | ocsvm | random_forest
python -m app.evaluation.evaluate [--run <runId>|latest]  # -> artifacts/evaluations/eval-<run>-<ts>/
```

or `scripts/benchmark/run_benchmark.ps1` / `.sh`. Explicit retraining via the API: `POST /api/v1/models/retrain`
(`source=benchmark`, or `source=feature-windows` with a training period — trains on stored windows excluding
unreviewed/confirmed anomalies; unlabelled threshold = 99th percentile of later held-out windows).

## Evaluation output

`metrics.json`, `metrics.csv` (Table 5.1 analogue + library-default comparison), `per_type_recall.csv`,
`threshold_sensitivity_validation_*.csv`, `threshold_sensitivity_test_posthoc_*.csv` (analysis only), `report.md`,
`environment.json`, `benchmark.yaml` snapshot, and `plots/`: `model_comparison.png`, `confusion_matrices.png`,
`confusion_<model>.png`, `score_distribution_<model>.png`, `threshold_sensitivity_<model>.png`, `feature_profile.png`,
`per_type_recall.png`.

## Results of the committed run (`eval-20261001t124738z-20261001t124752z`)

Computed, not copied from the report (test set: 8,000 normal + 450 anomaly windows):

| Model | Precision | Recall | F1 | FPR | ROC-AUC | PR-AUC |
|---|---|---|---|---|---|---|
| Isolation Forest | 0.318 | 0.411 | 0.359 | 0.0495 | 0.825 | 0.312 |
| Local Outlier Factor | 0.859 | 0.529 | 0.655 | 0.0049 | 0.827 | 0.642 |
| One-Class SVM | 0.830 | 0.573 | 0.678 | 0.0066 | 0.830 | 0.662 |
| Random Forest (supervised ref.) | 0.854 | 0.584 | 0.694 | 0.0056 | 0.858 | 0.680 |

Library default `predict()` for comparison: One-Class SVM F1 0.532 at FPR 0.048 vs 0.678 at FPR 0.0066 after
validation tuning; Isolation Forest default FPR 0.118 — illustrating report RQ3 (threshold governance matters).

**Error analysis.** Recall by type (OCSVM): traffic surge 1.00, latency spike 0.87, error burst 0.77, auth-failure
burst 0.57, dependency instability 0.12, retry burst 0.11. Shifting a zero-inflated count with mean 0.10–0.15 by ×5
leaves most anomalous windows indistinguishable from normal ones (many contain zero retries/failures) — exactly the
report's §5.9 observation that "a mild dependency slowdown may remain inside the learned normal boundary". Even the
supervised reference cannot detect them (recall ≤ 0.01), so this is a property of the data, not a model defect.

**Comparison with report Table 5.1.** The qualitative ranking matches (LOF/OCSVM ≫ Isolation Forest after tuning,
supervised RF slightly higher ranking quality), but absolute recall is lower than the report's (0.80–0.92) because the
count-based anomaly classes above are faithful to the stated parameters and largely undetectable. We did not tune
the generator to reproduce published numbers.
