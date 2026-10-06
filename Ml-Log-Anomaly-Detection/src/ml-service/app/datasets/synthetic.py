"""Synthetic benchmark generator (report §5.2).

Each row is one 5-minute observation window of API activity. Normal windows follow the explicit baseline
parameterisation in ``config/benchmark.yaml``; anomaly windows start from a normal window and apply a controlled,
moderate shift so that normal and anomalous distributions still overlap. A fixed seed makes the dataset exactly
reproducible. No production telemetry is used.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

import numpy as np
import pandas as pd

from app.core.config import BenchmarkConfig
from app.core.feature_schema import FEATURE_NAMES

ANOMALY_TYPES = (
    "latency_spike",
    "auth_failure_burst",
    "dependency_instability",
    "retry_burst",
    "traffic_surge",
    "error_burst",
)

SPLIT_ORDER = ("train", "validation", "test")


@dataclass(frozen=True)
class SyntheticDataset:
    frame: pd.DataFrame
    seed: int
    config_sha256: str

    def split(self, name: str) -> pd.DataFrame:
        return self.frame[self.frame["split"] == name].reset_index(drop=True)


def _beta(rng: np.random.Generator, mean: np.ndarray | float, concentration: float, size: int) -> np.ndarray:
    mean = np.clip(np.asarray(mean, dtype=float), 1e-4, 0.95)
    return rng.beta(mean * concentration, (1.0 - mean) * concentration, size=size)


def _zip(rng: np.random.Generator, mean: float, zero_inflation: float, size: int, rate_multiplier: float = 1.0) -> np.ndarray:
    """Zero-inflated Poisson with overall mean ``mean`` in the normal state; anomalies scale the Poisson rate."""
    lam = mean / (1.0 - zero_inflation) * rate_multiplier
    counts = rng.poisson(lam, size=size)
    structural_zero = rng.random(size) < zero_inflation
    return np.where(structural_zero, 0, counts)


def generate_normal(rng: np.random.Generator, n: int, normal_cfg: dict) -> pd.DataFrame:
    rc = normal_cfg["request_count"]
    lat = normal_cfg["avg_duration_ms"]
    p95 = normal_cfg["p95_duration_ms"]
    err = normal_cfg["error_rate"]
    auth = normal_cfg["auth_failure_rate"]
    dep = normal_cfg["dependency_failure_count"]
    ret = normal_cfg["retry_count"]
    ent = normal_cfg["endpoint_entropy"]

    request_count = rng.poisson(rc["lambda"], size=n)
    # Log-normal centred so that its mean is ~mean_ms: mu = ln(mean) - sigma^2/2.
    sigma = lat["sigma"]
    avg = rng.lognormal(np.log(lat["mean_ms"]) - sigma**2 / 2, sigma, size=n)
    # p95 is correlated with the mean (~2.6x with Gaussian noise) and constrained above the mean.
    ratio = rng.normal(p95["ratio_mean"], p95["ratio_std"], size=n)
    p95_values = avg * np.maximum(ratio, p95["min_ratio_over_mean"])

    frame = pd.DataFrame(
        {
            "request_count": request_count.astype(int),
            "error_rate": _beta(rng, err["mean"], err["concentration"], n),
            "avg_duration_ms": avg,
            "p95_duration_ms": p95_values,
            "auth_failure_rate": _beta(rng, auth["mean"], auth["concentration"], n),
            "dependency_failure_count": _zip(rng, dep["mean"], dep["zero_inflation"], n).astype(int),
            "retry_count": _zip(rng, ret["mean"], ret["zero_inflation"], n).astype(int),
            "endpoint_entropy": np.clip(rng.normal(ent["mean"], ent["std"], size=n), ent["min"], ent["max"]),
        }
    )
    frame["label"] = 0
    frame["anomaly_type"] = "normal"
    return frame


def apply_anomaly(rng: np.random.Generator, base: pd.DataFrame, anomaly_type: str, cfg: dict, normal_cfg: dict) -> pd.DataFrame:
    """Shift a block of normal windows according to one controlled anomaly class."""
    df = base.copy()
    n = len(df)
    p = cfg[anomaly_type]
    if anomaly_type == "latency_spike":
        m = rng.uniform(*p["duration_multiplier"], size=n)
        df["avg_duration_ms"] *= m
        df["p95_duration_ms"] *= m
    elif anomaly_type == "auth_failure_burst":
        m = rng.uniform(*p["rate_multiplier"], size=n)
        df["auth_failure_rate"] = np.clip(df["auth_failure_rate"] * m, 0, 1)
    elif anomaly_type == "dependency_instability":
        dep = normal_cfg["dependency_failure_count"]
        df["dependency_failure_count"] = _zip(rng, dep["mean"], dep["zero_inflation"], n, p["rate_multiplier"])
        lm = rng.uniform(*p["latency_multiplier"], size=n)  # a slow dependency can raise API latency (report §1.3)
        df["avg_duration_ms"] *= lm
        df["p95_duration_ms"] *= lm
    elif anomaly_type == "retry_burst":
        ret = normal_cfg["retry_count"]
        df["retry_count"] = _zip(rng, ret["mean"], ret["zero_inflation"], n, p["rate_multiplier"])
    elif anomaly_type == "traffic_surge":
        lam = normal_cfg["request_count"]["lambda"] * rng.uniform(*p["intensity_multiplier"], size=n)
        df["request_count"] = rng.poisson(lam)
        df["endpoint_entropy"] = df["endpoint_entropy"] * rng.uniform(*p["entropy_multiplier"], size=n)
    elif anomaly_type == "error_burst":
        mean = rng.uniform(*p["error_rate_mean"], size=n)
        df["error_rate"] = _beta(rng, mean, normal_cfg["error_rate"]["concentration"], n)
    else:  # pragma: no cover - guarded by config validation
        raise ValueError(f"Unknown anomaly type {anomaly_type}")
    df["label"] = 1
    df["anomaly_type"] = anomaly_type
    return df


def _allocate(total: int, weights: dict[str, float]) -> dict[str, int]:
    names = [n for n in ANOMALY_TYPES if n in weights]
    w = np.array([weights[n] for n in names], dtype=float)
    raw = w / w.sum() * total
    counts = np.floor(raw).astype(int)
    for i in np.argsort(-(raw - counts))[: total - counts.sum()]:
        counts[i] += 1
    return dict(zip(names, counts.tolist(), strict=True))


def generate_dataset(config: BenchmarkConfig) -> SyntheticDataset:
    """Generate the full benchmark with chronological train/validation/test periods."""
    rng = np.random.default_rng(config.seed)
    ds = config.dataset
    normal_cfg = ds["normal"]
    anomaly_cfg = ds["anomalies"]
    splits = config.splits
    services: list[str] = list(config.raw["services"])
    window = timedelta(minutes=int(config.raw["window_minutes"]))
    start = datetime.fromisoformat(config.raw["start_utc"].replace("Z", "+00:00")).astimezone(timezone.utc)

    total_normal = sum(s["normal"] for s in splits.values())
    total_anomaly = sum(s["anomaly"] for s in splits.values())
    if total_normal != ds["n_normal"] or total_anomaly != ds["n_anomaly"]:
        raise ValueError("Split sizes must add up to dataset.n_normal / dataset.n_anomaly")

    parts: list[pd.DataFrame] = []
    slot = 0
    for split_name in SPLIT_ORDER:
        sizes = splits[split_name]
        normal = generate_normal(rng, sizes["normal"], normal_cfg)
        allocation = _allocate(sizes["anomaly"], {k: v["weight"] for k, v in anomaly_cfg.items()})
        anomalies = [
            apply_anomaly(rng, generate_normal(rng, count, normal_cfg), name, anomaly_cfg, normal_cfg)
            for name, count in allocation.items()
            if count > 0
        ]
        block = pd.concat([normal, *anomalies], ignore_index=True)
        # Interleave anomalies at random positions inside this split's time period.
        block = block.iloc[rng.permutation(len(block))].reset_index(drop=True)
        slots = np.arange(slot, slot + len(block))
        slot += len(block)
        block["service"] = [services[s % len(services)] for s in slots]
        block["window_start_utc"] = [start + window * int(s // len(services)) for s in slots]
        block["split"] = split_name
        parts.append(block)

    frame = pd.concat(parts, ignore_index=True)
    frame["window_end_utc"] = frame["window_start_utc"] + window
    frame["environment"] = config.raw["environment"]
    frame["window_id"] = [f"bench-{i:06d}" for i in range(len(frame))]

    # Integer features stay integers; ratios bounded to [0, 1]; durations rounded like the feature service.
    for col in ("request_count", "dependency_failure_count", "retry_count"):
        frame[col] = frame[col].astype(int)
    for col in ("error_rate", "auth_failure_rate"):
        frame[col] = frame[col].clip(0.0, 1.0)
    frame["avg_duration_ms"] = frame["avg_duration_ms"].round(3)
    frame["p95_duration_ms"] = frame["p95_duration_ms"].round(3)

    columns = [
        "window_id", "service", "environment", "window_start_utc", "window_end_utc", "split", "label", "anomaly_type",
        *FEATURE_NAMES,
    ]
    return SyntheticDataset(frame=frame[columns], seed=config.seed, config_sha256=config.sha256)
