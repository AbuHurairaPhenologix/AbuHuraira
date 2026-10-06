"""Explicit, validation-based threshold selection (report §3.10, §5.8).

Candidate thresholds are quantiles of the *normal* validation-score distribution. For each candidate we compute
precision, recall, F1, false-positive rate, the confusion matrix and the review burden; the configured objective then
selects one operating point. The final test set is never used here. Decision rule everywhere: ``score >= threshold``.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass

import numpy as np


@dataclass(frozen=True)
class OperatingPoint:
    quantile: float
    threshold: float
    precision: float
    recall: float
    f1: float
    fpr: float
    tp: int
    fp: int
    tn: int
    fn: int
    alerts_per_1000_windows: float
    alerts_per_service_day: float

    def to_dict(self) -> dict:
        return asdict(self)


def confusion(y_true: np.ndarray, flagged: np.ndarray) -> tuple[int, int, int, int]:
    y = np.asarray(y_true).astype(bool)
    f = np.asarray(flagged).astype(bool)
    tp = int(np.sum(f & y))
    fp = int(np.sum(f & ~y))
    tn = int(np.sum(~f & ~y))
    fn = int(np.sum(~f & y))
    return tp, fp, tn, fn


def operating_point(y_true: np.ndarray, scores: np.ndarray, threshold: float, quantile: float, windows_per_day: int) -> OperatingPoint:
    flagged = scores >= threshold
    tp, fp, tn, fn = confusion(y_true, flagged)
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / (tp + fn) if tp + fn else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    fpr = fp / (fp + tn) if fp + tn else 0.0
    alert_rate = float(np.mean(flagged)) if len(flagged) else 0.0
    return OperatingPoint(
        quantile=float(quantile),
        threshold=float(threshold),
        precision=precision,
        recall=recall,
        f1=f1,
        fpr=fpr,
        tp=tp,
        fp=fp,
        tn=tn,
        fn=fn,
        alerts_per_1000_windows=alert_rate * 1000,
        alerts_per_service_day=alert_rate * windows_per_day,
    )


def candidate_quantiles(start: float, stop: float, num: int) -> np.ndarray:
    return np.linspace(start, stop, num)


def sensitivity_table(y_true: np.ndarray, scores: np.ndarray, quantiles: np.ndarray, windows_per_day: int) -> list[OperatingPoint]:
    """Evaluate every candidate threshold (quantiles of the normal-score distribution)."""
    normal_scores = scores[np.asarray(y_true) == 0]
    if len(normal_scores) == 0:
        raise ValueError("Threshold selection requires normal validation windows")
    thresholds = np.quantile(normal_scores, quantiles, method="linear")
    return [operating_point(y_true, scores, t, q, windows_per_day) for q, t in zip(quantiles, thresholds, strict=True)]


def select_threshold(table: list[OperatingPoint], objective: str, target_fpr: float) -> OperatingPoint:
    if objective == "max_f1":
        # Ties broken toward the lower false-positive rate (smaller review burden).
        return max(table, key=lambda p: (round(p.f1, 12), -p.fpr))
    if objective == "max_recall_at_fpr":
        eligible = [p for p in table if p.fpr <= target_fpr]
        if not eligible:
            return min(table, key=lambda p: p.fpr)
        return max(eligible, key=lambda p: (p.recall, p.precision))
    raise ValueError(f"Unknown threshold objective '{objective}'")


def unlabelled_threshold(normal_scores: np.ndarray, target_fpr: float) -> float:
    """For retraining on unlabelled (assumed normal) windows: alert on the top ``target_fpr`` of held-out scores."""
    return float(np.quantile(normal_scores, 1.0 - target_fpr, method="linear"))
