"""Grounded recording summary.

Nothing here is free-form generation: every sentence is assembled from measured analysis output
(topics, speaker statistics, events, anomalies, activity intervals) or quoted from the transcript.

* Topics      - KeyBERT-style: candidate 1-2-word phrases (see candidate_phrases) ranked by embedding similarity to the
                whole-transcript embedding, diversified with Maximal Marginal Relevance (MMR).
* Highlights  - extractive: the transcript segments closest to the transcript centroid, in time order.
"""
import re
from collections import Counter
from typing import Callable

import numpy as np
from sklearn.feature_extraction.text import ENGLISH_STOP_WORDS

STOP = set(ENGLISH_STOP_WORDS) | {"let", "lets", "okay", "yeah", "wow", "thank", "thanks", "really", "just", "like",
                                   "think", "know", "want", "good", "exactly", "today", "going", "say", "said"}

Embed = Callable[[list[str]], np.ndarray]


def fmt_ts(seconds: float) -> str:
    seconds = max(0, int(round(seconds)))
    h, rem = divmod(seconds, 3600)
    m, s = divmod(rem, 60)
    return f"{h}:{m:02d}:{s:02d}" if h else f"{m:02d}:{s:02d}"


def candidate_phrases(texts: list[str], max_n: int = 2) -> list[str]:
    """1-2-word phrases formed inside each segment from the raw word sequence, so no fake phrases appear
    across removed stopwords. Phrases contain no stopwords; single words must occur at least twice."""
    counts: Counter = Counter()
    for text in texts:
        tokens = re.findall(r"[a-z][a-z']+", text.lower())
        for n in range(1, max_n + 1):
            for i in range(len(tokens) - n + 1):
                gram = tokens[i:i + n]
                if any(t in STOP or "'" in t for t in gram):
                    continue
                counts[" ".join(gram)] += 1
    return [g for g, c in counts.most_common() if " " in g or c >= 2]


def extract_topics(texts: list[str], embed: Embed, top_n: int = 5, diversity: float = 0.4) -> list[str]:
    doc = " ".join(t.strip() for t in texts if t.strip())
    if len(doc.split()) < 5:
        return []
    cands = candidate_phrases(texts)[:400]
    if not cands:
        return []
    doc_vec = embed([doc])[0]
    cand_vecs = embed(cands)
    sims = cand_vecs @ doc_vec
    chosen = [int(np.argmax(sims))]
    while len(chosen) < min(top_n, len(cands)):
        rest = [i for i in range(len(cands)) if i not in chosen]
        redundancy = np.max(cand_vecs[rest] @ cand_vecs[chosen].T, axis=1)
        mmr = (1 - diversity) * sims[rest] - diversity * redundancy
        chosen.append(rest[int(np.argmax(mmr))])
    return [_title(cands[i]) for i in chosen]


def _title(phrase: str) -> str:
    return " ".join(w.upper() if w in ("ai", "ml", "api") else w.capitalize() for w in phrase.split())


def extract_highlights(segments: list[dict], vectors: np.ndarray, n: int = 3) -> list[dict]:
    if len(segments) == 0 or vectors.size == 0:
        return []
    centroid = vectors.mean(axis=0)
    centroid /= np.linalg.norm(centroid) + 1e-12
    sims = vectors @ centroid
    words = np.array([len(s["text"].split()) for s in segments])
    sims = np.where(words >= 6, sims, -1)  # prefer complete sentences
    idx = sorted(np.argsort(-sims)[:n].tolist())
    return [{"start": segments[i]["start"], "end": segments[i]["end"], "speaker": segments[i].get("speaker"),
             "text": segments[i]["text"].strip()} for i in idx if sims[i] > -1]


def _join(items: list[str]) -> str:
    if len(items) <= 1:
        return "".join(items)
    return ", ".join(items[:-1]) + " and " + items[-1]


def build_summary(*, duration: float, language: str | None, topics: list[str], highlights: list[dict],
                  speaker_stats: list[dict], events: list[dict], anomalies: list[dict], emotions: list[dict],
                  intervals: list[dict]) -> dict:
    lines: list[str] = []
    if topics:
        lines.append(f"The discussion focused on {_join(topics[:3])}.")
    elif not speaker_stats:
        lines.append("No intelligible speech was transcribed in this recording.")

    if speaker_stats:
        n = len(speaker_stats)
        lead = max(speaker_stats, key=lambda s: s["duration_s"])
        lines.append(f"{n} speaker{'s' if n != 1 else ''} {'were' if n != 1 else 'was'} identified; "
                     f"{lead['speaker']} spoke the most ({lead['percentage']:.0f}% of speaking time).")

    if intervals:
        best = _most_active_span(intervals)
        if best:
            lines.append(f"The most active discussion occurred between {fmt_ts(best[0])} and {fmt_ts(best[1])}.")

    first_seen: dict[str, float] = {}
    for e in sorted(events, key=lambda e: e["start"]):
        if e["label"] not in ("Speech", "Silence"):
            first_seen.setdefault(e["label"], e["start"])
    for label, t in list(first_seen.items())[:5]:
        lines.append(f"{label} was detected near {fmt_ts(t)}.")

    if anomalies:
        times = _join([fmt_ts(a["peak_time"]) for a in anomalies[:4]])
        lines.append(f"{len(anomalies)} unusual acoustic segment{'s were' if len(anomalies) != 1 else ' was'} detected (at {times}).")
    else:
        lines.append("No unusual acoustic segments were detected.")

    tone = None
    if emotions:
        counts = Counter(e["emotion"] for e in emotions)
        label, c = counts.most_common(1)[0]
        tone = {"dominant": label, "share": round(c / len(emotions) * 100, 1)}
        lines.append(f"The estimated vocal tone was mostly {label.lower()} ({tone['share']:.0f}% of speech segments; model estimate).")

    return {
        "headline": lines[0] if lines else "",
        "text": " ".join(lines),
        "sentences": lines,
        "topics": topics,
        "highlights": highlights,
        "duration": fmt_ts(duration),
        "language": language,
        "tone": tone,
        "method": "Grounded extractive summary: all statements are computed from this recording's analysis results.",
    }


def _most_active_span(intervals: list[dict], width: int = 3) -> tuple[float, float] | None:
    """Span of `width` consecutive intervals with the highest mean engagement."""
    if not intervals:
        return None
    width = min(width, len(intervals))
    vals = [iv["engagement"] for iv in intervals]
    best = max(range(len(vals) - width + 1), key=lambda i: sum(vals[i:i + width]))
    if sum(vals[best:best + width]) <= 0:
        return None
    return intervals[best]["start"], intervals[best + width - 1]["end"]
