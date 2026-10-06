from datetime import datetime

from pydantic import BaseModel, ConfigDict, Field


class _ORM(BaseModel):
    model_config = ConfigDict(from_attributes=True)


class AudioSummary(_ORM):
    id: int
    filename: str
    format: str
    duration_s: float
    sample_rate: int
    channels: int
    size_bytes: int
    status: str
    stage: str | None
    progress: float
    error: str | None
    language: str | None
    speaker_count: int | None
    created_at: datetime
    analyzed_at: datetime | None


class AudioDetail(AudioSummary):
    stats: dict | None
    counts: dict[str, int] = {}


class TranscriptOut(_ORM):
    id: int
    start: float
    end: float
    text: str
    speaker: str | None


class EventOut(_ORM):
    id: int
    label: str
    source_label: str
    start: float
    end: float
    confidence: float


class SpeakerSegmentOut(_ORM):
    id: int
    speaker: str
    start: float
    end: float


class SpeakersOut(BaseModel):
    segments: list[SpeakerSegmentOut]
    statistics: list[dict]
    method: dict | None
    note: str


class EmotionOut(_ORM):
    id: int
    start: float
    end: float
    emotion: str
    confidence: float
    scores: dict
    prosody: dict


class EmotionsOut(BaseModel):
    results: list[EmotionOut]
    disclaimer: str


class AnomalyOut(_ORM):
    id: int
    start: float
    end: float
    peak_time: float
    score: float
    status: str
    explanation: dict


class AnomaliesOut(BaseModel):
    anomalies: list[AnomalyOut]
    timeline: dict  # window times, scores, threshold


class PredictionOut(_ORM):
    metric: str
    current_value: float
    predicted_value: float
    trend: str
    model_mae: float | None
    baseline_mae: float | None
    details: dict


class PredictionsOut(BaseModel):
    predictions: list[PredictionOut]
    intervals: list[dict]
    interval_s: float
    method: str


class SearchRequest(BaseModel):
    query: str = Field(min_length=2, max_length=500)
    top_k: int = Field(default=5, ge=1, le=20)


class SearchHit(BaseModel):
    type: str
    start: float
    end: float
    text: str
    speaker: str | None
    score: float


class SearchResponse(BaseModel):
    query: str
    results: list[SearchHit]
