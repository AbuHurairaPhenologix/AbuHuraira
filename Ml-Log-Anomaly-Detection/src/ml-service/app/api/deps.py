"""Shared application state accessors for route handlers."""

from __future__ import annotations

from fastapi import Request

from app.registry.registry import ModelRegistry
from app.scoring.service import ActiveModels, ScoringService
from app.training.jobs import TrainingJobManager


def get_registry(request: Request) -> ModelRegistry:
    return request.app.state.registry


def get_active(request: Request) -> ActiveModels:
    return request.app.state.active


def get_scoring(request: Request) -> ScoringService:
    return request.app.state.scoring


def get_jobs(request: Request) -> TrainingJobManager:
    return request.app.state.jobs
