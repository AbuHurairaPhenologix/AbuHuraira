"""API responses. The model-heavy pipeline is replaced by a stub that stores fixed results, so these tests
exercise upload validation, persistence, status handling and response schemas without downloading models."""
import io as _io
import json

import numpy as np
import pytest
import soundfile as sf
from fastapi.testclient import TestClient

from app.ai import embeddings
from app.analytics import search as search_lib
from app.database.db import SessionLocal
from app.main import app
from app.models import AudioAnomaly, AudioFile, EmotionResult, Prediction, SoundEvent, SpeakerSegment, TranscriptSegment
from app.services import pipeline
from test_analytics import fake_embed


def _wav_bytes(seconds=2.0, sr=16_000) -> bytes:
    buf = _io.BytesIO()
    t = np.arange(int(seconds * sr)) / sr
    sf.write(buf, 0.3 * np.sin(2 * np.pi * 440 * t), sr, format="WAV")
    return buf.getvalue()


def fake_run_analysis(audio_id: int) -> None:
    out = pipeline.artifact_dir(audio_id)
    (out / "visuals.json").write_text(json.dumps({"duration": 2.0, "peaks": [0.1, 0.3], "active": [[0, 2]], "silences": [],
                                                  "spectrogram": {"n_mels": 1, "n_frames": 1, "hop_s": 1, "fmax": 8000, "data": "AA=="}}))
    (out / "timeline.json").write_text(json.dumps({"times": [0.5, 1.0], "anomaly_scores": [0.4, 0.8], "threshold": 0.6,
                                                   "rms_db": [-20, -10], "intervals": [], "interval_s": 5.0}))
    segs = [{"id": 1, "start": 0.2, "end": 1.0, "text": "artificial intelligence today", "speaker": "Speaker 1"},
            {"id": 2, "start": 1.1, "end": 1.9, "text": "lunch and weather", "speaker": "Speaker 2"}]
    docs = search_lib.build_documents(segs, [{"id": 1, "label": "Applause", "start": 1.5, "end": 2.0}])
    np.save(out / "search_index.npy", fake_embed([d.text for d in docs]))
    (out / "search_docs.json").write_text(json.dumps([d.__dict__ for d in docs]))
    with SessionLocal() as db:
        a = db.get(AudioFile, audio_id)
        a.transcript = [TranscriptSegment(start=s["start"], end=s["end"], text=s["text"], speaker=s["speaker"]) for s in segs]
        a.events = [SoundEvent(label="Applause", source_label="Applause", start=1.5, end=2.0, confidence=0.7)]
        a.speakers = [SpeakerSegment(speaker="Speaker 1", start=0.2, end=1.0), SpeakerSegment(speaker="Speaker 2", start=1.1, end=1.9)]
        a.emotions = [EmotionResult(start=0.2, end=1.0, emotion="Neutral", confidence=0.8, scores={"Neutral": 0.8}, prosody={})]
        a.anomalies = [AudioAnomaly(start=0.8, end=1.2, peak_time=1.0, score=0.8, status="Unusual acoustic event",
                                    explanation={"top_features": [], "overlapping_events": [], "threshold": 0.6})]
        a.predictions = [Prediction(metric="energy", current_value=50, predicted_value=40, trend="Declining",
                                    model_mae=None, baseline_mae=None, details={"backtest": [], "coefficients": {}})]
        a.summary = {"headline": "x", "text": "x", "sentences": ["x"], "topics": ["Artificial Intelligence"], "highlights": []}
        a.stats = {"speaker_stats": [{"speaker": "Speaker 1", "duration_s": 0.8, "percentage": 50, "turns": 1, "segments": 1}]}
        a.speaker_count, a.language, a.status, a.stage, a.progress = 2, "en", "completed", "Completed", 1.0
        db.commit()


@pytest.fixture
def client(monkeypatch):
    monkeypatch.setattr(pipeline, "run_analysis", fake_run_analysis)
    monkeypatch.setattr(embeddings, "embed_texts", fake_embed)
    with TestClient(app) as c:
        yield c


def _upload(client, name="meeting.wav", data=None):
    return client.post("/api/audio/upload", files={"file": (name, data or _wav_bytes(), "audio/wav")})


def test_upload_returns_metadata(client):
    r = _upload(client)
    assert r.status_code == 201
    body = r.json()
    assert body["filename"] == "meeting.wav" and body["status"] == "uploaded"
    assert body["sample_rate"] == 16_000 and abs(body["duration_s"] - 2.0) < 0.01 and body["channels"] == 1


def test_upload_rejects_unsupported_and_corrupt_files(client):
    assert _upload(client, "notes.txt", b"hello").status_code == 415
    assert _upload(client, "broken.wav", b"RIFF....not audio").status_code == 422


def test_results_require_analysis_and_unknown_ids_404(client):
    audio_id = _upload(client).json()["id"]
    assert client.get(f"/api/audio/{audio_id}/transcript").status_code == 409
    assert client.get("/api/audio/99999").status_code == 404


def test_full_flow_after_analysis(client):
    audio_id = _upload(client).json()["id"]
    assert client.post(f"/api/audio/{audio_id}/analyze").status_code == 202

    detail = client.get(f"/api/audio/{audio_id}").json()
    assert detail["status"] == "completed" and detail["speaker_count"] == 2
    assert detail["counts"] == {"transcript_segments": 2, "events": 1, "emotions": 1, "anomalies": 1, "non_speech_events": 1}
    assert any(a["id"] == audio_id for a in client.get("/api/audio").json())

    transcript = client.get(f"/api/audio/{audio_id}/transcript").json()
    assert [s["start"] for s in transcript] == [0.2, 1.1] and transcript[0]["speaker"] == "Speaker 1"
    assert client.get(f"/api/audio/{audio_id}/events").json()[0]["label"] == "Applause"
    assert "not psychological facts" in client.get(f"/api/audio/{audio_id}/emotions").json()["disclaimer"]
    an = client.get(f"/api/audio/{audio_id}/anomalies").json()
    assert an["anomalies"][0]["peak_time"] == 1.0 and an["timeline"]["threshold"] == 0.6
    pr = client.get(f"/api/audio/{audio_id}/predictions").json()
    assert pr["predictions"][0]["trend"] == "Declining" and "AR(1)" in pr["method"]
    assert client.get(f"/api/audio/{audio_id}/summary").json()["topics"] == ["Artificial Intelligence"]
    assert client.get(f"/api/audio/{audio_id}/speakers").json()["statistics"][0]["speaker"] == "Speaker 1"
    assert client.get(f"/api/audio/{audio_id}/visuals").json()["duration"] == 2.0

    file = client.get(f"/api/audio/{audio_id}/file")
    assert file.status_code == 200 and file.headers["content-type"] == "audio/wav"

    hits = client.post(f"/api/audio/{audio_id}/search", json={"query": "artificial intelligence"}).json()["results"]
    assert hits[0]["start"] == 0.2 and hits[0]["type"] == "transcript" and 0 < hits[0]["score"] <= 1
    assert client.post(f"/api/audio/{audio_id}/search", json={"query": "x"}).status_code == 422

    assert client.delete(f"/api/audio/{audio_id}").status_code == 204
    assert client.get(f"/api/audio/{audio_id}").status_code == 404
