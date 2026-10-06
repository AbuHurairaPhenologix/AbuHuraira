"""TC-03 (no arbitrary model path) and registry integrity: hash verification, containment, immutability."""

import json
import shutil

import pytest

from app.models.adapters import create_adapter
from app.registry.registry import (
    InvalidModelVersion,
    ModelArtifactUnavailable,
    ModelNotRegistered,
    ModelRegistry,
    RegistryError,
)


@pytest.fixture()
def copy_registry(trained, tmp_path):
    from app.core.config import get_settings

    target = tmp_path / "models"
    shutil.copytree(get_settings().models_dir, target)
    return ModelRegistry(target)


@pytest.mark.parametrize("label", ["../../etc/passwd", "C:\\models\\x.joblib", "/tmp/model.joblib", "model.joblib/..", "UPPER", "", "a"])
def test_path_like_or_invalid_labels_are_rejected(copy_registry, label):
    with pytest.raises(InvalidModelVersion):
        copy_registry.load(label)


def test_unknown_version_is_not_loaded(copy_registry):
    with pytest.raises(ModelNotRegistered):
        copy_registry.load("ocsvm-ops-v1-does-not-exist")


def test_tampered_artifact_is_refused(copy_registry, versions):
    version = versions["ocsvm"]
    artifact = copy_registry.models_dir / version / "model.joblib"
    artifact.write_bytes(artifact.read_bytes() + b"tampered")
    with pytest.raises(ModelArtifactUnavailable, match="hash mismatch"):
        copy_registry.load(version)


def test_missing_artifact_is_reported(copy_registry, versions):
    version = versions["lof"]
    (copy_registry.models_dir / version / "model.joblib").unlink()
    assert copy_registry.get(version)["artifactExists"] is False
    with pytest.raises(ModelArtifactUnavailable, match="missing"):
        copy_registry.load(version)


def test_registry_entry_cannot_escape_the_model_store(copy_registry, versions):
    path = copy_registry.models_dir / "registry.json"
    data = json.loads(path.read_text())
    for m in data["models"]:
        if m["modelVersion"] == versions["isolation_forest"]:
            m["artifactPath"] = "../../outside.joblib"
    path.write_text(json.dumps(data))
    with pytest.raises(ModelArtifactUnavailable, match="escapes"):
        copy_registry.load(versions["isolation_forest"])


def test_versions_are_immutable(copy_registry, versions):
    meta = copy_registry.get(versions["ocsvm"])
    adapter = create_adapter("isolation_forest", {"n_estimators": 5}, 1, "none")
    with pytest.raises(RegistryError):
        copy_registry.register(adapter, {**meta})


def test_library_version_mismatch_is_refused(copy_registry, versions):
    path = copy_registry.models_dir / "registry.json"
    data = json.loads(path.read_text())
    for m in data["models"]:
        m["libraryVersions"]["scikit-learn"] = "0.0.1"
    path.write_text(json.dumps(data))
    with pytest.raises(ModelArtifactUnavailable, match="scikit-learn"):
        copy_registry.load(versions["ocsvm"])
