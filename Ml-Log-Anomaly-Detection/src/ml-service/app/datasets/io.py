"""Dataset persistence: CSV + metadata JSON under data/generated/."""

from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

import pandas as pd

from app.core.config import BenchmarkConfig, project_root
from app.core.versions import library_versions
from app.datasets.synthetic import SyntheticDataset, generate_dataset


def dataset_path(config: BenchmarkConfig) -> Path:
    out = Path(config.dataset.get("output_dir", "data/generated"))
    if not out.is_absolute():
        out = project_root() / out
    return out / f"benchmark-{config.schema_version}-seed{config.seed}-{config.sha256[:10]}.csv"


def save_dataset(dataset: SyntheticDataset, path: Path) -> dict:
    path.parent.mkdir(parents=True, exist_ok=True)
    frame = dataset.frame.copy()
    for col in ("window_start_utc", "window_end_utc"):
        frame[col] = pd.to_datetime(frame[col], utc=True).dt.strftime("%Y-%m-%dT%H:%M:%SZ")
    frame.to_csv(path, index=False, float_format="%.6f")
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    counts = (
        dataset.frame.groupby(["split", "label"]).size().rename("windows").reset_index().to_dict(orient="records")
    )
    meta = {
        "path": path.name,
        "sha256": digest,
        "seed": dataset.seed,
        "configSha256": dataset.config_sha256,
        "rows": int(len(frame)),
        "countsBySplitAndLabel": counts,
        "anomalyTypeCounts": dataset.frame["anomaly_type"].value_counts().sort_index().to_dict(),
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "libraryVersions": library_versions(),
    }
    path.with_suffix(".meta.json").write_text(json.dumps(meta, indent=2, default=int), encoding="utf-8")
    return meta


def load_dataset(path: Path) -> pd.DataFrame:
    frame = pd.read_csv(path)
    for col in ("window_start_utc", "window_end_utc"):
        frame[col] = pd.to_datetime(frame[col], utc=True)
    return frame


def load_or_generate(config: BenchmarkConfig, regenerate: bool = False) -> tuple[pd.DataFrame, Path]:
    path = dataset_path(config)
    if regenerate or not path.exists():
        save_dataset(generate_dataset(config), path)
    return load_dataset(path), path
