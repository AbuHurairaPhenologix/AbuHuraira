# ThermoTwin.NET — experiment results

Generated 2026-10-04 10:25 UTC by `dotnet run --project src/backend/ThermoTwin.Cli -c Release` on the built-in demo scenario.

## Spatial convergence (exact solution, Δt ∝ h²)

| Scheme | Grid | Δx [mm] | RMSE [K] | Max error [K] | Observed order | Runtime [ms] |
|---|---|---:|---:|---:|---:|---:|
| ExplicitEuler | 20×10 | 10.00 | 1.981e-2 | 3.901e-2 | — | 3.6 |
| ExplicitEuler | 40×20 | 5.00 | 4.925e-3 | 9.812e-3 | 2.008 | 6.6 |
| ExplicitEuler | 80×40 | 2.50 | 1.230e-3 | 2.457e-3 | 2.002 | 48.0 |
| ExplicitEuler | 160×80 | 1.25 | 3.073e-4 | 6.145e-4 | 2.000 | 407.0 |
| ImplicitEuler | 20×10 | 10.00 | 7.032e-2 | 1.385e-1 | — | 8.9 |
| ImplicitEuler | 40×20 | 5.00 | 1.773e-2 | 3.531e-2 | 1.988 | 8.5 |
| ImplicitEuler | 80×40 | 2.50 | 4.441e-3 | 8.873e-3 | 1.997 | 210.7 |
| ImplicitEuler | 160×80 | 1.25 | 1.111e-3 | 2.221e-3 | 1.999 | 13720.5 |
| CrankNicolson | 20×10 | 10.00 | 2.554e-2 | 5.030e-2 | — | 1.2 |
| CrankNicolson | 40×20 | 5.00 | 6.418e-3 | 1.279e-2 | 1.993 | 20.1 |
| CrankNicolson | 80×40 | 2.50 | 1.607e-3 | 3.210e-3 | 1.998 | 561.8 |
| CrankNicolson | 160×80 | 1.25 | 4.018e-4 | 8.033e-4 | 2.000 | 15309.9 |

## Temporal convergence (exact semi-discrete solution, 40×20)

| Scheme | Δt [s] | RMSE [K] | Observed order |
|---|---:|---:|---:|
| ExplicitEuler | 0.641 | 8.683e-3 | — |
| ExplicitEuler | 0.32 | 4.332e-3 | 1.001 |
| ExplicitEuler | 0.16 | 2.165e-3 | 1.000 |
| ExplicitEuler | 0.08 | 1.083e-3 | 1.000 |
| ExplicitEuler | 0.04 | 5.413e-4 | 1.000 |
| ImplicitEuler | 20 | 2.614e-1 | — |
| ImplicitEuler | 10 | 1.330e-1 | 0.975 |
| ImplicitEuler | 5 | 6.707e-2 | 0.988 |
| ImplicitEuler | 2.5 | 3.368e-2 | 0.994 |
| ImplicitEuler | 1.25 | 1.688e-2 | 0.997 |
| CrankNicolson | 20 | 8.911e-3 | — |
| CrankNicolson | 10 | 2.223e-3 | 2.003 |
| CrankNicolson | 5 | 5.555e-4 | 2.001 |
| CrankNicolson | 2.5 | 1.389e-4 | 2.000 |
| CrankNicolson | 1.25 | 3.472e-5 | 2.000 |

## Explicit stability probe

| Δt / Δt_crit | Δt [s] | ‖T‖∞ after 400 steps | Diverged |
|---:|---:|---:|---|
| 0.5 | 0.3909 | 2.40e-3 | False |
| 0.9 | 0.7037 | 3.84e-4 | False |
| 0.99 | 0.7741 | 2.64e-4 | False |
| 1.01 | 0.7897 | 7.41e+1 | True |
| 1.1 | 0.8601 | 1.29e+30 | True |
| 1.5 | 1.1728 | 7.41e+118 | True |

## Energy conservation and performance

- ExplicitEuler: injected 9820.701 J, stored 9820.701 J, relative imbalance 7.4e-16
- ImplicitEuler: injected 9820.701 J, stored 9820.701 J, relative imbalance 5.5e-14
- CrankNicolson: injected 9820.701 J, stored 9820.701 J, relative imbalance 7.9e-14

