"""Isolation-Forest acoustic anomaly detection."""
import numpy as np

from app.analytics import anomaly
from app.audio import features
from conftest import SR


def _room(seconds: float, rng) -> np.ndarray:
    """Stationary 'room': low hum plus faint noise."""
    t = np.arange(int(seconds * SR)) / SR
    return (0.05 * np.sin(2 * np.pi * 120 * t) + 0.01 * rng.standard_normal(t.size)).astype(np.float32)


def test_sudden_broadband_burst_is_flagged_at_the_right_time(rng):
    y = _room(60.0, rng)
    burst_at = 37.0
    i = int(burst_at * SR)
    y[i:i + int(0.8 * SR)] += (0.9 * rng.standard_normal(int(0.8 * SR))).astype(np.float32)
    df = features.window_features(y, SR)
    result = anomaly.detect_anomalies(df)

    assert len(result.regions) == 1
    r = result.regions[0]
    assert r.start - 1.0 <= burst_at <= r.end
    assert r.score >= result.threshold >= 0.6
    assert r.top_features and {"feature", "z"} <= set(r.top_features[0])
    assert len(result.scores) == len(df) and np.all((result.scores >= 0) & (result.scores <= 1))


def test_stationary_signal_has_no_anomalies(rng):
    df = features.window_features(_room(40.0, rng), SR)
    assert anomaly.detect_anomalies(df).regions == []


def test_robust_threshold_has_a_floor_and_tracks_spread():
    flat = np.full(100, 0.45)
    assert anomaly.robust_threshold(flat) == 0.6
    spread = np.concatenate([np.linspace(0.3, 0.6, 99), [0.9]])
    assert anomaly.robust_threshold(spread, k=6) > 0.6


def test_novelty_features_are_one_sided_for_energy(rng):
    y = np.concatenate([_room(20.0, rng) * 10, _room(20.0, rng) * 0.01])  # loud -> sudden quiet
    nov = anomaly.novelty_features(features.window_features(y, SR))
    assert (nov["rms_db_novelty"] >= 0).all()
    assert nov["spectral_centroid_novelty"].min() < 0 or nov["spectral_centroid_novelty"].max() > 0


def test_too_short_input_returns_no_regions():
    df = features.window_features(np.zeros(SR * 2, dtype=np.float32), SR)
    assert anomaly.detect_anomalies(df).regions == []
