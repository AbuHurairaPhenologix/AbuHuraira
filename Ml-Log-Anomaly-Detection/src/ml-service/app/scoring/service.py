"""Scoring service: resolves a registered, active model and scores feature vectors.

* Only versions in the controlled registry can be used (never a path).
* Only versions activated through the administrative workflow are accepted for scoring (409 otherwise).
* The vector is assembled in explicit ``ops-v1`` order; the threshold stored with the model is applied
  (``score >= threshold``) and returned so the caller can persist the exact value used.
"""

from __future__ import annotations

import logging
import threading

import numpy as np

from app.core.feature_schema import FEATURE_NAMES, FEATURE_SCHEMA_VERSION
from app.registry.registry import LoadedModel, ModelRegistry, RegistryError
from app.schemas.scoring import FeatureValues
from app.scoring.reasons import feature_reasons, summarize

logger = logging.getLogger("ml.scoring")


class ModelInactive(RegistryError):
    status_code = 409
    code = "model_inactive"


class SchemaIncompatible(RegistryError):
    status_code = 422
    code = "schema_incompatible"


class ActiveModels:
    """In-memory activation state. The backend's database is authoritative and re-synchronises it after restarts."""

    def __init__(self) -> None:
        self._active: set[str] = set()
        self._lock = threading.Lock()

    def activate(self, version: str) -> None:
        with self._lock:
            self._active.add(version)

    def deactivate(self, version: str) -> bool:
        with self._lock:
            if version in self._active:
                self._active.remove(version)
                return True
            return False

    def is_active(self, version: str) -> bool:
        with self._lock:
            return version in self._active

    def list(self) -> list[str]:
        with self._lock:
            return sorted(self._active)


class ScoringService:
    def __init__(self, registry: ModelRegistry, active: ActiveModels) -> None:
        self.registry = registry
        self.active = active

    def resolve(self, version: str, schema_version: str) -> LoadedModel:
        metadata = self.registry.get(version)  # 422 invalid label / 404 unknown
        if not self.active.is_active(version):
            raise ModelInactive(f"Model version '{version}' is registered but not active.")
        if metadata["featureSchemaVersion"] != schema_version or metadata.get("featureNames") != list(FEATURE_NAMES):
            raise SchemaIncompatible(
                f"Model '{version}' expects schema '{metadata['featureSchemaVersion']}', request uses '{schema_version}'."
            )
        return self.registry.load(version)  # 503 if the artifact is missing or fails verification

    @staticmethod
    def vector(features: FeatureValues) -> np.ndarray:
        values = features.model_dump()
        return np.array([[float(values[name]) for name in FEATURE_NAMES]], dtype=float)

    def score_many(self, model: LoadedModel, items: list[tuple[str | None, FeatureValues]]) -> list[dict]:
        matrix = np.vstack([self.vector(f) for _, f in items])
        scores = model.adapter.score(matrix)
        threshold = model.threshold
        baseline = model.metadata.get("baseline", {})
        results = []
        for (window_id, features), score in zip(items, scores, strict=True):
            score = float(score)
            if not np.isfinite(score):
                raise RegistryError("Model produced a non-finite score.")
            is_anomaly = score >= threshold
            reasons = feature_reasons(features.model_dump(), baseline)
            results.append(
                {
                    "windowId": window_id,
                    "score": score,
                    "threshold": threshold,
                    "isAnomaly": bool(is_anomaly),
                    "modelVersion": model.version,
                    "modelId": model.metadata["modelId"],
                    "algorithm": model.metadata["algorithm"],
                    "schemaVersion": FEATURE_SCHEMA_VERSION,
                    "reasonSummary": summarize(reasons, is_anomaly),
                    "reasons": reasons,
                }
            )
        anomalies = sum(1 for r in results if r["isAnomaly"])
        logger.info("Scored %d windows with %s (%d above threshold)", len(results), model.version, anomalies)
        return results

    def self_test(self, model: LoadedModel) -> None:
        """Activation check: the artifact must load and score the baseline vector to a finite value."""
        baseline = model.metadata.get("baseline", {})
        vec = np.array([[float(baseline.get(n, {}).get("mean", 0.0)) for n in FEATURE_NAMES]])
        score = model.adapter.score(vec)
        if not np.all(np.isfinite(score)):
            raise RegistryError("Model self-test failed: non-finite score.")
