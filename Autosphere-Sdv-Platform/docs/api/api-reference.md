# AutoSphere API reference

AutoSphere exposes three interfaces:

1. **REST API** (ASP.NET Core controllers) — initial loads and commands. Interactive documentation:
   Swagger UI at `/swagger` (enabled with `Swagger:Enabled`, on by default in Development and the
   Docker demo).
2. **SignalR hub** `/hubs/vehicles` — live push to dashboards. Dashboards never poll REST for live values.
3. **MQTT topics** — vehicle ⇄ cloud messaging between gateway and backend.

All JSON uses camelCase property names and enums serialized as strings; `null` properties are omitted
in MQTT payloads.

---

## 1. Authentication and authorization

`POST /api/auth/login` returns a JWT (HMAC-SHA256, default lifetime 120 min). Send it as
`Authorization: Bearer <token>`; SignalR WebSocket connections pass it as `access_token` query parameter.
Every endpoint requires an authenticated user unless stated otherwise (fallback policy).

| Policy | Roles |
|---|---|
| `ViewVehicles` | Viewer, Engineer, Administrator |
| `OperateDiagnostics` | Engineer, Administrator |
| `ManageVehicles`, `ManageOta`, `InjectFaults`, `ManageUsers` | Administrator |

Login is rate limited to `RateLimiting:LoginPermitsPerMinute` (default 10) per client IP; accounts are
locked for 5 minutes after 5 failed attempts (ASP.NET Core Identity).

---

## 2. REST endpoints

### Authentication and users

| Method | Route | Policy | Purpose |
|---|---|---|---|
| POST | `/api/auth/login` | anonymous, rate-limited | Exchange `{ userName, password }` for `{ accessToken, expiresAt, userName, roles }` |
| GET | `/api/auth/me` | authenticated | Current user name and roles |
| GET | `/api/users` | ManageUsers | List users |
| POST | `/api/users` | ManageUsers | Create user `{ userName, email, password, role }` → 201 |
| PUT | `/api/users/{userId}/role` | ManageUsers | Change role `{ role }` |
| DELETE | `/api/users/{userId}` | ManageUsers | Delete user (the last administrator cannot be deleted) → 204 |

### Vehicles, telemetry and alerts

| Method | Route | Policy | Purpose |
|---|---|---|---|
| GET | `/api/vehicles` | ViewVehicles | Vehicles with connectivity and health |
| GET | `/api/vehicles/{vehicleId}` | ViewVehicles | Details incl. ECUs, software versions, health |
| GET | `/api/vehicles/{vehicleId}/ecus` | ViewVehicles | ECU list |
| GET | `/api/vehicles/{vehicleId}/health/history?take=50` | ViewVehicles | Persisted health changes |
| POST | `/api/vehicles` | ManageVehicles | Register `{ vehicleId, vin, model, modelYear, ecus? }` → 201 |
| DELETE | `/api/vehicles/{vehicleId}` | ManageVehicles | Delete vehicle and its data → 204 |
| GET | `/api/vehicles/{vehicleId}/telemetry/latest` | ViewVehicles | Latest live state (200) or 204 if none yet |
| GET | `/api/vehicles/{vehicleId}/telemetry/history?signal=…&signal=…&from&to&maxPoints=300` | ViewVehicles | Down-sampled series of 1–10 VSS paths, max. 24 h (default: last 10 min) |
| GET | `/api/vehicles/{vehicleId}/alerts?activeOnly=false&take=100` | ViewVehicles | Alerts |
| POST | `/api/alerts/{alertId}/acknowledge` | OperateDiagnostics | Acknowledge (closes OTA event alerts) |

### Diagnostics

All diagnostic commands are forwarded to the vehicle and wait for the correlated response
(`Diagnostics:ResponseTimeoutSeconds`, default 20 s). The vehicle must be online.

