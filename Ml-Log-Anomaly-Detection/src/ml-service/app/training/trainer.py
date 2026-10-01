"""Training protocol (report §5.3).

* Unsupervised models are fitted **only on normal training windows**.
* Thresholds are selected on the validation split; the test split is not touched here.
* Every artifact is stored with algorithm, parameters, library versions, feature schema, training period, seed,
  validation threshold and the config hash, and is registered as a new immutable version.
"""

from __future__ import annotations

import json
import logging
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any

import numpy as np
import pandas as pd
from sklearn.metrics import average_precision_score, roc_auc_score

from app.core.config import BenchmarkConfig
from app.core.feature_schema import FEATURE_NAMES, FEATURE_SCHEMA_VERSION
from app.core.versions import library_versions
from app.datasets.validation import feature_matrix
from app.models.adapters import DISPLAY_NAMES, SHORT_NAMES, create_adapter
from app.registry.registry import ModelRegistry
from app.training import thresholds as th

logger = logging.getLogger("ml.training")

UNSUPERVISED = ("isolation_forest", "lof", "ocsvm")
ALL_ALGORITHMS = (*UNSUPERVISED, "random_forest")


@dataclass
class TrainingRunResult:
    run_id: str
    versions: list[str] = field(default_factory=list)
    summaries: list[dict[str, Any]] = field(default_factory=list)
    recommended: str | None = None


def new_run_id() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%dt%H%M%Sz")


def _iso(value: Any) -> str | None:
    if value is None or (isinstance(value, float) and np.isnan(value)):
        return None
    return pd.Timestamp(value).tz_convert("UTC").strftime("%Y-%m-%dT%H:%M:%SZ")


def baseline_statistics(X: np.ndarray) -> dict[str, dict[str, float]]:
    """Per-feature mean/std of the normal training data — used for non-causal reason summaries."""
    means = X.mean(axis=0)
    stds = X.std(axis=0)
    return {name: {"mean": float(means[i]), "std": float(stds[i])} for i, name in enumerate(FEATURE_NAMES)}


def _subset(X: np.ndarray, size: int | None, seed: int) -> np.ndarray:
    if not size or size >= len(X):
        return X
    rng = np.random.default_rng(seed)
    return X[np.sort(rng.choice(len(X), size=size, replace=False))]


def _ranking_metrics(y: np.ndarray, scores: np.ndarray) -> dict[str, float]:
    return {"rocAuc": float(roc_auc_score(y, scores)), "prAuc": float(average_precision_score(y, scores))}


def train_benchmark(
    config: BenchmarkConfig,
    frame: pd.DataFrame,
    registry: ModelRegistry,
    algorithms: list[str],
    dataset_sha256: str,
    run_id: str | None = None,
    requested_by: str = "cli",
) -> TrainingRunResult:
    run_id = run_id or new_run_id()
    result = TrainingRunResult(run_id=run_id)
    train = frame[frame["split"] == "train"]
    validation = frame[frame["split"] == "validation"]
    train_normal = train[train["label"] == 0]

    X_train_normal = feature_matrix(train_normal.drop(columns=["label"]))
    X_train_all = feature_matrix(train.drop(columns=["label"]))
    y_train_all = train["label"].to_numpy()
    X_val = feature_matrix(validation.drop(columns=["label"]))
    y_val = validation["label"].to_numpy()
    baseline = baseline_statistics(X_train_normal)
    tcfg = config.threshold
    quantiles = th.candidate_quantiles(**tcfg["candidate_quantiles"])

    for algorithm in algorithms:
        mcfg = config.models[algorithm]
        adapter = create_adapter(algorithm, mcfg["params"], config.seed, mcfg.get("scaling", "none"))
        if adapter.supervised:
            # The Random Forest reference is the only model that sees labelled anomalies (training split only).
            adapter.fit(X_train_all, y_train_all)
            fitted_on = train
        else:
            X_fit = _subset(X_train_normal, mcfg.get("training_subset"), config.seed)
            adapter.fit(X_fit)
            fitted_on = train_normal
        logger.info("Fitted %s", algorithm)

        val_scores = adapter.score(X_val)
        table = th.sensitivity_table(y_val, val_scores, quantiles, tcfg["windows_per_service_day"])
        selected = th.select_threshold(table, tcfg["objective"], tcfg["target_fpr"])
        default_flags = adapter.default_predict(X_val)
        tp, fp, tn, fn = th.confusion(y_val, default_flags)

        version = f"{SHORT_NAMES[algorithm]}-{FEATURE_SCHEMA_VERSION}-{run_id}"
        metadata = {
            "modelId": str(uuid.uuid4()),
            "modelVersion": version,
            "algorithm": algorithm,
            "displayName": DISPLAY_NAMES[algorithm],
            "featureSchemaVersion": FEATURE_SCHEMA_VERSION,
            "featureNames": list(FEATURE_NAMES),
            "trainingPeriodStartUtc": _iso(fitted_on["window_start_utc"].min()),
            "trainingPeriodEndUtc": _iso(fitted_on["window_end_utc"].max()),
            "trainingWindows": int(len(X_fit) if not adapter.supervised else len(X_train_all)),
            "randomSeed": config.seed,
            "libraryVersions": library_versions(),
            "parameters": {**mcfg["params"], "scaling": mcfg.get("scaling", "none"), "trainingSubset": mcfg.get("training_subset")},
            "validationThreshold": selected.threshold,
            "thresholdObjective": tcfg["objective"],
            "thresholdQuantile": selected.quantile,
            "productionEligible": bool(mcfg.get("production_eligible", False)),
            "validationMetrics": {
                **selected.to_dict(),
                **_ranking_metrics(y_val, val_scores),
                "libraryDefaultDecision": {"tp": tp, "fp": fp, "tn": tn, "fn": fn},
            },
            "baseline": baseline,
            "createdAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "trainingRunId": run_id,
            "trainingSource": "benchmark",
            "configSha256": config.sha256,
            "datasetSha256": dataset_sha256,
            "requestedBy": requested_by,
            "recommended": False,
        }
        entry = registry.register(adapter, metadata)
        (registry.models_dir / version / "validation_sensitivity.json").write_text(
            json.dumps([p.to_dict() for p in table], indent=1), encoding="utf-8"
        )
        result.versions.append(version)
        result.summaries.append(
            {
                "modelVersion": version,
                "algorithm": algorithm,
                "threshold": selected.threshold,
                "validationF1": selected.f1,
                "validationPrecision": selected.precision,
                "validationRecall": selected.recall,
                "validationFpr": selected.fpr,
                "productionEligible": entry["productionEligible"],
            }
        )
        logger.info("Registered %s threshold=%.6f validation F1=%.3f", version, selected.threshold, selected.f1)

    # Recommendation uses VALIDATION F1 only (never the test set), among production-eligible models.
    eligible = [s for s in result.summaries if s["productionEligible"]]
    if eligible:
        best = max(eligible, key=lambda s: s["validationF1"])
        registry.set_recommended(best["modelVersion"])
        result.recommended = best["modelVersion"]
    return result


