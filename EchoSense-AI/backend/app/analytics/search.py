"""Local semantic search: cosine similarity over L2-normalised sentence embeddings kept in a .npy file.

The index holds every transcript segment, a short context passage (segment + neighbours) and one
natural-language description per detected sound event, so queries like "where did the audience
applaud?" can match non-speech events as well as what was said.
"""
from dataclasses import dataclass

import numpy as np

EVENT_DESCRIPTIONS = {
    "Speech": "people speaking, talking, conversation",
    "Music": "music playing, a song or melody, musical instruments",
    "Applause": "applause, the audience clapped and applauded, clapping hands",
    "Laughter": "laughter, people laughing",
    "Siren": "a siren wailing, emergency vehicle siren",
    "Car Horn": "a car horn honking",
    "Door": "a door opening, closing or slamming, knocking",
    "Keyboard": "typing on a computer keyboard, keystrokes",
    "Footsteps": "footsteps, someone walking",
    "Alarm": "an alarm sounding, beeping alert, buzzer",
    "Traffic": "traffic noise, cars and vehicles passing",
    "Impact": "a crash, bang, glass breaking, sudden loud impact",
    "Silence": "silence, a quiet pause with no sound",
}


@dataclass
class IndexItem:
    kind: str  # "transcript" | "event"
    start: float
    end: float
    text: str
    ref_id: int | None = None
    speaker: str | None = None
    display: str | None = None  # text shown to the user (context passages show their own segment)


def build_documents(segments: list[dict], events: list[dict]) -> list[IndexItem]:
    """segments: dicts with id/start/end/text/speaker; events: dicts with id/start/end/label."""
    docs: list[IndexItem] = []
    for i, s in enumerate(segments):
        docs.append(IndexItem("transcript", s["start"], s["end"], s["text"].strip(), s.get("id"), s.get("speaker")))
        if 0 < i < len(segments) - 1:
            ctx = " ".join(segments[j]["text"].strip() for j in (i - 1, i, i + 1))
            docs.append(IndexItem("context", s["start"], s["end"], ctx, s.get("id"), s.get("speaker"), s["text"].strip()))
    for e in events:
        if e["label"] in ("Speech", "Silence"):
            continue
        desc = EVENT_DESCRIPTIONS.get(e["label"], e["label"].lower())
        docs.append(IndexItem("event", e["start"], e["end"], f"{e['label']}: {desc}", e.get("id")))
    return docs


def cosine_top_k(query_vec: np.ndarray, matrix: np.ndarray, k: int) -> list[tuple[int, float]]:
    if matrix.size == 0:
        return []
    q = query_vec / (np.linalg.norm(query_vec) + 1e-12)
    m = matrix / (np.linalg.norm(matrix, axis=1, keepdims=True) + 1e-12)
    sims = m @ q
    order = np.argsort(-sims)[:k]
    return [(int(i), float(sims[i])) for i in order]


def search(query_vec: np.ndarray, matrix: np.ndarray, docs: list[IndexItem], top_k: int = 5) -> list[dict]:
    """Rank documents and de-duplicate by time span (a context passage and its segment count once)."""
    results, seen = [], set()
    for idx, score in cosine_top_k(query_vec, matrix, k=len(docs)):
        d = docs[idx]
        key = (round(d.start, 1), d.kind == "event")
        if key in seen:
            continue
        seen.add(key)
        results.append({
            "type": "event" if d.kind == "event" else "transcript",
            "start": round(d.start, 2), "end": round(d.end, 2),
            "text": d.display or d.text, "speaker": d.speaker, "score": round(score, 4),
        })
        if len(results) >= top_k:
            break
    return results
