# API reference

Base URL: `http://localhost:5180`. Interactive documentation is at **`/swagger`**.

JSON is camelCase and enums are strings (`"CrankNicolson"`, `"Autonomous"`, …). Errors use RFC 7807 problem details.

## Scenarios

| Method | Path | Description |
|---|---|---|
| GET | `/api/scenarios` | Built-in scenarios (demo first) |
| GET | `/api/scenarios/{key}` | One scenario definition |

A `ScenarioDefinition` groups `geometry`, `material`, `environment`, `cooling`, `heatSource`, `sensors`, `solver`, `estimator`, `control` and `playback`. Omitted fields take the demo defaults.

`control.optimizer` selects the MPC optimiser: `AdjointFullOrder` (default: PDE-constrained, discrete adjoint gradient), `AdjointReducedOrder` (POD-Galerkin ROM with `control.romModes` modes, every plan certified on the full model, rejected if the ROM peak error exceeds `control.romValidationThreshold` K) or `PenaltyFiniteDifference` (the original max-penalty / finite-difference optimiser).

## Simulations

| Method | Path | Description |
|---|---|---|
| POST | `/api/simulations` | Validate and start a live run. Body: `{ "scenarioKey": "…" }` **or** `{ "scenario": { … }, "name": "…" }`. Returns **201** with the run, or **400** with validation errors. |
| POST | `/api/simulations/validate` | Validate a scenario only (**204** / **400**) |
| GET | `/api/simulations?take=20` | Recent runs |
| GET | `/api/simulations/{id}` | Run summary (status, peak, counterfactual peak, energy, final RMSE, source error, hotspot error) |
| GET | `/api/simulations/{id}/snapshots` | Persisted time series (one point per 30 s) |

Example validation error (explicit Euler with Δt = 5 s):

```json
{
  "title": "Validation failed",
  "status": 400,
  "detail": "Validation failed: …",
  "errors": {
    "Solver.TimeStep": [
      "Explicit Euler is unstable for Δt = 5 s on this grid (Δt must be ≤ 0.196 s). Reduce the time step or choose Crank–Nicolson."
    ]
  }
}
```

## Live digital twin

| Method | Path | Description |
|---|---|---|
| GET | `/api/twin/state?includeFields=true` | Latest `TwinFrame` (**404** before the first run) |
| GET | `/api/twin/history` | Every per-step `TwinHistoryPoint` of the live run |
| POST | `/api/twin/pause` · `/resume` · `/stop` | Session control (**202**; **409** if the state does not allow it) |
| PUT | `/api/twin/mode` | `{ "mode": "Fixed" \| "Advisory" \| "Autonomous" }` |

### `TwinFrame` (abridged; values from the demo run at t = 1210 s)

```json
{
  "runId": "…", "status": "Running", "mode": "Autonomous",
  "step": 242, "totalSteps": 360, "time": 1210, "loadFactor": 0.98,
  "truth":    { "max": 44.88, "min": 37.15, "mean": 40.13, "spread": 7.73, "maxX": 0.1375, "maxY": 0.0675 },
  "estimate": { "max": 43.95, "…": "…" },
  "estimatedHotspot": { "x": 0.137, "y": 0.069, "peakPower": 243, "areaMm2": "…", "prominence": "…" },
  "trueHotspots": [ { "x": 0.138, "y": 0.064, "peakPower": 450 } ],
  "hotspotErrorMm": 5.2, "risk": "Elevated", "mitigationActive": true,
  "cooling":   { "level": 0.244, "baselineLevel": 0.15, "recommendedPlan": [0.24, 0.15, 0.11, 0.07, 0.06, 0.05], "powerWatts": 2.18, "energyJoules": 1850,
                 "optimizer": "AdjointFullOrder", "optimizerEvaluations": "…", "adjointSolves": "…", "projectedGradientNorm": "…",
                 "romValidationError": null, "romFallback": null },
  "forecast":  { "issuedAt": 1200, "times": [ … ], "plannedMax": [ … ], "unmitigatedMax": [ … ], "plannedPeak": 44.0, "unmitigatedPeak": 44.8 },
  "estimation":{ "regularization": "Gradient", "lambdaSelection": "Gcv", "relativeLambda": 6.3e-5, "residualNorm": 5.48,
                 "measurementCount": 2880, "unknowns": 91, "temperatureRmse": 0.222, "sourceRelativeError": 0.439, "solveMs": 188 },
  "sensors": [ { "id": "S01", "x": 0.024, "y": 0.0168, "value": 37.44, "trueValue": 37.46 } ],
  "fields": {
    "trueTemperature":      { "unit": "°C",    "nx": 40, "ny": 20, "min": 37.2, "max": 44.9, "values": [[…]] },
    "estimatedTemperature": { "…": "…" }, "estimationError": { "…": "…" },
    "trueSource":           { "unit": "kW/m³", "…": "…" }, "estimatedSource": { "…": "…" },
    "predictedTemperature": { "…": "…" }
  },
  "newHistory": [ … ]
}
```

