"""Reference implementation of the ``ops-v1`` window feature formulas.

Mirrors ``AnomalyDetection.Domain.Features.FeatureCalculator`` so offline dataset preparation and the online
feature service share identical definitions (report §3.5: shared preprocessing reduces training-serving skew).
Used by parity tests and by tooling that derives windows from raw event exports.
"""

from __future__ import annotations

import math
from collections import Counter
from collections.abc import Iterable, Mapping

import numpy as np


def percentile_linear(values: list[float], q: float) -> float:
    """Percentile with linear interpolation between closest ranks (NumPy default)."""
    if not values:
        return 0.0
    return float(np.percentile(np.asarray(values, dtype=float), q, method="linear"))


def shannon_entropy_bits(categories: Iterable[str]) -> float:
    counts = Counter(categories)
    total = sum(counts.values())
    if total == 0:
        return 0.0
    entropy = 0.0
    for count in sorted(counts.values()):
        p = count / total
        entropy -= p * math.log2(p)
    return entropy


def compute_window_features(events: Iterable[Mapping]) -> dict[str, float]:
    """Compute the eight features from canonical event dictionaries (keys as in the event schema)."""
    unique: dict[str, Mapping] = {}
    for e in events:
        unique.setdefault(str(e["event_id"]), e)  # duplicates count once (TC-09)
    rows = [unique[k] for k in sorted(unique)]
    requests = [e for e in rows if e["event_type"] == "http_request"]
    n = len(requests)
    durations = [float(e["duration_ms"]) for e in requests if e.get("duration_ms") is not None]
    return {
        "request_count": n,
        "error_rate": (sum(1 for e in requests if e.get("error_flag")) / n) if n else 0.0,
        "avg_duration_ms": (sum(durations) / len(durations)) if durations else 0.0,
        "p95_duration_ms": percentile_linear(durations, 95),
        "auth_failure_rate": (sum(1 for e in requests if e.get("authentication_result") == "failure") / n) if n else 0.0,
        "dependency_failure_count": sum(1 for e in rows if e["event_type"] == "dependency_call" and e.get("error_flag")),
        "retry_count": sum(max(0, int(e.get("retry_count") or 0)) for e in rows),
        "endpoint_entropy": shannon_entropy_bits(e.get("endpoint_group", "unknown") for e in requests),
    }
