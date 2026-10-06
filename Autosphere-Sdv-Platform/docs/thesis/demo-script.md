# Demonstration script

A 10-minute live demonstration of AutoSphere, matching the thesis' initial demo scenario. Everything runs on
one machine without vehicle hardware.

## Preparation

```bash
cp .env.example .env            # adjust passwords/keys
docker compose up -d --build    # ≈ 1 minute after images are built
```

Open <http://localhost:8080>, sign in as `admin` (password `SEED_ADMIN_PASSWORD` from `.env`).
Optional second window: <http://localhost:5080/swagger>.

An automated version of the same sequence: `./scripts/demo.sh` (requires `curl` and `jq`).

## 1. Vehicle online (Dashboard)

* AUTO-001 shows **Gateway Online**, **Health Healthy (100)**.
* Values close to: speed ≈ 72 km/h, motor ≈ 3 500 rpm, SOC ≈ 78 %, battery ≈ 38 °C, motor ≈ 67 °C
  (the drive cycle varies speed between 64 and 90 km/h).
* *Edge pipeline* panel: ≈ 95 CAN frames/s, decode time in µs, latency breakdown CAN → gateway → backend →
  dashboard. Values update without page refresh (SignalR).

## 2. Battery overheat (Fault Injection → Dashboard)

* Fault Injection page (labelled *Simulation / Development Mode*) → **Battery overheat → Inject**.
* Dashboard: battery temperature rises; health goes **Warning** (≥ 50 °C, edge alert) and then
  **Critical** (≥ 60 °C) when the BMS confirms **P0A7E** and the backend raises the critical alerts.
  BMS-001 card turns *Critical*.

## 3. Diagnostics

* Diagnostics → **Run full diagnostic scan**.
* Result: *Active faults: Battery Thermal Fault (P0A7E on BMS-001)*, round trip ≈ 0.1–0.3 s.
* Expand an ECU to show the raw UDS exchanges (e.g. `22F189` → `62F189…`, `1902FF` → `5902FF…`) and the
  freeze frame of P0A7E. *Clear* is not offered for the active DTC.

## 4. Resolve the fault

* Fault Injection → **Battery overheat → Clear** (cooling restored).
* Temperature falls; P0A7E becomes *Resolved*; health returns to **Healthy**.
* Diagnostics → **Clear** P0A7E (ClearDiagnosticInformation 0x14) → status *Cleared* in the history.

## 5. OTA update (OTA Updates)

* Installed software: BMS-001 `1.0.0`.
* *BatteryManagementSystem 1.1.0 (simulated)* → **Deploy**.
* Progress: Downloading → Verifying (checksum, signature, target ECU, version) → Installing (UDS transfer) →
  Restarting → HealthChecking → **Succeeded**; BMS-001 now `1.1.0` (≈ 10 s).

## 6. Failed update and rollback

* Deploy *BatteryManagementSystem 1.2.0 (simulated)* — a build that crashes after boot.
* Health check detects lost communication → **RollingBack** → **RolledBack**; BMS-001 back on `1.1.0`.
  The timeline shows each stage with timestamps.

## 7. Corrupted package

* Fault Injection → **Corrupted OTA package** → select *MotorControlUnit 1.1.0* → **Inject**.
* The deployment fails at **Verifying**: *SHA-256 of the received payload does not match the manifest*;
  no programming request is sent; MCU-001 stays on `1.0.0`.

## Talking points

* Where each step happens: ECU (fault memory), gateway (supervision, verification, UDS tester), backend
  (state, health, alerts), dashboard (presentation).
* What is simulated (plant, ECUs, firmware behaviour) versus implemented for real (CAN codec, ISO-TP, UDS
  services, ECDSA verification, A/B bank logic, MQTT/SignalR pipeline).
* Honest scope: UDS-inspired, AUTOSAR E2E-inspired, COVESA VSS-inspired — no certification claims.