| Workload | Steps | Total [ms] | Per step [ms] |
|---|---:|---:|---:|
| Crank–Nicolson step 40×20 | 200 | 23.8 | 0.119 |
| Crank–Nicolson step 80×40 | 200 | 173.2 | 0.866 |
| Crank–Nicolson step 160×80 | 200 | 1161.9 | 5.810 |
| Plant simulation 1800 s (80×40) | 360 | 380.4 | 1.057 |
| Inverse assimilation, 91 basis responses | 360 | 1526.4 | 4.240 |
| Tikhonov solve incl. GCV λ search | 1 | 314.8 | 314.775 |

## Inverse problem (m = 4320, n = 91, σ = 0.1 K, σ√m = 6.57 K)

| Regulariser | Rule | λ (rel.) | ‖Aq−d‖ [K] | T RMSE [K] | Source error | Hotspot error [mm] |
|---|---|---:|---:|---:|---:|---:|
| Identity | L-curve corner | 1.0e-3 | 6.655 | 0.066 | 44.8 % | 9.5 |
| Identity | GCV | 2.2e-5 | 6.624 | 0.051 | 40.8 % | 3.4 |
| Identity | Discrepancy principle | 1.0e-10 | 6.598 | 4.482 | 5,044.0 % | 127.1 |
| Identity | Oracle (min. source error) | 3.2e-5 | 6.625 | 0.051 | 40.4 % | 3.2 |
| Gradient | L-curve corner | 1.0e-2 | 6.704 | 0.070 | 45.4 % | 9.0 |
| Gradient | GCV | 3.0e-5 | 6.624 | 0.053 | 42.5 % | 2.3 |
| Gradient | Discrepancy principle | 1.0e-10 | 6.598 | 5.638 | 5,693.8 % | 126.9 |
| Gradient | Oracle (min. source error) | 5.6e-5 | 6.627 | 0.052 | 41.9 % | 2.1 |
| Laplacian | L-curve corner | 5.6e-2 | 6.726 | 0.072 | 46.8 % | 8.6 |
| Laplacian | GCV | 4.1e-5 | 6.624 | 0.057 | 45.6 % | 1.8 |
| Laplacian | Discrepancy principle | 1.0e-10 | 6.597 | 7.934 | 7,533.5 % | 127.4 |
| Laplacian | Oracle (min. source error) | 1.8e-4 | 6.631 | 0.056 | 44.0 % | 2.3 |

## Sensor count

| Sensor count | λ (rel.) | T RMSE [K] | Source error | Hotspot error [mm] |
|---:|---:|---:|---:|---:|
| 4 | 2.6e-5 | 0.235 | 51.9 % | 14.0 |
| 6 | 2.4e-4 | 0.061 | 44.6 % | 6.5 |
| 8 | 2.0e-4 | 0.083 | 48.5 % | 5.4 |
| 12 | 3.0e-5 | 0.053 | 42.5 % | 2.3 |
| 16 | 6.8e-5 | 0.040 | 36.3 % | 5.9 |
| 24 | 1.9e-5 | 0.039 | 34.5 % | 3.9 |
| 32 | 6.0e-5 | 0.011 | 14.4 % | 2.3 |

## Noise standard deviation σ

| Noise standard deviation σ | λ (rel.) | T RMSE [K] | Source error | Hotspot error [mm] |
|---:|---:|---:|---:|---:|
| 0 | 1.0e-8 | 0.030 | 30.2 % | 2.4 |
| 0.05 | 1.5e-5 | 0.050 | 41.5 % | 1.2 |
| 0.1 | 3.0e-5 | 0.053 | 42.5 % | 2.3 |
| 0.2 | 1.2e-3 | 0.062 | 44.3 % | 6.2 |
| 0.5 | 4.5e-3 | 0.072 | 45.3 % | 8.2 |
| 1 | 1.3e-2 | 0.094 | 46.1 % | 10.4 |

## Forecast accuracy (600 s horizon, baseline cooling)

