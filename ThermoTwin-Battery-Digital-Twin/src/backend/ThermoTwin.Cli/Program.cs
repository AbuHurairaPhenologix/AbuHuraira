using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ThermoTwin.Application;
using ThermoTwin.Application.Experiments;
using ThermoTwin.Domain.Enums;

// Usage: thermotwin [output-directory] [--only Kind1,Kind2]
//   Kinds: NumericalConvergence, Regularization, SensorDensity, NoiseRobustness, ForecastAccuracy, CoolingComparison,
//          FemVerification, AdjointGradientCheck, OptimizationBenchmark, ReducedOrderModel, ReducedOrderControl, ParameterIdentifiability
// Runs the experiments on the built-in demo scenario and writes:
//   <out>/<kind>.json      full payload (same JSON the API stores)
//   <out>/*.csv            tables used in the README
//   <out>/summary.md       human-readable digest of the headline numbers

var output = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? Path.Combine("docs", "results");
var onlyIndex = Array.IndexOf(args, "--only");
var kinds = onlyIndex >= 0 && onlyIndex + 1 < args.Length
    ? args[onlyIndex + 1].Split(',').Select(Enum.Parse<ExperimentKind>).ToArray()
    : Enum.GetValues<ExperimentKind>();

Directory.CreateDirectory(output);
var c = CultureInfo.InvariantCulture;
var results = new Dictionary<ExperimentKind, object>();
var timings = new Dictionary<ExperimentKind, double>();

foreach (var kind in kinds)
{
    Console.Write($"Running {kind,-22} ... ");
    var sw = Stopwatch.StartNew();
    var (payload, summary) = ExperimentService.Execute(kind, null);
    timings[kind] = sw.Elapsed.TotalSeconds;
    results[kind] = payload;
    await File.WriteAllTextAsync(Path.Combine(output, $"{Slug(kind)}.json"),
        JsonSerializer.Serialize(payload, payload.GetType(), new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true }));
    Console.WriteLine($"{sw.Elapsed.TotalSeconds,6:0.0} s  {summary}");
}

var md = new StringBuilder();
md.AppendLine("# ThermoTwin.NET — experiment results");
md.AppendLine();
md.AppendLine(c, $"Generated {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC by `dotnet run --project src/backend/ThermoTwin.Cli -c Release` on the built-in demo scenario.");
md.AppendLine();

if (results.TryGetValue(ExperimentKind.NumericalConvergence, out var convObj) && convObj is ConvergenceExperimentResult conv)
{
    var csv = new StringBuilder("study,scheme,nx,ny,dx_m,dt_s,steps,rmse_K,max_error_K,order_rmse,order_max,runtime_ms\n");
    foreach (var (study, rows) in new[] { ("spatial", conv.Spatial), ("temporal", conv.Temporal) })
    {
        foreach (var r in rows)
        {
            csv.AppendLine(c, $"{study},{r.Scheme},{r.Nx},{r.Ny},{r.Dx},{r.TimeStep},{r.Steps},{r.Rmse:E6},{r.MaxError:E6},{r.ObservedOrderRmse:0.0000},{r.ObservedOrderMax:0.0000},{r.RuntimeMs:0.00}");
        }
    }

    await File.WriteAllTextAsync(Path.Combine(output, "convergence.csv"), csv.ToString());

    md.AppendLine("## Spatial convergence (exact solution, Δt ∝ h²)");
    md.AppendLine();
    md.AppendLine("| Scheme | Grid | Δx [mm] | RMSE [K] | Max error [K] | Observed order | Runtime [ms] |");
    md.AppendLine("|---|---|---:|---:|---:|---:|---:|");
    foreach (var r in conv.Spatial)
    {
        md.AppendLine(c, $"| {r.Scheme} | {r.Nx}×{r.Ny} | {r.Dx * 1000:0.00} | {r.Rmse:0.000e+0} | {r.MaxError:0.000e+0} | {(r.ObservedOrderRmse is { } p ? p.ToString("0.000", c) : "—")} | {r.RuntimeMs:0.0} |");
    }

    md.AppendLine();
    md.AppendLine("## Temporal convergence (exact semi-discrete solution, 40×20)");
    md.AppendLine();
    md.AppendLine("| Scheme | Δt [s] | RMSE [K] | Observed order |");
    md.AppendLine("|---|---:|---:|---:|");
    foreach (var r in conv.Temporal)
    {
        md.AppendLine(c, $"| {r.Scheme} | {r.TimeStep:0.###} | {r.Rmse:0.000e+0} | {(r.ObservedOrderRmse is { } p ? p.ToString("0.000", c) : "—")} |");
    }

    md.AppendLine();
    md.AppendLine("## Explicit stability probe");
    md.AppendLine();
    md.AppendLine("| Δt / Δt_crit | Δt [s] | ‖T‖∞ after 400 steps | Diverged |");
    md.AppendLine("|---:|---:|---:|---|");
    foreach (var p in conv.StabilityProbes)
    {
        md.AppendLine(c, $"| {p.TimeStepRatio} | {p.TimeStep:0.0000} | {p.FinalAmplitude:0.00e+0} | {p.Diverged} |");
    }

    md.AppendLine();
    md.AppendLine("## Energy conservation and performance");
    md.AppendLine();
    foreach (var e in conv.EnergyConservation)
    {
        md.AppendLine(c, $"- {e.Scheme}: injected {e.InjectedEnergy:0.000} J, stored {e.StoredEnergyChange:0.000} J, relative imbalance {e.RelativeImbalance:0.0e+0}");
    }

    md.AppendLine();
    md.AppendLine("| Workload | Steps | Total [ms] | Per step [ms] |");
    md.AppendLine("|---|---:|---:|---:|");
    foreach (var p in conv.Performance)
    {
        md.AppendLine(c, $"| {p.Label} | {p.Steps} | {p.TotalMs:0.0} | {p.MsPerStep:0.000} |");
    }

    md.AppendLine();
}

