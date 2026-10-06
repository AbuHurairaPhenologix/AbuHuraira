# UDS-inspired diagnostics

> **UDS-inspired educational implementation — not ISO 14229 compliant.** AutoSphere implements a
> subset of the Unified Diagnostic Services. Service identifiers, positive-response offsets, negative
> response codes and DTC status bits use the byte values defined in ISO 14229-1 so that the concepts
> and traces transfer to real tooling. Data identifiers in the `0x01xx–0x04xx` range, the routine ids
> `0xF001/0xF002` and the seed/key algorithm are project-specific.

Components:

| Role | Implementation |
|---|---|
| Tester (client) | `UdsClient` in `AutoSphere.Diagnostics`, used by the vehicle gateway |
| Server | `EcuSimulator.Diagnostics.cs` / `EcuSimulator.Programming.cs` in every simulated ECU |
| Transport | ISO-TP style `IsoTpChannel` over CAN ([can-bus.md](can-bus.md#4-iso-tp-style-transport-isotpchannel)) |
| Remote access | Backend → MQTT → gateway → UDS → MQTT → backend ([diagnostics-flow](../architecture/diagnostics-flow.md)) |

---

## 1. Addressing and timing

| ECU | Request id (tester → ECU) | Response id (ECU → tester) |
|---|---|---|
| VCU-001 | `0x7E0` | `0x7E8` |
| MCU-001 | `0x7E1` | `0x7E9` |
| BMS-001 | `0x7E2` | `0x7EA` |
| BCM-001 | `0x7E3` | `0x7EB` |
| functional (defined, unused) | `0x7DF` | – |

The central gateway (`CGW-001`) has no CAN diagnostic address; diagnostic requests targeted at it are
answered locally from its own fault memory.

Client timing: **P2 = 500 ms** for the first response; a negative response `0x78`
(*requestCorrectlyReceived-ResponsePending*) extends the wait to **P2\* = 5 s**. A missing response raises
`UdsTimeoutException`, which the backend reports as *no response*. Requests on one ECU channel are
strictly sequential. The server announces P2 = 50 ms and P2\* = 5000 ms in its session response.

---

## 2. Supported services

A positive response SID is the request SID + `0x40`. A negative response is `7F <SID> <NRC>`.

| SID | Service | Request | Positive response | Session / security restrictions |
|---|---|---|---|---|
| `0x10` | DiagnosticSessionControl | `10 <01\|02\|03>` | `50 <session> 00 32 01 F4` | Programming (02) only from Extended (03) — otherwise NRC `0x22` |
| `0x11` | ECUReset | `11 <01\|02\|03>` | `51 <type>` (sent **before** the reset) | NRC `0x22` while a download is active |
| `0x14` | ClearDiagnosticInformation | `14 <DTC 3 bytes \| FF FF FF>` | `54` | not in Programming (NRC `0x7F`); see §5 |
| `0x19` | ReadDTCInformation | `19 01 <mask>`, `19 02 <mask>`, `19 04 <DTC> <rec>` | `59 …` (§4) | any session |
| `0x22` | ReadDataByIdentifier | `22 <DID>` (several DIDs allowed) | `62 <DID> <data>…` | unknown or unsupported DID → `0x31` |
| `0x27` | SecurityAccess | `27 01` (seed), `27 02 <key>` | `67 01 <seed 4 B>`, `67 02` | Extended or Programming only (`0x7F`) |
| `0x31` | RoutineControl | `31 01 <RID 2 B> [opt]` | `71 01 <RID> <status>` | see §6 |
| `0x34` | RequestDownload | `34 00 44 <addr 4 B> <size 4 B>` | `74 20 04 02` (max block 1026 B) | Programming + unlocked + erased |
| `0x36` | TransferData | `36 <counter> <data>` | `76 <counter>` | Programming + unlocked + active download |
| `0x37` | RequestTransferExit | `37` | `77` | all bytes received |
| `0x3E` | TesterPresent | `3E 00` / `3E 80` | `7E 00` / *(suppressed)* | any |

Sub-function bit 7 (`0x80`, *suppressPosRspMsgIndicationBit*) is honoured for TesterPresent. Any other
SID returns NRC `0x11`.

### Negative response codes used

| NRC | Name | Typical cause in AutoSphere |
|---|---|---|
| `0x11` | serviceNotSupported | unsupported SID |
| `0x12` | subFunctionNotSupported | unknown session/reset/sub-function |
| `0x13` | incorrectMessageLengthOrInvalidFormat | truncated or malformed request |
| `0x22` | conditionsNotCorrect | programming from default session; reset during download; clearing an active DTC; rollback without a valid previous bank |
| `0x24` | requestSequenceError | key before seed; download without erase; transfer exit before all data |
| `0x31` | requestOutOfRange | unknown DID/routine/DTC; image size 0 or > 1 MiB |
| `0x33` | securityAccessDenied | programming services while locked |
| `0x35` | invalidKey | wrong SecurityAccess key |
| `0x36` | exceededNumberOfAttempts | more than 3 invalid keys |
| `0x71` | transferDataSuspended | more data than announced |
| `0x73` | wrongBlockSequenceCounter | unexpected TransferData counter |
| `0x78` | requestCorrectlyReceived-ResponsePending | understood by the client (P2\*), not emitted by the simulators |
| `0x7F` | serviceNotSupportedInActiveSession | SecurityAccess in default session, ClearDTC in programming session |

---

## 3. Sessions and security access

* Default session `0x01` after every reset. A session change locks security access and discards an
  ongoing download.
* **S3 server timer: 5 s.** Without any request for 5 s a non-default session falls back to Default
  (security locked, programming state reset). Clients keep sessions alive with TesterPresent or regular requests.
* **Seed/key.** `27 01` returns a random 4-byte seed (or `00 00 00 00` if already unlocked). The key is
  the first 4 bytes of `HMAC-SHA256(secret, seed)`, where `secret` is configured as
  `Simulation:SecurityAccessSecret` (ECUs) and `Gateway:SecurityAccessSecret` (tester). Real OEM
  algorithms are confidential; AutoSphere deliberately uses a standard MAC instead of a home-made cipher.
  After three wrong keys the ECU answers `0x36`. The attempt counter is volatile: an ECU reset (power cycle)
  releases the lock-out. A real ECU would additionally enforce a delay timer — a known simplification.

---

## 4. ReadDTCInformation formats

**DTC encoding.** The 5-character code is packed into two bytes (SAE J2012 / ISO 15031-6 convention):
bits 15–14 system (`P`=0, `C`=1, `B`=2, `U`=3), bits 13–12 first digit, then three hex nibbles. UDS
appends a failure-type byte (AutoSphere always uses `00`).

| Code | 2-byte | UDS 3-byte |
|---|---|---|
| P0A7E | `0A 7E` | `0A 7E 00` |
| U0100 | `C1 00` | `C1 00 00` |
| U0140 | `C1 40` | `C1 40 00` |

**Status byte** (ISO 14229-1 `DTCStatusMask`): bit 0 testFailed, 1 testFailedThisOperationCycle,
2 pendingDTC, 3 confirmedDTC, 4 testNotCompletedSinceLastClear, 5 testFailedSinceLastClear,
6 testNotCompletedThisOperationCycle, 7 warningIndicatorRequested. Availability mask reported: `0xFF`.

| Sub-function | Request | Response |
|---|---|---|
| `0x01` reportNumberOfDTCByStatusMask | `19 01 <mask>` | `59 01 FF 01 <count hi> <count lo>` (format id `01` = ISO 14229-1) |
| `0x02` reportDTCByStatusMask | `19 02 <mask>` | `59 02 FF { DTC(3) status(1) }*` |
| `0x04` reportDTCSnapshotRecordByDTCNumber | `19 04 <DTC(3)> <record>` | `59 04 <DTC(3)> <status> [01 <n> { DID(2) data }*]` (no record part if no snapshot is stored) |

The gateway polls `19 02 FF` every `Gateway:DtcPollIntervalMs` (2 s) and reads the snapshot (`19 04 … FF`)
once for every newly confirmed DTC.

---

## 5. Fault memory and ClearDiagnosticInformation

Each ECU owns a non-volatile `DtcMemory` (it survives ECU resets) with counter-based debouncing.
Monitors run every 100 ms:

* **Failed report:** sets testFailed, testFailedThisOperationCycle, pendingDTC and
  testFailedSinceLastClear (status `0x27`). After **5 consecutive failed cycles (≈ 0.5 s)** confirmedDTC is
  set (status `0x2F`) and the **snapshot (freeze frame)** is captured from the ECU's snapshot DIDs.
* **Passed report:** testFailed is cleared (e.g. `0x2E`). A DTC that healed before confirmation is removed.

`14 FF FF FF` clears every stored DTC **whose condition is no longer present**; `14 <DTC>` clears one DTC.

> **Project-specific policy.** Real ECUs usually clear a DTC unconditionally and set it again in the next
> monitor cycle if the fault persists. AutoSphere ECUs refuse to clear a DTC whose test is *currently
> failing* (`7F 14 22`, conditionsNotCorrect). This makes the rule "only resolved DTCs are clearable",
> which the backend also enforces (`409 Conflict`), observable end to end.

---

## 6. Routines and the A/B bootloader

| RID | Name | Preconditions | Result byte |
|---|---|---|---|
| `0xFF00` | EraseMemory | Programming + unlocked | `00`; inactive bank erased |
| `0xFF01` | CheckProgrammingDependencies | Programming + unlocked | `00` image accepted, `01` rejected |
| `0xF001` | ActivatePreviousBank *(project-specific)* | Extended or Programming; inactive bank valid (else `0x22`) | `00`; next reset boots the previous bank |
| `0xF002` | ConfirmActiveImage *(project-specific)* | any session | `00`; ends the "trial boot" state |

`0xFF01` validates the received image *structure* (simulated firmware header and target ECU type) and
then marks the inactive bank valid and pending activation. Cryptographic verification is done by the
gateway **before** flashing (see [ota-update-flow](../architecture/ota-update-flow.md)); ECU-side signature
verification is listed as future work.

TransferData block counters start at `01` and wrap `FF → 00`; a repeated counter is acknowledged without
appending (as recommended for retries). Image size is limited to 1 MiB.

---

## 7. Data identifiers

Multi-byte numeric values are big-endian; ASCII values are space-padded to their fixed length.

| DID | Name | Type / length | Scaling | ECUs |
|---|---|---|---|---|
| `F186` | ActiveDiagnosticSession | uint8 | 1 | all |
| `F187` | SparePartNumber | ASCII 12 | – | all (e.g. `AS-BMS-3000`) |
| `F189` | ApplicationSoftwareVersion | ASCII 8 | – | all (active bank) |
| `F18C` | EcuSerialNumber | ASCII 12 | – | all |
| `F190` | VIN | ASCII 17 | – | all (`WASPH1EV2T0000001`) |
| `F191` | HardwareVersion | ASCII 8 | – | all (`HW-A1`) |
| `0301` | VehicleSpeed | uint16 | 0.01 km/h | VCU |
| `0302` | Odometer | uint32 | 0.1 km | VCU |
| `0201` | MotorSpeed | int16 | 1 rpm | MCU |
| `0202` | MotorTemperature | int16 | 0.1 °C | MCU |
| `0101` | BatteryTemperature | int16 | 0.1 °C | BMS |
| `0102` | BatteryStateOfCharge | uint16 | 0.1 % | BMS |
| `0103` | BatteryVoltage | uint16 | 0.1 V | BMS |
| `0104` | BatteryCurrent | int16 | 0.1 A | BMS |
| `0401` | DoorStatusBitfield | uint8 | bit0 FL, bit1 FR, bit2 RL, bit3 RR, bit4 trunk | BCM |

Snapshot DIDs: VCU `0301, 0302`; MCU `0202, 0201`; BMS `0101, 0102, 0103, 0104`; BCM `0401`.

---

## 8. DTC catalogue

Codes are taken from SAE J2012 ranges where an appropriate public code exists; descriptions are shortened
and partly adapted (`KnownDtcs`). This is a project catalogue, not a reproduction of SAE J2012.

| Code | Description | Severity | Diagnosis category | Set by |
|---|---|---|---|---|
| P0A7E | Hybrid/EV Battery Pack Over Temperature | Critical | Battery Thermal Fault | BMS: > 60 °C, cleared < 55 °C |
| P0A7F | Hybrid/EV Battery Pack Deterioration | Warning | Battery Degradation | (catalogue only) |
| P0A9C | Battery Temperature Sensor Signal Implausible | Warning | Battery Sensor Fault | BMS: invalid-sensor fault |
| P0A2F | Drive Motor Temperature Too High | Critical | Motor Thermal Fault | MCU: > 130 °C, cleared < 120 °C |
| P0A2C | Drive Motor Temperature Sensor Signal Implausible | Warning | Motor Sensor Fault | MCU: invalid-sensor fault |
| P0606 | Control Module Processor / Self-Test Fault | Critical | ECU Internal Fault | any ECU running a `SelfTestFailure` image |
| U0100 | Lost Communication With Motor Controller | Critical | Network Communication Fault | gateway: MCU message timeout |
| U0111 | Lost Communication With Battery Management System | Critical | Network Communication Fault | gateway: BMS message timeout |
| U0140 | Lost Communication With Body Control Module | Warning | Network Communication Fault | gateway: BCM message timeout |
| U0293 | Lost Communication With Vehicle Control Unit | Critical | Network Communication Fault | gateway: VCU message timeout |
| U0001 | CAN Bus Message Timing Out Of Range | Warning | Network Timing Fault | gateway: late frames in the last 5 s |
| U0401 | Invalid Data Received (E2E Check Failed) | Warning | Network Data Integrity Fault | gateway: E2E errors in the last 5 s |

Network DTCs are stored in the gateway's own fault memory (`CGW-001`); they are suppressed while an ECU is
flagged *Updating*.

---

## 9. Worked traces

Raw hex traces are included in every diagnostic response (`exchanges`) and shown on the dashboard.

**Identification read (BMS):**
```
→ 3E 00                                   TesterPresent
← 7E 00
→ 22 F1 89                                ReadDataByIdentifier ApplicationSoftwareVersion
← 62 F1 89 31 2E 30 2E 30 20 20 20        "1.0.0   "
```

**Battery overheat present (status 0x2F = confirmed + testFailed):**
```
→ 19 02 FF                                reportDTCByStatusMask, all bits
← 59 02 FF 0A 7E 00 2F                    P0A7E
→ 19 04 0A 7E 00 FF                       snapshot of P0A7E
← 59 04 0A 7E 00 2F 01 04  01 01 <int16>  01 02 <uint16>  01 03 <uint16>  01 04 <int16>
→ 14 0A 7E 00                             clear P0A7E while it is still failing
← 7F 14 22                                conditionsNotCorrect (project policy)
```

**After the pack has cooled down (status 0x2E):**
```
→ 14 0A 7E 00
← 54
```

**Flash sequence (as executed by the gateway OTA agent):**
```
10 03 → 50 03 00 32 01 F4            extended session
10 02 → 50 02 00 32 01 F4            programming session
27 01 → 67 01 <seed>                 request seed
27 02 <key> → 67 02                  unlocked
31 01 FF 00 → 71 01 FF 00 00         erase inactive bank
34 00 44 00000000 <size> → 74 20 04 02   max block 1026 bytes (1024 data)
36 01 <1024 B> → 76 01  …            transfer blocks
37 → 77                              transfer exit
31 01 FF 01 → 71 01 FF 01 00         dependencies OK
11 01 → 51 01                        hard reset: ECU boots the new bank (trial)
… health check …
10 03 / 31 01 F0 02 → 71 01 F0 02 00 confirm image   (or 31 01 F0 01 + 11 01 for rollback)
```

---

## 10. Remote diagnostic operations

The backend exposes these operations (`DiagnosticOperation`), which the gateway maps to UDS:

| Operation | UDS on each target ECU |
|---|---|
| `FullScan` | `3E`, `22` for identification + live DIDs, `19 02 FF`, `19 04` for confirmed DTCs |
| `ReadDtcs` | `19 02 FF` + snapshots |
| `ClearDtcs` | `14` (one DTC or all), then `19 02 FF` |
| `EcuReset` | `11 <type>` |
| `ReadDataByIdentifier` | `22 <DID>` per requested DID |
| `ReadSoftwareVersion` | `22 F189`, `22 F191` |
| `SessionControl` | `10 <session>` |
| `TesterPresent` | `3E 00` |

Results include per-ECU success, NRCs, decoded data, DTCs and raw exchanges. The backend derives a
human-readable diagnosis such as *"Active faults: Battery Thermal Fault (P0A7E on BMS-001)."*
