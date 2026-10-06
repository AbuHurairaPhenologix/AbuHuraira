"""Short-term predictive analytics over fixed time intervals.

For each interval we measure four 0-100 activity metrics (all derived from real analysis output):

* energy            - mean window loudness, min-max scaled to this recording's dB range
* speech_activity   - % of the interval covered by transcribed speech
* anomaly_likelihood- mean Isolation-Forest score rescaled between the recording median and the threshold
* engagement        - mean of speech_activity, speaker-turn rate (scaled) and energy

Model: a mean-reverting AR(1) per metric,  next = mu + phi * (current - mu),  with mu and phi
estimated by least squares on the intervals seen so far. phi ~ 0 means activity snaps back to the
recording's average; phi ~ 1 means the current level persists. (A ridge model with lags and
cross-metric terms was evaluated too and overfitted the ~20-60 intervals of a typical recording.)

Accuracy is reported honestly with a walk-forward backtest (each interval predicted only from the
intervals before it) and compared with the naive "same as last interval" persistence baseline.
"""
from dataclasses import dataclass

import numpy as np
import pandas as pd

METRICS = ["energy", "speech_activity", "anomaly_likelihood", "engagement"]
MIN_TRAIN = 6
TREND_DEADBAND = 5.0  # percentage points


def choose_interval(duration_s: float) -> float:
    """~24 intervals per recording, bounded to [5, 60] s."""
    return float(np.clip(round(duration_s / 24.0), 5.0, 60.0))


def _overlap(a0: float, a1: float, b0: float, b1: float) -> float:
    return max(0.0, min(a1, b1) - max(a0, b0))


def interval_metrics(duration_s: float, interval_s: float, window_times: np.ndarray, window_rms_db: np.ndarray,
                     anomaly_scores: np.ndarray, anomaly_threshold: float,
                     speech: list[tuple[float, float, str | None]]) -> pd.DataFrame:
    """speech: (start, end, speaker) for every transcript segment."""
    # A trailing remainder shorter than half an interval is absorbed into the last interval.
    n = max(1, int(round(duration_s / interval_s)))
    lo, hi = np.percentile(window_rms_db, [5, 95]) if len(window_rms_db) else (0.0, 1.0)
    med = float(np.median(anomaly_scores)) if len(anomaly_scores) else 0.0
    span = max(anomaly_threshold - med, 1e-6)
    rows = []
    for i in range(n):
        a, b = i * interval_s, duration_s if i == n - 1 else (i + 1) * interval_s
        mask = (window_times >= a) & (window_times < b)
        energy = float(np.clip((window_rms_db[mask].mean() - lo) / max(hi - lo, 1e-6), 0, 1) * 100) if mask.any() else 0.0
        anomaly = float(np.clip((anomaly_scores[mask].mean() - med) / span, 0, 1) * 100) if mask.any() else 0.0
        covered = sum(_overlap(a, b, s, e) for s, e, _ in speech)
        speech_pct = float(np.clip(covered / max(b - a, 1e-6), 0, 1) * 100)
        inside = [spk for s, e, spk in speech if _overlap(a, b, s, e) > 0 and spk]
        turns = sum(1 for x, y in zip(inside, inside[1:]) if x != y)
        rows.append({"start": round(a, 2), "end": round(b, 2), "energy": energy, "speech_activity": speech_pct,
                     "anomaly_likelihood": anomaly, "turns": turns})
    df = pd.DataFrame(rows)
    turn_scale = max(float(df["turns"].max()), 1.0)
    df["engagement"] = (df["speech_activity"] + df["turns"] / turn_scale * 100 + df["energy"]) / 3
    return df.round(2)


def fit_ar1(history: np.ndarray) -> tuple[float, float]:
    """Least-squares (mu, phi) for x[t] - mu = phi * (x[t-1] - mu); phi clipped to [0, 1]."""
    x = np.asarray(history, dtype=float)
    mu = float(x.mean())
    a, b = x[:-1] - mu, x[1:] - mu
    if len(x) < 3 or float(a @ a) < 1e-9:
        return mu, 1.0
    return mu, float(np.clip((a @ b) / (a @ a), 0.0, 1.0))


def ar1_predict(history: np.ndarray) -> float:
    mu, phi = fit_ar1(history)
    return float(np.clip(mu + phi * (history[-1] - mu), 0, 100))


@dataclass
class MetricForecast:
    metric: str
    current: float
    predicted: float
    trend: str
    model_mae: float | None
    baseline_mae: float | None
    backtest: list[dict]
    coefficients: dict


def trend_label(current: float, predicted: float, deadband: float = TREND_DEADBAND) -> str:
    if predicted - current > deadband:
        return "Rising"
    if current - predicted > deadband:
        return "Declining"
    return "Stable"


def forecast(df: pd.DataFrame, metric: str) -> MetricForecast:
    series = df[metric].to_numpy(dtype=float)
    current = float(series[-1]) if len(series) else 0.0
    if len(series) <= MIN_TRAIN:
        # Too short for a fitted model: fall back to persistence and say so (no accuracy claimed).
        return MetricForecast(metric, round(current, 2), round(current, 2), "Stable", None, None, [],
                              {"note": "insufficient history; persistence forecast"})

    backtest, err_model, err_base = [], [], []
    for t in range(MIN_TRAIN, len(series)):
        pred = ar1_predict(series[:t])
        err_model.append(abs(pred - series[t]))
        err_base.append(abs(series[t - 1] - series[t]))
        backtest.append({"index": t, "actual": round(float(series[t]), 2), "predicted": round(pred, 2)})

    mu, phi = fit_ar1(series)
    predicted = ar1_predict(series)
    return MetricForecast(
        metric=metric, current=round(current, 2), predicted=round(predicted, 2), trend=trend_label(current, predicted),
        model_mae=round(float(np.mean(err_model)), 2), baseline_mae=round(float(np.mean(err_base)), 2),
        backtest=backtest, coefficients={"mu": round(mu, 2), "phi": round(phi, 3)},
    )
