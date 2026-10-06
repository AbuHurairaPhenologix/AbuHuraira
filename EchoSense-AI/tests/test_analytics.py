"""Semantic search, predictive analytics and the grounded summary (with a deterministic stand-in embedder)."""
import re

import numpy as np
import pandas as pd

from app.analytics import prediction, search, summary

VOCAB = ["artificial", "intelligence", "ai", "future", "technology", "applause", "clapped", "audience", "music",
         "planning", "product", "siren", "weather", "lunch"]


def fake_embed(texts: list[str]) -> np.ndarray:
    """Bag-of-words vectors over a tiny vocabulary - enough to test ranking logic without a model."""
    out = np.zeros((len(texts), len(VOCAB)), dtype=np.float32)
    for i, t in enumerate(texts):
        for w in re.findall(r"[a-z]+", t.lower()):
            if w in VOCAB:
                out[i, VOCAB.index(w)] += 1
    out += 1e-3
    return out / np.linalg.norm(out, axis=1, keepdims=True)


SEGMENTS = [
    {"id": 1, "start": 5.0, "end": 9.0, "text": "Welcome everyone, let us talk about lunch.", "speaker": "Speaker 1"},
    {"id": 2, "start": 10.0, "end": 15.0, "text": "Artificial intelligence is changing everything.", "speaker": "Speaker 2"},
    {"id": 3, "start": 16.0, "end": 20.0, "text": "Future technology needs product planning.", "speaker": "Speaker 1"},
    {"id": 4, "start": 21.0, "end": 25.0, "text": "The weather is nice today.", "speaker": "Speaker 2"},
]
EVENTS = [{"id": 1, "label": "Applause", "start": 30.0, "end": 34.0}, {"id": 2, "label": "Speech", "start": 5, "end": 25}]


def _index():
    docs = search.build_documents(SEGMENTS, EVENTS)
    return docs, fake_embed([d.text for d in docs])


def test_search_finds_the_right_segment_and_timestamp():
    docs, matrix = _index()
    hits = search.search(fake_embed(["Where did they discuss artificial intelligence?"])[0], matrix, docs, top_k=3)
    assert hits[0]["type"] == "transcript" and hits[0]["start"] == 10.0
    assert hits[0]["text"] == SEGMENTS[1]["text"]  # context passages display their own segment
    assert hits[0]["score"] >= hits[1]["score"]


def test_search_matches_sound_events_but_not_speech_or_silence_events():
    docs, matrix = _index()
    assert not any(d.kind == "event" and d.text.startswith("Speech") for d in docs)
    hits = search.search(fake_embed(["Where did the audience applaud?"])[0], matrix, docs, top_k=2)
    assert hits[0]["type"] == "event" and hits[0]["start"] == 30.0


def test_search_results_are_deduplicated_by_time():
    docs, matrix = _index()
    hits = search.search(fake_embed(["future technology product planning"])[0], matrix, docs, top_k=10)
    starts = [(h["start"], h["type"]) for h in hits]
    assert len(starts) == len(set(starts))


def test_interval_metrics_absorb_short_tail_and_stay_in_range():
    times = np.arange(0.25, 61.0, 0.5)
    df = prediction.interval_metrics(61.0, 10.0, times, np.linspace(-40, -10, len(times)), np.full(len(times), 0.45), 0.65,
                                     [(0.0, 10.0, "Speaker 1"), (12.0, 18.0, "Speaker 2"), (18.0, 20.0, "Speaker 1")])
    assert len(df) == 6 and df["end"].iloc[-1] == 61.0
    for m in prediction.METRICS:
        assert df[m].between(0, 100).all()
    assert df["speech_activity"].iloc[0] == 100 and df["turns"].iloc[1] == 1  # S2 -> S1 within 10-20 s


def test_ar1_forecast_beats_persistence_on_a_mean_reverting_series(rng):
    x = np.empty(60)
    x[0] = 50
    for t in range(1, 60):
        x[t] = 50 + 0.2 * (x[t - 1] - 50) + rng.normal(0, 10)
    df = pd.DataFrame({m: np.clip(x, 0, 100) for m in prediction.METRICS})
    f = prediction.forecast(df, "energy")
    assert f.model_mae < f.baseline_mae
    assert 0 <= f.coefficients["phi"] < 0.6 and abs(f.coefficients["mu"] - 50) < 6
    assert len(f.backtest) == 60 - prediction.MIN_TRAIN


def test_forecast_falls_back_to_persistence_without_history():
    df = pd.DataFrame({m: [10.0, 20.0, 30.0] for m in prediction.METRICS})
    f = prediction.forecast(df, "engagement")
    assert f.predicted == 30.0 and f.model_mae is None and f.trend == "Stable"


def test_trend_labels():
    assert prediction.trend_label(80, 67) == "Declining"
    assert prediction.trend_label(40, 60) == "Rising"
    assert prediction.trend_label(50, 53) == "Stable"


def test_topic_candidates_never_bridge_stopwords():
    cands = summary.candidate_phrases(["Let's talk about product planning.", "Product planning matters for product teams."])
    assert "product planning" in cands
    assert "talk product" not in cands and not any(c.split()[0] in summary.STOP for c in cands)


def test_summary_is_grounded_in_inputs():
    s = summary.build_summary(
        duration=200, language="en", topics=["Artificial Intelligence", "Future Technology"], highlights=[],
        speaker_stats=[{"speaker": "Speaker 1", "duration_s": 60, "percentage": 60.0},
                       {"speaker": "Speaker 2", "duration_s": 40, "percentage": 40.0}],
        events=[{"label": "Applause", "start": 95.0, "end": 98.0}, {"label": "Speech", "start": 0, "end": 90}],
        anomalies=[], emotions=[{"emotion": "Neutral"}, {"emotion": "Neutral"}, {"emotion": "Happy"}],
        intervals=[{"start": 0, "end": 10, "engagement": 10}, {"start": 10, "end": 20, "engagement": 90},
                   {"start": 20, "end": 30, "engagement": 80}, {"start": 30, "end": 40, "engagement": 5}])
    text = s["text"]
    assert "Artificial Intelligence and Future Technology" in text
    assert "2 speakers" in text and "Applause was detected near 01:35" in text
    assert "No unusual acoustic segments" in text
    assert "Speech was detected" not in text  # speech is not reported as an 'event'
    assert s["tone"] == {"dominant": "Neutral", "share": 66.7}


def test_summary_without_speech_does_not_invent_topics():
    s = summary.build_summary(duration=30, language=None, topics=[], highlights=[], speaker_stats=[], events=[],
                              anomalies=[], emotions=[], intervals=[])
    assert s["sentences"][0] == "No intelligible speech was transcribed in this recording."
    assert s["tone"] is None
