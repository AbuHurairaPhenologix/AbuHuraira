"""Lazy, process-wide loading of the pretrained Hugging Face models (CPU by default, CUDA if available)."""
import threading
from functools import lru_cache

import torch

from app import config

DEVICE = "cuda" if torch.cuda.is_available() else "cpu"
_lock = threading.RLock()  # re-entrant: asr_pipeline() loads asr() while holding it


def _load(fn):
    @lru_cache(maxsize=1)
    def cached():
        with _lock:
            return fn()
    cached.__name__ = fn.__name__
    return cached


@_load
def asr():
    from transformers import WhisperForConditionalGeneration, WhisperProcessor
    processor = WhisperProcessor.from_pretrained(config.ASR_MODEL)
    model = WhisperForConditionalGeneration.from_pretrained(config.ASR_MODEL).to(DEVICE).eval()
    return processor, model


@_load
def asr_pipeline():
    from transformers import pipeline
    processor, model = asr()
    return pipeline("automatic-speech-recognition", model=model, tokenizer=processor.tokenizer,
                    feature_extractor=processor.feature_extractor, device=model.device)


@_load
def sound_events():
    from transformers import ASTFeatureExtractor, ASTForAudioClassification
    fe = ASTFeatureExtractor.from_pretrained(config.SOUND_EVENT_MODEL)
    model = ASTForAudioClassification.from_pretrained(config.SOUND_EVENT_MODEL).to(DEVICE).eval()
    return fe, model


@_load
def speaker():
    from transformers import AutoFeatureExtractor, WavLMForXVector
    fe = AutoFeatureExtractor.from_pretrained(config.SPEAKER_MODEL)
    model = WavLMForXVector.from_pretrained(config.SPEAKER_MODEL).to(DEVICE).eval()
    return fe, model


@_load
def emotion():
    from transformers import AutoFeatureExtractor, AutoModelForAudioClassification
    fe = AutoFeatureExtractor.from_pretrained(config.EMOTION_MODEL)
    model = AutoModelForAudioClassification.from_pretrained(config.EMOTION_MODEL).to(DEVICE).eval()
    return fe, model


@_load
def text_embedder():
    from transformers import AutoModel, AutoTokenizer
    tok = AutoTokenizer.from_pretrained(config.EMBEDDING_MODEL)
    model = AutoModel.from_pretrained(config.EMBEDDING_MODEL).to(DEVICE).eval()
    return tok, model
