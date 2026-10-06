"""Unsupervised acoustic anomaly detection with an Isolation Forest over window-level features.

Score: the original Isolation Forest anomaly score s(x) = 2^(-E[h(x)] / c(n)) in [0, 1]
(scikit-learn's `score_samples` returns -s). Scores near 0.5 are ordinary; values well above the
recording's own distribution are isolated quickly by random splits, i.e. acoustically unusual.

Feature vector per window = 10 absolute descriptors (energy, ZCR, spectral shape, onsets, low MFCCs)
+ 5 contextual "novelty" descriptors: the deviation of the window from the median of the preceding
10 s, scaled by that context's spread. Novelty makes *sudden* changes stand out instead of merely rare
but steady sounds (e.g. a long music bed). Energy/onset novelty is one-sided: sudden drops to silence
are reported by the silence detector, not as anomalies.

Threshold: robust, recording-relative: median + k * MAD of the scores, never below `min_score`.
Consecutive flagged windows are merged into one anomaly region.
"""
from dataclasses import dataclass, field

import numpy as np
import pandas as pd
from sklearn.ensemble import IsolationForest
from sklearn.preprocessing import RobustScaler

ABSOLUTE_FEATURES = ["rms_db", "rms_db_std", "zcr", "spectral_centroid", "spectral_rolloff", "spectral_flatness",
                     "onset_peak", "mfcc_1", "mfcc_2", "mfcc_3"]
NOVELTY_FEATURES = ["rms_db", "spectral_centroid", "spectral_flatness", "onset_peak", "mfcc_2"]
ONE_SIDED = {"rms_db", "onset_peak"}
CONTEXT_WINDOWS = 20  # 10 s of history at a 0.5 s hop


@dataclass
class AnomalyRegion:
    start: float
    end: float
    peak_time: float
    score: float
    top_features: list[dict] = field(default_factory=list)


@dataclass
class AnomalyResult:
    times: np.ndarray
    scores: np.ndarray
    threshold: float
    regions: list[AnomalyRegion]


def novelty_features(features: pd.DataFrame, context: int = CONTEXT_WINDOWS) -> pd.DataFrame:
    """Deviation of each window from the preceding `context` windows (median / inter-decile range)."""
    X = features[NOVELTY_FEATURES]
    hist = X.shift(1).rolling(context, min_periods=4)
    spread = (hist.quantile(0.9) - hist.quantile(0.1)).abs() + X.std() * 0.1 + 1e-9
    nov = ((X - hist.median()) / spread).fillna(0.0)
    for col in ONE_SIDED:
        nov[col] = nov[col].clip(lower=0)
    return nov.add_suffix("_novelty")


def anomaly_matrix(features: pd.DataFrame) -> pd.DataFrame:
    return pd.concat([features[ABSOLUTE_FEATURES], novelty_features(features)], axis=1)


def score_windows(features: pd.DataFrame, n_estimators: int = 300, random_state: int = 42) -> tuple[np.ndarray, np.ndarray, list[str]]:
    """Return (isolation-forest scores in [0,1], robust-scaled feature matrix, column names)."""
    M = anomaly_matrix(features)
    X = M.to_numpy(dtype=float)
    Z = RobustScaler(quantile_range=(10, 90)).fit_transform(X)
    Z = np.clip(Z, -25, 25)
    forest = IsolationForest(n_estimators=n_estimators, contamination="auto", random_state=random_state)
    forest.fit(Z)
    return -forest.score_samples(Z), Z, list(M.columns)


def robust_threshold(scores: np.ndarray, k: float = 6.0, min_score: float = 0.6) -> float:
    med = float(np.median(scores))
    mad = float(np.median(np.abs(scores - med))) * 1.4826
    return max(min_score, med + k * mad)


def detect_anomalies(features: pd.DataFrame, k: float = 6.0, min_score: float = 0.6, max_gap_s: float = 0.5) -> AnomalyResult:
    if len(features) < 8:
        return AnomalyResult(features["time"].to_numpy() if len(features) else np.array([]), np.zeros(len(features)), 1.0, [])
    scores, Z, cols = score_windows(features)
    thr = robust_threshold(scores, k, min_score)
    flagged = np.where(scores >= thr)[0]

    regions: list[AnomalyRegion] = []
    group: list[int] = []
    for idx in flagged:
        if group and features["start"].iloc[idx] - features["end"].iloc[group[-1]] > max_gap_s:
            regions.append(_region(features, scores, Z, cols, group))
            group = []
        group.append(int(idx))
    if group:
        regions.append(_region(features, scores, Z, cols, group))
    return AnomalyResult(features["time"].to_numpy(), scores, thr, regions)


def _region(features: pd.DataFrame, scores: np.ndarray, Z: np.ndarray, cols: list[str], idx: list[int]) -> AnomalyRegion:
    peak = idx[int(np.argmax(scores[idx]))]
    z = Z[peak]
    order = np.argsort(-np.abs(z))[:4]
    top = [{"feature": cols[i], "z": round(float(z[i]), 2)} for i in order]
    return AnomalyRegion(
        start=float(features["start"].iloc[idx[0]]),
        end=float(features["end"].iloc[idx[-1]]),
        peak_time=float(features["time"].iloc[peak]),
        score=round(float(scores[peak]), 3),
        top_features=top,
    )
