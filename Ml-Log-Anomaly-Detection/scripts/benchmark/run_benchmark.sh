#!/usr/bin/env bash
# Reproduce the benchmark: generate data -> train all models -> evaluate on the held-out test split.
# Results: artifacts/models (registry) and artifacts/evaluations/<evaluation-id>/
set -euo pipefail
cd "$(dirname "$0")/../../src/ml-service"
PY="${PYTHON:-.venv/Scripts/python}"
[ -x "$PY" ] || PY=".venv/bin/python"
"$PY" -m app.training.generate_data "$@"
"$PY" -m app.training.train --algorithm all
"$PY" -m app.evaluation.evaluate --run latest
