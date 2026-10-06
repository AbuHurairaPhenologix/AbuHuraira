"""Segment-level speaker clustering (lightweight diarization).

Each transcript segment is embedded with WavLM-Base-Plus-SV x-vectors; embeddings are clustered with
agglomerative clustering (cosine, average linkage). The number of speakers is chosen by silhouette
score, and a split is only accepted if every pair of cluster centroids is less similar than the
model's recommended same-speaker cosine threshold (0.86); otherwise the recording is single-speaker.

Limitations (documented, not hidden): one speaker per transcript segment, no overlapped-speech
handling, and speakers are anonymous ("Speaker 1", ...) - this is not identity recognition.
"""
import numpy as np
import torch
from sklearn.cluster import AgglomerativeClustering
from sklearn.metrics import silhouette_score

from app.ai import models

MIN_EMBED_S = 1.0
MAX_EMBED_S = 12.0
MIN_SILHOUETTE = 0.12
SAME_SPEAKER_SIM = 0.86  # recommended verification threshold for wavlm-base-plus-sv


@torch.inference_mode()
def embed_segments(y: np.ndarray, sr: int, segments: list[dict]) -> tuple[np.ndarray, list[int]]:
    """Return (embeddings, indices of segments long enough to embed)."""
    fe, model = models.speaker()
    vecs, used = [], []
    for i, s in enumerate(segments):
        if s["end"] - s["start"] < MIN_EMBED_S:
            continue
        clip = y[int(s["start"] * sr):int(min(s["end"], s["start"] + MAX_EMBED_S) * sr)]
        inputs = fe(clip, sampling_rate=sr, return_tensors="pt").to(models.DEVICE)
        emb = model(**inputs).embeddings
        vecs.append(torch.nn.functional.normalize(emb, dim=-1)[0].cpu().numpy())
        used.append(i)
    return (np.stack(vecs) if vecs else np.zeros((0, 512))), used


def cluster_embeddings(emb: np.ndarray, max_speakers: int = 6) -> tuple[np.ndarray, float | None]:
    """Return (labels, silhouette of the chosen k or None for a single cluster)."""
    n = len(emb)
    if n < 3:
        return np.zeros(n, dtype=int), None
    best_k, best_s, best_labels = 1, MIN_SILHOUETTE, np.zeros(n, dtype=int)
    for k in range(2, min(max_speakers, n - 1) + 1):
        labels = AgglomerativeClustering(n_clusters=k, metric="cosine", linkage="average").fit_predict(emb)
        if len(set(labels)) < 2:
            continue
        s = silhouette_score(emb, labels, metric="cosine")
        if s > best_s and _centroids_distinct(emb, labels):
            best_k, best_s, best_labels = k, s, labels
    return best_labels, (round(float(best_s), 3) if best_k > 1 else None)


def _centroids_distinct(emb: np.ndarray, labels: np.ndarray) -> bool:
    cents = np.stack([emb[labels == l].mean(axis=0) for l in np.unique(labels)])
    cents /= np.linalg.norm(cents, axis=1, keepdims=True)
    sims = cents @ cents.T
    return bool(np.all(sims[np.triu_indices(len(cents), 1)] < SAME_SPEAKER_SIM))


def assign_speakers(y: np.ndarray, sr: int, segments: list[dict]) -> tuple[list[str | None], dict]:
    """Return a speaker name per segment (None if unassignable) and clustering diagnostics."""
    if not segments:
        return [], {"method": "none", "speakers": 0}
    emb, used = embed_segments(y, sr, segments)
    if len(used) == 0:
        return [None] * len(segments), {"method": "none", "speakers": 0}
    labels, silhouette = cluster_embeddings(emb)

    # Name clusters by order of first appearance.
    order: dict[int, str] = {}
    for lab in labels:
        order.setdefault(int(lab), f"Speaker {len(order) + 1}")
    names: list[str | None] = [None] * len(segments)
    for seg_idx, lab in zip(used, labels):
        names[seg_idx] = order[int(lab)]

    # Segments too short to embed reliably inherit the previous speaker (or the next one at the start).
    for i, name in enumerate(names):
        if name is None:
            prev = next((names[j] for j in range(i - 1, -1, -1) if names[j]), None)
            names[i] = prev or next(n for n in names if n)
    return names, {"method": "WavLM x-vector + agglomerative clustering", "speakers": len(order),
                   "silhouette": silhouette, "embedded_segments": len(used)}


def speaker_statistics(segments: list[dict], names: list[str | None]) -> list[dict]:
    total = sum(s["end"] - s["start"] for s, n in zip(segments, names) if n)
    stats: dict[str, dict] = {}
    prev = None
    for s, n in zip(segments, names):
        if not n:
            continue
        st = stats.setdefault(n, {"speaker": n, "duration_s": 0.0, "turns": 0, "segments": 0})
        st["duration_s"] += s["end"] - s["start"]
        st["segments"] += 1
        if n != prev:
            st["turns"] += 1
        prev = n
    for st in stats.values():
        st["duration_s"] = round(st["duration_s"], 2)
        st["percentage"] = round(st["duration_s"] / total * 100, 1) if total else 0.0
    return sorted(stats.values(), key=lambda s: s["speaker"])


def merge_speaker_turns(segments: list[dict], names: list[str | None], max_gap_s: float = 1.5) -> list[dict]:
    turns: list[dict] = []
    for s, n in zip(segments, names):
        if not n:
            continue
        if turns and turns[-1]["speaker"] == n and s["start"] - turns[-1]["end"] <= max_gap_s:
            turns[-1]["end"] = s["end"]
        else:
            turns.append({"speaker": n, "start": s["start"], "end": s["end"]})
    return turns
