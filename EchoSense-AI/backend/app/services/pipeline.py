"""End-to-end analysis pipeline for one recording.

Upload -> Preprocessing -> Feature extraction -> AI analysis -> Analytics -> Results (SQLite + artifacts).
Runs in a FastAPI background thread; progress is written to the AudioFile row so the UI can poll it.
"""
import json
import logging
import threading
import time
import traceback
from dataclasses import asdict
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

from app import config
from app.ai import emotion, sound_events, speakers, transcription
from app.ai.embeddings import embed_texts
from app.analytics import anomaly, prediction, search, summary
from app.audio import features, io, preprocessing
from app.database.db import SessionLocal
from app.models import (AudioAnomaly, AudioFile, EmotionResult, Prediction, SoundEvent, SpeakerSegment,
                        TranscriptSegment)

log = logging.getLogger("echosense.pipeline")
_run_lock = threading.Lock()  # one analysis at a time keeps memory use predictable on a laptop


def artifact_dir(audio_id: int) -> Path:
    d = config.ARTIFACT_DIR / str(audio_id)
    d.mkdir(parents=True, exist_ok=True)
    return d


def _status(audio_id: int, **fields) -> None:
    with SessionLocal() as db:
        db.query(AudioFile).filter(AudioFile.id == audio_id).update(fields)
        db.commit()


def run_analysis(audio_id: int) -> None:
    with _run_lock:
        t0 = time.perf_counter()
        try:
            _run(audio_id, t0)
        except Exception as exc:  # surface the failure to the UI instead of dying silently
            log.exception("analysis failed for %s", audio_id)
            _status(audio_id, status="failed", stage="Failed", error=f"{type(exc).__name__}: {exc}\n{traceback.format_exc(limit=3)}")


