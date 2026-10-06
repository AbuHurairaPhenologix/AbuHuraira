"""Timestamp handling: word grouping, chunking, event merging, speaker turns and formatting."""
import numpy as np

from app.ai import sound_events, speakers, transcription
from app.analytics.summary import fmt_ts


def test_fmt_ts():
    assert fmt_ts(0) == "00:00"
    assert fmt_ts(754.4) == "12:34"
    assert fmt_ts(3725) == "1:02:05"
    assert fmt_ts(-3) == "00:00"


def _w(start, end, text):
    return {"start": start, "end": end, "text": text}


def test_group_words_splits_on_sentence_end_and_pause():
    words = [_w(0.0, 0.3, " Hello"), _w(0.3, 0.6, " there"), _w(0.6, 1.0, " everyone."),
             _w(1.1, 1.4, " Next"), _w(1.4, 1.8, " sentence"),
             _w(3.0, 3.4, " after"), _w(3.4, 3.8, " pause")]
    segs = transcription.group_words(words)
    assert [s["text"] for s in segs] == ["Hello there everyone.", "Next sentence", "after pause"]
    assert segs[0]["start"] == 0.0 and segs[0]["end"] == 1.0
    assert segs[2]["start"] == 3.0


def test_pack_chunks_merges_close_regions_but_respects_max_length():
    chunks = transcription.pack_chunks([(0.0, 5.0), (5.5, 10.0), (20.0, 50.0)], max_len=28.0, max_gap=1.0)
    assert chunks[0] == (0.0, 10.0)
    assert all(b - a <= 28.0 + 1e-6 for a, b in chunks)
    assert chunks[-1][1] == 50.0


def test_merge_events_joins_consecutive_windows_with_correct_times():
    windows = [(0, 2), (2, 4), (4, 6), (6, 8), (8, 10)]
    names = ["Applause", "Speech"]
    scores = {"Applause": np.array([0.1, 0.6, 0.7, 0.1, 0.5]), "Speech": np.array([0.0, 0.0, 0.0, 0.0, 0.0])}
    argmax = {"Applause": np.zeros(5, dtype=int), "Speech": np.ones(5, dtype=int)}
    events = sound_events.merge_events(windows, scores, argmax, names)
    assert [(e["label"], e["start"], e["end"]) for e in events] == [("Applause", 2, 6), ("Applause", 8, 10)]
    assert events[0]["confidence"] == 0.7


def test_speech_regions_use_overlap_weighted_probability():
    windows = [(0, 2), (2, 4), (4, 6)]
    speech = np.array([0.9, 0.05, 0.05])
    regions = sound_events.speech_regions([(0.5, 1.9), (2.1, 5.5)], windows, speech, min_prob=0.3)
    assert regions == [(0.5, 1.9)]


def test_speaker_statistics_and_turns():
    segs = [{"start": 0, "end": 4}, {"start": 4, "end": 6}, {"start": 6, "end": 10}, {"start": 11, "end": 12}]
    names = ["Speaker 1", "Speaker 1", "Speaker 2", "Speaker 1"]
    stats = {s["speaker"]: s for s in speakers.speaker_statistics(segs, names)}
    assert stats["Speaker 1"]["duration_s"] == 7 and stats["Speaker 1"]["turns"] == 2
    assert stats["Speaker 2"]["turns"] == 1
    assert abs(stats["Speaker 1"]["percentage"] + stats["Speaker 2"]["percentage"] - 100) < 0.2
    turns = speakers.merge_speaker_turns(segs, names)
    assert [(t["speaker"], t["start"], t["end"]) for t in turns] == [("Speaker 1", 0, 6), ("Speaker 2", 6, 10), ("Speaker 1", 11, 12)]


def test_cluster_embeddings_separates_distinct_voices(rng):
    a = rng.normal(0, 0.05, (6, 16)) + np.eye(16)[0]
    b = rng.normal(0, 0.05, (6, 16)) + np.eye(16)[1]
    emb = np.vstack([a, b])
    emb /= np.linalg.norm(emb, axis=1, keepdims=True)
    labels, silhouette = speakers.cluster_embeddings(emb)
    assert len(set(labels)) == 2 and silhouette > 0.5
    assert len(set(labels[:6])) == 1 and len(set(labels[6:])) == 1


def test_single_voice_is_not_split(rng):
    emb = rng.normal(0, 0.02, (10, 16)) + np.eye(16)[0]
    emb /= np.linalg.norm(emb, axis=1, keepdims=True)
    labels, silhouette = speakers.cluster_embeddings(emb)
    assert len(set(labels)) == 1 and silhouette is None
