using System.Collections.Concurrent;

namespace AnomalyDetection.Infrastructure.Workers;

public sealed record WorkerStatus(string Name, DateTime? LastRunUtc, DateTime? LastSuccessUtc, string? LastResult, string? LastError, long Runs, long Failures);

/// <summary>In-memory record of background worker activity, surfaced by <c>/api/v1/system/status</c>.</summary>
public sealed class WorkerStatusRegistry
{
    private readonly ConcurrentDictionary<string, WorkerStatus> _statuses = new();

    public void Success(string name, DateTime nowUtc, string? result) =>
        _statuses.AddOrUpdate(
            name,
            _ => new WorkerStatus(name, nowUtc, nowUtc, result, null, 1, 0),
            (_, s) => s with { LastRunUtc = nowUtc, LastSuccessUtc = nowUtc, LastResult = result, Runs = s.Runs + 1 });

    public void Failure(string name, DateTime nowUtc, string error) =>
        _statuses.AddOrUpdate(
            name,
            _ => new WorkerStatus(name, nowUtc, null, null, error, 1, 1),
            (_, s) => s with { LastRunUtc = nowUtc, LastError = error, Runs = s.Runs + 1, Failures = s.Failures + 1 });

    public IReadOnlyList<WorkerStatus> Snapshot() => _statuses.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
}
