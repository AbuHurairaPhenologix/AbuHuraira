using System.Globalization;
using System.Text;
using System.Text.Json;
using ThermoTwin.Application;
using ThermoTwin.Application.Experiments;
using ThermoTwin.Domain.Enums;

/// <summary>CSV / JSON tables and summary sections for experiments 7–12 (FEM, adjoint, optimisation, POD, ROM-MPC, identifiability).</summary>
internal static class MathematicsExport
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static async Task WriteAsync(IReadOnlyDictionary<ExperimentKind, object> results, string output, StringBuilder md)
    {
        if (results.TryGetValue(ExperimentKind.FemVerification, out var femObj) && femObj is FemVerificationResult fem)
        {
            await Fem(fem, output, md);
        }

        if (results.TryGetValue(ExperimentKind.AdjointGradientCheck, out var adjObj) && adjObj is AdjointCheckResult adj)
        {
            await Adjoint(adj, output, md);
        }

        if (results.TryGetValue(ExperimentKind.OptimizationBenchmark, out var optObj) && optObj is OptimizationBenchmarkResult opt)
        {
            await Optimization(opt, output, md);
        }

        if (results.TryGetValue(ExperimentKind.ReducedOrderModel, out var romObj) && romObj is ReducedOrderResult rom)
        {
            await ReducedOrder(rom, output, md);
        }

        if (results.TryGetValue(ExperimentKind.ReducedOrderControl, out var mpcObj) && mpcObj is ReducedOrderControlResult mpc)
        {
            await ReducedOrderControl(mpc, output, md);
        }

        if (results.TryGetValue(ExperimentKind.ParameterIdentifiability, out var idObj) && idObj is IdentifiabilityResult id)
        {
            await Identifiability(id, output, md);
        }
    }

    private static string O(double? p) => p is { } v ? v.ToString("0.000", C) : "—";

    private static Task Json(string output, string name, object value) =>
        File.WriteAllTextAsync(Path.Combine(output, name),
            JsonSerializer.Serialize(value, value.GetType(), new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true }));

    private static async Task Fem(FemVerificationResult r, string output, StringBuilder md)
    {
        var csv = new StringBuilder("problem,method,nx,ny,h_m,dofs,nonzeros,half_bandwidth,dt_s,steps,rmse_K,max_error_K,l2_error_K,h1_error_K_per_m,order_rmse,order_max,order_l2,order_h1,runtime_ms\n");
        foreach (var row in r.Eigenmode.Concat(r.Manufactured))
        {
            csv.AppendLine(C, $"\"{row.Problem}\",{row.Method},{row.Nx},{row.Ny},{row.H},{row.Dofs},{row.NonZeros},{row.HalfBandwidth},{row.TimeStep},{row.Steps},{row.Rmse:E6},{row.MaxError:E6},{row.L2Error:E6},{row.H1Error:E6},{O(row.OrderRmse)},{O(row.OrderMax)},{O(row.OrderL2)},{O(row.OrderH1)},{row.RuntimeMs:0.0}");
        }

        await File.WriteAllTextAsync(Path.Combine(output, "fem-convergence.csv"), csv.ToString());
        await Json(output, "fem-convergence.json", new { r.Eigenmode, r.Manufactured });

        csv = new StringBuilder("nx,ny,h_m,fvm_dofs,fem_dofs,fvm_nonzeros,fem_nonzeros,fvm_peak_C,fem_peak_C,fvm_final_max_C,fem_final_max_C,fvm_final_mean_C,fem_final_mean_C,field_rms_difference_K,field_max_difference_K,fvm_runtime_ms,fem_runtime_ms,fvm_energy_J,fem_energy_J\n");
        foreach (var b in r.Battery)
        {
            csv.AppendLine(C, $"{b.Nx},{b.Ny},{b.H},{b.FvmDofs},{b.FemDofs},{b.FvmNonZeros},{b.FemNonZeros},{b.FvmPeak:0.0000},{b.FemPeak:0.0000},{b.FvmFinalMax:0.0000},{b.FemFinalMax:0.0000},{b.FvmFinalMean:0.00000},{b.FemFinalMean:0.00000},{b.FieldRmsDifference:E4},{b.FieldMaxDifference:E4},{b.FvmRuntimeMs:0.0},{b.FemRuntimeMs:0.0},{b.FvmEnergyJoules:0.00},{b.FemEnergyJoules:0.00}");
        }

        await File.WriteAllTextAsync(Path.Combine(output, "fvm-fem-comparison.csv"), csv.ToString());
        await Json(output, "fvm-fem-comparison.json", new { r.CoolingLevel, r.SnapshotTime, r.Battery, r.Systems });

        md.AppendLine("## Finite elements: convergence against exact solutions (Crank–Nicolson, Δt ∝ h)");
        md.AppendLine();
        md.AppendLine("| Problem | Method | Grid | DOFs | RMSE [K] | L² error / √area [K] | H¹ semi-norm error / √area [K/m] | p (RMSE) | p (L²) | p (H¹) |");
        md.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in r.Eigenmode.Concat(r.Manufactured))
        {
            md.AppendLine(C, $"| {row.Problem} | {row.Method} | {row.Nx}×{row.Ny} | {row.Dofs} | {row.Rmse:0.000e+0} | {(row.L2Error is { } l2 ? l2.ToString("0.000e+0", C) : "—")} | {(row.H1Error is { } h1 ? h1.ToString("0.000e+0", C) : "—")} | {O(row.OrderRmse)} | {O(row.OrderL2)} | {O(row.OrderH1)} |");
        }

        md.AppendLine();
        md.AppendLine(C, $"## FVM vs FEM on the battery model (Robin edges, sink, defect, constant u = {r.CoolingLevel:0.00})");
        md.AppendLine();
        md.AppendLine("| Grid | FVM peak [°C] | FEM peak [°C] | RMS(T_FEM − T_FVM) [K] | max abs diff [K] | FVM energy [J] | FEM energy [J] | FVM [ms] | FEM [ms] |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var b in r.Battery)
        {
            md.AppendLine(C, $"| {b.Nx}×{b.Ny} | {b.FvmPeak:0.000} | {b.FemPeak:0.000} | {b.FieldRmsDifference:0.00e+0} | {b.FieldMaxDifference:0.00e+0} | {b.FvmEnergyJoules:0.0} | {b.FemEnergyJoules:0.0} | {b.FvmRuntimeMs:0} | {b.FemRuntimeMs:0} |");
        }

        md.AppendLine();
        foreach (var s in r.Systems)
        {
            md.AppendLine(C, $"- {s.Method}: {s.Dofs} unknowns ({s.Unknowns}), {s.NonZeros} non-zeros ({s.NonZerosPerRow} per row), half-bandwidth {s.HalfBandwidth}");
        }

        md.AppendLine();
    }

    private static async Task Adjoint(AdjointCheckResult r, string output, StringBuilder md)
    {
        var csv = new StringBuilder("model,vector,epsilon,relative_error,max_abs_error,adjoint_norm,fd_norm\n");
        foreach (var row in r.Rows)
        {
            csv.AppendLine(C, $"\"{row.Model}\",{row.Vector},{row.Epsilon:E0},{row.RelativeError:E6},{row.MaxAbsoluteError:E6},{row.AdjointNorm:E6},{row.FiniteDifferenceNorm:E6}");
        }

        await File.WriteAllTextAsync(Path.Combine(output, "adjoint-gradient-check.csv"), csv.ToString());
        csv = new StringBuilder("controls,segment_s,adjoint_ms,forward_fd_ms,central_fd_ms,adjoint_solves,forward_fd_solves,central_fd_solves,relative_difference\n");
        foreach (var row in r.Cost)
        {
            csv.AppendLine(C, $"{row.Controls},{row.SegmentDuration},{row.AdjointMs:0.00},{row.ForwardDifferenceMs:0.0},{row.CentralDifferenceMs:0.0},{row.AdjointSolves},{row.ForwardDifferenceSolves},{row.CentralDifferenceSolves},{row.RelativeDifference:E3}");
        }

        await File.WriteAllTextAsync(Path.Combine(output, "gradient-cost.csv"), csv.ToString());

        md.AppendLine(C, $"## Discrete adjoint gradient check ({r.Model}, n = {r.StateDimension}, N = {r.TimeSteps} steps, K = {r.Controls}, μ = {r.Mu:0e+0})");
        md.AppendLine();
        md.AppendLine("| ε | max over 4 control vectors of ‖g_adj − g_FD‖/‖g_FD‖ (central FD) |");
        md.AppendLine("|---:|---:|");
        foreach (var g in r.Rows.Where(x => x.Model == "Full-order FVM").GroupBy(x => x.Epsilon))
        {
            md.AppendLine(C, $"| {g.Key:0e+0} | {g.Max(x => x.RelativeError):0.000e+0} |");
        }

        md.AppendLine();
        md.AppendLine(C, $"Best agreement {r.BestRelativeError:0.0e+0} at ε = {r.BestEpsilon:0e+0}; reduced-model adjoint best agreement {r.RomBestRelativeError:0.0e+0}.");
        md.AppendLine();
        md.AppendLine("| K controls | adjoint gradient [ms] | forward FD [ms] | central FD [ms] | solves adj / FD / central |");
        md.AppendLine("|---:|---:|---:|---:|---|");
        foreach (var row in r.Cost)
        {
            md.AppendLine(C, $"| {row.Controls} | {row.AdjointMs:0.0} | {row.ForwardDifferenceMs:0} | {row.CentralDifferenceMs:0} | {row.AdjointSolves} / {row.ForwardDifferenceSolves} / {row.CentralDifferenceSolves} |");
        }

        md.AppendLine();
    }

    private static async Task Optimization(OptimizationBenchmarkResult r, string output, StringBuilder md)
    {
        var csv = new StringBuilder("method,gradient,objective,energy_J,model_peak_C,model_violation_K,plant_peak_C,plant_violation_K,feasible,iterations,forward_solves,adjoint_solves,full_order_solve_equivalents,runtime_ms,projected_gradient_norm,gradient_norm,active_lower,active_upper,inactive\n");
        foreach (var m in r.Methods)
        {
            csv.AppendLine(C, $"\"{m.Name}\",\"{m.Gradient}\",{m.Objective:E6},{m.EnergyJoules:0.00},{m.ModelPeak:0.0000},{m.ModelViolation:0.0000},{m.PlantPeak:0.0000},{m.PlantViolation:0.0000},{m.Feasible},{m.Iterations},{m.ForwardSolves},{m.AdjointSolves},{m.FullOrderSolveEquivalents},{m.RuntimeMs:0.0},{m.Kkt.ProjectedGradientNorm:E3},{m.Kkt.GradientNorm:E3},{m.Kkt.ActiveLower},{m.Kkt.ActiveUpper},{m.Kkt.Inactive}");
        }

        await File.WriteAllTextAsync(Path.Combine(output, "optimization-benchmark.csv"), csv.ToString());

        md.AppendLine(C, $"## PDE-constrained optimisation ({r.Segments} × {r.SegmentDuration:0} s, T_safe − margin = {r.ControlTemperature} °C, μ = {string.Join(" → ", r.PenaltySchedule.Select(p => p.ToString("0e+0", C)))})");
        md.AppendLine();
        md.AppendLine("| Method | J | Energy [J] | Model peak [°C] | Plant peak [°C] | Iter. | Forward / adjoint solves | Runtime [s] | ‖P(u−∇Φ)−u‖∞ |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var m in r.Methods)
        {
            md.AppendLine(C, $"| {m.Name} | {m.Objective:0.00000} | {m.EnergyJoules:0} | {m.ModelPeak:0.000} | {m.PlantPeak:0.000} | {m.Iterations} | {m.ForwardSolves} / {m.AdjointSolves} | {m.RuntimeMs / 1000:0.0} | {m.Kkt.ProjectedGradientNorm:0.0e+0} |");
        }

        md.AppendLine();
        md.AppendLine(C, $"Adjoint vs finite-difference gradient: {r.AdjointSpeedup:0.0}× faster, {r.SolveReduction:0.0}× fewer PDE solves.");
        md.AppendLine();
    }

    private static async Task ReducedOrder(ReducedOrderResult r, string output, StringBuilder md)
    {
        var csv = new StringBuilder("family,index,eigenvalue_K2,cumulative_energy\n");
        foreach (var p in r.Spectrum)
        {
            csv.AppendLine(C, $"\"{p.Family}\",{p.Index},{p.Eigenvalue:E6},{p.CumulativeEnergy:0.000000000000}");
        }

        await File.WriteAllTextAsync(Path.Combine(output, "pod-spectrum.csv"), csv.ToString());
        csv = new StringBuilder("family,test,modes,captured_energy,rmse_K,max_error_K,projection_rmse_K,peak_error_K,fom_ms,rom_ms,speedup,full_dimension\n");
        foreach (var c in r.Comparison)
        {
            csv.AppendLine(C, $"\"{c.Family}\",\"{c.Test}\",{c.Modes},{c.CapturedEnergy:0.000000000},{c.Rmse:E4},{c.MaxError:E4},{c.ProjectionRmse:E4},{c.PeakError:E4},{c.FullOrderMs:0.000},{c.ReducedOrderMs:0.000},{c.Speedup:0.00},{c.FullDimension}");
        }

        await File.WriteAllTextAsync(Path.Combine(output, "pod-rom-comparison.csv"), csv.ToString());
        await Json(output, "pod-rom-comparison.json", new { r.FullDimension, r.TimeStep, r.Training, r.SelectedModes, r.SelectionRule, r.Comparison });
        csv = new StringBuilder("model,modes,runtime_ms,speedup,objective,objective_gap,energy_J,fom_peak_C,feasible,screened_cells,correction_rounds,constraint_shift_K,repaired,fell_back,rom_solves,fom_solves,validation_error_K\n");
        foreach (var o in r.Optimization)
        {
            csv.AppendLine(C, $"\"{o.Model}\",{o.Modes},{o.RuntimeMs:0.0},{o.Speedup:0.00},{o.Objective:E6},{o.ObjectiveGap:E3},{o.EnergyJoules:0.00},{o.FullOrderPeak:0.0000},{o.Feasible},{o.ScreenedCells},{o.CorrectionRounds},{o.ConstraintShift:0.0000},{o.Repaired},{o.FellBack},{o.RomSolves},{o.FullOrderSolves},{o.ValidationError:0.0000}");
        }

        await File.WriteAllTextAsync(Path.Combine(output, "rom-optimization.csv"), csv.ToString());

        md.AppendLine(C, $"## POD reduced-order model (n = {r.FullDimension}, selected r = {r.SelectedModes}: {r.SelectionRule})");
        md.AppendLine();
        foreach (var t in r.Training)
        {
            md.AppendLine(C, $"- {t.Family}: {t.TrainingRuns} training runs, {t.Snapshots} snapshots, POD rank {t.Rank}, training {t.TrainingMs:0} ms, POD {t.PodMs:0} ms");
        }

        md.AppendLine();
        md.AppendLine("| Family | Test | r | 1 − E(r) | RMSE [K] | Projection RMSE [K] | Peak error [K] | FOM [ms] | ROM [ms] | Speed-up |");
        md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var c in r.Comparison.Where(x => x.Modes is 5 or 10 or 20 or 30 or 40 or 60 or 80))
        {
            md.AppendLine(C, $"| {c.Family} | {c.Test} | {c.Modes} | {1 - c.CapturedEnergy:0.0e+0} | {c.Rmse:0.0000} | {c.ProjectionRmse:0.0000} | {c.PeakError:0.0000} | {c.FullOrderMs:0.0} | {c.ReducedOrderMs:0.0} | {c.Speedup:0.0} |");
        }

        md.AppendLine();
        md.AppendLine("| Optimisation model | Runtime [s] | Speed-up | J | Gap | FOM peak [°C] | Feasible | Corrections | Fallback |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---|---:|---|");
        foreach (var o in r.Optimization)
        {
            md.AppendLine(C, $"| {o.Model} | {o.RuntimeMs / 1000:0.00} | {o.Speedup:0.0} | {o.Objective:0.00000} | {o.ObjectiveGap:P2} | {o.FullOrderPeak:0.000} | {o.Feasible} | {o.CorrectionRounds} | {(o.FellBack ? o.FallbackReason : "no")} |");
        }

        md.AppendLine();
    }

    private static async Task ReducedOrderControl(ReducedOrderControlResult r, string output, StringBuilder md)
    {
        var csv = new StringBuilder("run,plant_peak_C,energy_J,time_above_s,max_violation_K,feasible,optimizations,total_optimizer_ms,mean_optimizer_ms,forward_solves,adjoint_solves,full_order_solves,fallbacks,correction_rounds,repairs,mean_validation_error_K,max_validation_error_K\n");
        foreach (var x in r.Runs)
        {
            csv.AppendLine(C, $"\"{x.Name}\",{x.PlantPeak:0.0000},{x.EnergyJoules:0.0},{x.TimeAboveLimit:0},{x.MaxViolation:0.0000},{x.Feasible},{x.Optimizations},{x.TotalOptimizerMs:0.0},{x.MeanOptimizerMs:0.0},{x.ForwardSolves},{x.AdjointSolves},{x.FullOrderSolves},{x.Fallbacks},{x.CorrectionRounds},{x.Repairs},{x.MeanValidationError:0.0000},{x.MaxValidationError:0.0000}");
        }

        await File.WriteAllTextAsync(Path.Combine(output, "rom-mpc-comparison.csv"), csv.ToString());

        md.AppendLine(C, $"## Closed-loop MPC on the plant (T_safe = {r.SafeTemperature} °C)");
        md.AppendLine();
        md.AppendLine("| MPC optimiser | Plant peak [°C] | Energy [J] | Time above T_safe [s] | Optimiser time / call [ms] | Fallbacks | Mean ROM error [K] |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var x in r.Runs)
        {
            md.AppendLine(C, $"| {x.Name} | {x.PlantPeak:0.00} | {x.EnergyJoules:0} | {x.TimeAboveLimit:0} | {x.MeanOptimizerMs:0} | {x.Fallbacks} | {x.MeanValidationError:0.000} |");
        }

        md.AppendLine();
    }

    private static async Task Identifiability(IdentifiabilityResult r, string output, StringBuilder md)
    {
        var rep = r.Report;
        var csv = new StringBuilder("parameter,unit,nominal,uncertainty,rms_sensitivity_K,signal_to_noise,cramer_rao_std,cramer_rao_relative," + string.Join(",", rep.Parameters.Select(p => $"corr_{p}")) + "\n");
        for (var j = 0; j < rep.Parameters.Length; j++)
        {
            var correlations = string.Join(",", rep.Correlation[j].Select(v => v.ToString("0.0000", C)));
            csv.AppendLine(C, $"{rep.Parameters[j]},\"{rep.Units[j]}\",{rep.Nominal[j]},{rep.Uncertainty[j]},{rep.ColumnNorms[j]:0.00000},{rep.SignalToNoise[j]:0.000},{rep.CramerRaoStd[j]:E4},{rep.CramerRaoRelative[j]:E4},{correlations}");
        }

        await File.WriteAllTextAsync(Path.Combine(output, "parameter-sensitivity.csv"), csv.ToString());
        await Json(output, "parameter-sensitivity.json", rep);
        csv = new StringBuilder("case,parameter,truth,initial,estimate,standard_error,relative_error,at_bound,iterations,model_evaluations,initial_residual_K,final_residual_K,noise_estimate_K\n");
        foreach (var e in r.Estimations)
        {
            var x = e.Result;
            for (var j = 0; j < x.Parameters.Length; j++)
            {
                csv.AppendLine(C, $"{e.Key},{x.Parameters[j]},{x.Truth[j]},{x.Initial[j]},{x.Estimate[j]:G8},{x.StandardErrors[j]:E4},{x.RelativeErrors[j]:E4},{x.AtBound[j]},{x.Iterations},{x.ModelEvaluations},{x.InitialResidual:0.0000},{x.FinalResidual:0.0000},{x.NoiseEstimate:0.00000}");
            }
        }

        await File.WriteAllTextAsync(Path.Combine(output, "parameter-estimation.csv"), csv.ToString());

        md.AppendLine(C, $"## Parameter identifiability (m = {rep.ObservationCount} sensor samples, σ = {rep.NoiseStd} K)");
        md.AppendLine();
        md.AppendLine("| θ | nominal | δθ | RMS sensitivity [K] | SNR | Cramér–Rao std | rel. |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        for (var j = 0; j < rep.Parameters.Length; j++)
        {
            md.AppendLine(C, $"| {rep.Parameters[j]} | {rep.Nominal[j]:G4} | {rep.Uncertainty[j]:G3} | {rep.ColumnNorms[j]:0.000} | {rep.SignalToNoise[j]:0.0} | {rep.CramerRaoStd[j]:0.00e+0} | {rep.CramerRaoRelative[j]:P2} |");
        }

        md.AppendLine();
        md.AppendLine(C, $"Condition number of the column-normalised sensitivity matrix: {rep.ConditionNumber:0.00}. Largest collinearity indices: {string.Join("; ", rep.Collinearity.Take(3).Select(c => $"{{{string.Join(", ", c.Parameters)}}} γ = {c.Index:0.00}"))}.");
        md.AppendLine();
        md.AppendLine("| Estimation case | θ | truth | estimate | rel. error | std. error |");
        md.AppendLine("|---|---|---:|---:|---:|---:|");
        foreach (var e in r.Estimations)
        {
            for (var j = 0; j < e.Result.Parameters.Length; j++)
            {
                md.AppendLine(C, $"| {e.Name} | {e.Result.Parameters[j]} | {e.Result.Truth[j]:G5} | {e.Result.Estimate[j]:G5} | {e.Result.RelativeErrors[j]:P2} | {e.Result.StandardErrors[j]:0.00e+0} |");
            }
        }

        md.AppendLine();
    }
}
