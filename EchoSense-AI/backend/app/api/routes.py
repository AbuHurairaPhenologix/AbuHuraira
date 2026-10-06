import json
import shutil
import uuid
from pathlib import Path

import numpy as np
from fastapi import APIRouter, BackgroundTasks, Depends, File, HTTPException, UploadFile
from fastapi.responses import FileResponse
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app import config
from app.analytics import search as search_lib
from app.audio import io
from app.database.db import get_db
from app.models import AudioAnomaly, AudioFile, EmotionResult, SoundEvent, TranscriptSegment
from app.schemas import (AnomaliesOut, AudioDetail, AudioSummary, EmotionsOut, EventOut, PredictionsOut, SearchRequest,
                         SearchResponse, SpeakersOut, TranscriptOut)
from app.services import pipeline

router = APIRouter(prefix="/api/audio", tags=["audio"])

MEDIA_TYPES = {".wav": "audio/wav", ".mp3": "audio/mpeg", ".flac": "audio/flac"}


def _get(db: Session, audio_id: int) -> AudioFile:
    audio = db.get(AudioFile, audio_id)
    if not audio:
        raise HTTPException(404, f"Audio {audio_id} not found")
    return audio


def _require_analyzed(audio: AudioFile) -> None:
    if audio.status != "completed":
        raise HTTPException(409, f"Audio {audio.id} has not been analyzed yet (status: {audio.status})")


def _artifact(audio_id: int, name: str) -> dict:
    p = pipeline.artifact_dir(audio_id) / name
    if not p.exists():
        raise HTTPException(404, f"{name} not available - analyze the recording first")
    return json.loads(p.read_text())


@router.post("/upload", response_model=AudioSummary, status_code=201)
def upload(file: UploadFile = File(...), db: Session = Depends(get_db)):
    ext = Path(file.filename or "").suffix.lower()
    if ext not in config.ALLOWED_EXTENSIONS:
        raise HTTPException(415, f"Unsupported format '{ext}'. Allowed: WAV, MP3, FLAC")
    dest = config.UPLOAD_DIR / f"{uuid.uuid4().hex}{ext}"
    with dest.open("wb") as fh:
        shutil.copyfileobj(file.file, fh)
    size = dest.stat().st_size
    if size > config.MAX_UPLOAD_MB * 1024 * 1024:
        dest.unlink(missing_ok=True)
        raise HTTPException(413, f"File exceeds {config.MAX_UPLOAD_MB} MB")
    try:
        info = io.probe(dest)
    except Exception:
        dest.unlink(missing_ok=True)
        raise HTTPException(422, "Could not decode audio file")
    audio = AudioFile(filename=Path(file.filename).name, stored_path=str(dest), format=ext.lstrip("."),
                      duration_s=round(info.duration_s, 3), sample_rate=info.sample_rate, channels=info.channels,
                      size_bytes=size, status="uploaded", stage="Uploaded", progress=0.0)
    db.add(audio)
    db.commit()
    db.refresh(audio)
    return audio


@router.post("/{audio_id}/analyze", response_model=AudioSummary, status_code=202)
def analyze(audio_id: int, background: BackgroundTasks, db: Session = Depends(get_db)):
    audio = _get(db, audio_id)
    if audio.status == "processing":
        raise HTTPException(409, "Analysis already running")
    audio.status, audio.stage, audio.progress, audio.error = "processing", "Queued", 0.0, None
    db.commit()
    background.add_task(pipeline.run_analysis, audio_id)
    return audio


@router.get("", response_model=list[AudioSummary])
def list_audio(db: Session = Depends(get_db)):
    return db.scalars(select(AudioFile).order_by(AudioFile.created_at.desc())).all()


@router.get("/{audio_id}", response_model=AudioDetail)
def get_audio(audio_id: int, db: Session = Depends(get_db)):
    audio = _get(db, audio_id)
    counts = {}
    for name, model in (("transcript_segments", TranscriptSegment), ("events", SoundEvent),
                        ("emotions", EmotionResult), ("anomalies", AudioAnomaly)):
        counts[name] = db.scalar(select(func.count()).select_from(model).where(model.audio_id == audio_id)) or 0
    counts["non_speech_events"] = db.scalar(select(func.count()).select_from(SoundEvent).where(
        SoundEvent.audio_id == audio_id, SoundEvent.label.not_in(["Speech", "Silence"]))) or 0
    detail = AudioDetail.model_validate(audio)
    detail.counts = counts
    return detail


