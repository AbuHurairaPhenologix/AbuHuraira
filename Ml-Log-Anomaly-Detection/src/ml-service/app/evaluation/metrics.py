"""Evaluation metrics (report §5.4): precision, recall, F1, FPR, ROC-AUC, PR-AUC, confusion matrix."""

from __future__ import annotations

import numpy as np
import pandas as pd
from sklearn.metrics import average_precision_score, roc_auc_score

from app.training.thresholds import confusion


def binary_metrics(y_true: np.ndarray, flagged: np.ndarray) -> dict[str, float | int]:
    tp, fp, tn, fn = confusion(y_true, flagged)
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / (tp + fn) if tp + fn else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    fpr = fp / (fp + tn) if fp + tn else 0.0
    return {"precision": precision, "recall": recall, "f1": f1, "fpr": fpr, "tp": tp, "fp": fp, "tn": tn, "fn": fn}


def ranking_metrics(y_true: np.ndarray, scores: np.ndarray) -> dict[str, float]:
    return {"rocAuc": float(roc_auc_score(y_true, scores)), "prAuc": float(average_precision_score(y_true, scores))}


def per_type_recall(frame: pd.DataFrame, flagged: np.ndarray) -> dict[str, float]:
    tmp = frame.assign(_flag=flagged)
    return {k: float(v) for k, v in tmp[tmp["label"] == 1].groupby("anomaly_type")["_flag"].mean().sort_index().items()}
