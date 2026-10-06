"""Evaluation run: computed metrics, plots and reproducibility metadata."""

import json

from app.evaluation.evaluate import evaluate_run


def test_evaluation_produces_metrics_and_plots(trained, environment, config):
    out = evaluate_run(trained.run_id, environment["evaluations"])
    summary = json.loads((out / "metrics.json").read_text())
    test_anomalies = config.splits["test"]["anomaly"]
    test_normals = config.splits["test"]["normal"]
    assert summary["dataset"]["testAnomaly"] == test_anomalies
    assert {r["algorithm"] for r in summary["results"]} == {"isolation_forest", "lof", "ocsvm", "random_forest"}
    for r in summary["results"]:
        t = r["test"]
        assert t["tp"] + t["fn"] == test_anomalies
        assert t["tn"] + t["fp"] == test_normals
        assert 0 <= t["precision"] <= 1 and 0 <= t["recall"] <= 1 and 0 <= t["fpr"] <= 1
        assert 0.5 <= t["rocAuc"] <= 1.0
        assert set(t["perTypeRecall"]) == {"latency_spike", "auth_failure_burst", "dependency_instability", "retry_burst", "traffic_surge", "error_burst"}
    for name in ("model_comparison.png", "confusion_matrices.png", "feature_profile.png", "per_type_recall.png",
                 "score_distribution_if.png", "threshold_sensitivity_ocsvm.png", "confusion_lof.png"):
        assert (out / "plots" / name).stat().st_size > 1000, name
    assert (out / "metrics.csv").exists() and (out / "report.md").exists()
    env = json.loads((out / "environment.json").read_text())
    assert "scikit-learn" in env and "python" in env
    assert (out / "benchmark.yaml").exists()
