"""Silence detection, segmentation and display artifacts (waveform peaks, Mel spectrogram)."""
import base64

import librosa
import numpy as np


def detect_activity(y: np.ndarray, sr: int, top_db: float = 35.0, min_silence_s: float = 0.4) -> list[tuple[float, float]]:
    """Non-silent intervals in seconds. Gaps shorter than `min_silence_s` are bridged."""
    if y.size == 0:
        return []
    intervals = librosa.effects.split(y, top_db=top_db, frame_length=1024, hop_length=256)
    merged: list[list[float]] = []
    for s, e in intervals:
        start, end = s / sr, e / sr
        if merged and start - merged[-1][1] < min_silence_s:
            merged[-1][1] = end
        else:
            merged.append([start, end])
    return [(round(a, 3), round(b, 3)) for a, b in merged]


def silence_regions(active: list[tuple[float, float]], duration: float, min_len_s: float = 1.0) -> list[tuple[float, float]]:
    """Complement of the active intervals, keeping only gaps of at least `min_len_s`."""
    gaps, cursor = [], 0.0
    for s, e in active:
        if s - cursor >= min_len_s:
            gaps.append((round(cursor, 3), round(s, 3)))
        cursor = max(cursor, e)
    if duration - cursor >= min_len_s:
        gaps.append((round(cursor, 3), round(duration, 3)))
    return gaps


def segment_intervals(intervals: list[tuple[float, float]], max_len_s: float) -> list[tuple[float, float]]:
    """Split intervals longer than `max_len_s` into near-equal consecutive pieces."""
    out = []
    for s, e in intervals:
        n = max(1, int(np.ceil((e - s) / max_len_s - 1e-9)))
        step = (e - s) / n
        out.extend((round(s + i * step, 3), round(s + (i + 1) * step, 3)) for i in range(n))
    return out


def fixed_windows(duration: float, window_s: float, hop_s: float) -> list[tuple[float, float]]:
    """Sliding windows covering the whole signal; the last window is clipped to the duration."""
    if duration <= 0:
        return []
    starts = np.arange(0.0, max(duration - window_s, 0.0) + 1e-9, hop_s)
    wins = [(round(float(s), 3), round(float(min(s + window_s, duration)), 3)) for s in starts]
    if wins[-1][1] < duration - 1e-6:
        wins.append((round(max(duration - window_s, 0.0), 3), round(duration, 3)))
    return wins


def waveform_peaks(y: np.ndarray, n_buckets: int = 2400) -> list[float]:
    """Max absolute amplitude per bucket - enough to draw a waveform without shipping the signal."""
    if y.size == 0:
        return []
    n_buckets = min(n_buckets, y.size)
    edges = np.linspace(0, y.size, n_buckets + 1, dtype=int)
    return [round(float(np.max(np.abs(y[a:b]))), 4) if b > a else 0.0 for a, b in zip(edges[:-1], edges[1:])]


def mel_spectrogram(y: np.ndarray, sr: int, n_mels: int = 96, max_frames: int = 1600) -> dict:
    """Log-Mel spectrogram quantised to uint8 and base64-encoded (row-major, low frequencies first)."""
    hop = max(256, int(np.ceil(y.size / max_frames)))
    S = librosa.feature.melspectrogram(y=y, sr=sr, n_fft=1024, hop_length=hop, n_mels=n_mels, fmax=sr // 2)
    S_db = librosa.power_to_db(S, ref=np.max, top_db=80.0)  # range [-80, 0]
    q = np.clip((S_db + 80.0) / 80.0 * 255.0, 0, 255).astype(np.uint8)
    return {
        "n_mels": int(q.shape[0]),
        "n_frames": int(q.shape[1]),
        "hop_s": hop / sr,
        "fmax": sr // 2,
        "data": base64.b64encode(np.ascontiguousarray(q).tobytes()).decode("ascii"),
    }


def mfcc(y: np.ndarray, sr: int, n_mfcc: int = 13, hop_length: int = 256) -> np.ndarray:
    return librosa.feature.mfcc(y=y, sr=sr, n_mfcc=n_mfcc, n_fft=1024, hop_length=hop_length)
