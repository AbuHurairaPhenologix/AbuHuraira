"""CLI: generate the deterministic synthetic benchmark.

    python -m app.training.generate_data [--config ../../config/benchmark.yaml] [--force]
"""

from __future__ import annotations

import argparse
import json
import logging
from pathlib import Path

from app.core.config import load_benchmark_config
from app.datasets.io import dataset_path, save_dataset
from app.datasets.synthetic import generate_dataset


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, default=None, help="Path to benchmark.yaml")
    parser.add_argument("--force", action="store_true", help="Regenerate even if the dataset exists")
    args = parser.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")

    config = load_benchmark_config(args.config)
    path = dataset_path(config)
    if path.exists() and not args.force:
        print(f"Dataset already exists: {path} (use --force to regenerate)")
        print(path.with_suffix(".meta.json").read_text(encoding="utf-8"))
        return 0
    meta = save_dataset(generate_dataset(config), path)
    print(f"Generated {meta['rows']} windows -> {path}")
    print(json.dumps({k: meta[k] for k in ("sha256", "seed", "countsBySplitAndLabel", "anomalyTypeCounts")}, indent=2, default=int))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
