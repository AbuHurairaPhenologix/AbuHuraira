"""Evaluation plots (report Figures 5.1–5.5), generated from computed results only."""

from __future__ import annotations

from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402

from app.core.feature_schema import FEATURE_NAMES  # noqa: E402

BLUE, ORANGE, GREEN, RED = "#2a6fdb", "#f28e2b", "#3a9b52", "#c94040"
SHORT = {"isolation_forest": "IF", "lof": "LOF", "ocsvm": "OCSVM", "random_forest": "RF"}


def _save(fig: plt.Figure, path: Path) -> None:
    fig.tight_layout()
    fig.savefig(path, dpi=130)
    plt.close(fig)


def model_comparison(results: list[dict], path: Path) -> None:
    labels = [SHORT[r["algorithm"]] for r in results]
    x = np.arange(len(results))
    width = 0.25
    fig, ax = plt.subplots(figsize=(8, 4.5))
    for i, (key, color) in enumerate((("precision", BLUE), ("recall", ORANGE), ("f1", GREEN))):
        ax.bar(x + (i - 1) * width, [r["test"][key] for r in results], width, label=key.capitalize() if key != "f1" else "F1", color=color)
    ax.set_xticks(x, labels)
    ax.set_ylim(0, 1.05)
    ax.set_ylabel("Score")
    ax.set_title("Synthetic Benchmark Model Comparison (test set, validation-selected thresholds)")
    ax.legend()
    ax.grid(axis="y", alpha=0.3)
    _save(fig, path)


def confusion_matrix(result: dict, path: Path) -> None:
    t = result["test"]
    matrix = np.array([[t["tn"], t["fp"]], [t["fn"], t["tp"]]])
    fig, ax = plt.subplots(figsize=(4.6, 4))
    ax.imshow(matrix, cmap="Blues")
    for (i, j), v in np.ndenumerate(matrix):
        ax.text(j, i, str(v), ha="center", va="center", color="white" if v > matrix.max() / 2 else "black", fontsize=12)
    ax.set_xticks([0, 1], ["Normal", "Anomaly"])
    ax.set_yticks([0, 1], ["Normal", "Anomaly"])
    ax.set_xlabel("Predicted")
    ax.set_ylabel("Actual")
    ax.set_title(f"{result['displayName']} - test set")
    _save(fig, path)


def confusion_grid(results: list[dict], path: Path) -> None:
    fig, axes = plt.subplots(1, len(results), figsize=(4 * len(results), 3.8))
    for ax, r in zip(np.atleast_1d(axes), results, strict=True):
        t = r["test"]
        m = np.array([[t["tn"], t["fp"]], [t["fn"], t["tp"]]])
        ax.imshow(m, cmap="Blues")
        for (i, j), v in np.ndenumerate(m):
            ax.text(j, i, str(v), ha="center", va="center", color="white" if v > m.max() / 2 else "black")
        ax.set_xticks([0, 1], ["Normal", "Anomaly"])
        ax.set_yticks([0, 1], ["Normal", "Anomaly"])
        ax.set_title(SHORT[r["algorithm"]])
        ax.set_xlabel("Predicted")
    np.atleast_1d(axes)[0].set_ylabel("Actual")
    _save(fig, path)


def score_distribution(scores: np.ndarray, y: np.ndarray, threshold: float, title: str, path: Path) -> None:
    fig, ax = plt.subplots(figsize=(8, 4.2))
    bins = np.histogram_bin_edges(scores, bins=70)
    ax.hist(scores[y == 0], bins=bins, alpha=0.7, label="Normal", color=BLUE)
    ax.hist(scores[y == 1], bins=bins, alpha=0.7, label="Anomaly", color=ORANGE)
    ax.axvline(threshold, linestyle="--", color="#1f3b73", label="Selected threshold")
    ax.set_xlabel("Anomaly score (higher = more anomalous)")
    ax.set_ylabel("Frequency")
    ax.set_title(title)
    ax.legend()
    _save(fig, path)


def threshold_sensitivity(table: pd.DataFrame, selected_threshold: float, title: str, path: Path) -> None:
    t = table.sort_values("fpr")
    fig, ax = plt.subplots(figsize=(8, 4.2))
    ax.plot(t["fpr"], t["precision"], label="Precision", color=BLUE)
    ax.plot(t["fpr"], t["recall"], label="Recall", color=ORANGE)
    ax.plot(t["fpr"], t["f1"], label="F1", color=GREEN)
    sel = t.iloc[(t["threshold"] - selected_threshold).abs().argsort()[:1]]
    ax.scatter(sel["fpr"], sel["f1"], color=RED, zorder=5, label="Selected (validation)")
    ax.set_xlabel("False-positive rate")
    ax.set_ylabel("Score")
    ax.set_title(title)
    ax.grid(alpha=0.3)
    ax.legend()
    _save(fig, path)


def feature_profile(frame: pd.DataFrame, path: Path) -> None:
    """Standardized mean of each feature for anomalies vs normal (report Figure 5.1)."""
    normal = frame[frame["label"] == 0]
    anomaly = frame[frame["label"] == 1]
    mu = normal[list(FEATURE_NAMES)].mean()
    sd = normal[list(FEATURE_NAMES)].std().replace(0, 1)
    z_anom = ((anomaly[list(FEATURE_NAMES)].mean() - mu) / sd).to_numpy()
    labels = ["Req", "Err", "AvgDur", "P95", "AuthFail", "DepFail", "Retry", "Entropy"]
    x = np.arange(len(labels))
    fig, ax = plt.subplots(figsize=(8, 4.2))
    ax.bar(x - 0.2, np.zeros(len(labels)), 0.4, label="Normal (reference = 0)", color=BLUE)
    ax.bar(x + 0.2, z_anom, 0.4, label="Synthetic anomaly", color=ORANGE)
    ax.axhline(0, color="black", linewidth=0.8)
    ax.set_xticks(x, labels, rotation=20)
    ax.set_ylabel("Standardized mean (z vs normal)")
    ax.set_title("Feature Profile: Normal vs Synthetic Anomalies")
    ax.legend()
    _save(fig, path)


def per_type_recall(results: list[dict], path: Path) -> None:
    types = sorted(results[0]["test"]["perTypeRecall"])
    x = np.arange(len(types))
    width = 0.8 / len(results)
    fig, ax = plt.subplots(figsize=(10, 4.4))
    for i, r in enumerate(results):
        ax.bar(x + (i - (len(results) - 1) / 2) * width, [r["test"]["perTypeRecall"][t] for t in types], width, label=SHORT[r["algorithm"]])
    ax.set_xticks(x, [t.replace("_", "\n") for t in types])
    ax.set_ylim(0, 1.05)
    ax.set_ylabel("Recall (test)")
    ax.set_title("Detection rate by controlled anomaly type")
    ax.legend()
    ax.grid(axis="y", alpha=0.3)
    _save(fig, path)
