"""Liveness and readiness. Readiness checks registry readability and artifact accessibility."""

from __future__ import annotations

from fastapi import APIRouter, Depends
from fastapi.responses import JSONResponse

from app.api.deps import get_active, get_registry
from app.core.config import get_settings
from app.registry.registry import ModelRegistry
from app.scoring.service import ActiveModels

router = APIRouter(tags=["health"])


@router.get("/health/live")
def live() -> dict:
    return {"status": "Healthy"}


@router.get("/health/ready")
def ready(registry: ModelRegistry = Depends(get_registry), active: ActiveModels = Depends(get_active)) -> JSONResponse:
    checks: dict[str, str] = {"self": "Healthy"}
    try:
        models = registry.list()
        accessible = [m for m in models if m.get("artifactExists")]
        checks["registry"] = "Healthy" if models else "Unhealthy"
        checks["artifacts"] = "Healthy" if accessible and len(accessible) == len(models) else ("Degraded" if accessible else "Unhealthy")
    except Exception as exc:  # noqa: BLE001
        models, accessible = [], []
        checks["registry"] = f"Unhealthy: {type(exc).__name__}"
    checks["serviceAuth"] = "Healthy" if len(get_settings().token_signing_key.encode()) >= 32 else "Unhealthy"
    unhealthy = any(v.startswith("Unhealthy") for v in checks.values())
    degraded = any(v == "Degraded" for v in checks.values())
    status = "Unhealthy" if unhealthy else ("Degraded" if degraded else "Healthy")
    return JSONResponse(
        status_code=503 if unhealthy else 200,
        content={
            "status": status,
            "checks": checks,
            "registeredModels": len(models),
            "accessibleArtifacts": len(accessible),
            "activeModels": active.list(),
        },
    )
