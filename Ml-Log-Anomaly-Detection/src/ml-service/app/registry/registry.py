"""Controlled model registry (report §3.12, §4.6).

* ``artifacts/models/registry.json`` lists registered versions; each lives in ``<models_dir>/<version>/``.
* A model is **never** loaded from a caller-supplied path: callers pass a version label, which must match a strict
  pattern and resolve to a registry entry whose artifact path stays inside ``models_dir``.
* The artifact's SHA-256 is verified before unpickling, and the scikit-learn version must match the one that
  produced it (joblib artifacts are only trusted from this controlled store).
* Existing versions are immutable; training only adds new version directories.
"""

from __future__ import annotations

import hashlib
import json
import logging
import os
import re
import tempfile
import threading
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import joblib

from app.core.versions import library_versions
from app.models.adapters import ModelAdapter

logger = logging.getLogger("ml.registry")

VERSION_PATTERN = re.compile(r"^[a-z0-9][a-z0-9.\-]{1,63}$")
REGISTRY_FILE = "registry.json"


class RegistryError(Exception):
    status_code = 500
    code = "registry_error"


class InvalidModelVersion(RegistryError):
    status_code = 422
    code = "invalid_model_version"


class ModelNotRegistered(RegistryError):
    status_code = 404
    code = "model_not_registered"


class ModelArtifactUnavailable(RegistryError):
    status_code = 503
    code = "model_artifact_unavailable"


@dataclass(frozen=True)
class LoadedModel:
    metadata: dict[str, Any]
    adapter: ModelAdapter

    @property
    def version(self) -> str:
        return self.metadata["modelVersion"]

    @property
    def threshold(self) -> float:
        return float(self.metadata["validationThreshold"])


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


class ModelRegistry:
    def __init__(self, models_dir: Path) -> None:
        self.models_dir = Path(models_dir).resolve()
        self._lock = threading.RLock()
        self._cache: dict[tuple[str, str], LoadedModel] = {}

    # ---- reading ---------------------------------------------------------------------------------------------
    def _read(self) -> dict[str, Any]:
        path = self.models_dir / REGISTRY_FILE
        if not path.exists():
            return {"registryVersion": 1, "models": []}
        return json.loads(path.read_text(encoding="utf-8"))

    def list(self) -> list[dict[str, Any]]:
        with self._lock:
            return [self._with_status(m) for m in self._read()["models"]]

    def get(self, version: str) -> dict[str, Any]:
        if not isinstance(version, str) or not VERSION_PATTERN.match(version):
            raise InvalidModelVersion("Model version label is invalid (only registered version labels are accepted).")
        with self._lock:
            for entry in self._read()["models"]:
                if entry["modelVersion"] == version:
                    return self._with_status(entry)
        raise ModelNotRegistered(f"Model version '{version}' is not registered.")

    def _artifact_path(self, entry: dict[str, Any]) -> Path:
        candidate = (self.models_dir / entry["artifactPath"]).resolve()
        # Containment check: the artifact must live inside the controlled models directory.
        if self.models_dir not in candidate.parents:
            raise ModelArtifactUnavailable("Registered artifact path escapes the model store.")
        return candidate

    def _with_status(self, entry: dict[str, Any]) -> dict[str, Any]:
        result = dict(entry)
        try:
            result["artifactExists"] = self._artifact_path(entry).is_file()
        except ModelArtifactUnavailable:
            result["artifactExists"] = False
        return result

    def load(self, version: str) -> LoadedModel:
        entry = self.get(version)
        path = self._artifact_path(entry)
        if not path.is_file():
            raise ModelArtifactUnavailable(f"Artifact for '{version}' is missing from the model store.")
        key = (version, entry["artifactSha256"])
        with self._lock:
            if key in self._cache:
                return self._cache[key]
            actual = sha256_file(path)
            if actual != entry["artifactSha256"]:
                raise ModelArtifactUnavailable(f"Artifact hash mismatch for '{version}'; refusing to load.")
            trained_with = entry.get("libraryVersions", {}).get("scikit-learn")
            running = library_versions()["scikit-learn"]
            if trained_with and trained_with != running:
                raise ModelArtifactUnavailable(
                    f"'{version}' was trained with scikit-learn {trained_with} but {running} is installed."
                )
            adapter = joblib.load(path)
            if not isinstance(adapter, ModelAdapter):
                raise ModelArtifactUnavailable("Artifact does not contain a model adapter.")
            loaded = LoadedModel(metadata=entry, adapter=adapter)
            self._cache[key] = loaded
            logger.info("Loaded model %s (%s)", version, entry["algorithm"])
            return loaded

    # ---- writing (training only) -----------------------------------------------------------------------------
    def register(self, adapter: ModelAdapter, metadata: dict[str, Any]) -> dict[str, Any]:
        version = metadata["modelVersion"]
        if not VERSION_PATTERN.match(version):
            raise InvalidModelVersion(version)
        with self._lock:
            registry = self._read()
            if any(m["modelVersion"] == version for m in registry["models"]):
                raise RegistryError(f"Model version '{version}' already exists; versions are immutable.")
            directory = self.models_dir / version
            directory.mkdir(parents=True, exist_ok=False)
            artifact = directory / "model.joblib"
            joblib.dump(adapter, artifact, compress=3)
            entry = {
                **metadata,
                "artifactPath": f"{version}/model.joblib",
                "artifactSha256": sha256_file(artifact),
            }
            (directory / "metadata.json").write_text(json.dumps(entry, indent=2), encoding="utf-8")
            registry["models"].append(entry)
            self._write(registry)
            return entry

    def set_recommended(self, version: str) -> None:
        """Marks one production-eligible version as the recommended default (used by bootstrap only)."""
        with self._lock:
            registry = self._read()
            for m in registry["models"]:
                m["recommended"] = m["modelVersion"] == version
            self._write(registry)

    def _write(self, registry: dict[str, Any]) -> None:
        self.models_dir.mkdir(parents=True, exist_ok=True)
        fd, tmp = tempfile.mkstemp(dir=self.models_dir, prefix=".registry-", suffix=".json")
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(registry, handle, indent=2)
        os.replace(tmp, self.models_dir / REGISTRY_FILE)