def train_from_rows(
    config: BenchmarkConfig,
    rows: pd.DataFrame,
    registry: ModelRegistry,
    algorithm: str,
    period_start: str | None,
    period_end: str | None,
    requested_by: str,
) -> TrainingRunResult:
    """Retrain on stored (unlabelled, assumed-normal) feature windows supplied by the backend.

    Rows are in chronological order. The first 80 % fit the model; the later 20 % provide the held-out normal-score
    distribution for an unlabelled threshold at the configured target FPR.
    """
    if algorithm not in UNSUPERVISED:
        raise ValueError("Feature-window retraining supports isolation_forest, lof and ocsvm only")
    X = feature_matrix(rows)
    if len(X) < 200:
        raise ValueError("At least 200 windows are required")
    cut = int(len(X) * 0.8)
    X_fit, X_hold = X[:cut], X[cut:]
    mcfg = config.models[algorithm]
    adapter = create_adapter(algorithm, mcfg["params"], config.seed, mcfg.get("scaling", "none"))
    adapter.fit(_subset(X_fit, mcfg.get("training_subset"), config.seed))
    target_fpr = float(config.threshold["target_fpr"])
    threshold = th.unlabelled_threshold(adapter.score(X_hold), target_fpr)

    run_id = new_run_id()
    version = f"{SHORT_NAMES[algorithm]}-{FEATURE_SCHEMA_VERSION}-fw-{run_id}"
    metadata = {
        "modelId": str(uuid.uuid4()),
        "modelVersion": version,
        "algorithm": algorithm,
        "displayName": DISPLAY_NAMES[algorithm],
        "featureSchemaVersion": FEATURE_SCHEMA_VERSION,
        "featureNames": list(FEATURE_NAMES),
        "trainingPeriodStartUtc": period_start,
        "trainingPeriodEndUtc": period_end,
        "trainingWindows": int(len(X_fit)),
        "randomSeed": config.seed,
        "libraryVersions": library_versions(),
        "parameters": {**mcfg["params"], "scaling": mcfg.get("scaling", "none"), "trainingSubset": mcfg.get("training_subset")},
        "validationThreshold": threshold,
        "thresholdObjective": f"normal_quantile_target_fpr_{target_fpr}",
        "thresholdQuantile": 1.0 - target_fpr,
        "productionEligible": True,
        "validationMetrics": {"heldOutWindows": int(len(X_hold)), "targetFpr": target_fpr, "labelled": False},
        "baseline": baseline_statistics(X_fit),
        "createdAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "trainingRunId": run_id,
        "trainingSource": "feature-windows",
        "configSha256": config.sha256,
        "datasetSha256": None,
        "requestedBy": requested_by,
        "recommended": False,
    }
    registry.register(adapter, metadata)
    return TrainingRunResult(run_id=run_id, versions=[version], summaries=[{"modelVersion": version, "threshold": threshold}])
