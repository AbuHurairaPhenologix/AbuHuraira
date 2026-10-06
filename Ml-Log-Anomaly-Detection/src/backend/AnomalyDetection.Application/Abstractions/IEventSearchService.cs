using AnomalyDetection.Domain.Entities;

namespace AnomalyDetection.Application.Abstractions;

/// <summary>
/// Constrained investigation filters (report §4.9). The backend translates these into a search query;
/// clients can never submit raw query DSL.
/// </summary>
public sealed record EventSearchCriteria(
    string? Service,
    string? Environment,
    DateTime? FromUtc,
    DateTime? ToUtc,
    string? CorrelationId,
    string? EventType,
    int Page = 1,
    int PageSize = 50);

public sealed record EventDocument(
    Guid Id,
    string EventId,
    DateTime EventTimestampUtc,
    string ServiceName,
    string Environment,
    string EventType,
    string EndpointGroup,
    int? StatusCode,
    double? DurationMs,
    bool ErrorFlag,
    string AuthenticationResult,
    string? DependencyName,
    int RetryCount,
    string? CorrelationId,
    bool IsLate,
    Guid? FeatureWindowId);

public sealed record EventSearchResult(IReadOnlyList<EventDocument> Items, long Total, int Page, int PageSize, string Source);

/// <summary>Search index port (OpenSearch in Infrastructure).</summary>
public interface IEventSearchService
{
    Task EnsureIndexAsync(CancellationToken cancellationToken);

    /// <summary>Bulk-indexes events; returns the ids that were indexed successfully.</summary>
    Task<IReadOnlyCollection<Guid>> IndexAsync(IReadOnlyList<OperationalEvent> events, CancellationToken cancellationToken);

    Task<EventSearchResult> SearchAsync(EventSearchCriteria criteria, CancellationToken cancellationToken);
}

/// <summary>Bounded in-process queue used by the request middleware so event capture never blocks a request.</summary>
public interface IEventQueue
{
    bool TryEnqueue(Ingestion.RawEventInput rawEvent);

    long DroppedCount { get; }

    int ApproximateCount { get; }
}
