# Research questions

AutoSphere is the artefact of a design-science style Master's thesis in Automotive Software Engineering.
The prototype is built to answer the following research questions with measurable evidence.

## RQ1 — Edge processing

**How effectively can a .NET-based vehicle edge gateway process and distribute real-time automotive telemetry?**

* *Artefact*: `AutoSphere.VehicleGateway` — database-driven CAN decoding, E2E checks, message supervision,
  VSS-inspired normalization, aggregated MQTT publishing with store-and-forward.
* *Evidence*: decode cost per frame, sustained frames/s, CPU and memory of the worker, behaviour under
  bus faults (timeouts, E2E errors) and cloud disconnection (buffer, flush). Experiments E1–E3 in
  [performance-testing.md](performance-testing.md).
* *Criteria*: frame processing far below the shortest cycle time (20 ms); no frame loss in steady state;
  bounded memory under disconnection; correct detection of injected bus faults.

## RQ2 — End-to-end latency

**What latency is introduced when vehicle data flows from CAN through an edge gateway, MQTT backend and SignalR dashboard?**

* *Artefact*: the complete pipeline with per-hop timestamps (`LatencyDto`) and metrics.
* *Evidence*: distributions of CAN → gateway publish, gateway → backend, backend → dashboard; impact of the
  aggregation interval and transport. Experiment E2.
* *Criteria*: identify the dominant contributor; compare against human perception of a live dashboard
  (~100–250 ms) and discuss which use cases (monitoring vs. control) the architecture suits.

## RQ3 — Service-oriented diagnostics

**How effectively can service-oriented .NET components support vehicle diagnostics and ECU lifecycle management?**

* *Artefact*: correlated request/response over MQTT, the gateway as UDS-inspired tester over ISO-TP-style
  transport, DTC lifecycle in ECU and cloud, remote ECU reset and identification reads.
* *Evidence*: diagnostic round-trip times, failure-detection time for crashed/silent ECUs, correctness of
  DTC states (active/resolved/cleared, freeze frames), behaviour on NRCs and timeouts. Experiments E4, E5.
* *Criteria*: every request is answered or times out deterministically (no orphaned sessions); diagnosis
  identifies the injected root cause; eligibility rules prevent clearing active faults.

## RQ4 — Secure OTA

**How reliably can secure OTA validation and rollback mechanisms handle successful and failed vehicle software updates?**

* *Artefact*: ECDSA-signed manifests, vehicle-side verification (SHA-256, signature, target, versions),
  UDS programming into an inactive bank, post-install health check, automatic rollback, full event history.
* *Evidence*: success/failure matrix over valid and invalid packages and faulty builds; stage durations;
  rollback duration; final installed version. Experiments E6, E7 and the automated scenarios in
  `tests/AutoSphere.IntegrationTests/Scenarios`.
* *Criteria*: 100 % of corrupted/tampered/mis-targeted/downgrade packages rejected **before** installation;
  100 % of failed health checks end with the previous version running and confirmed; no deployment remains
  in a non-terminal state (timeout supervision).

## Scope and validity

The research is conducted on a simulated vehicle (plant model + four ECUs) on in-memory, UDP or SocketCAN
transports. Results characterize the **software architecture** and are not claims about production
vehicles. Standards are used as a reference (UDS/ISO 14229, ISO-TP/ISO 15765-2, AUTOSAR E2E, COVESA VSS)
without claiming compliance; deviations are documented in the automotive documentation.
