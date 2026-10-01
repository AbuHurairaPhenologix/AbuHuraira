"""CLI: train one or all models on the benchmark, select thresholds on validation, save and register artifacts.

    python -m app.training.train --algorithm all
    python -m app.training.train --algorithm ocsvm
"""

from __future__ import annotations

import argparse
import hashlib
import json
import logging
from pathlib import Path

from app.core.config import get_settings, load_benchmark_config
from app.datasets.io import load_or_generate
from app.registry.registry import ModelRegistry
from app.training.trainer import ALL_ALGORITHMS, train_benchmark


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--algorithm", default="all", choices=["all", *ALL_ALGORITHMS])
    parser.add_argument("--config", type=Path, default=None)
    parser.add_argument("--regenerate", action="store_true", help="Regenerate the dataset first")
    parser.add_argument("--if-missing", action="store_true", help="Skip training when the registry already has models")
    args = parser.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")

    settings = get_settings()
    registry = ModelRegistry(settings.models_dir)
    if args.if_missing and registry.list():
        print(f"Registry already contains {len(registry.list())} models; skipping training.")
        return 0

    config = load_benchmark_config(args.config)
    frame, path = load_or_generate(config, regenerate=args.regenerate)
    dataset_sha = hashlib.sha256(path.read_bytes()).hexdigest()
    algorithms = list(ALL_ALGORITHMS) if args.algorithm == "all" else [args.algorithm]
    result = train_benchmark(config, frame, registry, algorithms, dataset_sha)
    print(json.dumps({"runId": result.run_id, "recommended": result.recommended, "models": result.summaries}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
