using ThermoTwin.Application.Twin;
using ThermoTwin.Domain.Entities;
using ThermoTwin.Domain.Enums;

namespace ThermoTwin.Application.Abstractions;

public interface ISimulationRunRepository
{
    Task AddAsync(SimulationRun run, CancellationToken cancellationToken);

    Task<SimulationRun?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SimulationRun>> ListAsync(int take, CancellationToken cancellationToken);

    Task AddSnapshotsAsync(IEnumerable<SimulationSnapshot> snapshots, CancellationToken cancellationToken);

    Task<IReadOnlyList<SimulationSnapshot>> GetSnapshotsAsync(Guid runId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IExperimentRepository
{
    Task AddAsync(ExperimentRecord record, CancellationToken cancellationToken);

    Task<ExperimentRecord?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<ExperimentRecord?> GetLatestAsync(ExperimentKind kind, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExperimentRecord>> ListAsync(CancellationToken cancellationToken);

    Task<bool> AnyAsync(ExperimentKind kind, CancellationToken cancellationToken);
}

/// <summary>Real-time push channel to connected dashboards (implemented with SignalR).</summary>
public interface ITwinNotifier
{
    Task PublishFrameAsync(TwinFrame frame, CancellationToken cancellationToken);

    Task PublishExperimentAsync(ExperimentSummaryDto experiment, CancellationToken cancellationToken);
}

public sealed record ExperimentSummaryDto(Guid Id, ExperimentKind Kind, string Title, string Summary, DateTimeOffset CreatedAt, double DurationMs);

public sealed record SimulationRunDto(
    Guid Id,
    string Name,
    string ScenarioKey,
    SimulationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    double SimulatedSeconds,
    double PeakTemperature,
    double PeakCounterfactualTemperature,
    double CoolingEnergyJoules,
    double? FinalTemperatureRmse,
    double? FinalSourceRelativeError,
    double? HotspotLocalizationErrorMm,
    string? FailureReason)
{
    public static SimulationRunDto From(SimulationRun r) => new(
        r.Id, r.Name, r.ScenarioKey, r.Status, r.CreatedAt, r.StartedAt, r.CompletedAt, r.SimulatedSeconds, r.PeakTemperature,
        r.PeakCounterfactualTemperature, r.CoolingEnergyJoules, r.FinalTemperatureRmse, r.FinalSourceRelativeError,
        r.HotspotLocalizationErrorMm, r.FailureReason);
}
