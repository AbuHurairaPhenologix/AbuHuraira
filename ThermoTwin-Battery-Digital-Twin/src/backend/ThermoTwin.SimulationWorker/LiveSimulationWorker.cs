using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThermoTwin.Application.Abstractions;
using ThermoTwin.Application.Twin;
using ThermoTwin.Domain.Entities;
using ThermoTwin.Domain.Enums;

namespace ThermoTwin.SimulationWorker;

/// <summary>
/// Background loop that advances the live digital twin, streams frames over the notifier,
/// and persists a down-sampled time series plus the final run summary.
/// </summary>
public sealed class LiveSimulationWorker : BackgroundService
{
    private readonly SimulationCoordinator _coordinator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITwinNotifier _notifier;
    private readonly TimeProvider _clock;
    private readonly SimulationWorkerOptions _options;
    private readonly ILogger<LiveSimulationWorker> _logger;

    public LiveSimulationWorker(
        SimulationCoordinator coordinator,
        IServiceScopeFactory scopeFactory,
        ITwinNotifier notifier,
        TimeProvider clock,
        IOptions<SimulationWorkerOptions> options,
        ILogger<LiveSimulationWorker> logger)
    {
        _coordinator = coordinator;
        _scopeFactory = scopeFactory;
        _notifier = notifier;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var session = _coordinator.Session;
            if (session is null || session.Status is not (SimulationStatus.Running or SimulationStatus.Paused))
            {
                await WaitAsync(stoppingToken);
                continue;
            }

            try
            {
                await TickAsync(session, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Simulation {RunId} failed", session.RunId);
                session.Status = SimulationStatus.Failed;
                await UpdateRunAsync(session, run => run.Fail(_clock.GetUtcNow(), ex.Message), stoppingToken);
            }
        }
    }

    private async Task TickAsync(LiveSession session, CancellationToken ct)
    {
        var engine = session.Engine;

        if (session.StopRequested)
        {
            await FinishAsync(session, stoppedEarly: true, ct);
            return;
        }

        if (session.PauseRequested)
        {
            if (session.Status != SimulationStatus.Paused)
            {
                session.Status = SimulationStatus.Paused;
                await UpdateRunAsync(session, run =>
                {
                    RecordProgress(run, engine);
                    run.Pause();
                }, ct);
                await PublishAsync(session, engine.BuildFrame(SimulationStatus.Paused), ct);
            }

            await WaitAsync(ct);
            return;
        }

        if (session.Status == SimulationStatus.Paused)
        {
            await UpdateRunAsync(session, run => run.Resume(), ct);
        }

        session.Status = SimulationStatus.Running;
        engine.Mode = session.RequestedMode;

        var playback = engine.Scenario.Playback;
        for (var k = 0; k < playback.StepsPerFrame && !engine.IsComplete; k++)
        {
            engine.Advance();
        }

        if (engine.IsComplete)
        {
            await FinishAsync(session, stoppedEarly: false, ct);
            return;
        }

        var frame = engine.BuildFrame(SimulationStatus.Running);
        await PersistSnapshotsAsync(session, frame.NewHistory, ct);
        await PublishAsync(session, frame, ct);

        if (playback.FrameIntervalMs > 0)
        {
            await Task.Delay(playback.FrameIntervalMs, ct);
        }
    }

    private async Task FinishAsync(LiveSession session, bool stoppedEarly, CancellationToken ct)
    {
        var engine = session.Engine;
        session.Status = stoppedEarly ? SimulationStatus.Stopped : SimulationStatus.Completed;
        var frame = engine.BuildFrame(session.Status);
        await PersistSnapshotsAsync(session, frame.NewHistory, ct);
        await UpdateRunAsync(session, run =>
        {
            RecordProgress(run, engine);
            if (run.IsActive)
            {
                run.Complete(_clock.GetUtcNow(), engine.TemperatureRmse, engine.SourceRelativeError, engine.HotspotErrorMm, stoppedEarly);
            }
        }, ct);
        await PublishAsync(session, frame, ct);
        _logger.LogInformation(
            "Simulation {RunId} {Status} at t = {Time:0} s: peak {Peak:0.00} °C (counterfactual {Counterfactual:0.00} °C), energy {Energy:0} J",
            session.RunId, session.Status, engine.Time, engine.PeakTemperature, engine.PeakCounterfactualTemperature, engine.CoolingEnergy);
    }

    private async Task PublishAsync(LiveSession session, TwinFrame frame, CancellationToken ct)
    {
        if (!ReferenceEquals(session, _coordinator.Session))
        {
            return;
        }

        _coordinator.Publish(session, frame);
        try
        {
            await _notifier.PublishFrameAsync(frame, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to push frame to clients");
        }
    }

    private async Task PersistSnapshotsAsync(LiveSession session, IReadOnlyList<TwinHistoryPoint> points, CancellationToken ct)
    {
        var every = Math.Max(1, _options.SnapshotEverySteps);
        var dt = session.Engine.Scenario.Solver.TimeStep;
        var selected = points
            .Where(p => (int)Math.Round(p.Time / dt) % every == 0)
            .Select(p => new SimulationSnapshot
            {
                RunId = session.RunId,
                Time = p.Time,
                MaxTemperature = p.TrueMax,
                MeanTemperature = p.TrueMean,
                MinTemperature = p.TrueMin,
                EstimatedMaxTemperature = p.EstimatedMax,
                CounterfactualMaxTemperature = p.CounterfactualMax,
                PredictedPeakTemperature = p.LeadPrediction,
                CoolingLevel = p.CoolingLevel,
                CoolingPowerWatts = p.CoolingPower,
                CumulativeEnergyJoules = p.CumulativeEnergy,
                TemperatureRmse = p.TemperatureRmse,
                SourceRelativeError = p.SourceRelativeError,
                HotspotErrorMm = p.HotspotErrorMm,
                Risk = p.Risk,
            })
            .ToArray();

        if (selected.Length == 0)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISimulationRunRepository>();
        await repository.AddSnapshotsAsync(selected, ct);
        if (await repository.GetAsync(session.RunId, ct) is { IsActive: true } run)
        {
            RecordProgress(run, session.Engine);
            await repository.SaveChangesAsync(ct);
        }
    }

    private static void RecordProgress(SimulationRun run, DigitalTwinEngine engine) =>
        run.RecordProgress(engine.Time, engine.PeakTemperature, engine.PeakCounterfactualTemperature, engine.CoolingEnergy);

    private async Task UpdateRunAsync(LiveSession session, Action<SimulationRun> update, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISimulationRunRepository>();
        var run = await repository.GetAsync(session.RunId, ct);
        if (run is null)
        {
            return;
        }

        update(run);
        await repository.SaveChangesAsync(ct);
    }

    private async Task WaitAsync(CancellationToken ct)
    {
        try
        {
            await _coordinator.WaitForWorkAsync(TimeSpan.FromMilliseconds(250), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