@router.delete("/{audio_id}", status_code=204)
def delete_audio(audio_id: int, db: Session = Depends(get_db)):
    audio = _get(db, audio_id)
    if audio.status == "processing":
        raise HTTPException(409, "Cannot delete while analysis is running")
    Path(audio.stored_path).unlink(missing_ok=True)
    shutil.rmtree(config.ARTIFACT_DIR / str(audio_id), ignore_errors=True)
    db.delete(audio)
    db.commit()


@router.get("/{audio_id}/file")
def audio_file(audio_id: int, db: Session = Depends(get_db)):
    audio = _get(db, audio_id)
    ext = Path(audio.stored_path).suffix.lower()
    return FileResponse(audio.stored_path, media_type=MEDIA_TYPES.get(ext, "application/octet-stream"), filename=audio.filename)


@router.get("/{audio_id}/visuals")
def visuals(audio_id: int, db: Session = Depends(get_db)):
    _get(db, audio_id)
    return _artifact(audio_id, "visuals.json")


@router.get("/{audio_id}/transcript", response_model=list[TranscriptOut])
def transcript(audio_id: int, db: Session = Depends(get_db)):
    audio = _get(db, audio_id)
    _require_analyzed(audio)
    return audio.transcript


@router.get("/{audio_id}/events", response_model=list[EventOut])
def events(audio_id: int, db: Session = Depends(get_db)):
    audio = _get(db, audio_id)
    _require_analyzed(audio)
    return audio.events


@router.get("/{audio_id}/speakers", response_model=SpeakersOut)
def speakers(audio_id: int, db: Session = Depends(get_db)):
    audio = _get(db, audio_id)
    _require_analyzed(audio)
    stats = audio.stats or {}
    return SpeakersOut(segments=audio.speakers, statistics=stats.get("speaker_stats", []), method=stats.get("speaker_method"),
                       note="Anonymous speaker clusters from voice embeddings (one speaker per transcript segment; "
                            "overlapping speech is not separated).")


@router.get("/{audio_id}/emotions", response_model=EmotionsOut)
def emotions(audio_id: int, db: Session = Depends(get_db)):
    audio = _get(db, audio_id)
    _require_analyzed(audio)
    return EmotionsOut(results=audio.emotions,
                       disclaimer="Emotion labels are AI model estimates of vocal tone, not psychological facts. "
                                  "Arousal (Calm/Moderate/Animated) is a heuristic relative to this recording.")


@router.get("/{audio_id}/anomalies", response_model=AnomaliesOut)
def anomalies(audio_id: int, db: Session = Depends(get_db)):
    audio = _get(db, audio_id)
    _require_analyzed(audio)
    tl = _artifact(audio_id, "timeline.json")
    return AnomaliesOut(anomalies=audio.anomalies, timeline={k: tl[k] for k in ("times", "anomaly_scores", "threshold", "rms_db")})


@router.get("/{audio_id}/predictions", response_model=PredictionsOut)
def predictions(audio_id: int, db: Session = Depends(get_db)):
    audio = _get(db, audio_id)
    _require_analyzed(audio)
    tl = _artifact(audio_id, "timeline.json")
    return PredictionsOut(predictions=audio.predictions, intervals=tl["intervals"], interval_s=tl["interval_s"],
                          method="Mean-reverting AR(1) per metric, walk-forward backtest vs. persistence baseline")


@router.post("/{audio_id}/search", response_model=SearchResponse)
def semantic_search(audio_id: int, req: SearchRequest, db: Session = Depends(get_db)):
    from app.ai.embeddings import embed_texts  # imported lazily so the API starts without loading torch models

    audio = _get(db, audio_id)
    _require_analyzed(audio)
    d = pipeline.artifact_dir(audio_id)
    matrix = np.load(d / "search_index.npy")
    docs = [search_lib.IndexItem(**x) for x in json.loads((d / "search_docs.json").read_text())]
    hits = search_lib.search(embed_texts([req.query])[0], matrix, docs, req.top_k)
    return SearchResponse(query=req.query, results=hits)


@router.get("/{audio_id}/summary")
def summary(audio_id: int, db: Session = Depends(get_db)):
    audio = _get(db, audio_id)
    _require_analyzed(audio)
    return audio.summary
