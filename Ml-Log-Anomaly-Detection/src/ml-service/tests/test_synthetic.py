"""Synthetic benchmark generator: counts, determinism, baseline parameters, chronology, overlap."""

import numpy as np
import pandas as pd
import pytest

from app.core.config import load_benchmark_config
from app.datasets.synthetic import ANOMALY_TYPES, generate_dataset


def test_split_counts_are_exact(config):
    frame = generate_dataset(config).frame
    for split, sizes in config.splits.items():
        part = frame[frame["split"] == split]
        assert (part["label"] == 0).sum() == sizes["normal"]
        assert (part["label"] == 1).sum() == sizes["anomaly"]
    assert set(frame.loc[frame["label"] == 1, "anomaly_type"]) == set(ANOMALY_TYPES)


def test_generation_is_deterministic_for_a_seed(config):
    a = generate_dataset(config).frame
    b = generate_dataset(config).frame
    pd.testing.assert_frame_equal(a, b)


def test_full_config_matches_report_parameters():
    """The committed manifest encodes the report's §5.2 benchmark (32,000 normal + 1,800 anomaly windows)."""
    from app.core.config import project_root

    cfg = load_benchmark_config(project_root() / "config" / "benchmark.yaml")
    assert cfg.dataset["n_normal"] == 32000 and cfg.dataset["n_anomaly"] == 1800
    n = cfg.dataset["normal"]
    assert n["request_count"]["lambda"] == 140
    assert n["avg_duration_ms"]["mean_ms"] == 250
    assert n["p95_duration_ms"]["ratio_mean"] == 2.6
    assert n["error_rate"]["mean"] == 0.02 and n["auth_failure_rate"]["mean"] == 0.015
    assert n["dependency_failure_count"]["mean"] == 0.15 and n["retry_count"]["mean"] == 0.10
    assert n["endpoint_entropy"]["mean"] == 2.3


def test_normal_baseline_statistics(config):
    frame = generate_dataset(config).frame
    normal = frame[frame["label"] == 0]
    assert normal["request_count"].mean() == pytest.approx(140, rel=0.03)
    assert normal["avg_duration_ms"].mean() == pytest.approx(250, rel=0.05)
    assert normal["error_rate"].mean() == pytest.approx(0.02, rel=0.1)
    assert normal["auth_failure_rate"].mean() == pytest.approx(0.015, rel=0.1)
    assert normal["dependency_failure_count"].mean() == pytest.approx(0.15, abs=0.04)
    assert normal["retry_count"].mean() == pytest.approx(0.10, abs=0.04)
    assert normal["endpoint_entropy"].mean() == pytest.approx(2.3, abs=0.02)
    assert (normal["p95_duration_ms"] > normal["avg_duration_ms"]).all()
    ratio = (normal["p95_duration_ms"] / normal["avg_duration_ms"]).mean()
    assert ratio == pytest.approx(2.6, rel=0.05)


def test_splits_are_chronological(config):
    frame = generate_dataset(config).frame
    t = {s: frame[frame["split"] == s]["window_start_utc"] for s in ("train", "validation", "test")}
    assert t["train"].max() < t["validation"].min()
    assert t["validation"].max() < t["test"].min()


def test_anomalies_shift_their_features_but_still_overlap(config):
    frame = generate_dataset(config).frame
    normal = frame[frame["label"] == 0]
    lat = frame[frame["anomaly_type"] == "latency_spike"]
    auth = frame[frame["anomaly_type"] == "auth_failure_burst"]
    surge = frame[frame["anomaly_type"] == "traffic_surge"]
    retry = frame[frame["anomaly_type"] == "retry_burst"]
    assert lat["avg_duration_ms"].mean() > 2.4 * normal["avg_duration_ms"].mean()
    assert auth["auth_failure_rate"].mean() > 5 * normal["auth_failure_rate"].mean()
    assert surge["request_count"].mean() > 1.7 * normal["request_count"].mean()
    # Not perfectly separable: some retry-burst windows look exactly like normal ones.
    assert (retry["retry_count"] == 0).any()
    assert np.isin(retry["retry_count"].unique(), normal["retry_count"].unique()).any()