| Issued at [s] | MAE [K] | Error at horizon [K] | Predicted peak [°C] | Actual peak [°C] |
|---:|---:|---:|---:|---:|
| 300 | 0.983 | -0.978 | 45.08 | 46.06 |
| 600 | 0.925 | -0.921 | 48.15 | 49.07 |
| 900 | 0.793 | -0.454 | 48.49 | 49.29 |
| 1200 | 0.529 | -0.237 | 48.47 | 49.29 |

## Cooling strategies (T_safe = 45 °C, optimiser target 44 °C)

| Strategy | Peak T [°C] | Energy [J] | Max violation [K] | Time above T_safe [s] | Objective Φ | Feasible |
|---|---:|---:|---:|---:|---:|---|
| No cooling | 61.96 | 0 | 16.96 | 1235 | 1.1723e+6 | False |
| Constant baseline (15 %) | 49.29 | 911 | 4.29 | 880 | 4.0007e+4 | False |
| Constant full cooling | 31.76 | 270000 | 0.00 | 0 | 1.0000e+0 | True |
| Constant minimal-feasible (23.9 %) | 44.93 | 3679 | 0.00 | 0 | 1.3630e-2 | True |
| Original optimiser (open loop) | 44.92 | 2369 | 0.00 | 0 | 8.9300e-3 | True |
| PDE-constrained optimum (open loop) | 44.94 | 2022 | 0.00 | 0 | 7.8700e-3 | True |
| Closed-loop MPC (live twin) | 44.93 | 2042 | 0.00 | 0 | 8.1300e-3 | True |

Optimised plan (u per 100 s): 0.092, 0.105, 0.122, 0.141, 0.160, 0.179, 0.199, 0.219, 0.240, 0.263, 0.284, 0.294, 0.244, 0.157, 0.097, 0.064, 0.048, 0.041
Optimisation: 215 PDE solves in 12.3 s; minimal feasible constant level 23.9 %.

## Finite elements: convergence against exact solutions (Crank–Nicolson, Δt ∝ h)

| Problem | Method | Grid | DOFs | RMSE [K] | L² error / √area [K] | H¹ semi-norm error / √area [K/m] | p (RMSE) | p (L²) | p (H¹) |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|
| Dirichlet eigenmode | FVM | 10×5 | 50 | 1.025e-1 | — | — | — | — | — |
| Dirichlet eigenmode | FVM | 20×10 | 200 | 2.561e-2 | — | — | 2.001 | — | — |
| Dirichlet eigenmode | FVM | 40×20 | 800 | 6.400e-3 | — | — | 2.000 | — | — |
| Dirichlet eigenmode | FVM | 80×40 | 3200 | 1.600e-3 | — | — | 2.000 | — | — |
| Dirichlet eigenmode | FVM | 160×80 | 12800 | 4.000e-4 | — | — | 2.000 | — | — |
| Dirichlet eigenmode | FEM | 10×5 | 66 | 1.738e-1 | 3.551e-1 | 3.056e+1 | — | — | — |
| Dirichlet eigenmode | FEM | 20×10 | 231 | 4.691e-2 | 9.180e-2 | 1.526e+1 | 1.889 | 1.952 | 1.002 |
| Dirichlet eigenmode | FEM | 40×20 | 861 | 1.218e-2 | 2.314e-2 | 7.626e+0 | 1.946 | 1.988 | 1.001 |
| Dirichlet eigenmode | FEM | 80×40 | 3321 | 3.101e-3 | 5.798e-3 | 3.813e+0 | 1.973 | 1.997 | 1.000 |
| Dirichlet eigenmode | FEM | 160×80 | 13041 | 7.826e-4 | 1.450e-3 | 1.906e+0 | 1.986 | 1.999 | 1.000 |
| Manufactured (sink + source, insulated) | FVM | 10×5 | 50 | 6.863e-1 | — | — | — | — | — |
| Manufactured (sink + source, insulated) | FVM | 20×10 | 200 | 1.666e-1 | — | — | 2.043 | — | — |
| Manufactured (sink + source, insulated) | FVM | 40×20 | 800 | 4.133e-2 | — | — | 2.011 | — | — |
| Manufactured (sink + source, insulated) | FVM | 80×40 | 3200 | 1.031e-2 | — | — | 2.003 | — | — |
| Manufactured (sink + source, insulated) | FVM | 160×80 | 12800 | 2.577e-3 | — | — | 2.001 | — | — |
| Manufactured (sink + source, insulated) | FEM | 10×5 | 66 | 9.919e-1 | 1.628e+0 | 1.627e+2 | — | — | — |
| Manufactured (sink + source, insulated) | FEM | 20×10 | 231 | 2.502e-1 | 4.538e-1 | 8.236e+1 | 1.987 | 1.843 | 0.982 |
| Manufactured (sink + source, insulated) | FEM | 40×20 | 861 | 6.098e-2 | 1.167e-1 | 4.132e+1 | 2.037 | 1.959 | 0.995 |
| Manufactured (sink + source, insulated) | FEM | 80×40 | 3321 | 1.493e-2 | 2.939e-2 | 2.068e+1 | 2.030 | 1.989 | 0.998 |
| Manufactured (sink + source, insulated) | FEM | 160×80 | 13041 | 3.687e-3 | 7.363e-3 | 1.034e+1 | 2.018 | 1.997 | 1.000 |