| Method | Route | Policy | Purpose |
|---|---|---|---|
| POST | `/api/vehicles/{vehicleId}/diagnostics` | OperateDiagnostics | Generic request `{ operation, ecuId?, dtcCode?, dataIdentifiers?, session?, resetKind? }` |
| POST | `/api/vehicles/{vehicleId}/diagnostics/scan` | OperateDiagnostics | Full scan of all ECUs with interpreted diagnosis |
| POST | `/api/vehicles/{vehicleId}/diagnostics/session` | OperateDiagnostics | DiagnosticSessionControl `{ ecuId, session: Default\|Programming\|Extended }` |
| GET | `/api/vehicles/{vehicleId}/diagnostics?take=50` | ViewVehicles | Diagnostic session history |
| GET | `/api/diagnostics/{correlationId}` | ViewVehicles | One session by correlation id |
| GET | `/api/vehicles/{vehicleId}/dtcs?includeHistory=false` | ViewVehicles | Active and resolved DTCs (+ cleared with `includeHistory`) |
| POST | `/api/vehicles/{vehicleId}/dtcs/clear` | OperateDiagnostics | Clear `{ ecuId?, code? }` — only resolved (eligible) DTCs |
| POST | `/api/vehicles/{vehicleId}/ecus/{ecuId}/reset` | OperateDiagnostics | ECUReset `{ resetKind: HardReset\|KeyOffOnReset\|SoftReset }` |
| GET | `/api/vehicles/{vehicleId}/ecus/{ecuId}/software-version` | OperateDiagnostics | Live read of DIDs F189/F191 |
| POST | `/api/vehicles/{vehicleId}/ecus/{ecuId}/data` | OperateDiagnostics | ReadDataByIdentifier `{ dataIdentifiers: [0x0101, …] }` (max. 16) |

### OTA

| Method | Route | Policy | Purpose |
|---|---|---|---|
| GET | `/api/ota/packages` | ViewVehicles | Packages incl. SHA-256, signature, signing key id |
| GET | `/api/ota/packages/{packageId}` | ViewVehicles | One package |
| POST | `/api/ota/packages` | ManageOta | Multipart upload: `file`, `name`, `targetEcuType`, `version`, `minimumCompatibleVersion`, `releaseNotes` (≤ 2 MB request, ≤ 1 MiB payload) → 201 |
| POST | `/api/ota/packages/sample` | ManageOta | Generate a signed *simulated* firmware image `{ targetEcuType, version, minimumCompatibleVersion, bootBehavior, releaseNotes?, sizeBytes? }` → 201 |
| GET | `/api/ota/trust-anchor` | ViewVehicles | Public key vehicles must trust (PEM) |
| GET | `/api/ota/campaigns` | ViewVehicles | Campaigns with deployments |
| POST | `/api/ota/campaigns` | ManageOta | Start a campaign `{ name?, packageId, vehicleIds }` → 201 (transport corruption can only be requested via fault injection) |
| GET | `/api/ota/deployments?vehicleId=` | ViewVehicles | Deployments with event history |
| GET | `/api/ota/deployments/{deploymentId}` | ViewVehicles | One deployment |
| GET | `/api/vehicles/{vehicleId}/ota/history` | ViewVehicles | OTA history of a vehicle |

### Simulation (test harness)

| Method | Route | Policy | Purpose |
|---|---|---|---|
| GET | `/api/simulation/status` | ViewVehicles | `{ faultInjectionEnabled }` |
| GET | `/api/vehicles/{vehicleId}/faults` | InjectFaults | Injection history |
| POST | `/api/vehicles/{vehicleId}/faults` | InjectFaults | Inject/clear a fault → 202 (see [fault-injection.md](../automotive/fault-injection.md)) |

### Operations (anonymous)

| Route | Content |
|---|---|
| `/health` | All checks with JSON details: `database`, `mqtt`, `vehicle-gateways` (Degraded if no gateway online), `redis` (when configured) |
| `/health/live` | Liveness only (no dependency checks) |
| `/health/ready` | Checks tagged `ready` |
| `/swagger` | Swagger UI / `swagger/v1/swagger.json` |

---

## 3. Status codes and errors

Errors are RFC 9457 `application/problem+json` documents with `traceId` and `correlationId` extensions.
Clients may send `X-Correlation-Id` (GUID); it is echoed and attached to all log events.

