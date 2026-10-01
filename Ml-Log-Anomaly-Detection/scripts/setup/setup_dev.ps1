# One-time local developer setup (Windows PowerShell). Requires .NET 10 SDK, Python 3.12, Node 24, Docker.
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Push-Location $root
try {
    python scripts/setup/generate_env.py
    Push-Location src/ml-service
    if (-not (Test-Path .venv)) { py -3.12 -m venv .venv }
    .venv\Scripts\python -m pip install -r requirements-dev.txt
    Pop-Location
    dotnet restore src/backend/AnomalyDetection.sln
    npm --prefix src/frontend ci
    Write-Host "Setup complete. Next: docker compose up -d --build   (or see README 'Running locally')"
} finally { Pop-Location }
