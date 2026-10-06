"""Sentence embeddings (all-MiniLM-L6-v2, mean pooling, L2-normalised) via plain Transformers."""
import numpy as np
import torch

from app.ai import models


@torch.inference_mode()
def embed_texts(texts: list[str], batch_size: int = 64) -> np.ndarray:
    if not texts:
        return np.zeros((0, 384), dtype=np.float32)
    tok, model = models.text_embedder()
    out = []
    for i in range(0, len(texts), batch_size):
        batch = tok(texts[i:i + batch_size], padding=True, truncation=True, max_length=256, return_tensors="pt").to(models.DEVICE)
        hidden = model(**batch).last_hidden_state
        mask = batch["attention_mask"].unsqueeze(-1).float()
        pooled = (hidden * mask).sum(1) / mask.sum(1).clamp(min=1e-9)
        out.append(torch.nn.functional.normalize(pooled, dim=-1).cpu().numpy())
    return np.concatenate(out).astype(np.float32)
