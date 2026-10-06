"""POST /internal/anomaly/score and /internal/anomaly/score/batch (report §4.7)."""

from __future__ import annotations

from fastapi import APIRouter, Depends

from app.api.deps import get_scoring
from app.core.security import SCOPE_SCORE, require_scope
from app.schemas.scoring import BatchScoreRequest, BatchScoreResponse, ScoreRequest, ScoreResponse
from app.scoring.service import ScoringService

router = APIRouter(prefix="/internal/anomaly", tags=["scoring"])


@router.post("/score", response_model=ScoreResponse)
def score(request: ScoreRequest, scoring: ScoringService = Depends(get_scoring), _claims: dict = Depends(require_scope(SCOPE_SCORE))) -> dict:
    model = scoring.resolve(request.model_version, request.schema_version)
    return scoring.score_many(model, [(request.window_id, request.features)])[0]


@router.post("/score/batch", response_model=BatchScoreResponse)
def score_batch(
    request: BatchScoreRequest,
    scoring: ScoringService = Depends(get_scoring),
    _claims: dict = Depends(require_scope(SCOPE_SCORE)),
) -> dict:
    # Batch scoring amortises serialisation and network overhead (report §4.7).
    model = scoring.resolve(request.model_version, request.schema_version)
    results = scoring.score_many(model, [(item.window_id, item.features) for item in request.items])
    return {"modelVersion": model.version, "schemaVersion": request.schema_version, "results": results}
