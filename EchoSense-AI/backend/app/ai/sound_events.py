"""Sound-event detection with the Audio Spectrogram Transformer fine-tuned on AudioSet (527 classes).

The recording is classified in fixed windows; AudioSet classes are grouped into the dashboard
categories below (category score = max sigmoid probability of its member classes). Consecutive
windows of the same category are merged into timed events.
"""
import numpy as np
import torch

from app.ai import models

CATEGORIES: dict[str, list[str]] = {
    "Speech": ["Speech", "Male speech, man speaking", "Female speech, woman speaking", "Conversation",
               "Narration, monologue", "Speech synthesizer"],
    "Music": ["Music", "Musical instrument", "Piano", "Keyboard (musical)", "Synthesizer", "Electronic music",
              "Ambient music", "Background music", "Guitar", "Orchestra"],
    "Applause": ["Applause", "Clapping", "Cheering"],
    "Laughter": ["Laughter", "Giggle", "Chuckle, chortle", "Belly laugh", "Snicker"],
    "Siren": ["Siren", "Civil defense siren", "Police car (siren)", "Ambulance (siren)",
              "Fire engine, fire truck (siren)", "Emergency vehicle"],
    "Car Horn": ["Vehicle horn, car horn, honking", "Air horn, truck horn", "Toot"],
    "Door": ["Door", "Slam", "Knock", "Doorbell", "Sliding door", "Cupboard open or close"],
    "Keyboard": ["Typing", "Computer keyboard", "Typewriter"],
    "Footsteps": ["Walk, footsteps", "Run", "Shuffle"],
    "Alarm": ["Alarm", "Alarm clock", "Smoke detector, smoke alarm", "Fire alarm", "Beep, bleep", "Buzzer"],
    "Traffic": ["Traffic noise, roadway noise", "Car passing by", "Vehicle", "Car"],
    "Impact": ["Smash, crash", "Glass", "Shatter", "Bang", "Breaking", "Explosion", "Thump, thud"],
}
THRESHOLDS = {"Speech": 0.35, "Music": 0.30}
DEFAULT_THRESHOLD = 0.20


def _category_index(id2label: dict[int, str]) -> dict[str, list[int]]:
    label2id = {v: int(k) for k, v in id2label.items()}
    return {cat: [label2id[l] for l in labels if l in label2id] for cat, labels in CATEGORIES.items()}


@torch.inference_mode()
def classify_windows(y: np.ndarray, sr: int, windows: list[tuple[float, float]], batch_size: int = 8) -> tuple[np.ndarray, list[str]]:
    """Return (probs[n_windows, n_classes], class_names)."""
    fe, model = models.sound_events()
    out = []
    for i in range(0, len(windows), batch_size):
        clips = [y[int(s * sr):int(e * sr)] for s, e in windows[i:i + batch_size]]
        inputs = fe(clips, sampling_rate=sr, return_tensors="pt").to(models.DEVICE)
        out.append(torch.sigmoid(model(**inputs).logits).cpu().numpy())
    names = [model.config.id2label[i] for i in range(model.config.num_labels)]
    return (np.concatenate(out) if out else np.zeros((0, len(names)))), names


def category_scores(probs: np.ndarray, names: list[str]) -> tuple[dict[str, np.ndarray], dict[str, np.ndarray]]:
    """Per category: max probability per window and the index of the class that produced it."""
    idx = _category_index(dict(enumerate(names)))
    scores, argmax = {}, {}
    for cat, cols in idx.items():
        if not cols:
            continue
        sub = probs[:, cols]
        scores[cat] = sub.max(axis=1)
        argmax[cat] = np.array(cols)[sub.argmax(axis=1)]
    return scores, argmax


def merge_events(windows: list[tuple[float, float]], scores: dict[str, np.ndarray], argmax: dict[str, np.ndarray],
                 names: list[str], max_gap_s: float = 0.0) -> list[dict]:
    events = []
    for cat, sc in scores.items():
        thr = THRESHOLDS.get(cat, DEFAULT_THRESHOLD)
        run: list[int] = []
        for i, val in enumerate(sc):
            if val >= thr and (not run or windows[i][0] - windows[run[-1]][1] <= max_gap_s + 1e-6):
                run.append(i)
                continue
            if run:
                events.append(_event(cat, run, windows, sc, argmax[cat], names))
            run = [i] if val >= thr else []
        if run:
            events.append(_event(cat, run, windows, sc, argmax[cat], names))
    return sorted(events, key=lambda e: (e["start"], e["label"]))


def _event(cat: str, run: list[int], windows, sc, am, names) -> dict:
    best = run[int(np.argmax(sc[run]))]
    return {"label": cat, "start": windows[run[0]][0], "end": windows[run[-1]][1],
            "confidence": round(float(sc[run].max()), 3), "source_label": names[int(am[best])]}


def silence_events(silences: list[tuple[float, float]], min_len_s: float = 2.0) -> list[dict]:
    """Silence comes from energy-based detection, not the classifier."""
    return [{"label": "Silence", "start": s, "end": e, "confidence": 1.0, "source_label": "energy below -35 dB re peak"}
            for s, e in silences if e - s >= min_len_s]


def speech_regions(active: list[tuple[float, float]], windows: list[tuple[float, float]], speech_scores: np.ndarray,
                   min_prob: float = 0.3) -> list[tuple[float, float]]:
    """Activity intervals whose overlap-weighted classifier speech probability is at least `min_prob`."""
    keep = []
    for s, e in active:
        w = np.array([max(0.0, min(b, e) - max(a, s)) for a, b in windows])
        if w.sum() > 0 and float((w * speech_scores).sum() / w.sum()) >= min_prob:
            keep.append((s, e))
    return keep


def speech_probability(windows: list[tuple[float, float]], speech_scores: np.ndarray, start: float, end: float) -> float:
    """Max classifier speech probability over windows overlapping [start, end]."""
    vals = [speech_scores[i] for i, (a, b) in enumerate(windows) if b > start and a < end]
    return float(max(vals)) if vals else 0.0
