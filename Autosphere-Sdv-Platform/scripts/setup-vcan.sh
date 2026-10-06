#!/usr/bin/env bash
# Creates a virtual SocketCAN interface (Linux only).
#   sudo ./scripts/setup-vcan.sh [vcan0]
# Inspect traffic with can-utils:  candump vcan0   |   cansend vcan0 7E2#0322F189
set -euo pipefail
IFACE="${1:-vcan0}"

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "SocketCAN is only available on Linux." >&2
  exit 1
fi

modprobe vcan
if ! ip link show "$IFACE" >/dev/null 2>&1; then
  ip link add dev "$IFACE" type vcan
fi
ip link set up "$IFACE"
echo "$IFACE is up:"
ip -details link show "$IFACE"
