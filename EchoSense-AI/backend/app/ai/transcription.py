"""Speech transcription with Whisper (Transformers).

* Only speech regions are transcribed (energy-based activity intervals that the sound-event model
  scores as speech), packed into <=28 s chunks. This avoids Whisper's tendency to hallucinate text
  over silence or music and keeps timestamps anchored to the real timeline.
* Word-level timestamps (cross-attention DTW) are regrouped into segments at sentence ends and
  pauses, so a segment rarely spans two speakers - which the speaker-clustering step relies on.
"""
import re

import numpy as np
import torch

from app.ai import models
from app.audio.preprocessing import segment_intervals

CHUNK_S = 28.0
MAX_WORD_S = 1.5  # DTW sometimes stretches a word over a leading pause; clip it
SENTENCE_END = re.compile(r"[.?!]['\"]?$")


def pack_chunks(regions: list[tuple[float, float]], max_len: float = CHUNK_S, max_gap: float = 1.0) -> list[tuple[float, float]]:
    """Merge nearby regions into chunks no longer than `max_len` seconds."""
    chunks: list[list[float]] = []
    for s, e in segment_intervals(regions, max_len):
        if chunks and s - chunks[-1][1] <= max_gap and e - chunks[-1][0] <= max_len:
            chunks[-1][1] = e
        else:
            chunks.append([s, e])
    return [(a, b) for a, b in chunks]


def group_words(words: list[dict], max_gap: float = 0.45, max_words: int = 32) -> list[dict]:
    """words: [{"start", "end", "text"}] in absolute seconds -> sentence/pause-delimited segments."""
    segments, cur = [], []

    def flush():
        if cur:
            segments.append({"start": round(cur[0]["start"], 2), "end": round(cur[-1]["end"], 2),
                             "text": "".join(w["text"] for w in cur).strip()})
            cur.clear()

    for w in words:
        if cur and (w["start"] - cur[-1]["end"] > max_gap
                    or (SENTENCE_END.search(cur[-1]["text"].strip()) and len(cur) >= 3)
                    or len(cur) >= max_words):
            flush()
        cur.append(w)
    flush()
    return [s for s in segments if s["text"] and s["end"] - s["start"] >= 0.2]


@torch.inference_mode()
def detect_language(y: np.ndarray, sr: int, regions: list[tuple[float, float]]) -> str | None:
    if not regions:
        return None
    processor, model = models.asr()
    s, e = regions[0][0], min(regions[0][0] + 30.0, regions[-1][1])
    feats = processor(y[int(s * sr):int(e * sr)], sampling_rate=sr, return_tensors="pt").input_features.to(models.DEVICE)
    lang_ids = model.detect_language(feats)
    token = processor.tokenizer.convert_ids_to_tokens(int(lang_ids[0]))
    return token.strip("<|>") if token else None


@torch.inference_mode()
def transcribe(y: np.ndarray, sr: int, regions: list[tuple[float, float]], language: str | None = None) -> list[dict]:
    pipe = models.asr_pipeline()
    segments: list[dict] = []
    for c0, c1 in pack_chunks(regions):
        clip = y[int(c0 * sr):int(c1 * sr)]
        if clip.size < sr * 0.3:
            continue
        out = pipe({"raw": clip, "sampling_rate": sr}, return_timestamps="word",
                   generate_kwargs={"language": language, "task": "transcribe"})
        words = []
        for w in out.get("chunks", []):
            t0, t1 = w["timestamp"]
            if t0 is None or not w["text"].strip():
                continue
            t1 = t1 if t1 is not None else t0 + 0.3
            t0 = max(t0, t1 - MAX_WORD_S)
            words.append({"start": c0 + float(t0), "end": min(c0 + float(t1), c1), "text": w["text"]})
        segments.extend(group_words(words))  # grouped per chunk: a segment never crosses a chunk boundary
    return segments