def _run(audio_id: int, t0: float) -> None:
    with SessionLocal() as db:
        audio = db.get(AudioFile, audio_id)
        path = audio.stored_path
    timings: dict[str, float] = {}
    current = {"name": None, "t": time.perf_counter()}

    def stage(name: str | None, progress: float = 1.0) -> None:
        now = time.perf_counter()
        if current["name"]:
            timings[current["name"]] = round(now - current["t"], 2)
        current.update(name=name, t=now)
        if name:
            _status(audio_id, status="processing", stage=name, progress=progress, error=None)

    # 1. Preprocessing ------------------------------------------------------------------------------
    stage("Preprocessing", 0.03)
    sr = config.TARGET_SR
    y, info = io.load_for_analysis(path)
    duration = y.size / sr
    active = preprocessing.detect_activity(y, sr)
    silences = preprocessing.silence_regions(active, duration)
    out = artifact_dir(audio_id)
    visuals = {"duration": duration, "peaks": preprocessing.waveform_peaks(y),
               "spectrogram": preprocessing.mel_spectrogram(y, sr), "active": active, "silences": silences}
    (out / "visuals.json").write_text(json.dumps(visuals))

    # 2. Feature extraction ---------------------------------------------------------------------------
    stage("Feature extraction", 0.10)
    feats = features.window_features(y, sr, config.FEATURE_WINDOW_S, config.FEATURE_HOP_S)

    # 3. Sound events -----------------------------------------------------------------------------
    stage("Sound event detection", 0.16)
    ev_windows = preprocessing.fixed_windows(duration, config.EVENT_WINDOW_S, config.EVENT_WINDOW_S)
    probs, names = sound_events.classify_windows(y, sr, ev_windows)
    cat_scores, cat_argmax = sound_events.category_scores(probs, names)
    events = sound_events.merge_events(ev_windows, cat_scores, cat_argmax, names) + sound_events.silence_events(silences)
    events.sort(key=lambda e: (e["start"], e["label"]))

    # 4. Transcription ---------------------------------------------------------------------------
    stage("Speech transcription", 0.35)
    speech_scores = cat_scores.get("Speech", np.zeros(len(ev_windows)))
    speech_regions = sound_events.speech_regions(active, ev_windows, speech_scores)
    language = transcription.detect_language(y, sr, speech_regions)
    raw_segments = transcription.transcribe(y, sr, speech_regions, language)
    # Drop text decoded where the event model is confident there is no speech (Whisper hallucination guard).
    segments = [s for s in raw_segments
                if sound_events.speech_probability(ev_windows, speech_scores, s["start"], s["end"]) >= 0.15]

    # 5. Speakers ----------------------------------------------------------------------------------
    stage("Speaker analysis", 0.62)
    spk_names, spk_info = speakers.assign_speakers(y, sr, segments)
    for s, n in zip(segments, spk_names):
        s["speaker"] = n
    spk_stats = speakers.speaker_statistics(segments, spk_names)
    turns = speakers.merge_speaker_turns(segments, spk_names)

    # 6. Emotion / prosody ---------------------------------------------------------------------------
    stage("Emotion & prosody", 0.72)
    emo = emotion.classify(y, sr, segments)
    pros = [emotion.prosody(y, sr, s) for s in segments]
    arousal = emotion.arousal_labels(pros)
    emotions = []
    for s, e, p, a in zip(segments, emo, pros, arousal):
        if e:
            emotions.append({"start": s["start"], "end": s["end"], "speaker": s.get("speaker"), **e,
                             "prosody": {**p, "arousal": a}})

    # 7. Anomaly detection ---------------------------------------------------------------------------
    stage("Anomaly detection", 0.80)
    an = anomaly.detect_anomalies(feats)
    anomalies = []
    for r in an.regions:
        overlapping = sorted({e["label"] for e in events if e["start"] < r.end and e["end"] > r.start
                              and e["label"] not in ("Speech", "Silence")})
        anomalies.append({"start": r.start, "end": r.end, "peak_time": r.peak_time, "score": r.score,
                          "status": "Unusual acoustic event",
                          "explanation": {"top_features": r.top_features, "overlapping_events": overlapping,
                                          "threshold": round(an.threshold, 3)}})

    # 8. Predictive analytics -------------------------------------------------------------------------
    stage("Predictive analytics", 0.86)
    interval_s = prediction.choose_interval(duration)
    iv = prediction.interval_metrics(duration, interval_s, feats["time"].to_numpy(), feats["rms_db"].to_numpy(),
                                     an.scores, an.threshold, [(s["start"], s["end"], s.get("speaker")) for s in segments])
    forecasts = [prediction.forecast(iv, m) for m in prediction.METRICS]

    # 9. Semantic index + summary -----------------------------------------------------------------
    stage("Semantic indexing & summary", 0.92)
    docs = search.build_documents(segments, events)
    matrix = embed_texts([d.text for d in docs])
    np.save(out / "search_index.npy", matrix)
    (out / "search_docs.json").write_text(json.dumps([asdict(d) for d in docs]))
    seg_vecs = embed_texts([s["text"] for s in segments])
    topics = summary.extract_topics([s["text"] for s in segments], embed_texts)
    highlights = summary.extract_highlights(segments, seg_vecs)
    summ = summary.build_summary(duration=duration, language=language, topics=topics, highlights=highlights,
                                 speaker_stats=spk_stats, events=events, anomalies=anomalies, emotions=emotions,
                                 intervals=iv.to_dict("records"))

    timeline = {"times": feats["time"].round(3).tolist(), "rms_db": feats["rms_db"].round(2).tolist(),
                "spectral_centroid": feats["spectral_centroid"].round(1).tolist(),
                "zcr": feats["zcr"].round(4).tolist(), "anomaly_scores": np.round(an.scores, 4).tolist(),
                "threshold": round(an.threshold, 4), "intervals": iv.to_dict("records"), "interval_s": interval_s}
    (out / "timeline.json").write_text(json.dumps(timeline))
    stage(None)

    # 10. Persist -------------------------------------------------------------------------------------
    with SessionLocal() as db:
        audio = db.get(AudioFile, audio_id)
        for rel in (audio.transcript, audio.events, audio.speakers, audio.emotions, audio.anomalies, audio.predictions):
            rel.clear()
        db.flush()
        audio.transcript = [TranscriptSegment(start=s["start"], end=s["end"], text=s["text"], speaker=s.get("speaker")) for s in segments]
        audio.events = [SoundEvent(**e) for e in events]
        audio.speakers = [SpeakerSegment(**t) for t in turns]
        audio.emotions = [EmotionResult(start=e["start"], end=e["end"], emotion=e["emotion"], confidence=e["confidence"],
                                        scores=e["scores"], prosody={**e["prosody"], "speaker": e.get("speaker")}) for e in emotions]
        audio.anomalies = [AudioAnomaly(**a) for a in anomalies]
        audio.predictions = [Prediction(metric=f.metric, current_value=f.current, predicted_value=f.predicted, trend=f.trend,
                                        model_mae=f.model_mae, baseline_mae=f.baseline_mae,
                                        details={"backtest": f.backtest, "coefficients": f.coefficients}) for f in forecasts]
        audio.language = language
        audio.speaker_count = len(spk_stats)
        audio.summary = summ
        audio.stats = {
            "speaker_stats": spk_stats, "speaker_method": spk_info, "topics": topics,
            "active_ratio": round(sum(e - s for s, e in active) / max(duration, 1e-6), 3),
            "segments_dropped_as_non_speech": len(raw_segments) - len(segments),
            "feature_windows": len(feats), "event_windows": len(ev_windows),
            "timings_s": timings, "total_s": round(time.perf_counter() - t0, 2),
            "models": {"asr": config.ASR_MODEL, "sound_events": config.SOUND_EVENT_MODEL, "speaker": config.SPEAKER_MODEL,
                       "emotion": config.EMOTION_MODEL, "embedding": config.EMBEDDING_MODEL},
        }
        audio.status, audio.stage, audio.progress = "completed", "Completed", 1.0
        audio.analyzed_at = datetime.now(timezone.utc)
        db.commit()
    log.info("analysis of %s finished in %.1fs", audio_id, time.perf_counter() - t0)