| Status | Meaning |
|---|---|
| 200 / 201 / 202 / 204 | Success (202 for fault injection) |
| 400 | Validation failed (`ValidationProblemDetails` with `errors` per field) |
| 401 | Missing/invalid token, or invalid credentials at login |
| 403 | Authenticated but the role is not sufficient |
| 404 | Vehicle, ECU, DTC, package, deployment … not found |
| 409 | Business rule violated: vehicle offline, active DTC cannot be cleared, version not newer, update already running, fault injection disabled, duplicate vehicle |
| 429 | Login rate limit exceeded |
| 503 | MQTT broker unavailable; command not sent |
| 504 | Diagnostic request sent but the vehicle did not answer in time (body: session with status `TimedOut`) |
| 500 | Unexpected error (details only in Development) |

A negative UDS answer (NRC) is a valid diagnostic outcome and is returned with **200**; the session
contains `success: false` and the NRC per ECU.

---

## 4. Examples

```bash
B=http://localhost:5080
TOKEN=$(curl -s -X POST $B/api/auth/login -H 'Content-Type: application/json' \
  -d '{"userName":"engineer","password":"<from .env>"}' | jq -r .accessToken)

# Full diagnostic scan
curl -s -X POST $B/api/vehicles/AUTO-001/diagnostics/scan -H "Authorization: Bearer $TOKEN" | jq '.diagnosis, .roundTripMs'

# Clear one resolved DTC
curl -s -X POST $B/api/vehicles/AUTO-001/dtcs/clear -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"code":"P0A7E"}'

# Deploy an OTA package (administrator token)
curl -s -X POST $B/api/ota/campaigns -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  -d '{"packageId":"<package-guid>","vehicleIds":["AUTO-001"]}'

# Inject a fault (administrator token)
curl -s -X POST $B/api/vehicles/AUTO-001/faults -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  -d '{"fault":"BatteryOverheat","action":"Inject"}'
```

Excerpt of a scan response:

```json
{
  "correlationId": "6f6ebb7a-a413-4646-8cb7-32a3126a5c3f",
  "vehicleId": "AUTO-001",
  "operation": "FullScan",
  "status": "Completed",
  "roundTripMs": 273.4,
  "vehicleDurationMs": 42.6,
  "diagnosis": "Active faults: Battery Thermal Fault (P0A7E on BMS-001).",
  "results": [
    {
      "ecuId": "BMS-001", "success": true, "durationMs": 3.2,
      "exchanges": [ { "service": "ReadDtcInformation", "requestHex": "1902FF", "responseHex": "5902FF0A7E002F", "durationMs": 0.4 } ],
      "dtcs": [ { "code": "P0A7E", "ecuId": "BMS-001", "statusMask": 47, "testFailed": true, "confirmed": true,
                  "description": "Hybrid/EV Battery Pack Over Temperature", "severity": "Critical",
                  "faultCategory": "Battery Thermal Fault", "snapshot": [ { "name": "BatteryTemperature", "value": 60.4, "unit": "°C" } ] } ],
      "data": [ { "identifier": "F189", "name": "ApplicationSoftwareVersion", "value": "1.0.0" } ]
    }
  ]
}
```

---

## 5. SignalR hub `/hubs/vehicles`

Requires `ViewVehicles`. On connect, every client joins the fleet group; clients subscribe to vehicles
they display.

| Direction | Name | Payload |
|---|---|---|
| client → server | `SubscribeVehicle(vehicleId)` | join `vehicle:<ID>` group |
| client → server | `UnsubscribeVehicle(vehicleId)` | leave the group |
| server → client | `Telemetry` | `VehicleTelemetryDto` (snapshot, all signals, gateway statistics, latency breakdown) — vehicle group, ~4 Hz |
| server → client | `VehicleUpdated` | `VehicleDetailsDto` (connectivity, health, ECUs) — fleet group |
| server → client | `DtcsChanged` | `(vehicleId, DtcRecordDto[])` — vehicle group |
| server → client | `Alert` | `AlertDto` — fleet group |
| server → client | `OtaDeployment` | `OtaDeploymentDto` — fleet group |
| server → client | `DiagnosticCompleted` | `DiagnosticSessionDto` — vehicle group |
| server → client | `FaultInjection` | `FaultInjectionDto` — vehicle group |

