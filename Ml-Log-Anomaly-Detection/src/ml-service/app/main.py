"""FastAPI application: internal, model-agnostic anomaly scoring API (report §4.7).

Run locally:  uvicorn app.main:app --port 8000
"""

from __future__ import annotations

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

from app.api import routes_health, routes_models, routes_scoring, routes_training
from app.core.config import get_settings
from app.registry.registry import ModelRegistry, RegistryError
from app.scoring.service import ActiveModels, ScoringService
from app.training.jobs import TrainingJobManager

logging.basicConfig(level=logging.INFO, format='{"ts":"%(asctime)s","level":"%(levelname)s","logger":"%(name)s","msg":"%(message)s"}')
logger = logging.getLogger("ml.api")


def create_app() -> FastAPI:
    @asynccontextmanager
    async def lifespan(app: FastAPI):
        settings = get_settings()
        if len(settings.token_signing_key.encode()) < 32:
            logger.error("ML_SERVICE_TOKEN_SIGNING_KEY is not configured (>= 32 bytes); internal endpoints will reject calls.")
        registry = ModelRegistry(settings.models_dir)
        active = ActiveModels()
        app.state.registry = registry
        app.state.active = active
        app.state.scoring = ScoringService(registry, active)
        app.state.jobs = TrainingJobManager(registry)
        for version in settings.preactivate_models:
            try:
                app.state.scoring.self_test(registry.load(version))
                active.activate(version)
                logger.info("Pre-activated model %s from ML_ACTIVE_MODELS", version)
            except RegistryError as exc:
                logger.error("Could not pre-activate %s: %s", version, exc)
        logger.info("ML service started with %d registered models in %s", len(registry.list()), settings.models_dir)
        yield

    app = FastAPI(
        title="Anomaly Scoring Service (internal)",
        version="1.0.0",
        description="Model-agnostic internal scoring API for ops-v1 feature windows. Not publicly exposed.",
        lifespan=lifespan,
    )

    @app.exception_handler(RegistryError)
    async def registry_error(_: Request, exc: RegistryError) -> JSONResponse:
        logger.warning("Request rejected: %s (%s)", exc.code, exc)
        return JSONResponse(status_code=exc.status_code, content={"error": {"code": exc.code, "message": str(exc)}})

    @app.exception_handler(RequestValidationError)
    async def validation_error(request: Request, exc: RequestValidationError) -> JSONResponse:
        # Structured rejection log (TC-02 evidence); input values are not logged.
        fields = sorted({".".join(str(p) for p in e["loc"][1:]) for e in exc.errors()})
        logger.warning("Validation rejected %s %s: fields=%s", request.method, request.url.path, fields)
        errors = [{"loc": e["loc"], "msg": e["msg"], "type": e["type"]} for e in exc.errors()]
        return JSONResponse(status_code=422, content={"error": {"code": "validation_failed", "message": "Request failed validation.", "details": errors}})

    app.include_router(routes_health.router)
    app.include_router(routes_scoring.router)
    app.include_router(routes_models.router)
    app.include_router(routes_training.router)
    return app


app = create_app()
