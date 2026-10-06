"""Configuration: the reproducibility manifest (config/benchmark.yaml) and runtime settings (environment)."""

from __future__ import annotations

import hashlib
import json
import os
from dataclasses import dataclass, field
from functools import lru_cache
from pathlib import Path
from typing import Any

import yaml


def project_root() -> Path:
    """Repository root (``H:\\ML`` locally, ``/app`` in the container), overridable with ``ML_PROJECT_ROOT``."""
    override = os.environ.get("ML_PROJECT_ROOT")
    if override:
        return Path(override).resolve()
    # app/core/config.py -> app -> ml-service -> src -> <root>
    candidate = Path(__file__).resolve().parents[4]
    if (candidate / "config").is_dir():
        return candidate
    return Path(__file__).resolve().parents[2]


def _path_from_env(name: str, default: Path) -> Path:
    value = os.environ.get(name)
    return Path(value).resolve() if value else default.resolve()


@dataclass(frozen=True)
class BenchmarkConfig:
    """Parsed ``benchmark.yaml`` with a stable content hash recorded in artifacts."""

    raw: dict[str, Any]
    path: Path
    sha256: str

    @property
    def seed(self) -> int:
        return int(self.raw["random_seed"])

    @property
    def schema_version(self) -> str:
        return str(self.raw["feature_schema_version"])

    @property
    def dataset(self) -> dict[str, Any]:
        return self.raw["dataset"]

    @property
    def splits(self) -> dict[str, dict[str, int]]:
        return self.raw["splits"]

    @property
    def models(self) -> dict[str, dict[str, Any]]:
        return self.raw["models"]

    @property
    def threshold(self) -> dict[str, Any]:
        return self.raw["threshold"]


def load_benchmark_config(path: Path | None = None) -> BenchmarkConfig:
    config_path = path or _path_from_env("ML_CONFIG_PATH", project_root() / "config" / "benchmark.yaml")
    text = config_path.read_text(encoding="utf-8")
    raw = yaml.safe_load(text)
    canonical = json.dumps(raw, sort_keys=True, separators=(",", ":"))
    return BenchmarkConfig(raw=raw, path=config_path, sha256=hashlib.sha256(canonical.encode()).hexdigest())


@dataclass(frozen=True)
class Settings:
    """Runtime settings for the scoring service, from environment variables."""

    models_dir: Path
    data_dir: Path
    evaluations_dir: Path
    config_path: Path
    token_signing_key: str
    token_issuer: str = "anomaly-backend"
    token_audience: str = "ml-scoring"
    preactivate_models: tuple[str, ...] = field(default_factory=tuple)
    max_batch_size: int = 500


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    root = project_root()
    preactivate = tuple(v.strip() for v in os.environ.get("ML_ACTIVE_MODELS", "").split(",") if v.strip())
    return Settings(
        models_dir=_path_from_env("ML_MODELS_DIR", root / "artifacts" / "models"),
        data_dir=_path_from_env("ML_DATA_DIR", root / "data" / "generated"),
        evaluations_dir=_path_from_env("ML_EVALUATIONS_DIR", root / "artifacts" / "evaluations"),
        config_path=_path_from_env("ML_CONFIG_PATH", root / "config" / "benchmark.yaml"),
        token_signing_key=os.environ.get("ML_SERVICE_TOKEN_SIGNING_KEY", ""),
        token_issuer=os.environ.get("ML_SERVICE_TOKEN_ISSUER", "anomaly-backend"),
        token_audience=os.environ.get("ML_SERVICE_TOKEN_AUDIENCE", "ml-scoring"),
        preactivate_models=preactivate,
        max_batch_size=int(os.environ.get("ML_MAX_BATCH_SIZE", "500")),
    )
