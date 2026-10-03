# ThermoTwin.NET — experiment results

Generated 2026-10-03 11:34 UTC by `dotnet run --project src/backend/ThermoTwin.Cli -c Release` on the built-in demo scenario.

## Spatial convergence (exact solution, Δt ∝ h²)

| Scheme | Grid | Δx [mm] | RMSE [K] | Max error [K] | Observed order | Runtime [ms] |
|---|---|---:|---:|---:|---:|---:|
| ExplicitEuler | 20×10 | 10.00 | 1.981e-2 | 3.901e-2 | — | 2.5 |
| ExplicitEuler | 40×20 | 5.00 | 4.925e-3 | 9.812e-3 | 2.008 | 3.4 |
| ExplicitEuler | 80×40 | 2.50 | 1.230e-3 | 2.457e-3 | 2.002 | 29.3 |
| ExplicitEuler | 160×80 | 1.25 | 3.073e-4 | 6.145e-4 | 2.000 | 286.0 |
| ImplicitEuler | 20×10 | 10.00 | 7.032e-2 | 1.385e-1 | — | 5.3 |
| ImplicitEuler | 40×20 | 5.00 | 1.773e-2 | 3.531e-2 | 1.988 | 5.3 |
| ImplicitEuler | 80×40 | 2.50 | 4.441e-3 | 8.873e-3 | 1.997 | 138.0 |
| ImplicitEuler | 160×80 | 1.25 | 1.111e-3 | 2.221e-3 | 1.999 | 7995.4 |
| CrankNicolson | 20×10 | 10.00 | 2.554e-2 | 5.030e-2 | — | 0.6 |
| CrankNicolson | 40×20 | 5.00 | 6.418e-3 | 1.279e-2 | 1.993 | 10.9 |
| CrankNicolson | 80×40 | 2.50 | 1.607e-3 | 3.210e-3 | 1.998 | 317.9 |
| CrankNicolson | 160×80 | 1.25 | 4.018e-4 | 8.033e-4 | 2.000 | 11384.5 |

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
| Crank–Nicolson step 40×20 | 200 | 11.8 | 0.059 |
| Crank–Nicolson step 80×40 | 200 | 103.7 | 0.518 |
| Crank–Nicolson step 160×80 | 200 | 930.5 | 4.652 |
| Plant simulation 1800 s (80×40) | 360 | 317.1 | 0.881 |
| Inverse assimilation, 91 basis responses | 360 | 1218.0 | 3.383 |
| Tikhonov solve incl. GCV λ search | 1 | 226.7 | 226.733 |

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
| Optimised schedule (open loop) | 44.92 | 2369 | 0.00 | 0 | 8.9300e-3 | True |
| Closed-loop MPC (live twin) | 44.90 | 2109 | 0.00 | 0 | 8.2900e-3 | True |

Optimised plan (u per 100 s): 0.161, 0.167, 0.176, 0.185, 0.195, 0.205, 0.216, 0.228, 0.241, 0.253, 0.264, 0.269, 0.237, 0.164, 0.151, 0.150, 0.150, 0.150
Optimisation: 955 PDE solves in 9.6 s; minimal feasible constant level 23.9 %.

## Experiment wall-clock time

- NumericalConvergence: 23.8 s
- Regularization: 6.7 s
- SensorDensity: 8.1 s
- NoiseRobustness: 6.3 s
- ForecastAccuracy: 2.2 s
- CoolingComparison: 72.7 s
