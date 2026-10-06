import os
import sys
import tempfile
from pathlib import Path

import numpy as np
import pytest

# Isolate all test data (SQLite DB, uploads, artifacts) from the real backend/data directory.
os.environ.setdefault("ECHOSENSE_DATA_DIR", tempfile.mkdtemp(prefix="echosense-tests-"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "backend"))

SR = 16_000


def tone(freq: float, seconds: float, amp: float = 0.5, sr: int = SR) -> np.ndarray:
    t = np.arange(int(seconds * sr)) / sr
    return (amp * np.sin(2 * np.pi * freq * t)).astype(np.float32)


def silence(seconds: float, sr: int = SR) -> np.ndarray:
    return np.zeros(int(seconds * sr), dtype=np.float32)


@pytest.fixture
def rng():
    return np.random.default_rng(0)
