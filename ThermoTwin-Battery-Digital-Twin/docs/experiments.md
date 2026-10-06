# Experiments

Twelve reproducible experiments are implemented in `ThermoTwin.Application/Experiments/ExperimentRunner.cs` (1–6) and `ExperimentRunner.Mathematics.cs` (7–12). They run:

- inside the application, on the background `ExperimentWorker` (seeded automatically on first start, re-runnable from the *Experiment Results* page or `POST /api/experiments/{kind}/run`);
- headless, from **one command** that regenerates every reported number:

```bash
dotnet run -c Release --project src/backend/ThermoTwin.Cli -- docs/results
dotnet run -c Release --project src/backend/ThermoTwin.Cli -- docs/results --only FemVerification,AdjointGradientCheck
```

The CLI writes `docs/results/<kind>.json` (the same payload the API stores), CSV tables and [`summary.md`](results/summary.md). **The committed files in `docs/results/` are the source of every number in the README and in this document.** All experiments use the built-in demo scenario with fixed seeds, so they are deterministic. Only wall-clock timings vary between machines and runs (the reference run used a 12-thread desktop CPU; timings below are from that run).

| # | Kind | Output files | Wall-clock |
|---:|---|---|---:|
| 1 | `NumericalConvergence` | `numerical-convergence.json`, `convergence.csv` | 35 s |
| 2 | `Regularization` | `regularization.json/.csv` | 9 s |
| 3 | `SensorDensity` | `sensor-density.json/.csv` | 9 s |
| 4 | `NoiseRobustness` | `noise-robustness.json/.csv` | 8 s |
| 5 | `ForecastAccuracy` | `forecast-accuracy.json` | 3 s |
| 6 | `CoolingComparison` | `cooling-comparison.json/.csv` | 131 s |
| 7 | `FemVerification` | `fem-verification.json`, `fem-convergence.json/.csv`, `fvm-fem-comparison.json/.csv` | 25 s |
| 8 | `AdjointGradientCheck` | `adjoint-gradient-check.json/.csv`, `gradient-cost.csv` | 43 s |
| 9 | `OptimizationBenchmark` | `optimization-benchmark.json/.csv` | 45 s |
| 10 | `ReducedOrderModel` | `reduced-order-model.json`, `pod-spectrum.csv`, `pod-rom-comparison.json/.csv`, `rom-optimization.csv` | 12 s |
| 11 | `ReducedOrderControl` | `rom-mpc-comparison.json/.csv` | 77 s |
| 12 | `ParameterIdentifiability` | `parameter-identifiability.json`, `parameter-sensitivity.json/.csv`, `parameter-estimation.csv` | 1 s |

**Verification, validation, benchmarking.** Experiments 1, 7 and 8 are *verification*: they check that equations and gradients are solved correctly, against exact or manufactured solutions and independent methods. Experiments 2–6 and 9–12 are *synthetic benchmarks*: the "physical" battery is an 80×40 finite-volume plant with an exact Gaussian defect and sensor noise. No experiment is a *validation* against measured battery data.

---

## 1. Finite-volume convergence, stability, conservation, performance

