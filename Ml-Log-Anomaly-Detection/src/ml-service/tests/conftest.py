"""Shared fixtures: a reduced benchmark config, a temporary registry trained once per session, and a test client."""

from __future__ import annotations

import time
import uuid
from pathlib import Path

import jwt
import pytest
import yaml

SIGNING_KEY = "test-signing-key-for-ml-service-0123456789abcdef"
REPO_ROOT = Path(__file__).resolve().parents[3]


@pytest.fixture(scope="session")
def workdir(tmp_path_factory: pytest.TempPathFactory) -> Path:
    return tmp_path_factory.mktemp("ml")


@pytest.fixture(scope="session", autouse=True)
def environment(workdir: Path) -> dict[str, Path]:
    """Point the service at temporary storage and a small (fast) copy of benchmark.yaml."""
    mp = pytest.MonkeyPatch()
    raw = yaml.safe_load((REPO_ROOT / "config" / "benchmark.yaml").read_text(encoding="utf-8"))
    raw["dataset"]["n_normal"] = 4000
    raw["dataset"]["n_anomaly"] = 240
    raw["dataset"]["output_dir"] = str(workdir / "data")
    raw["splits"] = {
        "train": {"normal": 2000, "anomaly": 120},
        "validation": {"normal": 1000, "anomaly": 60},
        "test": {"normal": 1000, "anomaly": 60},
    }
    raw["models"]["ocsvm"]["training_subset"] = 1000
    raw["models"]["isolation_forest"]["params"]["n_estimators"] = 100
    raw["models"]["random_forest"]["params"]["n_estimators"] = 100
    config_path = workdir / "benchmark.test.yaml"
    config_path.write_text(yaml.safe_dump(raw), encoding="utf-8")

    paths = {"models": workdir / "models", "evaluations": workdir / "evaluations", "config": config_path}
    mp.setenv("ML_CONFIG_PATH", str(config_path))
    mp.setenv("ML_MODELS_DIR", str(paths["models"]))
    mp.setenv("ML_EVALUATIONS_DIR", str(paths["evaluations"]))
    mp.setenv("ML_SERVICE_TOKEN_SIGNING_KEY", SIGNING_KEY)
    mp.delenv("ML_ACTIVE_MODELS", raising=False)

    from app.core.config import get_settings

    get_settings.cache_clear()
    yield paths
    get_settings.cache_clear()
    mp.undo()


@pytest.fixture(scope="session")
def config(environment):
    from app.core.config import load_benchmark_config

    return load_benchmark_config()


@pytest.fixture(scope="session")
def dataset(config):
    from app.datasets.io import load_or_generate

    frame, path = load_or_generate(config)
    return frame, path


@pytest.fixture(scope="session")
def trained(config, dataset, environment):
    """Train all four models once for the session (small data → seconds)."""
    import hashlib

    from app.registry.registry import ModelRegistry
    from app.training.trainer import ALL_ALGORITHMS, train_benchmark

    frame, path = dataset
    registry = ModelRegistry(environment["models"])
    result = train_benchmark(config, frame, registry, list(ALL_ALGORITHMS), hashlib.sha256(path.read_bytes()).hexdigest(), requested_by="pytest")
    return result


@pytest.fixture(scope="session")
def versions(trained) -> dict[str, str]:
    from app.registry.registry import ModelRegistry
    from app.core.config import get_settings

    registry = ModelRegistry(get_settings().models_dir)
    return {m["algorithm"]: m["modelVersion"] for m in registry.list() if m["trainingRunId"] == trained.run_id}


def make_token(scope: str, *, audience: str = "ml-scoring", issuer: str = "anomaly-backend", key: str = SIGNING_KEY, expires_in: int = 300) -> str:
    now = int(time.time())
    return jwt.encode(
        {"iss": issuer, "aud": audience, "sub": "anomaly-backend", "scope": scope, "iat": now, "nbf": now - 5, "exp": now + expires_in, "jti": uuid.uuid4().hex},
        key,
        algorithm="HS256",
    )


def auth(scope: str, **kwargs) -> dict[str, str]:
    return {"Authorization": f"Bearer {make_token(scope, **kwargs)}"}


@pytest.fixture()
def client(trained):
    from fastapi.testclient import TestClient

    from app.main import create_app

    with TestClient(create_app()) as c:
        yield c


NORMAL_FEATURES = {
    # Appendix "API Contract" example window.
    "request_count": 142,
    "error_rate": 0.031,
    "avg_duration_ms": 248.4,
    "p95_duration_ms": 681.7,
    "auth_failure_rate": 0.017,
    "dependency_failure_count": 1,
    "retry_count": 0,
    "endpoint_entropy": 2.31,
}
