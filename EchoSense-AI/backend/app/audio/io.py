"""Audio loading. The uploaded original is never modified; analysis works on an in-memory 16 kHz mono copy."""
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import soundfile as sf
import torch
import torchaudio.functional as AF

from app.config import TARGET_SR


@dataclass
class AudioInfo:
    duration_s: float
    sample_rate: int
    channels: int


def probe(path: str | Path) -> AudioInfo:
    """Read header information (libsndfile handles WAV, FLAC and MP3)."""
    info = sf.info(str(path))
    return AudioInfo(duration_s=float(info.frames) / info.samplerate, sample_rate=int(info.samplerate), channels=int(info.channels))


def load_original(path: str | Path) -> tuple[np.ndarray, int]:
    """Return (samples[channels, n], sample_rate) exactly as stored."""
    data, sr = sf.read(str(path), dtype="float32", always_2d=True)
    return data.T, int(sr)


def to_mono(samples: np.ndarray) -> np.ndarray:
    return samples.mean(axis=0) if samples.ndim == 2 and samples.shape[0] > 1 else samples.reshape(-1)


def resample(y: np.ndarray, orig_sr: int, target_sr: int = TARGET_SR) -> np.ndarray:
    if orig_sr == target_sr:
        return y.astype(np.float32, copy=True)
    out = AF.resample(torch.from_numpy(np.ascontiguousarray(y, dtype=np.float32)), orig_sr, target_sr)
    return out.numpy()


def peak_normalize(y: np.ndarray, peak_dbfs: float = -1.0) -> np.ndarray:
    peak = float(np.max(np.abs(y))) if y.size else 0.0
    if peak < 1e-9:
        return y.copy()
    return (y * (10 ** (peak_dbfs / 20) / peak)).astype(np.float32)


def load_for_analysis(path: str | Path) -> tuple[np.ndarray, AudioInfo]:
    """Resample -> mono -> peak-normalise. Returns the analysis signal at TARGET_SR plus original info."""
    samples, sr = load_original(path)
    info = AudioInfo(duration_s=samples.shape[1] / sr, sample_rate=sr, channels=samples.shape[0])
    y = peak_normalize(resample(to_mono(samples), sr))
    return y, info
