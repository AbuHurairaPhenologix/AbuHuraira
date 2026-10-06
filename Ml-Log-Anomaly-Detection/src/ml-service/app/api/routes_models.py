"""Registry inspection and activation (called by the backend's administrative workflow)."""

from __future__ import annotations

import logging
from typing import Any

from fastapi import APIRouter, Depends

from app.api.deps import get_active, get_registry, get_scoring
from app.core.security import SCOPE_MODELS_ADMIN, SCOPE_MODELS_READ, require_scope
from app.registry.registry import ModelRegistry
from app.scoring.service import ActiveModels, ScoringService

router = APIRouter(prefix="/internal/models", tags=["models"])
logger = logging.getLogger("ml.models")

PUBLIC_FIELDS = (
    "modelId", "modelVersion", "algorithm", "displayName", "featureSchemaVersion", "featureNames",
    "trainingPeriodStartUtc", "trainingPeriodEndUtc", "trainingWindows", "randomSeed", "libraryVersions", "parameters",
    "validationThreshold", "thresholdObjective", "thresholdQuantile", "artifactPath", "artifactSha256",
    "productionEligible", "validationMetrics", "createdAtUtc", "trainingRunId", "trainingSource", "configSha256",
    "datasetSha256", "recommended", "artifactExists",
)


def public(entry: dict[str, Any], active: ActiveModels) -> dict[str, Any]:
    result = {k: entry.get(k) for k in PUBLIC_FIELDS}
    result["recommended"] = bool(entry.get("recommended", False))
    result["activeInService"] = active.is_active(entry["modelVersion"])
    return result


@router.get("")
def list_models(registry: ModelRegistry = Depends(get_registry), active: ActiveModels = Depends(get_active), _c: dict = Depends(require_scope(SCOPE_MODELS_READ))) -> dict:
    return {"models": [public(m, active) for m in registry.list()]}


@router.get("/{version}")
def get_model(version: str, registry: ModelRegistry = Depends(get_registry), active: ActiveModels = Depends(get_active), _c: dict = Depends(require_scope(SCOPE_MODELS_READ))) -> dict:
    return public(registry.get(version), active)


@router.post("/{version}/activate")
def activate(
    version: str,
    scoring: ScoringService = Depends(get_scoring),
    active: ActiveModels = Depends(get_active),
    claims: dict = Depends(require_scope(SCOPE_MODELS_ADMIN)),
) -> dict:
    """Verifies the artifact (exists, hash, library version), loads it, self-tests it, then marks it active."""
    loaded = scoring.registry.load(version)
    scoring.self_test(loaded)
    active.activate(version)
    logger.info("Model %s activated (requested by %s)", version, claims.get("sub"))
    return public(scoring.registry.get(version), active)


@router.post("/{version}/deactivate")
def deactivate(
    version: str,
    registry: ModelRegistry = Depends(get_registry),
    active: ActiveModels = Depends(get_active),
    claims: dict = Depends(require_scope(SCOPE_MODELS_ADMIN)),
) -> dict:
    registry.get(version)
    changed = active.deactivate(version)
    logger.info("Model %s deactivated=%s (requested by %s)", version, changed, claims.get("sub"))
    return {"modelVersion": version, "activeInService": False}
