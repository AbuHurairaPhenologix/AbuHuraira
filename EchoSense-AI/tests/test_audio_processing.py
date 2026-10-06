"""Audio preprocessing, segmentation and feature extraction."""
import base64
import hashlib

import numpy as np
import soundfile as sf

from app.audio import features, io, preprocessing
from conftest import SR, silence, tone


def test_activity_and_silence_detection_find_the_gap():
    y = np.concatenate([tone(440, 2.0), silence(1.5), tone(440, 2.0)])
    active = preprocessing.detect_activity(y, SR)
    assert len(active) == 2
    assert abs(active[0][0] - 0.0) < 0.05 and abs(active[0][1] - 2.0) < 0.1
    assert abs(active[1][0] - 3.5) < 0.1 and abs(active[1][1] - 5.5) < 0.05
    gaps = preprocessing.silence_regions(active, duration=5.5)
    assert len(gaps) == 1 and abs(gaps[0][0] - 2.0) < 0.1 and abs(gaps[0][1] - 3.5) < 0.1


def test_short_pauses_are_bridged():
    y = np.concatenate([tone(300, 1.0), silence(0.2), tone(300, 1.0)])
    assert len(preprocessing.detect_activity(y, SR, min_silence_s=0.4)) == 1


def test_segment_intervals_respects_max_length_and_covers_input():
    pieces = preprocessing.segment_intervals([(0.0, 65.0), (70.0, 75.0)], max_len_s=28.0)
    assert all(e - s <= 28.0 + 1e-6 for s, e in pieces)
    assert pieces[0][0] == 0.0 and pieces[2][1] == 65.0 and pieces[-1] == (70.0, 75.0)
    assert len(pieces) == 4


def test_fixed_windows_cover_the_whole_signal():
    wins = preprocessing.fixed_windows(10.3, 1.0, 0.5)
    assert wins[0] == (0.0, 1.0)
    assert wins[-1][1] == 10.3
    assert all(b - a <= 1.0 + 1e-9 for a, b in wins)


def test_waveform_peaks_and_mel_spectrogram_shapes():
    y = np.concatenate([tone(440, 1.0, 0.2), tone(440, 1.0, 0.8)])
    peaks = preprocessing.waveform_peaks(y, n_buckets=100)
    assert len(peaks) == 100 and max(peaks) <= 1.0
    assert np.mean(peaks[50:]) > 3 * np.mean(peaks[:50])
    spec = preprocessing.mel_spectrogram(y, SR, n_mels=64, max_frames=200)
    raw = base64.b64decode(spec["data"])
    assert len(raw) == spec["n_mels"] * spec["n_frames"] and spec["n_mels"] == 64 and spec["n_frames"] <= 200


def test_load_for_analysis_resamples_mono_normalises_and_keeps_original(tmp_path):
    t = np.arange(44_100) / 44_100
    stereo = np.stack([0.3 * np.sin(2 * np.pi * 220 * t), 0.1 * np.sin(2 * np.pi * 220 * t)], axis=1)
    path = tmp_path / "stereo.wav"
    sf.write(path, stereo, 44_100)
    before = hashlib.sha256(path.read_bytes()).hexdigest()

    y, info = io.load_for_analysis(path)
    assert info.sample_rate == 44_100 and info.channels == 2 and abs(info.duration_s - 1.0) < 1e-3
    assert y.ndim == 1 and abs(len(y) - SR) <= 2
    assert abs(np.max(np.abs(y)) - 10 ** (-1 / 20)) < 1e-3  # peak-normalised to -1 dBFS
    assert hashlib.sha256(path.read_bytes()).hexdigest() == before  # original untouched


def test_window_features_respond_to_loudness_and_brightness():
    y = np.concatenate([tone(300, 2.0, 0.05), tone(3000, 2.0, 0.6)])
    df = features.window_features(y, SR, 1.0, 0.5)
    assert list(df.columns[3:]) == features.FEATURE_COLUMNS
    assert len(df) == 7
    quiet, loud = df.iloc[0], df.iloc[-1]
    assert loud["rms_db"] > quiet["rms_db"] + 15
    assert loud["spectral_centroid"] > quiet["spectral_centroid"] * 3
    assert loud["zcr"] > quiet["zcr"]
    assert df.notna().all().all()