`fields.*.values` holds rows indexed by y (row 0 is the bottom edge), each a list of x values.

## Experiments

| Method | Path | Description |
|---|---|---|
| GET | `/api/experiments` | Stored results (newest first) plus the background queue status |
| GET | `/api/experiments/latest/{kind}` | Latest result for a kind, including the full JSON payload (`result`) |
| GET | `/api/experiments/{id}` | One stored result |
| POST | `/api/experiments/{kind}/run` | Queue a run (**202**; **409** if already queued or running) |

| `kind` | Payload record | Content |
|---|---|---|
| `NumericalConvergence` | `ConvergenceExperimentResult` | FVM spatial/temporal convergence, stability probe, energy balance, timings |
| `Regularization` | `RegularizationExperimentResult` | L-curve, GCV, discrepancy principle, three priors, reconstruction fields |
| `SensorDensity` · `NoiseRobustness` | `SensitivityExperimentResult` | reconstruction accuracy vs sensor count / noise |
| `ForecastAccuracy` | `ForecastExperimentResult` | predicted vs actual peak temperature |
| `CoolingComparison` | `CoolingComparisonResult` | no / constant / optimised / MPC cooling on the plant |
| `FemVerification` | `FemVerificationResult` | FEM and FVM convergence (L², H¹), FVM–FEM comparison fields, mesh, sparsity |
| `AdjointGradientCheck` | `AdjointCheckResult` | adjoint vs finite differences for several ε, Taylor test, gradient cost vs K |
| `OptimizationBenchmark` | `OptimizationBenchmarkResult` | adjoint vs FD vs ROM vs original optimiser, histories, plans, KKT diagnostics |
| `ReducedOrderModel` | `ReducedOrderResult` | POD spectra, FOM vs ROM accuracy and speed, modes, ROM-accelerated optimisation |
| `ReducedOrderControl` | `ReducedOrderControlResult` | closed-loop MPC on the plant with the three optimisers |
| `ParameterIdentifiability` | `IdentifiabilityResult` | sensitivity matrix statistics, Fisher/Cramér–Rao, collinearity, LM estimation |

Payload schemas are the records in `ThermoTwin.Application/Experiments/ExperimentResults.cs` and `MathematicsResults.cs` (TypeScript mirrors in `core/models.ts` and `core/math-models.ts`).

## Numerics

| Method | Path | Description |
|---|---|---|
| POST | `/api/numerics/stability` | Body: a `ScenarioDefinition`. Returns, for the model and plant grids and every scheme: Fourier number, Gershgorin bound and power-iteration estimate of ρ(M), explicit Δt<sub>crit</sub>, max \|G\|, stiff-mode amplification and stability, plus the validation result. |
| GET | `/api/numerics/fem/mesh?nx=10&ny=5&lengthX=0.2&lengthY=0.1` | The P1 triangulation used by the finite-element solver: node coordinates, counter-clockwise triangles, boundary edge count (**400** for nx ∉ [1, 80], ny ∉ [1, 40]). |
| POST | `/api/numerics/adjoint/gradient-check` | Body `{ "segments": 6, "segmentDuration": 100, "mu": 1e4, "epsilon": 1e-6, "seed": 1 }`. Computes the discrete-adjoint gradient of the PDE-constrained cooling objective for a random control vector on the demo cell and compares it with central finite differences (relative error, both gradients, timings, solve counts). **400** if the segment duration is not a multiple of the 10 s prediction step, K ∉ [1, 36], the horizon exceeds 3600 s, or ε ∉ [10⁻⁹, 10⁻¹]. |

```json
POST /api/numerics/adjoint/gradient-check   { "segments": 3, "segmentDuration": 15 }
→ 400 { "title": "Validation failed", "errors": { "SegmentDuration": [ "SegmentDuration must be a multiple of the 10 s prediction time step." ] } }
```

## Health

`GET /health` returns `Healthy` when SQLite is reachable.

## SignalR

Hub: **`/hubs/twin`** (JSON protocol).

| Direction | Message | Payload |
|---|---|---|
| server → client | `twinFrame` | `TwinFrame` (every frame, roughly 4–8 per second at default playback) |
| server → client | `experimentCompleted` | `{ id, kind, title, summary, createdAt, durationMs }` |
| client → server | `GetLatestFrame()` | returns the latest `TwinFrame` |

```ts
const hub = new HubConnectionBuilder().withUrl('/hubs/twin').withAutomaticReconnect().build();
hub.on('twinFrame', (frame: TwinFrame) => render(frame));
await hub.start();
```