if (results.TryGetValue(ExperimentKind.Regularization, out var regObj) && regObj is RegularizationExperimentResult reg)
{
    md.AppendLine(c, $"## Inverse problem (m = {reg.MeasurementCount}, n = {reg.Unknowns}, σ = {reg.NoiseStd} K, σ√m = {reg.DiscrepancyTarget:0.00} K)");
    md.AppendLine();
    md.AppendLine("| Regulariser | Rule | λ (rel.) | ‖Aq−d‖ [K] | T RMSE [K] | Source error | Hotspot error [mm] |");
    md.AppendLine("|---|---|---:|---:|---:|---:|---:|");
    var csv = new StringBuilder("regularization,rule,relative_lambda,residual_K,temperature_rmse_K,source_relative_error,hotspot_error_mm\n");
    foreach (var s in reg.Studies)
    {
        foreach (var ch in s.Choices)
        {
            md.AppendLine(c, $"| {s.Regularization} | {ch.Method} | {ch.RelativeLambda:0.0e+0} | {ch.ResidualNorm:0.000} | {ch.Metrics.TemperatureRmse:0.000} | {ch.Metrics.SourceRelativeError:P1} | {ch.Metrics.HotspotErrorMm:0.0} |");
            csv.AppendLine(c, $"{s.Regularization},{ch.Method},{ch.RelativeLambda:E4},{ch.ResidualNorm:0.0000},{ch.Metrics.TemperatureRmse:0.00000},{ch.Metrics.SourceRelativeError:0.00000},{ch.Metrics.HotspotErrorMm:0.000}");
        }
    }

    await File.WriteAllTextAsync(Path.Combine(output, "regularization.csv"), csv.ToString());
    md.AppendLine();
}

foreach (var kind in new[] { ExperimentKind.SensorDensity, ExperimentKind.NoiseRobustness })
{
    if (results.TryGetValue(kind, out var obj) && obj is SensitivityExperimentResult sens)
    {
        md.AppendLine(c, $"## {sens.Parameter}");
        md.AppendLine();
        md.AppendLine(c, $"| {sens.Parameter} | λ (rel.) | T RMSE [K] | Source error | Hotspot error [mm] |");
        md.AppendLine("|---:|---:|---:|---:|---:|");
        var csv = new StringBuilder("parameter,relative_lambda,temperature_rmse_K,source_relative_error,hotspot_error_mm\n");
        foreach (var r in sens.Rows)
        {
            md.AppendLine(c, $"| {r.Parameter} | {r.RelativeLambda:0.0e+0} | {r.Metrics.TemperatureRmse:0.000} | {r.Metrics.SourceRelativeError:P1} | {r.Metrics.HotspotErrorMm:0.0} |");
            csv.AppendLine(c, $"{r.Parameter},{r.RelativeLambda:E4},{r.Metrics.TemperatureRmse:0.00000},{r.Metrics.SourceRelativeError:0.00000},{r.Metrics.HotspotErrorMm:0.000}");
        }

        await File.WriteAllTextAsync(Path.Combine(output, $"{Slug(kind)}.csv"), csv.ToString());
        md.AppendLine();
    }
}

