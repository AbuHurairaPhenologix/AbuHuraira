"""Model adapters: one interface for four estimators, with a single score convention.

scikit-learn estimators disagree on score direction (``decision_function`` is *higher = more normal* for the
one-class models; ``predict_proba`` is *higher = more anomalous* for the classifier). Every adapter exposes
``score(X)`` where **higher always means more anomalous** (report §2.3), so thresholds, the API and the backend never
need to know which estimator is behind a model version.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Any, ClassVar

import numpy as np
from sklearn.ensemble import IsolationForest, RandomForestClassifier
from sklearn.neighbors import LocalOutlierFactor
from sklearn.pipeline import Pipeline, make_pipeline
from sklearn.preprocessing import StandardScaler
from sklearn.svm import OneClassSVM

from app.core.feature_schema import FEATURE_NAMES


class ModelAdapter(ABC):
    algorithm: ClassVar[str]
    supervised: ClassVar[bool] = False

    def __init__(self, params: dict[str, Any], seed: int, scaling: str) -> None:
        self.params = dict(params)
        self.seed = int(seed)
        self.scaling = scaling
        self.feature_names = list(FEATURE_NAMES)
        self.estimator: Pipeline | None = None

    @abstractmethod
    def _build(self) -> Any: ...

    def _pipeline(self) -> Pipeline:
        estimator = self._build()
        # Scaling is part of the persisted pipeline, so training and inference transformations are identical.
        return make_pipeline(StandardScaler(), estimator) if self.scaling == "standard" else make_pipeline(estimator)

    def fit(self, X: np.ndarray, y: np.ndarray | None = None) -> ModelAdapter:
        self._check(X)
        self.estimator = self._pipeline()
        if self.supervised:
            if y is None:
                raise ValueError(f"{self.algorithm} is supervised and requires labels")
            self.estimator.fit(X, y)
        else:
            self.estimator.fit(X)
        return self

    @abstractmethod
    def score(self, X: np.ndarray) -> np.ndarray:
        """Anomaly score: higher = more anomalous."""

    def default_predict(self, X: np.ndarray) -> np.ndarray:
        """The library's default decision (``predict() == -1`` / ``predict() == 1``) — reported for comparison only."""
        self._check(X)
        assert self.estimator is not None
        prediction = self.estimator.predict(X)
        return prediction == (1 if self.supervised else -1)

    def _check(self, X: np.ndarray) -> None:
        if X.ndim != 2 or X.shape[1] != len(FEATURE_NAMES):
            raise ValueError(f"Expected matrix with {len(FEATURE_NAMES)} ops-v1 features, got shape {X.shape}")

    def _decision(self, X: np.ndarray) -> np.ndarray:
        self._check(X)
        if self.estimator is None:
            raise RuntimeError("Model is not fitted")
        return np.asarray(self.estimator.decision_function(X), dtype=float)


class IsolationForestAdapter(ModelAdapter):
    algorithm = "isolation_forest"

    def _build(self) -> IsolationForest:
        return IsolationForest(random_state=self.seed, n_jobs=-1, **self.params)

    def score(self, X: np.ndarray) -> np.ndarray:
        # decision_function > 0 for inliers; negate so that larger = more abnormal.
        return -self._decision(X)


class LocalOutlierFactorAdapter(ModelAdapter):
    algorithm = "lof"

    def _build(self) -> LocalOutlierFactor:
        params = {**self.params, "novelty": True}  # novelty mode: fit on normal windows, score unseen windows
        return LocalOutlierFactor(n_jobs=-1, **params)

    def score(self, X: np.ndarray) -> np.ndarray:
        return -self._decision(X)


class OneClassSvmAdapter(ModelAdapter):
    algorithm = "ocsvm"

    def _build(self) -> OneClassSVM:
        return OneClassSVM(**self.params)

    def score(self, X: np.ndarray) -> np.ndarray:
        # Report §4.6: validation_scores = -model.decision_function(validation_windows)
        return -self._decision(X)


class RandomForestAdapter(ModelAdapter):
    """Supervised benchmark reference — never production eligible."""

    algorithm = "random_forest"
    supervised = True

    def _build(self) -> RandomForestClassifier:
        return RandomForestClassifier(random_state=self.seed, n_jobs=-1, **self.params)

    def score(self, X: np.ndarray) -> np.ndarray:
        self._check(X)
        if self.estimator is None:
            raise RuntimeError("Model is not fitted")
        return np.asarray(self.estimator.predict_proba(X)[:, 1], dtype=float)


ADAPTERS: dict[str, type[ModelAdapter]] = {
    a.algorithm: a for a in (IsolationForestAdapter, LocalOutlierFactorAdapter, OneClassSvmAdapter, RandomForestAdapter)
}

SHORT_NAMES = {"isolation_forest": "if", "lof": "lof", "ocsvm": "ocsvm", "random_forest": "rf"}
DISPLAY_NAMES = {
    "isolation_forest": "Isolation Forest",
    "lof": "Local Outlier Factor",
    "ocsvm": "One-Class SVM",
    "random_forest": "Random Forest (supervised ref.)",
}


def create_adapter(algorithm: str, params: dict[str, Any], seed: int, scaling: str) -> ModelAdapter:
    if algorithm not in ADAPTERS:
        raise ValueError(f"Unknown algorithm '{algorithm}'. Expected one of {sorted(ADAPTERS)}")
    return ADAPTERS[algorithm](params, seed, scaling)
