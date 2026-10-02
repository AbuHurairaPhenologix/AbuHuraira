#!/usr/bin/env bash
# Runs the infrastructure in Docker and the API + gateway (with simulated ECUs) as local processes.
#   cp .env.example .env && ./scripts/run-local.sh
# Then: cd src/frontend/autosphere-web && npm start   → http://localhost:4200
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

[[ -f .env ]] || { echo ".env not found. Run: cp .env.example .env" >&2; exit 1; }
set -a; source .env; set +a

docker compose up -d postgres redis mqtt

# Secrets are passed as environment variables only; nothing is written to configuration files.
export PGPASSWORD="$POSTGRES_PASSWORD"
export ConnectionStrings__AutoSphere="Host=localhost;Port=${POSTGRES_PORT:-5432};Database=${POSTGRES_DB:-autosphere};Username=${POSTGRES_USER:-autosphere}"
export Jwt__SigningKey="$JWT_SIGNING_KEY"
export Seed__AdminPassword="$SEED_ADMIN_PASSWORD" Seed__EngineerPassword="$SEED_ENGINEER_PASSWORD" Seed__ViewerPassword="$SEED_VIEWER_PASSWORD"
export Gateway__SecurityAccessSecret="$SECURITY_ACCESS_SECRET"

dotnet build AutoSphere.sln -v quiet
dotnet run --project src/backend/AutoSphere.Api --no-build &
API_PID=$!
trap 'kill $API_PID 2>/dev/null || true' EXIT
sleep 8
echo "API: http://localhost:5080/swagger — starting the vehicle gateway (Ctrl+C stops both)"
dotnet run --project src/gateway/AutoSphere.VehicleGateway --no-build
