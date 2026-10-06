"""Window-level acoustic features used by anomaly detection and predictive analytics.

Frame-level descriptors are computed once (n_fft=1024, hop=256 @16 kHz = 16 ms) and then aggregated
into overlapping analysis windows (default 1.0 s window, 0.5 s hop).
"""
import librosa
import numpy as np
import pandas as pd

from app.audio.preprocessing import fixed_windows, mfcc

N_FFT, HOP = 1024, 256
N_MFCC = 13

FEATURE_COLUMNS = (
    ["rms_db", "rms_db_std", "zcr", "spectral_centroid", "spectral_rolloff", "spectral_bandwidth",
     "spectral_flatness", "onset_strength", "onset_peak"]
    + [f"mfcc_{i + 1}" for i in range(N_MFCC)]
)


def frame_features(y: np.ndarray, sr: int) -> dict[str, np.ndarray]:
    rms = librosa.feature.rms(y=y, frame_length=N_FFT, hop_length=HOP)[0]
    return {
        "rms_db": librosa.amplitude_to_db(rms + 1e-10, ref=1.0),
        "zcr": librosa.feature.zero_crossing_rate(y, frame_length=N_FFT, hop_length=HOP)[0],
        "spectral_centroid": librosa.feature.spectral_centroid(y=y, sr=sr, n_fft=N_FFT, hop_length=HOP)[0],
        "spectral_rolloff": librosa.feature.spectral_rolloff(y=y, sr=sr, n_fft=N_FFT, hop_length=HOP, roll_percent=0.85)[0],
        "spectral_bandwidth": librosa.feature.spectral_bandwidth(y=y, sr=sr, n_fft=N_FFT, hop_length=HOP)[0],
        "spectral_flatness": librosa.feature.spectral_flatness(y=y, n_fft=N_FFT, hop_length=HOP)[0],
        "onset_strength": librosa.onset.onset_strength(y=y, sr=sr, hop_length=HOP),
        "mfcc": mfcc(y, sr, N_MFCC, HOP),
    }


def window_features(y: np.ndarray, sr: int, window_s: float = 1.0, hop_s: float = 0.5) -> pd.DataFrame:
    """One row per analysis window: start, end, time (centre) and FEATURE_COLUMNS."""
    duration = y.size / sr
    windows = fixed_windows(duration, window_s, hop_s)
    if not windows:
        return pd.DataFrame(columns=["start", "end", "time", *FEATURE_COLUMNS])
    ff = frame_features(y, sr)
    n_frames = ff["rms_db"].shape[0]
    rows = []
    for start, end in windows:
        a = min(int(start * sr / HOP), n_frames - 1)
        b = max(a + 1, min(int(np.ceil(end * sr / HOP)), n_frames))
        sl = slice(a, b)
        row = {
            "start": start, "end": end, "time": round((start + end) / 2, 3),
            "rms_db": float(ff["rms_db"][sl].mean()),
            "rms_db_std": float(ff["rms_db"][sl].std()),
            "zcr": float(ff["zcr"][sl].mean()),
            "spectral_centroid": float(ff["spectral_centroid"][sl].mean()),
            "spectral_rolloff": float(ff["spectral_rolloff"][sl].mean()),
            "spectral_bandwidth": float(ff["spectral_bandwidth"][sl].mean()),
            "spectral_flatness": float(ff["spectral_flatness"][sl].mean()),
            "onset_strength": float(ff["onset_strength"][sl].mean()),
            "onset_peak": float(ff["onset_strength"][sl].max()),
        }
        row.update({f"mfcc_{i + 1}": float(v) for i, v in enumerate(ff["mfcc"][:, sl].mean(axis=1))})
        rows.append(row)
    return pd.DataFrame(rows)
