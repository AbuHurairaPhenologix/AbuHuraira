using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThermoTwin.Application.Abstractions;
using ThermoTwin.Application.Scenarios;
using ThermoTwin.Domain.Enums;

namespace ThermoTwin.SimulationWorker;

public sealed class SimulationWorkerOptions
{
    public const string SectionName = "ThermoTwin";

    /// <summary>Start the flagship demo scenario when the host starts.</summary>
    public bool AutoStartDemo { get; set; } = true;

    /// <summary>Queue every experiment that has no stored result yet.</summary>
    public bool SeedExperiments { get; set; } = true;

    /// <summary>Persist one snapshot every N solver steps.</summary>
    public int SnapshotEverySteps { get; set; } = 6;
}

/// <summary>Makes a fresh installation demonstrable: starts the demo twin and seeds experiment results.</summary>
public sealed class DemoBootstrapper : BackgroundService
{
    private readonly SimulationCoordinator _coordinator;
    private readonly ExperimentQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SimulationWorkerOptions _options;
    private readonly ILogger<DemoBootstrapper> _logger;

    public DemoBootstrapper(
        SimulationCoordinator coordinator,
        ExperimentQueue queue,
        IServiceScopeFactory scopeFactory,
        IOptions<SimulationWorkerOptions> options,
        ILogger<DemoBootstrapper> logger)
    {
        _coordinator = coordinator;
        _queue = queue;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.AutoStartDemo)
        {
            await _coordinator.StartAsync(ScenarioCatalog.RapidChargeHiddenHotspot, null, stoppingToken);
            _logger.LogInformation("Demo scenario '{Scenario}' started", ScenarioCatalog.RapidChargeHiddenHotspot.Name);
        }

        if (_options.SeedExperiments)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IExperimentRepository>();
            foreach (var kind in Enum.GetValues<ExperimentKind>())
            {
                if (!await repository.AnyAsync(kind, stoppingToken))
                {
                    _queue.Enqueue(kind);
                    _logger.LogInformation("Seeding experiment {Kind}", kind);
                }
            }
        }
    }
}
