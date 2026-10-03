# Experiments

Six reproducible experiments are implemented in `ThermoTwin.Application/Experiments/ExperimentRunner.cs`. They run:

- inside the application, on the background `ExperimentWorker` (seeded automatically on first start and re-runnable from the *Experiment Results* page or `POST /api/experiments/{kind}/run`);
- headless, from the CLI:

```bash
dotnet run -c Release --project src/backend/ThermoTwin.Cli -- docs/results
```

The CLI writes `docs/results/<kind>.json` (the same payload the API stores), CSV tables and `summary.md`. **The committed files in `docs/results/` are the source of every number in the README.** All experiments use the built-in demo scenario with fixed seeds, so they are deterministic. Only the wall-clock timings vary between machines.

| Kind | Output files | Wall-clock (reference run) |
|---|---|---:|
| `NumericalConvergence` | `numerical-convergence.json`, `convergence.csv` | 24 s |
| `Regularization` | `regularization.json`, `regularization.csv` | 7 s |
| `SensorDensity` | `sensor-density.json/.csv` | 8 s |
| `NoiseRobustness` | `noise-robustness.json/.csv` | 6 s |
| `ForecastAccuracy` | `forecast-accuracy.json` | 2 s |
| `CoolingComparison` | `cooling-comparison.json/.csv` | 73 s |

---

## 1. Numerical convergence, stability, conservation, performance