## FVM vs FEM on the battery model (Robin edges, sink, defect, constant u = 0.15)

| Grid | FVM peak [°C] | FEM peak [°C] | RMS(T_FEM − T_FVM) [K] | max abs diff [K] | FVM energy [J] | FEM energy [J] | FVM [ms] | FEM [ms] |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 20×10 | 49.342 | 49.119 | 2.85e-2 | 1.55e-1 | 20658.2 | 20655.3 | 9 | 22 |
| 40×20 | 49.328 | 49.280 | 7.49e-3 | 4.25e-2 | 20657.6 | 20656.9 | 49 | 91 |
| 80×40 | 49.335 | 49.315 | 1.90e-3 | 1.10e-2 | 20657.5 | 20657.3 | 340 | 537 |
| 160×80 | 49.333 | 49.329 | 4.76e-4 | 2.76e-3 | 20657.4 | 20657.4 | 2469 | 3383 |

- FVM: 800 unknowns (cell averages), 3880 non-zeros (4.85 per row), half-bandwidth 20
- FEM: 861 unknowns (nodal values (P1)), 5781 non-zeros (6.71 per row), half-bandwidth 22

## Discrete adjoint gradient check (Full-order FVM (40×20), n = 800, N = 180 steps, K = 18, μ = 1e+4)

| ε | max over 4 control vectors of ‖g_adj − g_FD‖/‖g_FD‖ (central FD) |
|---:|---:|
| 1e-1 | 3.752e-1 |
| 1e-2 | 5.066e-3 |
| 1e-3 | 2.326e-5 |
| 1e-4 | 4.388e-7 |
| 1e-5 | 1.169e-8 |
| 1e-6 | 1.593e-8 |
| 1e-7 | 1.727e-7 |
| 1e-8 | 6.724e-7 |

Best agreement 1.2e-8 at ε = 1e-5; reduced-model adjoint best agreement 1.3e-10.

| K controls | adjoint gradient [ms] | forward FD [ms] | central FD [ms] | solves adj / FD / central |
|---:|---:|---:|---:|---|
| 3 | 31.7 | 73 | 103 | 2 / 4 / 6 |
| 6 | 30.1 | 114 | 205 | 2 / 7 / 12 |
| 9 | 33.4 | 171 | 301 | 2 / 10 / 18 |
| 18 | 31.1 | 314 | 589 | 2 / 19 / 36 |
| 36 | 30.1 | 602 | 1193 | 2 / 37 / 72 |
| 60 | 31.6 | 1015 | 2042 | 2 / 61 / 120 |
| 90 | 32.5 | 1569 | 3056 | 2 / 91 / 180 |

## PDE-constrained optimisation (18 × 100 s, T_safe − margin = 44 °C, μ = 1e+2 → 1e+3 → 1e+4 → 1e+5)

