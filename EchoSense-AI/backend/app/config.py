"""Central configuration. Every value can be overridden with an ECHOSENSE_* environment variable."""
import os
from pathlib import Path

BACKEND_DIR = Path(__file__).resolve().parent.parent
DATA_DIR = Path(os.getenv("ECHOSENSE_DATA_DIR", BACKEND_DIR / "data"))
UPLOAD_DIR = DATA_DIR / "uploads"
ARTIFACT_DIR = DATA_DIR / "artifacts"
DATABASE_URL = os.getenv("ECHOSENSE_DATABASE_URL", f"sqlite:///{(DATA_DIR / 'echosense.db').as_posix()}")

ALLOWED_EXTENSIONS = {".wav", ".mp3", ".flac"}
MAX_UPLOAD_MB = int(os.getenv("ECHOSENSE_MAX_UPLOAD_MB", "200"))

# Analysis sample rate: every pretrained model used here expects 16 kHz mono input.
TARGET_SR = 16_000

# Pretrained models (all open, non-gated Hugging Face checkpoints).
ASR_MODEL = os.getenv("ECHOSENSE_ASR_MODEL", "openai/whisper-base")
SOUND_EVENT_MODEL = os.getenv("ECHOSENSE_SED_MODEL", "MIT/ast-finetuned-audioset-10-10-0.4593")
SPEAKER_MODEL = os.getenv("ECHOSENSE_SPEAKER_MODEL", "microsoft/wavlm-base-plus-sv")
EMOTION_MODEL = os.getenv("ECHOSENSE_EMOTION_MODEL", "superb/wav2vec2-base-superb-er")
EMBEDDING_MODEL = os.getenv("ECHOSENSE_EMBEDDING_MODEL", "sentence-transformers/all-MiniLM-L6-v2")

# Analysis windows (seconds).
FEATURE_WINDOW_S = 1.0
FEATURE_HOP_S = 0.5
EVENT_WINDOW_S = 2.0

for _d in (DATA_DIR, UPLOAD_DIR, ARTIFACT_DIR):
    _d.mkdir(parents=True, exist_ok=True)
