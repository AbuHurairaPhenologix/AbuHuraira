using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThermoTwin.Application.Abstractions;
using ThermoTwin.Application.Scenarios;
using ThermoTwin.Domain.Entities;
using ThermoTwin.Domain.Enums;

namespace ThermoTwin.Application.Experiments;

/// <summary>Runs an experiment, stores its JSON payload and returns the persisted record.</summary>
public sealed class ExperimentService
{
    private readonly IExperimentRepository _repository;
    private readonly ILogger<ExperimentService> _logger;
    private readonly TimeProvider _clock;

    public ExperimentService(IExperimentRepository repository, ILogger<ExperimentService> logger, TimeProvider clock)
    {
        _repository = repository;
        _logger = logger;
        _clock = clock;
    }

    public async Task<ExperimentRecord> RunAsync(ExperimentKind kind, ScenarioDefinition? scenario, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running experiment {Kind}", kind);
        var sw = Stopwatch.StartNew();
        var (payload, summary) = await Task.Run(() => Execute(kind, scenario), cancellationToken);
        sw.Stop();

        var record = new ExperimentRecord(kind, _clock.GetUtcNow(), sw.Elapsed.TotalMilliseconds, summary,
            JsonSerializer.Serialize(payload, payload.GetType(), JsonDefaults.Options));
        await _repository.AddAsync(record, cancellationToken);
        _logger.LogInformation("Experiment {Kind} finished in {Elapsed:0} ms: {Summary}", kind, sw.Elapsed.TotalMilliseconds, summary);
        return record;
    }

    /// <summary>Synchronous execution shared by the API worker and the command-line runner.</summary>
    public static (object Payload, string Summary) Execute(ExperimentKind kind, ScenarioDefinition? scenario)
    {
        var runner = new ExperimentRunner(scenario);
        var c = CultureInfo.InvariantCulture;
        switch (kind)
        {
            case ExperimentKind.NumericalConvergence:
            {
                var r = runner.RunConvergence();
                var cn = r.Spatial.Where(x => x.Scheme == Numerics.Pde.TimeScheme.CrankNicolson).ToArray();
                return (r, string.Format(c, "Crank–Nicolson spatial order {0:0.00}; finest RMSE {1:0.00e+0} K; energy imbalance {2:0.0e+0}",
                    cn[^1].ObservedOrderRmse, cn[^1].Rmse, r.EnergyConservation.Max(e => e.RelativeImbalance)));
            }

            case ExperimentKind.Regularization:
            {
                var r = runner.RunRegularization();
                var gcv = r.Studies.First(s => s.Regularization == "Gradient").Choices.First(x => x.Method == "GCV").Metrics;
                return (r, string.Format(c, "GCV (gradient prior): temperature RMSE {0:0.000} K, source error {1:P1}, hotspot error {2:0.0} mm",
                    gcv.TemperatureRmse, gcv.SourceRelativeError, gcv.HotspotErrorMm));
            }

            case ExperimentKind.SensorDensity:
            {
                var r = runner.RunSensorDensity();
                return (r, string.Join(" · ", r.Rows.Select(x => string.Format(c, "{0:0}: {1:0.0} mm", x.Parameter, x.Metrics.HotspotErrorMm))));
            }

            case ExperimentKind.NoiseRobustness:
            {
                var r = runner.RunNoiseRobustness();
                return (r, string.Join(" · ", r.Rows.Select(x => string.Format(c, "σ={0:0.00}: {1:0.000} K", x.Parameter, x.Metrics.TemperatureRmse))));
            }

            case ExperimentKind.ForecastAccuracy:
            {
                var r = runner.RunForecastAccuracy();
                return (r, string.Join(" · ", r.Forecasts.Select(f => string.Format(c, "t={0:0}s MAE {1:0.00} K", f.IssuedAt, f.MeanAbsoluteError))));
            }

            case ExperimentKind.CoolingComparison:
            {
                var r = runner.RunCoolingComparison();
                var opt = r.Strategies.First(s => s.Key == "optimized");
                var min = r.Strategies.First(s => s.Key == "min-constant");
                var mpc = r.Strategies.First(s => s.Key == "mpc");
                return (r, string.Format(c, "Optimised peak {0:0.00} °C with {1:0} J ({2:P0} less than minimal constant); MPC peak {3:0.00} °C",
                    opt.PeakTemperature, opt.CoolingEnergyJoules, 1 - opt.CoolingEnergyJoules / min.CoolingEnergyJoules, mpc.PeakTemperature));
            }

            case ExperimentKind.FemVerification:
            {
                var r = runner.RunFemVerification();
                var fem = r.Manufactured.Where(x => x.Method == "FEM").ToArray();
                var finest = r.Battery[^1];
                return (r, string.Format(c, "P1 FEM L² order {0:0.00}, H¹ order {1:0.00}; FVM–FEM RMS difference {2:0.0e+0} K on {3}×{4}",
                    fem[^1].OrderL2, fem[^1].OrderH1, finest.FieldRmsDifference, finest.Nx, finest.Ny));
            }

            case ExperimentKind.AdjointGradientCheck:
            {
                var r = runner.RunAdjointGradientCheck();
                var largest = r.Cost.MaxBy(x => x.Controls)!;
                return (r, string.Format(c, "Adjoint vs central FD: relative error {0:0.0e+0} (ε = {1:0e+0}); K = {2}: adjoint {3:0.0} ms vs FD {4:0} ms",
                    r.BestRelativeError, r.BestEpsilon, largest.Controls, largest.AdjointMs, largest.ForwardDifferenceMs));
            }

            case ExperimentKind.OptimizationBenchmark:
            {
                var r = runner.RunOptimizationBenchmark();
                var adjoint = r.Methods.First(m => m.Key == "adjoint");
                return (r, string.Format(c, "Adjoint optimisation {0:0.0}× faster than finite differences ({1:0.0}× fewer PDE solves); ‖P(u − ∇Φ) − u‖∞ = {2:0.0e+0}",
                    r.AdjointSpeedup, r.SolveReduction, adjoint.Kkt.ProjectedGradientNorm));
            }

            case ExperimentKind.ReducedOrderModel:
            {
                var r = runner.RunReducedOrderModel();
                var row = r.Comparison.First(x => x.Family == "Source-basis family" && x.Modes == r.SelectedModes);
                var opt = r.Optimization.FirstOrDefault(x => x.Modes == r.SelectedModes);
                return (r, string.Format(c, "POD r = {0}: energy {1:0.000000}, RMSE {2:0.000} K, peak error {3:0.000} K; ROM optimisation {4:0.0}× faster",
                    r.SelectedModes, row.CapturedEnergy, row.Rmse, row.PeakError, opt?.Speedup ?? 0));
            }

            case ExperimentKind.ReducedOrderControl:
            {
                var r = runner.RunReducedOrderControl();
                return (r, string.Join(" · ", r.Runs.Select(x => string.Format(c, "{0}: peak {1:0.00} °C, {2:0} J, {3:0} ms/opt",
                    x.Key, x.PlantPeak, x.EnergyJoules, x.MeanOptimizerMs))));
            }

            case ExperimentKind.ParameterIdentifiability:
            {
                var r = runner.RunParameterIdentifiability();
                var joint = r.Estimations.First(e => e.Key == "joint").Result;
                return (r, string.Format(c, "cond = {0:0.0}, max collinearity γ = {1:0.0}; joint estimate max error {2:P1}",
                    r.Report.ConditionNumber, r.Report.Collinearity[0].Index, joint.RelativeErrors.Max(Math.Abs)));
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown experiment.");
        }
    }
}
