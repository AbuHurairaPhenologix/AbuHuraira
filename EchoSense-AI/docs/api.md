# EchoSense AI — REST API

Base URL: `http://localhost:8000`. Interactive OpenAPI docs: `http://localhost:8000/docs`.

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/health` | Liveness check |
| `POST` | `/api/audio/upload` | Multipart upload (`file`) of a WAV / MP3 / FLAC recording. Returns filename, duration, sample rate, channels, status. `415` unsupported type, `422` undecodable, `413` too large |
| `POST` | `/api/audio/{id}/analyze` | Starts the analysis pipeline in a background thread (`202`). Poll `GET /api/audio/{id}` for `status`, `stage`, `progress` |
| `GET` | `/api/audio` | All recordings, newest first |
| `GET` | `/api/audio/{id}` | Recording detail: status, language, speaker count, counts per result type, model names, stage timings |
| `DELETE` | `/api/audio/{id}` | Deletes the recording, its results and artifacts |
| `GET` | `/api/audio/{id}/file` | Streams the **original** uploaded file (used by the player) |
| `GET` | `/api/audio/{id}/visuals` | Waveform peaks, base64 uint8 log-Mel spectrogram, activity and silence intervals |
| `GET` | `/api/audio/{id}/transcript` | Transcript segments `{start, end, text, speaker}` |
| `GET` | `/api/audio/{id}/events` | Sound events `{label, start, end, confidence, source_label}` |
| `GET` | `/api/audio/{id}/speakers` | Speaker turns, per-speaker duration / share / turns, clustering diagnostics |
| `GET` | `/api/audio/{id}/emotions` | Per-segment emotion estimate, class scores, measured prosody, disclaimer |
| `GET` | `/api/audio/{id}/anomalies` | Anomaly regions with peak, score, explanation + full score timeline and threshold |
| `GET` | `/api/audio/{id}/predictions` | Per-metric current value, next-interval forecast, trend, walk-forward MAE vs persistence, interval history |
| `POST` | `/api/audio/{id}/search` | Body `{"query": "...", "top_k": 5}` → ranked `{type, start, end, text, speaker, score}` |
| `GET` | `/api/audio/{id}/summary` | Grounded summary: sentences, topics, quoted highlights, dominant tone |

Result endpoints return `409` until the recording has been analyzed.

## Example

```bash
curl -F "file=@sample-data/future-ai-conference.wav" localhost:8000/api/audio/upload
curl -X POST localhost:8000/api/audio/1/analyze
curl localhost:8000/api/audio/1            # repeat until "status": "completed"
curl -X POST localhost:8000/api/audio/1/search -H "Content-Type: application/json" \
     -d '{"query": "Where did they discuss artificial intelligence?"}'
```

```json
{
  "query": "Where did they discuss artificial intelligence?",
  "results": [
    {"type": "transcript", "start": 8.21, "end": 12.09, "speaker": "Speaker 1", "score": 0.433,
     "text": "Good morning everyone, and welcome to the future of AI conference."},
    {"type": "transcript", "start": 28.9, "end": 32.9, "speaker": "Speaker 2", "score": 0.430,
     "text": "Ten years ago a computer could barely recognize a spoken word."}
  ]
}
```

## Persistence

SQLite (`backend/data/echosense.db`) holds `audio_files`, `transcript_segments`, `sound_events`, `speaker_segments`,
`emotion_results`, `audio_anomalies` and `predictions`. Bulky numeric artifacts live next to it in
`backend/data/artifacts/<id>/`: `visuals.json`, `timeline.json` (window features, anomaly scores, interval metrics),
`search_index.npy` + `search_docs.json` (the local vector index). Uploaded originals are stored unmodified in
`backend/data/uploads/`.
