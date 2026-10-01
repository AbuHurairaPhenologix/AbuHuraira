"""The versioned ``ops-v1`` feature contract (report §3.8, §4.5).

Order is significant: it is the column order of every model input. Mirrors
``config/feature-schema.ops-v1.json`` and the C# ``FeatureSchema`` class.
"""

from __future__ import annotations

FEATURE_SCHEMA_VERSION = "ops-v1"

FEATURE_NAMES: tuple[str, ...] = (
    "request_count",
    "error_rate",
    "avg_duration_ms",
    "p95_duration_ms",
    "auth_failure_rate",
    "dependency_failure_count",
    "retry_count",
    "endpoint_entropy",
)

INTEGER_FEATURES = frozenset({"request_count", "dependency_failure_count", "retry_count"})
RATIO_FEATURES = frozenset({"error_rate", "auth_failure_rate"})

FEATURE_LABELS = {
    "request_count": "request count",
    "error_rate": "error rate",
    "avg_duration_ms": "average response time",
    "p95_duration_ms": "p95 response time",
    "auth_failure_rate": "authentication-failure rate",
    "dependency_failure_count": "dependency failure count",
    "retry_count": "retry count",
    "endpoint_entropy": "endpoint diversity (entropy)",
}


def is_compatible(schema_version: str | None) -> bool:
    return schema_version == FEATURE_SCHEMA_VERSION
