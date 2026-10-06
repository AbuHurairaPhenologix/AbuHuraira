# EchoSense AI — Architecture & design decisions

## Components

| Layer | Location | Responsibility |
|---|---|---|
| API | `backend/app/api/routes.py` | FastAPI endpoints, upload validation, background analysis start |
| Pipeline | `backend/app/services/pipeline.py` | Orchestrates every stage, reports progress, persists results |
| Audio | `backend/app/audio/` | Loading (libsndfile), resampling (torchaudio), mono mix, normalisation, silence detection, segmentation, waveform peaks, Mel spectrogram, MFCC and window features (librosa) |
| AI | `backend/app/ai/` | Thin wrappers around pretrained Hugging Face models, loaded lazily once per process |
| Analytics | `backend/app/analytics/` | Isolation-Forest anomalies, AR(1) forecasts, vector search, grounded summary — pure functions, unit-tested |
| Storage | `backend/app/models`, `backend/app/database` | SQLAlchemy ORM on SQLite + JSON/NumPy artifacts |
| Frontend | `frontend/src` | React + TypeScript; WaveSurfer.js, Recharts, Three.js, Web Audio API |

## Pipeline order and why

1. **Preprocessing** – 16 kHz mono, peak-normalised analysis copy; the uploaded file is never modified.
2. **Feature extraction** – 1 s windows / 0.5 s hop: RMS (mean, std), ZCR, spectral centroid / rolloff / bandwidth / flatness, onset strength, 13 MFCCs.
3. **Sound events first** – AST runs on 2 s windows *before* transcription because its speech probability is reused to:
   - choose which activity regions are sent to Whisper (music, applause and alarms are not), and
   - drop any transcript text decoded where the classifier is confident there is no speech.
   This removed Whisper's timestamp drift over the music intro (first segment started at 0.0 s instead of 8.2 s) and prevents hallucinated text.
4. **Transcription** – Whisper-base with word timestamps; words are regrouped at sentence ends and ≥0.45 s pauses so a segment rarely spans two speakers.
5. **Speakers** – WavLM x-vector per segment → agglomerative clustering; k chosen by silhouette, a split is only accepted if all centroid pairs are below the model's 0.86 same-speaker cosine threshold.
6. **Emotion / prosody** – wav2vec2 (IEMOCAP 4-class) per segment + YIN pitch, pitch variability, loudness, speaking rate.
7. **Anomalies** – Isolation Forest on 10 absolute + 5 contextual-novelty features (see README).
8. **Predictions** – interval metrics → mean-reverting AR(1) with walk-forward evaluation.
9. **Semantic index & summary** – MiniLM embeddings saved as `.npy`; topics via MMR; summary built only from computed facts.

## Decisions taken from measurements (not assumptions)

| Question | Tried | Result | Decision |
|---|---|---|---|
| Anomaly features | 22 raw features | glass shatter ranked 3rd (0.605), below the siren and the music intro | add 10 s contextual novelty features → glass shatter 0.739, clear #1 |
| Sudden drop to silence | two-sided novelty | music→silence transition flagged | energy/onset novelty one-sided (silence is reported by the silence detector) |
| Forecast model | ridge with 3 lags + cross-metric terms | worse than persistence on all 4 metrics (overfits ~23 points) | mean-reverting AR(1): beats persistence on all 4 |
| Topic candidates | stop-words removed before n-grams | fake phrases such as "talk product" | n-grams over the raw word sequence, no stop-words inside |
| AST speed-up | truncated positional embeddings (2 s and 4 s inputs) | 7× faster but Beep 0.81→0.22, Applause 0.50→0.12 | keep full 10.24 s input; accuracy over speed |
| Third demo voice | pitch-shifted David +4 st | 0.86 cosine to Zira → merged | David −4 st (0.28 to Zira, 0.73 to David) |

## Honesty rules built into the code

- Speakers are anonymous clusters, never identities; overlapped speech is not separated.
- Emotion labels carry a disclaimer in the API response and the UI.
- Forecast accuracy is always shown next to the persistence baseline, including when the model loses; with < 7 intervals the API returns a labelled persistence forecast and no accuracy.
- The summary contains only statements computed from analysis output or quoted transcript lines.
- Spatial positions are layout, not localisation; the UI states the channel count and the reason.