```ts
import { HubConnectionBuilder } from '@microsoft/signalr';

const hub = new HubConnectionBuilder()
  .withUrl('/hubs/vehicles', { accessTokenFactory: () => token })
  .withAutomaticReconnect()
  .build();

hub.on('Telemetry', (t) => console.log(t.snapshot.speedKmh, t.latency.canToBackendMs));
hub.on('Alert', (a) => console.log(a.severity, a.message));
await hub.start();
await hub.invoke('SubscribeVehicle', 'AUTO-001');
```

---

## 6. MQTT interface

Broker: Eclipse Mosquitto (MQTT 5 used by gateway and backend). Topics are defined in
`AutoSphere.Contracts.Mqtt.MqttTopics`; vehicle ids may contain only letters, digits, `-` and `_`.
Every payload contains `schemaVersion` (1), `vehicleId` and UTC `timestamp`; commands and their answers
carry a `correlationId`. The backend rejects messages whose payload `vehicleId` differs from the topic
and ignores unregistered vehicles.

| Topic | Direction | QoS | Retained | Payload |
|---|---|---|---|---|
| `autosphere/vehicles/{id}/telemetry` | vehicle → cloud | 0 | no | `TelemetryMessage` (every 250 ms) |
| `autosphere/vehicles/{id}/status` | vehicle → cloud | 1 | **yes**, also last will (`Offline`) | `VehicleStatusMessage` (every 5 s and on change) |
| `autosphere/vehicles/{id}/dtcs` | vehicle → cloud | 1 | no | `DtcReportMessage` (on change, at least every 30 s) |
| `autosphere/vehicles/{id}/alerts` | vehicle → cloud | 1 | no | `AlertMessage` (rising/falling edges) |
| `autosphere/vehicles/{id}/diagnostics/request` | cloud → vehicle | 1 | no | `DiagnosticRequestMessage` |
| `autosphere/vehicles/{id}/diagnostics/response` | vehicle → cloud | 1 | no | `DiagnosticResponseMessage` |
| `autosphere/vehicles/{id}/ota/command` | cloud → vehicle | 1 | no | `OtaUpdateCommand` |
| `autosphere/vehicles/{id}/ota/status` | vehicle → cloud | 1 | no | `OtaStatusMessage` |
| `autosphere/simulation/{id}/faults` | cloud → simulator/gateway | 1 | no | `FaultInjectionCommand` |
| `autosphere/simulation/{id}/faults/ack` | simulator/gateway → cloud | 1 | no | `FaultInjectionAck` |

### Example payloads

`TelemetryMessage`
```json
{ "schemaVersion": 1, "vehicleId": "AUTO-001", "timestamp": "2026-10-02T14:55:26.250+00:00", "sequence": 239,
  "signals": [
    { "path": "Vehicle.Speed", "name": "VehicleSpeed", "value": 71.97, "unit": "km/h", "sourceEcuId": "VCU-001",
      "timestamp": "2026-10-02T14:55:26.235+00:00", "quality": "Valid" },
    { "path": "Vehicle.Powertrain.Transmission.SelectedGear", "name": "GearPosition", "value": 3, "unit": "",
      "sourceEcuId": "VCU-001", "timestamp": "2026-10-02T14:55:26.235+00:00", "quality": "Valid", "label": "Drive" } ],
  "statistics": { "framesReceived": 24, "framesDecoded": 24, "framesRejected": 0, "framesPerSecond": 96,
                  "averageDecodeMicroseconds": 27.8, "maxDecodeMicroseconds": 56.6 } }
```

`VehicleStatusMessage`
```json
{ "schemaVersion": 1, "vehicleId": "AUTO-001", "timestamp": "…", "connectivity": "Online", "gatewayId": "CGW-001",
  "gatewaySoftwareVersion": "1.0.0", "canState": "Connected", "canTransport": "in-memory", "simulationMode": true,
  "uptimeSeconds": 120,
  "ecus": [ { "ecuId": "BMS-001", "ecuType": "BatteryManagementSystem", "name": "Battery Management System",
              "status": "Online", "softwareVersion": "1.0.0", "hardwareVersion": "HW-A1", "lastSeen": "…",
              "activeDtcCount": 0, "timeoutCount": 0, "e2EErrorCount": 0 } ] }
```