| Method | J | Energy [J] | Model peak [°C] | Plant peak [°C] | Iter. | Forward / adjoint solves | Runtime [s] | ‖P(u−∇Φ)−u‖∞ |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Adjoint gradient · full order | 0.00787 | 2022 | 44.001 | 44.935 | 66 | 215 / 70 | 5.6 | 4.8e-5 |
| Finite-difference gradient · full order | 0.00789 | 2029 | 44.000 | 44.925 | 57 | 1387 / 0 | 23.8 | 1.5e-2 |
| Adjoint gradient · POD ROM (r = 40), FOM-certified | 0.00790 | 2031 | 43.986 | 44.919 | 60 | 185 / 64 | 0.5 | 1.7e-2 |
| Original optimiser (max-penalty, parallel FD on 12 threads) | 0.00893 | 2369 | 43.996 | 44.917 | 31 | 955 / 0 | 12.1 | 1.4e-2 |

Adjoint vs finite-difference gradient: 4.3× faster, 4.9× fewer PDE solves.

## POD reduced-order model (n = 800, selected r = 40: smallest r with peak-temperature error ≤ 0.1 K and field RMSE ≤ 0.05 K on every held-out test)

- Source-basis family: 92 training runs, 553 snapshots, POD rank 80, training 253 ms, POD 777 ms
- Defect-lattice family: 28 training runs, 169 snapshots, POD rank 34, training 82 ms, POD 38 ms

