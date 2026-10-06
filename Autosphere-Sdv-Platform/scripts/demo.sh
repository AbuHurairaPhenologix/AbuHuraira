#!/usr/bin/env bash
# Scripted thesis demonstration against a running AutoSphere stack (requires curl and jq).
#   ./scripts/demo.sh [base-url]        default: http://localhost:8080 (docker compose)
# Steps: vehicle online -> battery overheat -> DTC/critical -> diagnostics -> recovery -> clear DTC ->
#        OTA 1.0.0 -> 1.1.0 -> crash-loop build rolled back -> corrupted package rejected.
set -euo pipefail

B="${1:-http://localhost:8080}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
set -a
if [[ -f "$ROOT/.env" ]]; then source "$ROOT/.env"; fi
set +a
PASSWORD="${SEED_ADMIN_PASSWORD:?Set SEED_ADMIN_PASSWORD (see .env.example)}"
V=AUTO-001

say() { printf '\n\033[1m== %s\033[0m\n' "$*"; }

TOKEN=$(curl -sf -X POST "$B/api/auth/login" -H 'Content-Type: application/json' \
  -d "{\"userName\":\"admin\",\"password\":\"$PASSWORD\"}" | jq -r .accessToken)

api() {
  if [[ $# -ge 3 ]]; then
    curl -sf -X "$1" "$B$2" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$3"
  else
    curl -sf -X "$1" "$B$2" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json'
  fi
}

health() {
  api GET "/api/vehicles/$V" | jq -r '"\(.connectivity) | health \(.health.status) (\(.health.score)) | \(.health.summary)"'
}

snapshot() {
  api GET "/api/vehicles/$V/telemetry/latest" | jq -r '.snapshot |
    "speed \(.speedKmh) km/h | \(.motorRpm) rpm | SOC \(.batteryStateOfChargePercent) % | battery \(.batteryTemperatureC) C | motor \(.motorTemperatureC) C"'
}

wait_for() {
  local pattern="$1" attempts="$2"
  for _ in $(seq 1 "$attempts"); do
    if health | grep -q "$pattern"; then return 0; fi
    sleep 3
  done
  echo "  (timed out waiting for $pattern)"
}

package_id() {
  api GET /api/ota/packages | jq -r ".[] | select(.targetEcuType==\"$1\" and .version==\"$2\") | .id"
}

deploy() {
  local id d status
  id=$(api POST /api/ota/campaigns "{\"packageId\":\"$1\",\"vehicleIds\":[\"$V\"]}" | jq -r '.deployments[0].id')
  for _ in $(seq 1 40); do
    d=$(api GET "/api/ota/deployments/$id")
    status=$(jq -r .status <<<"$d")
    case "$status" in
      Succeeded | Failed | RolledBack | RollbackFailed)
        jq -r '"  -> \(.status): \(.fromVersion) -> \(.toVersion), installed \(.installedVersion) (\(.durationSeconds) s) \(.failureReason // "")"' <<<"$d"
        return ;;
    esac
    sleep 2
  done
}

say "1. Vehicle online"
health
snapshot

say "2. Inject battery overheat"
api POST "/api/vehicles/$V/faults" '{"fault":"BatteryOverheat"}' | jq -r '"  injected \(.fault) (correlation \(.correlationId))"'
wait_for Critical 30
health
for _ in $(seq 1 10); do
  if api GET "/api/vehicles/$V/dtcs" | jq -e 'any(.[]; .code == "P0A7E")' >/dev/null; then break; fi
  sleep 2
done
api GET "/api/vehicles/$V/dtcs" | jq -r '.[] | "  DTC \(.code) \(.description) on \(.ecuId): \(.status)"'

say "3. Remote diagnostics (UDS scan)"
api POST "/api/vehicles/$V/diagnostics/scan" | jq -r '"  \(.diagnosis) (round trip \(.roundTripMs | floor) ms)"'

say "4. Resolve the fault and clear the DTC"
api POST "/api/vehicles/$V/faults" '{"fault":"BatteryOverheat","action":"Clear"}' >/dev/null
wait_for Healthy 60
health
api POST "/api/vehicles/$V/dtcs/clear" '{"code":"P0A7E"}' | jq -r '"  ClearDiagnosticInformation: \(.status)"'

say "5. OTA: BMS 1.0.0 -> 1.1.0"
deploy "$(package_id BatteryManagementSystem 1.1.0)"

say "6. OTA: faulty build 1.2.0 (crash loop -> rollback)"
deploy "$(package_id BatteryManagementSystem 1.2.0)"

say "7. OTA: package corrupted in transit"
api POST "/api/vehicles/$V/faults" "{\"fault\":\"CorruptedOtaPackage\",\"packageId\":\"$(package_id MotorControlUnit 1.1.0)\"}" | jq -r '"  \(.result)"'
sleep 8
api GET "/api/ota/deployments?vehicleId=$V" | jq -r '.[0] | "  -> \(.status): \(.failureReason)"'

say "Done"
health
