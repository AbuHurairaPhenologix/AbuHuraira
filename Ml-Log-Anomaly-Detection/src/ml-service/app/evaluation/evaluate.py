"""CLI: evaluate a training run on the held-out TEST split (report §5.4–§5.9).

Thresholds were fixed on validation data during training; this step only applies them to the untouched test set.

    python -m app.evaluation.evaluate                 # latest benchmark training run
    python -m app.evaluation.evaluate --run 20261001t121944z

Outputs go to artifacts/evaluations/<evaluation-id>/ (metrics.json, metrics.csv, report.md, plots, environment).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import logging
import shutil
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import pandas as pd

from app.core.config import get_settings, load_benchmark_config
from app.core.versions import library_versions
from app.datasets.io import dataset_path, load_dataset
from app.datasets.validation import feature_matrix
from app.evaluation import metrics as m
from app.evaluation import plots
from app.registry.registry import ModelRegistry
from app.training import thresholds as th

logger = logging.getLogger("ml.evaluation")
ORDER = ("isolation_forest", "lof", "ocsvm", "random_forest")


def latest_run(registry: ModelRegistry) -> str:
    runs = sorted({m["trainingRunId"] for m in registry.list() if m.get("trainingSource") == "benchmark"})
    if not runs:
        raise SystemExit("No benchmark training runs in the registry. Run: python -m app.training.train --algorithm all")
    return runs[-1]


def evaluate_run(run_id: str, out_root: Path | None = None) -> Path:
    settings = get_settings()
    registry = ModelRegistry(settings.models_dir)
    config = load_benchmark_config()
    models = sorted(
        (e for e in registry.list() if e.get("trainingRunId") == run_id and e.get("trainingSource") == "benchmark"),
        key=lambda e: ORDER.index(e["algorithm"]),
    )
    if not models:
        raise SystemExit(f"No benchmark models found for training run {run_id}")

    path = dataset_path(config)
    dataset_sha = hashlib.sha256(path.read_bytes()).hexdigest()
    for e in models:
        if e.get("datasetSha256") != dataset_sha:
            raise SystemExit(f"Dataset changed since {e['modelVersion']} was trained; refusing to evaluate (leakage guard).")

    frame = load_dataset(path)
    test = frame[frame["split"] == "test"].reset_index(drop=True)
    X_test = feature_matrix(test)
    y_test = test["label"].to_numpy()

    evaluation_id = f"eval-{run_id}-{datetime.now(timezone.utc).strftime('%Y%m%dt%H%M%Sz')}"
    out = (out_root or settings.evaluations_dir) / evaluation_id
    (out / "plots").mkdir(parents=True, exist_ok=True)
    tcfg = config.threshold
    quantiles = th.candidate_quantiles(**tcfg["candidate_quantiles"])

    results = []
    for entry in models:
        loaded = registry.load(entry["modelVersion"])
        scores = loaded.adapter.score(X_test)
        threshold = loaded.threshold
        flagged = scores >= threshold
        default_flags = loaded.adapter.default_predict(X_test)
        test_metrics = {
            **m.binary_metrics(y_test, flagged),
            **m.ranking_metrics(y_test, scores),
            "perTypeRecall": m.per_type_recall(test, flagged),
            "alertsPer1000Windows": float(flagged.mean() * 1000),
        }
        result = {
            "algorithm": entry["algorithm"],
            "displayName": entry["displayName"],
            "modelVersion": entry["modelVersion"],
            "productionEligible": entry["productionEligible"],
            "threshold": threshold,
            "thresholdQuantile": entry.get("thresholdQuantile"),
            "thresholdObjective": entry["thresholdObjective"],
            "validation": {k: entry["validationMetrics"][k] for k in ("precision", "recall", "f1", "fpr", "rocAuc", "prAuc")},
            "test": test_metrics,
            "testLibraryDefaultDecision": m.binary_metrics(y_test, default_flags),
        }
        results.append(result)

        # Plots and sensitivity tables per model.
        short = plots.SHORT[entry["algorithm"]].lower()
        plots.confusion_matrix(result, out / "plots" / f"confusion_{short}.png")
        plots.score_distribution(scores, y_test, threshold, f"{entry['displayName']} score distribution (test)", out / "plots" / f"score_distribution_{short}.png")
        validation_table = pd.DataFrame(
            json.loads((registry.models_dir / entry["modelVersion"] / "validation_sensitivity.json").read_text(encoding="utf-8"))
        )
        validation_table.to_csv(out / f"threshold_sensitivity_validation_{short}.csv", index=False)
        plots.threshold_sensitivity(validation_table, threshold, f"Threshold sensitivity - {entry['displayName']} (validation)", out / "plots" / f"threshold_sensitivity_{short}.png")
        # Post-hoc test-set sensitivity is reported for analysis only; it was NOT used to choose the threshold.
        test_table = pd.DataFrame([p.to_dict() for p in th.sensitivity_table(y_test, scores, quantiles, tcfg["windows_per_service_day"])])
        test_table.to_csv(out / f"threshold_sensitivity_test_posthoc_{short}.csv", index=False)
        logger.info("%s test: P=%.3f R=%.3f F1=%.3f FPR=%.4f", entry["algorithm"], test_metrics["precision"], test_metrics["recall"], test_metrics["f1"], test_metrics["fpr"])

    plots.model_comparison(results, out / "plots" / "model_comparison.png")
    plots.confusion_grid(results, out / "plots" / "confusion_matrices.png")
    plots.per_type_recall(results, out / "plots" / "per_type_recall.png")
    plots.feature_profile(test, out / "plots" / "feature_profile.png")

    table = pd.DataFrame(
        [
            {
                "model": r["displayName"],
                "model_version": r["modelVersion"],
                "threshold": r["threshold"],
                "precision": r["test"]["precision"],
                "recall": r["test"]["recall"],
                "f1": r["test"]["f1"],
                "fpr": r["test"]["fpr"],
                "roc_auc": r["test"]["rocAuc"],
                "pr_auc": r["test"]["prAuc"],
                "tp": r["test"]["tp"],
                "fp": r["test"]["fp"],
                "tn": r["test"]["tn"],
                "fn": r["test"]["fn"],
                "default_precision": r["testLibraryDefaultDecision"]["precision"],
                "default_recall": r["testLibraryDefaultDecision"]["recall"],
                "default_f1": r["testLibraryDefaultDecision"]["f1"],
                "default_fpr": r["testLibraryDefaultDecision"]["fpr"],
            }
            for r in results
        ]
    )
    table.to_csv(out / "metrics.csv", index=False, float_format="%.4f")
    pd.DataFrame([{"model": r["displayName"], **r["test"]["perTypeRecall"]} for r in results]).to_csv(out / "per_type_recall.csv", index=False, float_format="%.4f")

    summary = {
        "evaluationId": evaluation_id,
        "trainingRunId": run_id,
        "evaluatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "dataset": {"path": path.name, "sha256": dataset_sha, "testNormal": int((y_test == 0).sum()), "testAnomaly": int((y_test == 1).sum())},
        "configSha256": config.sha256,
        "randomSeed": config.seed,
        "thresholdPolicy": "selected on validation split (quantiles of normal validation scores); test set untouched until now",
        "decisionRule": "score >= threshold",
        "recommendedByValidation": next((e["modelVersion"] for e in models if e.get("recommended")), None),
        "results": results,
    }
    (out / "metrics.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    (out / "environment.json").write_text(json.dumps(library_versions(), indent=2), encoding="utf-8")
    shutil.copyfile(config.path, out / "benchmark.yaml")
    (out / "report.md").write_text(_report(summary), encoding="utf-8")
    (out.parent / "latest.json").write_text(json.dumps({"evaluationId": evaluation_id, "trainingRunId": run_id}, indent=2), encoding="utf-8")
    return out


def _report(s: dict) -> str:
    lines = [
        f"# Benchmark evaluation `{s['evaluationId']}`",
        "",
        f"* Training run: `{s['trainingRunId']}` - seed {s['randomSeed']} - config sha256 `{s['configSha256'][:12]}`",
        f"* Test set: {s['dataset']['testNormal']} normal + {s['dataset']['testAnomaly']} anomaly windows (`{s['dataset']['path']}`)",
        f"* Threshold policy: {s['thresholdPolicy']}; decision rule `{s['decisionRule']}`",
        f"* Recommended production model (by validation F1): `{s['recommendedByValidation']}`",
        "",
        "## Test-set results (validation-selected thresholds)",
        "",
        "| Model | Precision | Recall | F1 | FPR | ROC-AUC | PR-AUC | TP | FP | TN | FN |",
        "|---|---|---|---|---|---|---|---|---|---|---|",
    ]
    for r in s["results"]:
        t = r["test"]
        lines.append(
            f"| {r['displayName']} | {t['precision']:.3f} | {t['recall']:.3f} | {t['f1']:.3f} | {t['fpr']:.4f} | {t['rocAuc']:.3f} | {t['prAuc']:.3f} | {t['tp']} | {t['fp']} | {t['tn']} | {t['fn']} |"
        )
    lines += [
        "",
        "## Library default decision (`predict()`), for comparison only",
        "",
        "| Model | Precision | Recall | F1 | FPR |",
        "|---|---|---|---|---|",
    ]
    for r in s["results"]:
        d = r["testLibraryDefaultDecision"]
        lines.append(f"| {r['displayName']} | {d['precision']:.3f} | {d['recall']:.3f} | {d['f1']:.3f} | {d['fpr']:.4f} |")
    types = sorted(s["results"][0]["test"]["perTypeRecall"])
    lines += ["", "## Recall by controlled anomaly type (test)", "", "| Model | " + " | ".join(types) + " |", "|---|" + "---|" * len(types)]
    for r in s["results"]:
        lines.append(f"| {r['displayName']} | " + " | ".join(f"{r['test']['perTypeRecall'][t]:.2f}" for t in types) + " |")
    lines += [
        "",
        "Values are computed from the generated benchmark only; they are not evidence of production performance",
        "(report §1.8, §5.10). See `plots/` for model comparison, confusion matrices, score distributions and",
        "threshold-sensitivity curves.",
        "",
    ]
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", default="latest", help="Training run id or 'latest'")
    args = parser.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    registry = ModelRegistry(get_settings().models_dir)
    run_id = latest_run(registry) if args.run == "latest" else args.run
    out = evaluate_run(run_id)
    print((out / "report.md").read_text(encoding="utf-8"))
    print(f"Evaluation written to {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
