using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ThermoTwin.Application;
using ThermoTwin.Application.Abstractions;
using ThermoTwin.Application.Scenarios;
using ThermoTwin.Application.Twin;
using ThermoTwin.Domain;
using ThermoTwin.Domain.Entities;
using ThermoTwin.Domain.Enums;

namespace ThermoTwin.SimulationWorker;

/// <summary>
/// Owns the single live digital-twin session. API requests issue commands here; the
/// <see cref="LiveSimulationWorker"/> is the only thread that advances the engine, so the engine
/// itself needs no locking. Shared read models (latest frame, history) are swapped atomically.
/// </summary>
public sealed class SimulationCoordinator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<SimulationCoordinator> _logger;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Lock _historyLock = new();
    private List<TwinHistoryPoint> _history = [];
    private volatile TwinFrame? _latestFrame;
    private volatile LiveSession? _session;

    public SimulationCoordinator(IServiceScopeFactory scopeFactory, TimeProvider clock, ILogger<SimulationCoordinator> logger)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _logger = logger;
    }

    public LiveSession? Session => _session;

    public TwinFrame? LatestFrame => _latestFrame;

    public IReadOnlyList<TwinHistoryPoint> History
    {
        get
        {
            lock (_historyLock)
            {
                return _history.ToArray();
            }
        }
    }

    public async Task<SimulationRun> StartAsync(ScenarioDefinition scenario, string? name, CancellationToken cancellationToken)
    {
        var previous = _session;
        if (previous is { Status: SimulationStatus.Running or SimulationStatus.Paused })
        {
            // Detach the old session first so the worker stops publishing its frames, then close its record.
            _session = null;
            previous.Status = SimulationStatus.Stopped;
            await using var cleanup = _scopeFactory.CreateAsyncScope();
            var repository = cleanup.ServiceProvider.GetRequiredService<ISimulationRunRepository>();
            if (await repository.GetAsync(previous.RunId, cancellationToken) is { IsActive: true } old)
            {
                old.RecordProgress(previous.Engine.Time, previous.Engine.PeakTemperature, previous.Engine.PeakCounterfactualTemperature,
                    previous.Engine.CoolingEnergy);
                old.Complete(_clock.GetUtcNow(), previous.Engine.TemperatureRmse, previous.Engine.SourceRelativeError,
                    previous.Engine.HotspotErrorMm, stoppedEarly: true);
                await repository.SaveChangesAsync(cancellationToken);
            }
        }

        var run = new SimulationRun(string.IsNullOrWhiteSpace(name) ? scenario.Name : name, scenario.Key,
            JsonSerializer.Serialize(scenario, JsonDefaults.Options), _clock.GetUtcNow());
        run.Start(_clock.GetUtcNow());

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISimulationRunRepository>().AddAsync(run, cancellationToken);
        }

        var engine = new DigitalTwinEngine(scenario, run.Id);
        lock (_historyLock)
        {
            _history = [];
        }

        var session = new LiveSession(run.Id, engine);
        _latestFrame = engine.BuildFrame(SimulationStatus.Running) with { NewHistory = [] };
        engine.ResetHistoryCursor();
        _session = session;
        _logger.LogInformation("Started simulation {RunId} ({Scenario})", run.Id, scenario.Key);
        _signal.Release();
        return run;
    }

    public void Pause() => RequireSession().RequestPause();

    public void Resume()
    {
        RequireSession().RequestResume();
        _signal.Release();
    }

    public void Stop()
    {
        RequireSession().RequestStop();
        _signal.Release();
    }

    public void SetMode(CoolingMode mode) => RequireSession().RequestedMode = mode;

    internal void Publish(LiveSession session, TwinFrame frame)
    {
        if (!ReferenceEquals(session, _session))
        {
            return;
        }

        lock (_historyLock)
        {
            _history.AddRange(frame.NewHistory);
        }

        _latestFrame = frame with { NewHistory = [] };
    }

    internal Task WaitForWorkAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);

    private LiveSession RequireSession() =>
        _session ?? throw new DomainException("No simulation has been started.");
}

/// <summary>Mutable state of the live run. Command flags are written by API threads and read by the worker.</summary>
public sealed class LiveSession
{
    private volatile bool _pauseRequested;
    private volatile bool _stopRequested;

    public LiveSession(Guid runId, DigitalTwinEngine engine)
    {
        RunId = runId;
        Engine = engine;
        RequestedMode = engine.Mode;
        Status = SimulationStatus.Running;
    }

    public Guid RunId { get; }

    public DigitalTwinEngine Engine { get; }

    public SimulationStatus Status { get; internal set; }

    public CoolingMode RequestedMode { get; set; }

    public bool PauseRequested => _pauseRequested;

    public bool StopRequested => _stopRequested;

    public void RequestPause()
    {
        if (Status != SimulationStatus.Running)
        {
            throw new DomainException($"Only a running simulation can be paused (state {Status}).");
        }

        _pauseRequested = true;
    }

    public void RequestResume()
    {
        if (Status != SimulationStatus.Paused && !_pauseRequested)
        {
            throw new DomainException($"Only a paused simulation can be resumed (state {Status}).");
        }

        _pauseRequested = false;
    }

    public void RequestStop()
    {
        if (Status is not (SimulationStatus.Running or SimulationStatus.Paused))
        {
            throw new DomainException($"The simulation is already {Status}.");
        }

        _stopRequested = true;
    }
}
