# Benchmark evaluation `eval-20261001t124738z-20261001t124752z`

* Training run: `20261001t124738z` - seed 20220215 - config sha256 `ae708426ee5f`
* Test set: 8000 normal + 450 anomaly windows (`benchmark-ops-v1-seed20220215-ae708426ee.csv`)
* Threshold policy: selected on validation split (quantiles of normal validation scores); test set untouched until now; decision rule `score >= threshold`
* Recommended production model (by validation F1): `ocsvm-ops-v1-20261001t124738z`

## Test-set results (validation-selected thresholds)

| Model | Precision | Recall | F1 | FPR | ROC-AUC | PR-AUC | TP | FP | TN | FN |
|---|---|---|---|---|---|---|---|---|---|---|
| Isolation Forest | 0.318 | 0.411 | 0.359 | 0.0495 | 0.825 | 0.312 | 185 | 396 | 7604 | 265 |
| Local Outlier Factor | 0.859 | 0.529 | 0.655 | 0.0049 | 0.827 | 0.642 | 238 | 39 | 7961 | 212 |
| One-Class SVM | 0.830 | 0.573 | 0.678 | 0.0066 | 0.830 | 0.662 | 258 | 53 | 7947 | 192 |
| Random Forest (supervised ref.) | 0.854 | 0.584 | 0.694 | 0.0056 | 0.858 | 0.680 | 263 | 45 | 7955 | 187 |

## Library default decision (`predict()`), for comparison only

| Model | Precision | Recall | F1 | FPR |
|---|---|---|---|---|
| Isolation Forest | 0.232 | 0.636 | 0.340 | 0.1182 |
| Local Outlier Factor | 0.809 | 0.544 | 0.651 | 0.0073 |
| One-Class SVM | 0.441 | 0.671 | 0.532 | 0.0479 |
| Random Forest (supervised ref.) | 0.785 | 0.593 | 0.676 | 0.0091 |

## Recall by controlled anomaly type (test)

| Model | auth_failure_burst | dependency_instability | error_burst | latency_spike | retry_burst | traffic_surge |
|---|---|---|---|---|---|---|
| Isolation Forest | 0.37 | 0.20 | 0.41 | 0.95 | 0.17 | 0.36 |
| Local Outlier Factor | 0.55 | 0.04 | 0.68 | 0.85 | 0.05 | 1.00 |
| One-Class SVM | 0.57 | 0.12 | 0.77 | 0.87 | 0.11 | 1.00 |
| Random Forest (supervised ref.) | 0.61 | 0.01 | 0.95 | 0.92 | 0.01 | 1.00 |

Values are computed from the generated benchmark only; they are not evidence of production performance
(report §1.8, §5.10). See `plots/` for model comparison, confusion matrices, score distributions and
threshold-sensitivity curves.
