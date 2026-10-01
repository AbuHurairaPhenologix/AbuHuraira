# Reproduce the benchmark: generate data -> train all models -> evaluate on the held-out test split.
$ErrorActionPreference = "Stop"
Push-Location (Join-Path $PSScriptRoot "..\..\src\ml-service")
try {
    $py = ".venv\Scripts\python.exe"
    & $py -m app.training.generate_data @args
    & $py -m app.training.train --algorithm all
    & $py -m app.evaluation.evaluate --run latest
} finally { Pop-Location }
