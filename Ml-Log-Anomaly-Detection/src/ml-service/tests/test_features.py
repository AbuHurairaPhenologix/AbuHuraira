"""Feature formula reference implementation (mirrors the .NET FeatureCalculator; same expected values)."""

import math

import pytest

from app.core.feature_schema import FEATURE_NAMES, FEATURE_SCHEMA_VERSION
from app.datasets.features import compute_window_features, percentile_linear, shannon_entropy_bits


def test_schema_order_is_explicit_and_versioned():
    assert FEATURE_SCHEMA_VERSION == "ops-v1"
    assert FEATURE_NAMES == (
        "request_count", "error_rate", "avg_duration_ms", "p95_duration_ms",
        "auth_failure_rate", "dependency_failure_count", "retry_count", "endpoint_entropy",
    )


def test_p95_linear_interpolation_matches_numpy_definition():
    values = [float(v) for v in range(1, 101)]  # 1..100
    assert percentile_linear(values, 95) == pytest.approx(95.05)
    assert percentile_linear([10.0, 20.0], 95) == pytest.approx(19.5)
    assert percentile_linear([], 95) == 0.0


def test_shannon_entropy_bits():
    assert shannon_entropy_bits([]) == 0.0
    assert shannon_entropy_bits(["a", "a", "a"]) == 0.0
    assert shannon_entropy_bits(["a", "b"]) == pytest.approx(1.0)
    assert shannon_entropy_bits(["a", "b", "c", "d"]) == pytest.approx(2.0)
    p = [0.5, 0.25, 0.25]
    assert shannon_entropy_bits(["a", "a", "b", "c"]) == pytest.approx(-sum(x * math.log2(x) for x in p))


def _event(i, **kw):
    base = {"event_id": f"e{i}", "event_type": "http_request", "endpoint_group": "/a", "duration_ms": 100.0,
            "error_flag": False, "authentication_result": "none", "retry_count": 0}
    base.update(kw)
    return base


def test_window_features_and_duplicates_counted_once():
    events = [
        _event(1, duration_ms=100.0),
        _event(2, duration_ms=200.0, error_flag=True, endpoint_group="/b"),
        _event(3, duration_ms=300.0, authentication_result="failure", endpoint_group="/b"),
        _event(3, duration_ms=300.0, authentication_result="failure", endpoint_group="/b"),  # duplicate (TC-09)
        _event(4, event_type="dependency_call", error_flag=True, retry_count=2, duration_ms=None),
        _event(5, event_type="background_job", retry_count=1, duration_ms=None),
    ]
    f = compute_window_features(events)
    assert f["request_count"] == 3
    assert f["error_rate"] == pytest.approx(1 / 3)
    assert f["avg_duration_ms"] == pytest.approx(200.0)
    assert f["p95_duration_ms"] == pytest.approx(290.0)
    assert f["auth_failure_rate"] == pytest.approx(1 / 3)
    assert f["dependency_failure_count"] == 1
    assert f["retry_count"] == 3
    assert f["endpoint_entropy"] == pytest.approx(shannon_entropy_bits(["/a", "/b", "/b"]))
