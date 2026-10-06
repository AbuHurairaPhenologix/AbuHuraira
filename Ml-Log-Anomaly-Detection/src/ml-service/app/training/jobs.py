"""Explicit, asynchronous retraining jobs (report §2.9: retraining is a controlled administrative action).

At most one job runs at a time. New versions are registered *inactive*; activation is a separate audited step
performed by an administrator through the backend.
"""

from __future__ import annotations

import hashlib
import logging
import threading
import uuid
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from datetime import datetime, timezone

from app.core.config import load_benchmark_config
from app.datasets.io import load_or_generate
from app.datasets.validation import rows_to_frame
from app.registry.registry import ModelRegistry
from app.training.trainer import ALL_ALGORITHMS, train_benchmark, train_from_rows

logger = logging.getLogger("ml.training.jobs")


@dataclass
class TrainingJob:
    jobId: str
    status: str
    algorithm: str
    source: str
    requestedBy: str
    createdAtUtc: str
    completedAtUtc: str | None = None
    modelVersions: list[str] = field(default_factory=list)
    error: str | None = None

    def to_dict(self) -> dict:
        return {
            "jobId": self.jobId,
            "status": self.status,
            "algorithm": self.algorithm,
            "source": self.source,
            "requestedBy": self.requestedBy,
            "createdAtUtc": self.createdAtUtc,
            "completedAtUtc": self.completedAtUtc,
            "modelVersions": self.modelVersions,
            "error": self.error,
        }


def _now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


class TrainingJobManager:
    def __init__(self, registry: ModelRegistry) -> None:
        self.registry = registry
        self._jobs: dict[str, TrainingJob] = {}
        self._lock = threading.Lock()
        self._executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="training")

    def get(self, job_id: str) -> TrainingJob | None:
        with self._lock:
            return self._jobs.get(job_id)

    def submit(self, algorithm: str, source: str, rows: list[dict] | None, period_start: str | None, period_end: str | None, requested_by: str) -> TrainingJob:
        if source == "benchmark" and algorithm not in (*ALL_ALGORITHMS, "all"):
            raise ValueError(f"Unknown algorithm '{algorithm}'")
        frame = None
        if source == "feature-windows":
            if not rows:
                raise ValueError("Feature-window retraining requires rows")
            frame = rows_to_frame(rows)  # validates feature names before accepting the job
        elif source != "benchmark":
            raise ValueError("source must be 'benchmark' or 'feature-windows'")

        job = TrainingJob(uuid.uuid4().hex, "queued", algorithm, source, requested_by, _now())
        with self._lock:
            self._jobs[job.jobId] = job
        self._executor.submit(self._run, job, frame, period_start, period_end)
        return job

    def _run(self, job: TrainingJob, frame, period_start, period_end) -> None:
        job.status = "running"
        try:
            config = load_benchmark_config()
            if job.source == "benchmark":
                data, path = load_or_generate(config)
                algorithms = list(ALL_ALGORITHMS) if job.algorithm == "all" else [job.algorithm]
                result = train_benchmark(config, data, self.registry, algorithms, hashlib.sha256(path.read_bytes()).hexdigest(), requested_by=job.requestedBy)
            else:
                result = train_from_rows(config, frame, self.registry, job.algorithm, period_start, period_end, job.requestedBy)
            job.modelVersions = result.versions
            job.status = "succeeded"
        except Exception as exc:  # noqa: BLE001 - job failures are reported, not raised
            logger.exception("Training job %s failed", job.jobId)
            job.status = "failed"
            job.error = f"{type(exc).__name__}: {exc}"
        finally:
            job.completedAtUtc = _now()
