# Roadmap

Issue-ready breakdown of the project. Each checkbox can be converted into a GitHub issue under the
corresponding milestone. `[x]` = implemented in this repository, `[ ]` = open / future work.

## Milestone 1 — Foundation
- [x] Create solution, central package management, analyzers-as-errors build
- [x] Architecture design and decision record (`docs/architecture/design-overview.md`)
- [x] Docker environment (PostgreSQL, Redis, Mosquitto, API, gateway, web)
- [x] Database schema and EF Core migrations
- [x] Base API with authentication, ProblemDetails, health checks, Swagger

## Milestone 2 — Vehicle simulation
- [x] CAN abstraction (`ICanBus`, `ICanChannel`, readers/writers)
- [x] Transports: in-memory, UDP bridge, Linux SocketCAN (vcan)
- [x] Plant model and drive cycle
- [x] ECU simulators: VCU, MCU, BMS, BCM with fault memory and diagnostic server
- [x] Vehicle signal database and codec (Intel/Motorola, E2E)
- [ ] Charging scenario (parked vehicle, AC/DC charging profile)

## Milestone 3 — Gateway
- [x] .NET worker with CAN reception, decoding, VSS normalization
- [x] Message timeout / late frame / E2E supervision and network DTCs
- [x] MQTT 5 integration with last will, reconnect and store-and-forward
- [ ] Persistent offline buffer (survives gateway restarts)

## Milestone 4 — Backend
- [x] Vehicle management and ECU inventory
- [x] Telemetry ingestion, Redis live state, sampling and retention
- [x] SignalR real-time push
- [x] Health calculation and history
- [ ] Time-series storage (TimescaleDB hypertables, continuous aggregates)
- [ ] Horizontal scaling (MQTT shared subscriptions, Redis SignalR backplane)

## Milestone 5 — Diagnostics
- [x] ECU monitoring and connectivity status
- [x] UDS-inspired services over ISO-TP-style transport
- [x] DTC lifecycle, freeze frames, clear eligibility, diagnosis interpretation
- [ ] Functional addressing (0x7DF) broadcast requests
- [ ] ReadDataByPeriodicIdentifier / dynamic data identifiers

## Milestone 6 — OTA
- [x] Package management and ECDSA P-256 signing
- [x] Vehicle-side validation (checksum, signature, target, version rules)
- [x] Deployment via UDS programming into an inactive bank
- [x] Post-install health check and automatic rollback
- [ ] Delta updates and resumable chunked download via CDN URL
- [ ] Key rotation / multiple trust anchors (Uptane-style roles)

## Milestone 7 — Dashboard
- [x] Real-time vehicle view (gauges, KPIs, health, edge pipeline)
- [x] ECU monitoring
- [x] Diagnostics UI with raw UDS exchanges
- [x] OTA UI with progress and history
- [x] Fault injection UI (simulation mode)
- [ ] Fleet overview map / multi-vehicle comparison

## Milestone 8 — Testing
- [x] Unit tests (codec, ISO-TP, UDS server, bootloader, OTA verifier, domain rules)
- [x] Integration and end-to-end scenarios (in-process platform)
- [x] Architecture tests
- [x] Fault injection
- [x] Benchmarks (BenchmarkDotNet)
- [ ] Load test harness for many simulated vehicles (experiment E3)

## Milestone 9 — Thesis
- [x] Research questions and performance methodology
- [x] Architecture and automotive documentation with diagrams
- [ ] Run experiments E1–E7 and analyse results
- [ ] Write evaluation and discussion chapters
