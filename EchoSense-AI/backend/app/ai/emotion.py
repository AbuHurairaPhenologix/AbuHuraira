"""Speech emotion estimates + measured prosody.

* Emotion: superb/wav2vec2-base-superb-er (IEMOCAP, 4 classes: neutral, happy, angry, sad).
  These are model estimates of vocal tone, not psychological facts.
* Prosody: measured pitch (YIN), pitch variability, loudness and speaking rate per segment. A relative
  arousal descriptor (Calm / Moderate / Animated) compares each segment with the recording's own
  distribution - it is a heuristic, labelled as such.
"""
import librosa
import numpy as np
import torch

from app.ai import models

LABELS = {"neu": "Neutral", "hap": "Happy", "ang": "Angry", "sad": "Sad"}
MIN_SEG_S = 0.8


@torch.inference_mode()
def classify(y: np.ndarray, sr: int, segments: list[dict]) -> list[dict | None]:
    fe, model = models.emotion()
    id2label = model.config.id2label
    out: list[dict | None] = []
    for s in segments:
        if s["end"] - s["start"] < MIN_SEG_S:
            out.append(None)
            continue
        clip = y[int(s["start"] * sr):int(min(s["end"], s["start"] + 15.0) * sr)]
        inputs = fe(clip, sampling_rate=sr, return_tensors="pt").to(models.DEVICE)
        probs = torch.softmax(model(**inputs).logits, dim=-1)[0].cpu().numpy()
        scores = {LABELS.get(id2label[i], id2label[i]): round(float(p), 4) for i, p in enumerate(probs)}
        best = max(scores, key=scores.get)
        out.append({"emotion": best, "confidence": scores[best], "scores": scores})
    return out


def prosody(y: np.ndarray, sr: int, segment: dict) -> dict:
    clip = y[int(segment["start"] * sr):int(segment["end"] * sr)]
    dur = max(segment["end"] - segment["start"], 1e-6)
    if clip.size < 2048:
        return {"pitch_hz": None, "pitch_var_st": None, "loudness_db": None, "words_per_s": None}
    f0 = librosa.yin(clip, fmin=65, fmax=400, sr=sr, frame_length=1024, hop_length=256)
    rms = librosa.feature.rms(y=clip, frame_length=1024, hop_length=256)[0]
    voiced = f0[(rms[:len(f0)] > np.percentile(rms, 40)) & (f0 > 66) & (f0 < 399)]
    pitch = float(np.median(voiced)) if voiced.size >= 5 else None
    pitch_var = float(np.std(12 * np.log2(voiced / np.median(voiced)))) if voiced.size >= 5 else None
    return {
        "pitch_hz": round(pitch, 1) if pitch else None,
        "pitch_var_st": round(pitch_var, 2) if pitch_var is not None else None,
        "loudness_db": round(float(20 * np.log10(np.sqrt(np.mean(clip ** 2)) + 1e-10)), 1),
        "words_per_s": round(len(segment.get("text", "").split()) / dur, 2),
    }


def arousal_labels(prosodies: list[dict]) -> list[str | None]:
    """Relative arousal from z-scored loudness, pitch variability and speaking rate."""
    keys = ["loudness_db", "pitch_var_st", "words_per_s"]
    mat = np.array([[p.get(k) if p.get(k) is not None else np.nan for k in keys] for p in prosodies], dtype=float)
    if len(mat) < 3:
        return ["Moderate"] * len(prosodies)
    mu, sd = np.nanmean(mat, axis=0), np.nanstd(mat, axis=0) + 1e-6
    z = np.nanmean((mat - mu) / sd, axis=1)
    return [None if np.isnan(v) else ("Animated" if v > 0.6 else "Calm" if v < -0.6 else "Moderate") for v in z]