| Family | Test | r | 1 − E(r) | RMSE [K] | Projection RMSE [K] | Peak error [K] | FOM [ms] | ROM [ms] | Speed-up |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Source-basis family | True defect · baseline cooling | 5 | 2.6e-3 | 0.5586 | 0.5560 | 1.4017 | 10.3 | 1.1 | 9.1 |
| Source-basis family | True defect · baseline cooling | 10 | 9.4e-4 | 0.2106 | 0.2094 | 0.7373 | 10.3 | 1.7 | 6.0 |
| Source-basis family | True defect · baseline cooling | 20 | 2.6e-4 | 0.0951 | 0.0944 | 0.4214 | 10.3 | 2.5 | 4.1 |
| Source-basis family | True defect · baseline cooling | 30 | 1.1e-4 | 0.0360 | 0.0358 | 0.1544 | 10.3 | 3.7 | 2.8 |
| Source-basis family | True defect · baseline cooling | 40 | 5.3e-5 | 0.0231 | 0.0230 | 0.0935 | 10.3 | 5.2 | 2.0 |
| Source-basis family | True defect · baseline cooling | 60 | 1.6e-5 | 0.0098 | 0.0097 | 0.0224 | 10.3 | 6.4 | 1.6 |
| Source-basis family | True defect · baseline cooling | 80 | 1.9e-6 | 0.0079 | 0.0078 | 0.0024 | 10.3 | 10.0 | 1.0 |
| Defect-lattice family | True defect · baseline cooling | 5 | 1.2e-3 | 0.5496 | 0.5205 | 0.8940 | 10.3 | 1.2 | 8.9 |
| Defect-lattice family | True defect · baseline cooling | 10 | 2.1e-5 | 0.4851 | 0.4548 | 0.9666 | 10.3 | 1.4 | 7.1 |
| Defect-lattice family | True defect · baseline cooling | 20 | 2.1e-8 | 0.2099 | 0.2058 | 0.7997 | 10.3 | 2.3 | 4.4 |
| Defect-lattice family | True defect · baseline cooling | 30 | 1.3e-12 | 0.1579 | 0.1541 | 0.5662 | 10.3 | 4.4 | 2.3 |
| Source-basis family | True defect · random schedule | 5 | 2.6e-3 | 0.5325 | 0.5310 | 1.4783 | 14.4 | 2.1 | 6.9 |
| Source-basis family | True defect · random schedule | 10 | 9.4e-4 | 0.2086 | 0.2076 | 0.7262 | 14.4 | 1.7 | 8.3 |
| Source-basis family | True defect · random schedule | 20 | 2.6e-4 | 0.0932 | 0.0926 | 0.4262 | 14.4 | 3.2 | 4.6 |
| Source-basis family | True defect · random schedule | 30 | 1.1e-4 | 0.0360 | 0.0358 | 0.1589 | 14.4 | 4.2 | 3.5 |
| Source-basis family | True defect · random schedule | 40 | 5.3e-5 | 0.0226 | 0.0225 | 0.0872 | 14.4 | 4.9 | 3.0 |
| Source-basis family | True defect · random schedule | 60 | 1.6e-5 | 0.0097 | 0.0096 | 0.0213 | 14.4 | 7.2 | 2.0 |
| Source-basis family | True defect · random schedule | 80 | 1.9e-6 | 0.0078 | 0.0077 | 0.0023 | 14.4 | 10.3 | 1.4 |
| Defect-lattice family | True defect · random schedule | 5 | 1.2e-3 | 0.5197 | 0.5005 | 0.9807 | 14.4 | 1.3 | 10.8 |
| Defect-lattice family | True defect · random schedule | 10 | 2.1e-5 | 0.4600 | 0.4339 | 1.0329 | 14.4 | 2.2 | 6.7 |
| Defect-lattice family | True defect · random schedule | 20 | 2.1e-8 | 0.2046 | 0.2009 | 0.7856 | 14.4 | 4.2 | 3.4 |
| Defect-lattice family | True defect · random schedule | 30 | 1.3e-12 | 0.1541 | 0.1505 | 0.5648 | 14.4 | 6.1 | 2.4 |
| Source-basis family | Reconstructed source q̂ · ramp | 5 | 2.6e-3 | 0.4605 | 0.4585 | 0.6778 | 19.9 | 1.7 | 11.9 |
| Source-basis family | Reconstructed source q̂ · ramp | 10 | 9.4e-4 | 0.1332 | 0.1325 | 0.1102 | 19.9 | 2.8 | 7.0 |
| Source-basis family | Reconstructed source q̂ · ramp | 20 | 2.6e-4 | 0.0181 | 0.0179 | 0.0009 | 19.9 | 5.7 | 3.5 |
| Source-basis family | Reconstructed source q̂ · ramp | 30 | 1.1e-4 | 0.0114 | 0.0111 | 0.0236 | 19.9 | 7.3 | 2.7 |
| Source-basis family | Reconstructed source q̂ · ramp | 40 | 5.3e-5 | 0.0085 | 0.0084 | 0.0170 | 19.9 | 14.6 | 1.4 |
| Source-basis family | Reconstructed source q̂ · ramp | 60 | 1.6e-5 | 0.0026 | 0.0025 | 0.0018 | 19.9 | 12.9 | 1.5 |
| Source-basis family | Reconstructed source q̂ · ramp | 80 | 1.9e-6 | 0.0005 | 0.0005 | 0.0001 | 19.9 | 21.2 | 0.9 |
| Defect-lattice family | Reconstructed source q̂ · ramp | 5 | 1.2e-3 | 0.4462 | 0.4188 | 0.2707 | 19.9 | 1.3 | 14.9 |
| Defect-lattice family | Reconstructed source q̂ · ramp | 10 | 2.1e-5 | 0.3826 | 0.3542 | 0.2514 | 19.9 | 2.3 | 8.7 |
| Defect-lattice family | Reconstructed source q̂ · ramp | 20 | 2.1e-8 | 0.0673 | 0.0640 | 0.0369 | 19.9 | 4.7 | 4.2 |
| Defect-lattice family | Reconstructed source q̂ · ramp | 30 | 1.3e-12 | 0.0239 | 0.0228 | 0.0251 | 19.9 | 5.1 | 3.9 |

| Optimisation model | Runtime [s] | Speed-up | J | Gap | FOM peak [°C] | Feasible | Corrections | Fallback |
|---|---:|---:|---:|---:|---:|---|---:|---|
| Full-order FVM (adjoint) | 6.21 | 1.0 | 0.00787 | 0.00 % | 44.001 | True | 0 | no |
| POD ROM r = 20 | 0.33 | 19.0 | 0.00787 | 0.02 % | 44.000 | True | 0 | no |
| POD ROM r = 30 | 0.35 | 18.0 | 0.00792 | 0.63 % | 43.983 | True | 0 | no |
| POD ROM r = 40 | 0.50 | 12.5 | 0.00790 | 0.45 % | 43.986 | True | 0 | no |
| POD ROM r = 60 | 0.68 | 9.1 | 0.00787 | 0.04 % | 43.999 | True | 0 | no |

