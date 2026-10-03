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

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown experiment.");
        }
    }
}
