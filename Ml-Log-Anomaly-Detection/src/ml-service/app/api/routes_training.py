"""Explicit retraining jobs (administrative scope only; never triggered automatically)."""

from __future__ import annotations

from datetime import datetime

from fastapi import APIRouter, Depends, HTTPException, status
from pydantic import BaseModel, ConfigDict, Field

from app.api.deps import get_jobs
from app.core.security import SCOPE_TRAINING, require_scope
from app.training.jobs import TrainingJobManager


class TrainingJobRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    algorithm: str = Field(pattern=r"^(all|isolation_forest|lof|ocsvm|random_forest)$")
    source: str = Field(pattern=r"^(benchmark|feature-windows)$")
    rows: list[dict[str, float]] | None = Field(default=None, max_length=500_000)
    trainingPeriodStartUtc: datetime | None = None
    trainingPeriodEndUtc: datetime | None = None
    requestedBy: str = Field(default="unknown", max_length=128)


router = APIRouter(prefix="/internal/training", tags=["training"])


def _iso(value: datetime | None) -> str | None:
    return value.strftime("%Y-%m-%dT%H:%M:%SZ") if value else None


@router.post("/jobs", status_code=status.HTTP_202_ACCEPTED)
def start(request: TrainingJobRequest, jobs: TrainingJobManager = Depends(get_jobs), _c: dict = Depends(require_scope(SCOPE_TRAINING))) -> dict:
    try:
        job = jobs.submit(request.algorithm, request.source, request.rows, _iso(request.trainingPeriodStartUtc), _iso(request.trainingPeriodEndUtc), request.requestedBy)
    except ValueError as exc:
        raise HTTPException(status.HTTP_422_UNPROCESSABLE_CONTENT, str(exc)) from exc
    return job.to_dict()


@router.get("/jobs/{job_id}")
def get_job(job_id: str, jobs: TrainingJobManager = Depends(get_jobs), _c: dict = Depends(require_scope(SCOPE_TRAINING))) -> dict:
    job = jobs.get(job_id)
    if job is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "Training job not found")
    return job.to_dict()
