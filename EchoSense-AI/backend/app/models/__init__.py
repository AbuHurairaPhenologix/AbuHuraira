"""SQLite ORM tables. Large numeric artifacts (spectrogram, feature matrix, embeddings) live as files
under data/artifacts/<audio_id>/ and only their summaries are stored relationally."""
from datetime import datetime, timezone

from sqlalchemy import JSON, DateTime, Float, ForeignKey, Integer, String, Text
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.database.db import Base


def _now() -> datetime:
    return datetime.now(timezone.utc)


class AudioFile(Base):
    __tablename__ = "audio_files"

    id: Mapped[int] = mapped_column(primary_key=True)
    filename: Mapped[str] = mapped_column(String(255))
    stored_path: Mapped[str] = mapped_column(String(500))
    format: Mapped[str] = mapped_column(String(10))
    duration_s: Mapped[float] = mapped_column(Float, default=0.0)
    sample_rate: Mapped[int] = mapped_column(Integer, default=0)
    channels: Mapped[int] = mapped_column(Integer, default=1)
    size_bytes: Mapped[int] = mapped_column(Integer, default=0)
    status: Mapped[str] = mapped_column(String(20), default="uploaded")  # uploaded|processing|completed|failed
    stage: Mapped[str | None] = mapped_column(String(60), nullable=True)
    progress: Mapped[float] = mapped_column(Float, default=0.0)
    error: Mapped[str | None] = mapped_column(Text, nullable=True)
    language: Mapped[str | None] = mapped_column(String(20), nullable=True)
    speaker_count: Mapped[int | None] = mapped_column(Integer, nullable=True)
    summary: Mapped[dict | None] = mapped_column(JSON, nullable=True)
    stats: Mapped[dict | None] = mapped_column(JSON, nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=_now)
    analyzed_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)

    transcript: Mapped[list["TranscriptSegment"]] = relationship(cascade="all, delete-orphan", order_by="TranscriptSegment.start")
    events: Mapped[list["SoundEvent"]] = relationship(cascade="all, delete-orphan", order_by="SoundEvent.start")
    speakers: Mapped[list["SpeakerSegment"]] = relationship(cascade="all, delete-orphan", order_by="SpeakerSegment.start")
    emotions: Mapped[list["EmotionResult"]] = relationship(cascade="all, delete-orphan", order_by="EmotionResult.start")
    anomalies: Mapped[list["AudioAnomaly"]] = relationship(cascade="all, delete-orphan", order_by="AudioAnomaly.start")
    predictions: Mapped[list["Prediction"]] = relationship(cascade="all, delete-orphan", order_by="Prediction.id")


class _Child:
    audio_id: Mapped[int] = mapped_column(ForeignKey("audio_files.id", ondelete="CASCADE"), index=True)
    start: Mapped[float] = mapped_column(Float)
    end: Mapped[float] = mapped_column(Float)


class TranscriptSegment(_Child, Base):
    __tablename__ = "transcript_segments"
    id: Mapped[int] = mapped_column(primary_key=True)
    text: Mapped[str] = mapped_column(Text)
    speaker: Mapped[str | None] = mapped_column(String(40), nullable=True)


class SoundEvent(_Child, Base):
    __tablename__ = "sound_events"
    id: Mapped[int] = mapped_column(primary_key=True)
    label: Mapped[str] = mapped_column(String(60))
    source_label: Mapped[str] = mapped_column(String(120))  # raw AudioSet class that produced the event
    confidence: Mapped[float] = mapped_column(Float)


class SpeakerSegment(_Child, Base):
    __tablename__ = "speaker_segments"
    id: Mapped[int] = mapped_column(primary_key=True)
    speaker: Mapped[str] = mapped_column(String(40))


class EmotionResult(_Child, Base):
    __tablename__ = "emotion_results"
    id: Mapped[int] = mapped_column(primary_key=True)
    emotion: Mapped[str] = mapped_column(String(30))
    confidence: Mapped[float] = mapped_column(Float)
    scores: Mapped[dict] = mapped_column(JSON)
    prosody: Mapped[dict] = mapped_column(JSON)  # measured pitch / energy / speaking rate


class AudioAnomaly(_Child, Base):
    __tablename__ = "audio_anomalies"
    id: Mapped[int] = mapped_column(primary_key=True)
    peak_time: Mapped[float] = mapped_column(Float)
    score: Mapped[float] = mapped_column(Float)
    status: Mapped[str] = mapped_column(String(60))
    explanation: Mapped[dict] = mapped_column(JSON)  # most deviating features (z-scores)


class Prediction(Base):
    __tablename__ = "predictions"
    id: Mapped[int] = mapped_column(primary_key=True)
    audio_id: Mapped[int] = mapped_column(ForeignKey("audio_files.id", ondelete="CASCADE"), index=True)
    metric: Mapped[str] = mapped_column(String(40))
    current_value: Mapped[float] = mapped_column(Float)
    predicted_value: Mapped[float] = mapped_column(Float)
    trend: Mapped[str] = mapped_column(String(20))
    model_mae: Mapped[float | None] = mapped_column(Float, nullable=True)
    baseline_mae: Mapped[float | None] = mapped_column(Float, nullable=True)
    details: Mapped[dict] = mapped_column(JSON)  # history, fitted series, coefficients