`DtcReportMessage`
```json
{ "schemaVersion": 1, "vehicleId": "AUTO-001", "timestamp": "…",
  "reportedEcuIds": ["CGW-001", "VCU-001", "MCU-001", "BMS-001", "BCM-001"],
  "dtcs": [ { "code": "P0A7E", "ecuId": "BMS-001", "statusMask": 47, "testFailed": true, "confirmed": true,
              "description": "Hybrid/EV Battery Pack Over Temperature", "severity": "Critical",
              "faultCategory": "Battery Thermal Fault",
              "snapshot": [ { "name": "BatteryTemperature", "value": 61.1, "unit": "°C" } ] } ] }
```
DTCs of ECUs missing from `reportedEcuIds` (e.g. offline) keep their previous state in the backend.

`AlertMessage`
```json
{ "schemaVersion": 1, "vehicleId": "AUTO-001", "timestamp": "…", "alertKey": "battery-temperature", "state": "Raised",
  "severity": "Critical", "category": "Thermal", "message": "Battery temperature 60.2 °C exceeds the critical threshold of 60 °C",
  "ecuId": "BMS-001", "signalPath": "Vehicle.Powertrain.TractionBattery.Temperature.Average", "value": 60.2, "threshold": 60 }
```

`DiagnosticRequestMessage` / `DiagnosticResponseMessage`
```json
{ "schemaVersion": 1, "vehicleId": "AUTO-001", "timestamp": "…", "correlationId": "4b7f…", "operation": "ClearDtcs",
  "ecuId": "BMS-001", "dtcCode": "P0A7E", "requestedBy": "engineer" }

{ "schemaVersion": 1, "vehicleId": "AUTO-001", "timestamp": "…", "correlationId": "4b7f…", "operation": "ClearDtcs",
  "success": true, "totalDurationMs": 4.1,
  "results": [ { "ecuId": "BMS-001", "success": true, "durationMs": 3.9,
                 "exchanges": [ { "service": "ClearDiagnosticInformation", "requestHex": "140A7E00", "responseHex": "54", "durationMs": 1.36 } ],
                 "dtcs": [] } ] }
```

`OtaUpdateCommand` / `OtaStatusMessage`
```json
{ "schemaVersion": 1, "vehicleId": "AUTO-001", "timestamp": "…", "correlationId": "27e5…", "deploymentId": "27e5…",
  "targetEcuId": "BMS-001", "packageId": "258b…", "targetEcuType": "BatteryManagementSystem", "version": "1.1.0",
  "minimumCompatibleVersion": "1.0.0", "payloadSha256": "c322e99e…", "payloadSize": 16490,
  "packageCreatedAt": "…", "signatureBase64": "…", "payloadBase64": "…", "healthCheckSeconds": 8 }

{ "schemaVersion": 1, "vehicleId": "AUTO-001", "timestamp": "…", "correlationId": "27e5…", "deploymentId": "27e5…",
  "ecuId": "BMS-001", "status": "Verifying", "progressPercent": 30, "previousVersion": "1.0.0",
  "message": "Package verified: PayloadSize, Checksum, Signature, TargetEcu, VersionCompatibility, Downgrade",
  "verificationChecksPassed": ["PayloadSize", "Checksum", "Signature", "TargetEcu", "VersionCompatibility", "Downgrade"] }
```
The payload is embedded as Base64 (a simplification; production systems send a CDN download URL). The
deployment id doubles as correlation id.

`FaultInjectionCommand` / `FaultInjectionAck`
```json
{ "schemaVersion": 1, "vehicleId": "AUTO-001", "timestamp": "…", "correlationId": "e697…", "fault": "EcuCrash",
  "action": "Inject", "targetEcuId": "MCU-001", "durationSeconds": 20 }

{ "schemaVersion": 1, "vehicleId": "AUTO-001", "timestamp": "…", "correlationId": "e697…", "fault": "EcuCrash",
  "action": "Inject", "accepted": true, "message": "MCU-001 crashed: no CAN traffic, no diagnostic responses.",
  "handledBy": "simulator" }
```