## Closed-loop MPC on the plant (T_safe = 45 °C)

| MPC optimiser | Plant peak [°C] | Energy [J] | Time above T_safe [s] | Optimiser time / call [ms] | Fallbacks | Mean ROM error [K] |
|---|---:|---:|---:|---:|---:|---:|
| Original MPC (max-penalty, finite differences) | 44.90 | 2109 | 0 | 1333 | 0 | 0.000 |
| Adjoint MPC · full-order model | 44.93 | 2042 | 0 | 755 | 0 | 0.000 |
| Adjoint MPC · POD ROM (r = 40), FOM-certified | 44.89 | 2063 | 0 | 89 | 0 | 0.037 |

## Parameter identifiability (m = 720 sensor samples, σ = 0.1 K)

| θ | nominal | δθ | RMS sensitivity [K] | SNR | Cramér–Rao std | rel. |
|---|---:|---:|---:|---:|---:|---:|
| k | 20 | 4 | 0.239 | 2.4 | 1.07e-1 | 0.54 % |
| h_e | 10 | 5 | 0.446 | 4.5 | 1.26e-1 | 1.26 % |
| Q | 4.5E+05 | 1.5E+05 | 1.594 | 15.9 | 1.13e+3 | 0.25 % |
| x₀ | 0.138 | 0.01 | 0.486 | 4.9 | 8.66e-5 | 0.06 % |
| y₀ | 0.064 | 0.01 | 0.512 | 5.1 | 9.88e-5 | 0.15 % |

Condition number of the column-normalised sensitivity matrix: 6.75. Largest collinearity indices: {k, h_e, Q, x₀, y₀} γ = 4.45; {k, h_e, Q, y₀} γ = 4.44; {k, h_e, Q, x₀} γ = 4.44.

| Estimation case | θ | truth | estimate | rel. error | std. error |
|---|---|---:|---:|---:|---:|
| Joint estimation of all five parameters | k | 20 | 20.123 | 0.61 % | 1.05e-1 |
| Joint estimation of all five parameters | h_e | 10 | 9.9061 | -0.94 % | 1.22e-1 |
| Joint estimation of all five parameters | Q | 4.5E+05 | 4.4871E+05 | -0.29 % | 1.10e+3 |
| Joint estimation of all five parameters | x₀ | 0.138 | 0.13802 | 0.01 % | 8.43e-5 |
| Joint estimation of all five parameters | y₀ | 0.064 | 0.064198 | 0.31 % | 9.71e-5 |
| Defect only · k and h_e known exactly | Q | 4.5E+05 | 4.494E+05 | -0.13 % | 3.63e+2 |
| Defect only · k and h_e known exactly | x₀ | 0.138 | 0.13795 | -0.04 % | 7.53e-5 |
| Defect only · k and h_e known exactly | y₀ | 0.064 | 0.064084 | 0.13 % | 7.50e-5 |
| Defect only · k and h_e mis-specified | Q | 4.5E+05 | 4.8537E+05 | 7.86 % | 1.29e+3 |
| Defect only · k and h_e mis-specified | x₀ | 0.138 | 0.13554 | -1.79 % | 2.22e-4 |
| Defect only · k and h_e mis-specified | y₀ | 0.064 | 0.060299 | -5.78 % | 1.99e-4 |

## Experiment wall-clock time

- NumericalConvergence: 35.1 s
- Regularization: 8.8 s
- SensorDensity: 9.4 s
- NoiseRobustness: 7.8 s
- ForecastAccuracy: 2.7 s
- CoolingComparison: 131.0 s
- FemVerification: 25.2 s
- AdjointGradientCheck: 43.0 s
- OptimizationBenchmark: 45.3 s
- ReducedOrderModel: 12.1 s
- ReducedOrderControl: 77.1 s
- ParameterIdentifiability: 0.7 s
