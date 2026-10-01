"""Scoring contract (report §4.7 and Appendix "API Contract").

Strict validation: missing features, unexpected features, wrong types (strings, booleans), non-finite numbers,
out-of-range ratios and incompatible schema versions are all rejected with HTTP 422.
"""

from __future__ import annotations

from datetime import datetime
from typing import Annotated

from pydantic import AliasChoices, BaseModel, ConfigDict, Field, StrictFloat, StrictInt, field_validator

from app.core.feature_schema import FEATURE_SCHEMA_VERSION

ModelVersionLabel = Annotated[str, Field(pattern=r"^[a-z0-9][a-z0-9.\-]{1,63}$", description="Registered model version label")]
Count = Annotated[StrictInt, Field(ge=0, le=100_000_000)]
Ratio = Annotated[StrictFloat | StrictInt, Field(ge=0, le=1, allow_inf_nan=False)]
NonNegative = Annotated[StrictFloat | StrictInt, Field(ge=0, le=1e9, allow_inf_nan=False)]


class FeatureValues(BaseModel):
    """Exactly the eight ``ops-v1`` features — no more, no fewer."""

    model_config = ConfigDict(extra="forbid", protected_namespaces=())

    request_count: Count
    error_rate: Ratio
    avg_duration_ms: NonNegative
    p95_duration_ms: NonNegative
    auth_failure_rate: Ratio
    dependency_failure_count: Count
    retry_count: Count
    endpoint_entropy: Annotated[StrictFloat | StrictInt, Field(ge=0, le=64, allow_inf_nan=False)]


def _check_schema(value: str) -> str:
    if value != FEATURE_SCHEMA_VERSION:
        raise ValueError(f"incompatible feature schema '{value}'; this service accepts '{FEATURE_SCHEMA_VERSION}'")
    return value


class WindowContext(BaseModel):
    model_config = ConfigDict(extra="forbid", protected_namespaces=())

    window_id: str | None = Field(default=None, alias="windowId", max_length=128)
    service: str | None = Field(default=None, max_length=100)
    environment: str | None = Field(default=None, max_length=32)
    window_start_utc: datetime | None = Field(default=None, alias="windowStartUtc")
    window_end_utc: datetime | None = Field(default=None, alias="windowEndUtc")


class ScoreRequest(WindowContext):
    model_version: ModelVersionLabel = Field(alias="modelVersion")
    schema_version: str = Field(validation_alias=AliasChoices("schemaVersion", "featureSchema"), serialization_alias="schemaVersion")
    features: FeatureValues

    _schema = field_validator("schema_version")(_check_schema)


class BatchItem(WindowContext):
    window_id: str = Field(alias="windowId", min_length=1, max_length=128)
    features: FeatureValues


class BatchScoreRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", protected_namespaces=())

    model_version: ModelVersionLabel = Field(alias="modelVersion")
    schema_version: str = Field(validation_alias=AliasChoices("schemaVersion", "featureSchema"), serialization_alias="schemaVersion")
    items: list[BatchItem] = Field(min_length=1, max_length=500)

    _schema = field_validator("schema_version")(_check_schema)


class FeatureReason(BaseModel):
    feature: str
    value: float
    baselineMean: float
    baselineStd: float
    zScore: float
    direction: str


class ScoreResponse(BaseModel):
    windowId: str | None
    score: float
    threshold: float
    isAnomaly: bool
    modelVersion: str
    modelId: str
    algorithm: str
    schemaVersion: str
    reasonSummary: str
    reasons: list[FeatureReason]


class BatchScoreResponse(BaseModel):
    modelVersion: str
    schemaVersion: str
    results: list[ScoreResponse]