if (results.TryGetValue(ExperimentKind.ForecastAccuracy, out var fcObj) && fcObj is ForecastExperimentResult fc)
{
    md.AppendLine("## Forecast accuracy (600 s horizon, baseline cooling)");
    md.AppendLine();
    md.AppendLine("| Issued at [s] | MAE [K] | Error at horizon [K] | Predicted peak [°C] | Actual peak [°C] |");
    md.AppendLine("|---:|---:|---:|---:|---:|");
    foreach (var f in fc.Forecasts)
    {
        md.AppendLine(c, $"| {f.IssuedAt:0} | {f.MeanAbsoluteError:0.000} | {f.ErrorAtHorizon:0.000} | {f.PredictedPeak:0.00} | {f.ActualPeak:0.00} |");
    }

    md.AppendLine();
}

if (results.TryGetValue(ExperimentKind.CoolingComparison, out var ccObj) && ccObj is CoolingComparisonResult cc)
{
    md.AppendLine(c, $"## Cooling strategies (T_safe = {cc.SafeTemperature} °C, optimiser target {cc.ControlTemperature} °C)");
    md.AppendLine();
    md.AppendLine("| Strategy | Peak T [°C] | Energy [J] | Max violation [K] | Time above T_safe [s] | Objective Φ | Feasible |");
    md.AppendLine("|---|---:|---:|---:|---:|---:|---|");
    var csv = new StringBuilder("strategy,peak_C,energy_J,max_violation_K,time_above_s,objective,feasible\n");
    foreach (var s in cc.Strategies)
    {
        md.AppendLine(c, $"| {s.Name} | {s.PeakTemperature:0.00} | {s.CoolingEnergyJoules:0} | {s.MaxViolation:0.00} | {s.TimeAboveLimit:0} | {s.Objective:0.0000e+0} | {s.Feasible} |");
        csv.AppendLine(c, $"\"{s.Name}\",{s.PeakTemperature:0.000},{s.CoolingEnergyJoules:0.0},{s.MaxViolation:0.000},{s.TimeAboveLimit:0},{s.Objective:E6},{s.Feasible}");
    }

    await File.WriteAllTextAsync(Path.Combine(output, "cooling-comparison.csv"), csv.ToString());
    md.AppendLine();
    md.AppendLine(c, $"Optimised plan (u per 100 s): {string.Join(", ", cc.OptimizedPlan.Select(v => v.ToString("0.000", c)))}");
    md.AppendLine(c, $"Optimisation: {cc.OptimizationEvaluations} PDE solves in {cc.OptimizationMs / 1000:0.0} s; minimal feasible constant level {cc.MinimumFeasibleConstantLevel:P1}.");
    md.AppendLine();
}

await MathematicsExport.WriteAsync(results, output, md);

md.AppendLine("## Experiment wall-clock time");
md.AppendLine();
foreach (var (kind, seconds) in timings)
{
    md.AppendLine(c, $"- {kind}: {seconds:0.0} s");
}

await File.WriteAllTextAsync(Path.Combine(output, "summary.md"), md.ToString());
Console.WriteLine($"Results written to {Path.GetFullPath(output)}");

static string Slug(ExperimentKind kind) => kind switch
{
    ExperimentKind.NumericalConvergence => "numerical-convergence",
    ExperimentKind.Regularization => "regularization",
    ExperimentKind.SensorDensity => "sensor-density",
    ExperimentKind.NoiseRobustness => "noise-robustness",
    ExperimentKind.ForecastAccuracy => "forecast-accuracy",
    ExperimentKind.CoolingComparison => "cooling-comparison",
    ExperimentKind.FemVerification => "fem-verification",
    ExperimentKind.AdjointGradientCheck => "adjoint-gradient-check",
    ExperimentKind.OptimizationBenchmark => "optimization-benchmark",
    ExperimentKind.ReducedOrderModel => "reduced-order-model",
    ExperimentKind.ReducedOrderControl => "rom-mpc-comparison",
    ExperimentKind.ParameterIdentifiability => "parameter-identifiability",
    _ => kind.ToString().ToLowerInvariant(),
};