**Setup.** The exact Dirichlet sine-mode solution (see [mathematics.md §4](mathematics.md#4-verification-problem-validationconvergencestudycs)), 0.2 × 0.1 m, α = 8·10⁻⁶ m²/s.

- *Spatial:* grids 20×10, 40×20, 80×40, 160×80 with Δt = 0.2 h²/α, t<sub>end</sub> = 100 s, all three schemes.
- *Temporal:* 40×20 grid against the exact semi-discrete solution, t<sub>end</sub> = 200 s. Explicit Euler with Δt ∈ {0.64 … 0.04} s, implicit Euler and Crank–Nicolson with Δt ∈ {20 … 1.25} s.
- *Stability probe:* explicit Euler from a random field, 400 steps at Δt/Δt<sub>crit</sub> ∈ {0.5, 0.9, 0.99, 1.01, 1.1, 1.5}.
- *Energy balance:* insulated cell, non-uniform source, 600 s.
- *Performance:* Crank–Nicolson steps on 40×20 / 80×40 / 160×80 grids, the full 30-minute plant run, inverse assimilation, and a GCV solve.

**Results.**

| | Observed order (finest pair) |
|---|---|
| Spatial, explicit / implicit / CN | 2.000 / 1.999 / 2.000 |
| Temporal, explicit / implicit / CN | 1.000 / 0.997 / 2.000 |

The explicit scheme is stable at 0.99 Δt<sub>crit</sub> and blows up at 1.01 Δt<sub>crit</sub> (Δt<sub>crit</sub> = 2/ρ(M) = 0.783 s on the 40×20 grid, against a textbook bound of 0.781 s). Energy is conserved to 7.4·10⁻¹⁶ (EE), 5.5·10⁻¹⁴ (IE) and 7.9·10⁻¹⁴ (CN). A Crank–Nicolson step takes 0.059 ms on 40×20 and 0.52 ms on 80×40.

## 2. Inverse problem regularisation

**Setup.** The demo charge runs with constant 15 % cooling (no control, so the cooling history is simple). The inverse problem is solved every 60 s with GCV (timeline). At the end (m = 4320, n = 91), three priors are compared:

- the L-curve traced over 37 values of λ ∈ [10⁻⁸, 10¹] (relative), with temperature RMSE, source error and hotspot error at every point;
- four λ rules: L-curve corner, GCV, discrepancy principle (τ = 1), and the oracle (minimum source error, unavailable in practice);
- reconstructions at λ<sub>GCV</sub>/1000, λ<sub>GCV</sub> and 1000·λ<sub>GCV</sub>, to visualise under- and over-regularisation.

**Results (gradient prior).** GCV: 0.053 K RMSE, 2.3 mm hotspot error, 42.5 % source error. The oracle reaches 0.052 K, 2.1 mm and 41.9 %. The L-curve corner over-smooths (9.0 mm). The discrepancy principle fails: σ√m = 6.57 K lies below the residual floor of 6.60 K set by model error, so it picks λ → 0 and the error explodes. The timeline shows the hotspot error falling from 21.5 mm at t = 120 s to 5.8 mm at t = 180 s, then steadily to 2.3–2.8 mm over the last five minutes of the charge.

## 3. Sensor density

**Setup.** The same charge with 4, 6, 8, 12, 16, 24 and 32 sensors on near-square lattices, GCV at the end.

**Results.** The temperature RMSE falls from 0.235 K (4 sensors) to 0.011 K (32). Hotspot error is non-monotone: 14.0, 6.5, 5.4, **2.3**, 5.9, 3.9 and 2.3 mm. The geometry of the lattice relative to the defect matters as much as the number of sensors, which motivates optimal experimental design.

## 4. Noise robustness

**Setup.** 12 sensors, σ ∈ {0, 0.05, 0.1, 0.2, 0.5, 1.0} K.

**Results.** GCV raises λ with the noise level (10⁻⁸ → 1.3·10⁻²). The temperature RMSE degrades gracefully from 0.030 K to 0.094 K, and the hotspot error from about 2 mm to 10.4 mm at σ = 1 K.

## 5. Forecast accuracy

**Setup.** Baseline 15 % cooling. At t = 300, 600, 900 and 1200 s, the twin estimates (T̂, q̂) and forecasts 600 s ahead. The forecast is then compared with the plant's actual T<sub>max</sub>.

**Results.** Mean absolute errors are 0.98, 0.93, 0.79 and 0.53 K. Forecasts are biased low because the smoothed source under-estimates the defect peak. The live twin therefore controls to T<sub>safe</sub> − 1 K.

## 6. Cooling strategy comparison

**Setup.** Six strategies, each simulated on the **ground-truth plant** (80×40) for the full 30-minute charge:

| Strategy | How u(t) is chosen |
|---|---|
| No cooling | u = 0 |
| Constant baseline | u = 0.15 (vehicle default) |
| Constant full | u = 1 |
| Constant minimal-feasible | smallest constant u with forecast ≤ 44 °C (bisection on the twin model) |
| Optimised schedule | 18 × 100 s plan from the penalty/projected-gradient optimiser on the twin model, using the source identified in a *previous* charge cycle |
| Closed-loop MPC | the live `DigitalTwinEngine` in Autonomous mode, with no prior knowledge |

Metrics: peak temperature, pump energy, maximum violation of 45 °C, time above 45 °C, violation integral, and the penalised objective Φ (μ = 10⁴).

**Results.**

| Strategy | Peak | Energy | Feasible |
|---|---:|---:|:---:|
| No cooling | 61.96 °C | 0 J | ❌ |
| Constant 15 % | 49.29 °C | 911 J | ❌ |
| Constant 100 % | 31.76 °C | 270 kJ | ✅ |
| Constant 23.9 % | 44.93 °C | 3 679 J | ✅ |
| Optimised schedule | 44.92 °C | 2 369 J | ✅ |
| Closed-loop MPC | 44.90 °C | 2 109 J | ✅ |

The optimiser used 955 PDE solves (9.6 s). Compared with the cheapest safe constant level, the optimised schedule saves 35.6 % of the energy and the MPC 42.7 %. MPC beats the open-loop plan because it re-plans with fresh measurements and drives cooling to zero during the CV taper.

---

## Reproducing the screenshots

```bash
# Terminal 1
dotnet run -c Release --project src/backend/ThermoTwin.Api
# Terminal 2
cd src/frontend/thermotwin-web && npm start
# Terminal 3 (Python 3 + playwright, Chrome installed)
python scripts/capture_screenshots.py --pause-at 1200
```

The script starts a fresh demo run, waits until t ≥ 1200 s, pauses the twin so every page shows the same state, captures 14 images into `docs/screenshots/` at 1680 × 1050 (device scale 1.5) and resumes the run.
