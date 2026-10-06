#!/usr/bin/env bash
# Runs the ECU simulator and the gateway as two separate processes connected by a CAN bus:
#   ./scripts/run-split-simulation.sh udp        # any OS: CAN-over-UDP bridge (ports 20000/20001)
#   ./scripts/run-split-simulation.sh socketcan  # Linux: real SocketCAN on vcan0 (run setup-vcan.sh first)
# Requires the backend/MQTT to be running (see run-local.sh or docker compose).
set -euo pipefail
MODE="${1:-udp}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
set -a; [[ -f .env ]] && source .env; set +a
SECRET="${SECURITY_ACCESS_SECRET:?Set SECURITY_ACCESS_SECRET (see .env.example)}"

if [[ "$MODE" == "socketcan" ]]; then
  SIM_ARGS=(--CanBus:Transport=SocketCan --CanBus:Interface=vcan0)
  GW_ARGS=(--CanBus:Transport=SocketCan --CanBus:Interface=vcan0)
else
  SIM_ARGS=(--CanBus:Transport=Udp --CanBus:UdpLocalPort=20000 --CanBus:UdpPeers:0=127.0.0.1:20001)
  GW_ARGS=(--CanBus:Transport=Udp --CanBus:UdpLocalPort=20001 --CanBus:UdpPeers:0=127.0.0.1:20000)
fi

dotnet build AutoSphere.sln -v quiet
dotnet run --project src/simulator/AutoSphere.VehicleSimulator --no-build -- "${SIM_ARGS[@]}" --Simulation:SecurityAccessSecret="$SECRET" &
SIM_PID=$!
trap 'kill $SIM_PID 2>/dev/null || true' EXIT
dotnet run --project src/gateway/AutoSphere.VehicleGateway --no-build -- "${GW_ARGS[@]}" --Simulation:Enabled=false --Gateway:SecurityAccessSecret="$SECRET"