- **Question.** Does the finite-volume θ-scheme converge at its theoretical rates, and where is explicit Euler stable?
- **Configuration.** The exact Dirichlet eigenmode (see [mathematics.md §13](mathematics.md#13-verification-problems-validationconvergencestudycs-validationfemverificationstudycs)), 0.2 × 0.1 m, α = 8·10⁻⁶ m²/s. Spatial study on 20×10 … 160×80 with Δt = 0.2h²/α. Temporal study on 40×20 against the exact semi-discrete solution. Stability probe at Δt/Δt<sub>crit</sub> ∈ {0.5 … 1.5}. Energy balance on an insulated cell.
- **Expected.** Spatial order 2; temporal order 1 (Euler) and 2 (CN); divergence just above Δt<sub>crit</sub> = 2/ρ(M); energy conserved to round-off.
- **Result.** Spatial order 2.000 / 1.999 / 2.000 (EE / IE / CN, finest pair). Temporal order 1.000 / 0.997 / 2.000. Explicit Euler stays bounded at 0.99 Δt<sub>crit</sub> (‖T‖∞ = 2.6·10⁻⁴) and diverges at 1.01 Δt<sub>crit</sub> (7.4·10¹). Relative energy imbalance 7.4·10⁻¹⁶ / 5.5·10⁻¹⁴ / 7.9·10⁻¹⁴.
- **Interpretation.** The discretisation behaves exactly as the analysis predicts. The stability limit from power iteration is sharp to 1 %.

## 2. Inverse problem regularisation

- **Question.** Which λ-selection rule recovers the hidden source best from 12 noisy sensors?
- **Configuration.** Demo charge with 15 % baseline cooling. At the end, m = 4320 samples and n = 91 unknowns. Three priors (I, ∇<sub>h</sub>, Δ<sub>h</sub>), four rules (L-curve, GCV, discrepancy τ = 1, oracle).
- **Result (gradient prior).** GCV gives 0.053 K temperature RMSE, 2.3 mm hotspot error and 42.5 % source error. The oracle reaches 0.052 K, 2.1 mm and 41.9 %. The L-curve corner over-smooths (9.0 mm). The discrepancy principle fails: its target σ√m = 6.57 K lies below the residual floor (≈ 6.60 K) set by plant/model mismatch, so it collapses to λ → 0 (126.9 mm).
- **Interpretation / limitation.** The source error stays near 40 % because a 12 mm Gaussian cannot be represented by a 16.7 mm hat basis under smoothing regularisation. The *location* is found to 2–3 mm; the *peak* is spread.

## 3. Sensor density · 4. Noise robustness

- **Result.** Temperature RMSE falls from 0.235 K (4 sensors) to 0.011 K (32). Hotspot error is non-monotone: 14.0, 6.5, 5.4, **2.3**, 5.9, 3.9, 2.3 mm, so placement matters as much as count. With noise σ from 0 to 1 K, GCV raises λ (10⁻⁸ → 1.3·10⁻²) and the RMSE degrades gracefully from 0.030 K to 0.094 K.

## 5. Forecast accuracy

- **Result.** Mean absolute error of the 600 s T<sub>max</sub> forecast: 0.98, 0.92, 0.79, 0.53 K (issued at 300, 600, 900, 1200 s). Forecasts are biased low (the smoothed source under-estimates the defect peak). That bias is why the controller keeps a 1 K margin.

## 6. Cooling strategy comparison (ground-truth plant)

- **Configuration.** Seven strategies on the 80×40 plant for the full charge. The open-loop plans are optimised on the twin's model with the source identified in a *previous* cycle.
- **Result.**

| Strategy | Peak | Energy | Feasible (≤ 45 °C) |
|---|---:|---:|:---:|
| No cooling | 61.96 °C | 0 J | ❌ |
| Constant 15 % (baseline) | 49.29 °C | 911 J | ❌ |
| Constant 100 % | 31.76 °C | 270 000 J | ✅ |
| Constant minimal-feasible (23.9 %) | 44.93 °C | 3 679 J | ✅ |
| Original optimiser (open loop) | 44.92 °C | 2 369 J | ✅ |
| **PDE-constrained optimum (open loop)** | 44.94 °C | **2 022 J** | ✅ |
| **Closed-loop MPC (adjoint)** | 44.93 °C | **2 042 J** | ✅ |

- **Interpretation.** The PDE-constrained optimum needs 45.0 % less energy than the cheapest safe constant level and 14.6 % less than the original optimiser. The original penalised only max<sub>x</sub>T, which is non-smooth, and stopped early. The MPC starts with no knowledge of the defect and still stays feasible.

---

## 7. Finite elements: convergence and FVM–FEM cross-validation

- **Question.** Is the P1 Galerkin solver correct, and do two independent discretisations of the same PDE agree?
- **Configuration.** (a) Dirichlet eigenmode; (b) manufactured solution with sink, space–time source and insulated edges. Grids 10×5 … 160×80, Crank–Nicolson with Δt ∝ h (Δt = 4 s on the coarsest grid). L² and H¹ errors are integrated with a degree-4 quadrature. (c) The full battery model (Robin edges, cold-plate sink at u = 0.15, Gaussian defect, CC–CV load, 1800 s) on 20×10 … 160×80 with both methods.
- **Metric.** RMSE at DOFs, ‖e‖<sub>L²</sub>/√|Ω|, |e|<sub>H¹</sub>/√|Ω|, observed orders; RMS/max of T<sub>FEM</sub> − T<sub>FVM</sub> at the cell centres; total energy; runtime; DOFs; non-zeros.
- **Expected.** FEM: O(h²) in L², O(h) in H¹. FVM: O(h²). The FVM–FEM difference should vanish like O(h²).
- **Result.** FEM L² order **1.999 / 1.997** and H¹ order **1.000 / 1.000** (eigenmode / manufactured, finest pair). FVM order 2.000 / 2.001. On the battery model the RMS FVM–FEM difference is 2.85·10⁻², 7.49·10⁻³, 1.90·10⁻³, **4.76·10⁻⁴ K**: it shrinks by ×3.8, ×3.9, ×4.0 per halving. Stored energy agrees to within 0.1 J of 20 657 J at 160×80, and the peak temperatures to 4 mK (49.333 vs 49.329 °C). At 40×20 the FEM has 861 nodal unknowns and 5781 non-zeros (6.7 per row, half-bandwidth 22); the FVM has 800 unknowns and 3880 non-zeros (4.9 per row, half-bandwidth 20).
- **Interpretation.** Both discretisations converge to the same limit at second order. That is strong evidence against implementation errors in either. The FVM is more accurate per DOF on these smooth problems: the eigenmode is an exact eigenvector of its stencil, and cell-centre sampling is superconvergent. The FEM's advantage (unstructured meshes, natural weak boundary terms) is not exercised by a rectangle.
- **Limitation.** Structured, uniformly refined meshes only; no adaptivity. The maximum nodal error at Robin corners converges pre-asymptotically (≈ 1.7) because of the one-directional diagonal of the triangulation, while the L² error is cleanly second order.

## 8. Discrete adjoint gradient validation

- **Question.** Is the adjoint gradient the exact gradient of the discrete objective, and how does its cost scale with the number of controls?
- **Configuration.** Full-order model 40×20 (n = 800), Crank–Nicolson Δt = 10 s, N = 180 steps, K = 18 controls, μ = 10⁴, T<sub>safe</sub> − margin = 44 °C, true defect source. Four random control vectors u ∈ [0.05, 0.55]<sup>18</sup> (penalty active). Central differences with ε = 10⁻¹ … 10⁻⁸. Taylor test along a random unit direction. The same check is run for the POD-ROM adjoint (r = 40). Cost study: fixed 30-min horizon split into K = 3 … 90 controls.
- **Metric.** ‖g<sub>adj</sub> − g<sub>FD</sub>‖/‖g<sub>FD</sub>‖ (max over the four vectors); Taylor remainders; wall time per gradient.
- **Expected.** A V-shaped error curve (O(ε²) truncation, then O(εₘ/ε) round-off). Taylor remainder R₁ = O(ε²). Adjoint cost independent of K, finite-difference cost linear in K.
- **Result.** Relative error 3.8·10⁻¹, 5.1·10⁻³, 2.3·10⁻⁵, 4.4·10⁻⁷, **1.2·10⁻⁸** (ε = 10⁻⁵), then rising to 6.7·10⁻⁷ at ε = 10⁻⁸ (round-off). Reduced-model adjoint: best 1.3·10⁻¹⁰. The Taylor remainder R₁ falls by exactly 100× per decade of ε (slope 2), while R₀ falls by 10× (slope 1). Gradient cost for K = 3 → 90: adjoint **30–33 ms (constant)**; forward FD 73 → **1569 ms**; central FD 103 → **3056 ms**. At K = 90 that is 48× (forward) and 94× (central).
- **Interpretation.** The adjoint is correct to the accuracy finite differences can resolve. The cost of the adjoint gradient is one forward plus one backward sweep, independent of K.
- **Limitation.** The squared hinge is only C¹, so the O(ε²) regime of central differences is slightly disturbed near points where cells enter or leave the active set. This is visible as small irregularities at large ε.

## 9. PDE-constrained optimisation benchmark

- **Question.** What do the adjoint gradient and the reduced model buy in an actual optimisation, and how good are the computed solutions?
- **Configuration.** The open-loop plan of experiment 6: 18 × 100 s, source identified in a previous cycle, initial guess u = 0.3, μ = 10² → 10³ → 10⁴ → 10⁵, ≤ 40 projected-BFGS iterations per stage. Four methods: (A) adjoint gradient; (B) the same algorithm with forward finite differences (ε = 10⁻⁴, sequential); (C) adjoint on the POD ROM (r = 40) with constraint screening and full-order certification; (D) the original optimiser (max-penalty, parallel finite differences). Every final plan is re-evaluated on the full model, on the plant, and with KKT diagnostics at μ = 10⁵.
- **Metric.** J, energy, model/plant peak, iterations, forward/adjoint solves, runtime, ‖P(u − ∇Φ) − u‖∞, active sets.
- **Result.**

| Method | J | Energy | Plant peak | Forward / adjoint solves | Runtime | KKT residual |
|---|---:|---:|---:|---:|---:|---:|
| (A) Adjoint · full order | 0.00787 | 2022 J | 44.94 °C | 215 / 70 | 5.6 s | **4.8·10⁻⁵** |
| (B) Finite differences · full order | 0.00789 | 2029 J | 44.93 °C | 1387 / 0 | 23.8 s | 1.5·10⁻² |
| (C) Adjoint · POD ROM r = 40 | 0.00790 | 2031 J | 44.92 °C | 185 / 64 (reduced) | 0.5 s | 1.7·10⁻² |
| (D) Original optimiser | 0.00893 | 2369 J | 44.92 °C | 955 / 0 | 12.1 s* | 1.4·10⁻² |

<sub>*parallel on 12 threads; (A)–(C) are single-threaded.</sub>

- **Interpretation.** With the same algorithm, the adjoint gradient is **4.3× faster** and needs **4.9× fewer PDE solves** than finite differences. It also converges further: the finite-difference gradient error (O(ε)) stalls the line search at a KKT residual of 1.5·10⁻², against 4.8·10⁻⁵ with the exact gradient. At the adjoint solution all 18 controls are inactive (0 < u<sub>k</sub> < 1), so stationarity means ∇Φ ≈ 0. The ROM-based plan is within 0.4 % of the full-order objective. Its KKT residual is measured on the full-order objective, which the ROM approximates; it is therefore not expected to be small.
- **Limitation.** The problem is non-convex (bilinear control); the solutions are local. All methods start from the same guess and reach objectives within 0.4 % of each other (A–C), which suggests, but does not prove, a common basin.

## 10. POD reduced-order model

- **Question.** How many POD modes are needed, how should the ROM be trained, and what accuracy and speed does it give?
- **Configuration.** Full-order prediction model n = 800, Δt = 10 s. Two training families with geometric snapshot times (steps 0, 2, 6, 13, 32, 76, 180):
  - **source-basis family:** Joule heating plus one localised source on each of the 91 hat functions of the inverse problem, under a ramped cooling schedule (92 runs, 553 snapshots);
  - **defect-lattice family:** Gaussian defects at 6 lattice locations under 4 cooling schedules (28 runs, 169 snapshots).
  
  Three held-out tests: the true defect (not a training location) under baseline cooling and under a random schedule, and the reconstructed source q̂ under a ramp. r ∈ {2, …, 80}.
- **Metric.** 1 − E(r), field RMSE over the whole trajectory, best-approximation (projection) RMSE, peak error, runtime of full-field simulation.
- **Expected.** Fast spectral decay. ROM error close to the projection error (Galerkin quasi-optimality). A training set limited to a few defect locations should fail to represent a defect elsewhere (n-width).
- **Result.** The selection rule (peak error ≤ 0.1 K and RMSE ≤ 0.05 K on every test) picks **r = 40**: 1 − E = 5.3·10⁻⁵, RMSE **0.023 K**, peak error **0.094 K** (baseline test). For every r and every test the source-basis ROM error is within 2.5 % of the projection error. The defect-lattice family captures more of its *own* snapshot energy (1 − E = 1.3·10⁻¹² at r = 30) but generalises worse: 0.158 K RMSE and 0.57 K peak error at r = 30, against 0.036 K and 0.154 K for the source-basis family. For the reconstructed source the source-basis ROM is accurate already at r = 20 (RMSE 0.018 K). Full-field simulation speed-up at r = 40 is only ×2–3, because the O(nr) field reconstruction at every step dominates.
- **ROM-accelerated optimisation** (same problem as experiment 9): full-order adjoint 6.2 s. ROM r = 20 / 30 / 40 / 60: **0.33 / 0.35 / 0.50 / 0.68 s** (×19 / ×18 / ×12.5 / ×9.1), objective gap 0.02 / 0.63 / 0.45 / 0.04 %. All plans are feasible on the full model without correction.
- **Interpretation.** "Captured energy" is not a sufficient criterion. Out-of-sample accuracy depends on whether the training set spans the right *source shapes*. The commuting-sink argument (mathematics §19) makes cooling-schedule variety unnecessary. The large optimisation speed-up comes from evaluating the state constraint only on the screened hot region (≈ 300 of 800 cells) plus O(r) dynamics, not from full-field simulation.
- **Limitation.** On a 40×20 grid the full model is already cheap; the ROM pays off far more on larger 2-D or 3-D grids. The ROM is not certified a priori: no rigorous error bound is computed, only the empirical validation and the full-order certification of each plan.

## 11. Reduced-order vs full-order MPC (closed loop on the plant)

- **Question.** Can the ROM replace the full model inside the live MPC loop without losing safety?
- **Configuration.** The live `DigitalTwinEngine` for the full 30-minute charge with no prior knowledge of the defect: re-estimation every 30 s, re-optimisation every 60 s, horizon 6 × 100 s. Three optimisers: original, adjoint full order, adjoint POD-ROM (r = 40, validation threshold 0.25 K, certification on the full model).
- **Result.**

| MPC optimiser | Plant peak | Energy | Time above 45 °C | Optimiser time per decision | Fallbacks |
|---|---:|---:|---:|---:|---:|
| Original (FD) | 44.90 °C | 2109 J | 0 s | 1333 ms | — |
| Adjoint · full order | 44.93 °C | 2042 J | 0 s | 755 ms | — |
| Adjoint · POD ROM | 44.89 °C | 2063 J | 0 s | **89 ms** | 0 of 29 |

- **Interpretation.** ROM-based MPC is **8.5× faster** per decision than full-order adjoint MPC and **15× faster** than the original. It keeps the plant below T<sub>safe</sub> with 1 % more energy. The mean ROM peak error is 0.037 K (max 0.17 K, below the 0.25 K gate), so no plan was rejected. One of the 29 decisions needed a defect-correction round to become feasible on the full model.
- **Limitation.** A single scenario. The fallback path is exercised by unit tests, not by this experiment.

## 12. Parameter sensitivity and identifiability

- **Question.** Can 12 sensors distinguish anomalous heat generation (Q, x<sub>0</sub>, y<sub>0</sub>) from uncertainty in the thermal parameters (k, h<sub>e</sub>)?
- **Configuration.** θ = (k, h<sub>e</sub>, Q, x<sub>0</sub>, y<sub>0</sub>) around the demo values with prior uncertainties δθ = (20 %, 50 %, 33 %, 10 mm, 10 mm). Sensor histories every 30 s over the charge (m = 720) at baseline cooling, Crank–Nicolson Δt = 10 s. Central-difference sensitivities (FD consistency check: relative change 4·10⁻⁷ when the step is halved). For estimation, synthetic data come from the 80×40 plant grid with σ = 0.1 K; the estimator uses the 40×20 model.
- **Result.** Signal-to-noise per one-δ change: k 2.4, h<sub>e</sub> 4.5, Q 15.9, x<sub>0</sub> 4.9, y<sub>0</sub> 5.1. The most collinear pair is **h<sub>e</sub> ↔ Q (cos = −0.90)**: more edge cooling looks like less defect power. The condition number of the normalised sensitivity matrix is 6.75 and the largest collinearity index is γ = 4.45 (all five), below the usual 10–15 threshold. Cramér–Rao relative standard deviations are 0.06–1.3 %. Estimation results:
  - **joint estimation of all five parameters:** all within **0.94 %**;
  - **defect only, k and h<sub>e</sub> exact:** within 0.13 %;
  - **defect only, k and h<sub>e</sub> mis-specified (−20 %, +50 %):** Q biased by **+7.9 %**, location by 2.5 / 3.7 mm. The residual noise estimate rises from σ̂ = 0.097 K (correct model) to 0.340 K and flags the mismatch.
- **Interpretation.** Locally, the sensors *can* separate the defect from k and h<sub>e</sub>, but only if k and h<sub>e</sub> are estimated jointly. Treating them as known when they are wrong biases the defect estimate. The h<sub>e</sub>–Q correlation is the physical reason.
- **Limitation.** Local (linearised) analysis at the true parameters, model form assumed correct, a single defect, synthetic data. It says nothing about global identifiability or multiple defects.

---

## Reproducing the screenshots

```bash
# Terminal 1 (fresh database so all twelve experiments are seeded by the worker)
dotnet run -c Release --project src/backend/ThermoTwin.Api
# Terminal 2
cd src/frontend/thermotwin-web && npm start
# Terminal 3 (Python 3 + playwright, Chrome installed)
python scripts/capture_screenshots.py --pause-at 1200
```

The script waits until all twelve experiment kinds have stored results, starts a fresh demo run, pauses it at t ≥ 1200 s so every page shows the same deterministic state, captures 25 images into `docs/screenshots/` at 1680 × 1050 (device scale 1.5) and resumes the run.
