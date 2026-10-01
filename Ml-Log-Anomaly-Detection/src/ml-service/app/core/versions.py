"""Library/runtime versions recorded with every model artifact and evaluation run (reproducibility)."""

from __future__ import annotations

import platform
from importlib.metadata import PackageNotFoundError, version

TRACKED_PACKAGES = ("scikit-learn", "numpy", "pandas", "scipy", "joblib", "matplotlib", "fastapi", "pydantic")


def library_versions() -> dict[str, str]:
    result = {"python": platform.python_version()}
    for name in TRACKED_PACKAGES:
        try:
            result[name] = version(name)
        except PackageNotFoundError:  # pragma: no cover - optional package
            result[name] = "not-installed"
    return result
