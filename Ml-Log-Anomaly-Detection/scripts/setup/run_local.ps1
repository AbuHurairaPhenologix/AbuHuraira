# Run backend + ML service on the host (PostgreSQL/OpenSearch from docker compose), using secrets from .env.
#   docker compose up -d postgres opensearch
#   ./scripts/setup/run_local.ps1 ml        # terminal 1 -> http://localhost:8000
#   ./scripts/setup/run_local.ps1 backend   # terminal 2 -> http://localhost:5080/swagger
#   npm --prefix src/frontend start         # terminal 3 -> http://localhost:4200
param([ValidateSet("ml", "backend")][string]$Component = "backend")
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$envVars = @{}
Get-Content (Join-Path $root ".env") | Where-Object { $_ -match "^[A-Z_]+=" } | ForEach-Object { $k, $v = $_ -split "=", 2; $envVars[$k] = $v }

if ($Component -eq "ml") {
    $env:ML_SERVICE_TOKEN_SIGNING_KEY = $envVars["ML_SERVICE_TOKEN_SIGNING_KEY"]
    Push-Location (Join-Path $root "src/ml-service")
    try { .venv\Scripts\python -m uvicorn app.main:app --port 8000 } finally { Pop-Location }
} else {
    $port = if ($envVars["POSTGRES_HOST_PORT"]) { $envVars["POSTGRES_HOST_PORT"] } else { "5433" }
    $env:ConnectionStrings__Postgres = "Host=localhost;Port=$port;Database=$($envVars['POSTGRES_DB']);Username=$($envVars['POSTGRES_USER']);Password=$($envVars['POSTGRES_PASSWORD'])"
    $env:MlService__ServiceTokenSigningKey = $envVars["ML_SERVICE_TOKEN_SIGNING_KEY"]
    $env:Auth__SigningKey = $envVars["AUTH_SIGNING_KEY"]
    $env:Auth__DemoEngineerPassword = $envVars["DEMO_ENGINEER_PASSWORD"]
    $env:Auth__DemoAdminPassword = $envVars["DEMO_ADMIN_PASSWORD"]
    $env:Ingestion__ApiKey = $envVars["INGESTION_API_KEY"]
    $env:Models__Bootstrap__Enabled = "true"
    dotnet run --project (Join-Path $root "src/backend/AnomalyDetection.Api") --launch-profile http
}
