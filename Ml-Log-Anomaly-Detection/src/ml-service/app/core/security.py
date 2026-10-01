"""Service-to-service authentication for the internal ML API (report §4.8).

The backend presents a short-lived HS256 JWT with ``aud=ml-scoring`` and a single ``scope`` per operation.
Scoring requires ``anomaly.score``; model reads ``models.read``; activation ``models.admin``; training
``training.run``. Administrative scopes are never granted implicitly by the scoring scope.
"""

from __future__ import annotations

import logging

import jwt
from fastapi import Depends, HTTPException, status
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer

from app.core.config import Settings, get_settings

logger = logging.getLogger("ml.security")

SCOPE_SCORE = "anomaly.score"
SCOPE_MODELS_READ = "models.read"
SCOPE_MODELS_ADMIN = "models.admin"
SCOPE_TRAINING = "training.run"

_bearer = HTTPBearer(auto_error=False)


def verify_token(token: str, settings: Settings) -> dict:
    if len(settings.token_signing_key.encode()) < 32:
        raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE, "Service authentication is not configured.")
    try:
        return jwt.decode(
            token,
            settings.token_signing_key,
            algorithms=["HS256"],
            audience=settings.token_audience,
            issuer=settings.token_issuer,
            options={"require": ["exp", "iat", "aud", "iss", "scope"]},
            leeway=30,
        )
    except jwt.PyJWTError as exc:
        # Never log the token itself.
        logger.warning("Rejected service token: %s", type(exc).__name__)
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "Invalid or expired service token.") from exc


def require_scope(scope: str):
    def dependency(
        credentials: HTTPAuthorizationCredentials | None = Depends(_bearer),
        settings: Settings = Depends(get_settings),
    ) -> dict:
        if credentials is None or credentials.scheme.lower() != "bearer":
            raise HTTPException(status.HTTP_401_UNAUTHORIZED, "Service token required.")
        claims = verify_token(credentials.credentials, settings)
        scopes = str(claims.get("scope", "")).split()
        if scope not in scopes:
            logger.warning("Service token lacks scope %s (subject=%s)", scope, claims.get("sub"))
            raise HTTPException(status.HTTP_403_FORBIDDEN, f"Scope '{scope}' required.")
        return claims

    return dependency
