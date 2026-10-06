using AnomalyDetection.Domain.Enums;
using AnomalyDetection.Domain.Features;

namespace AnomalyDetection.Domain.Entities;

/// <summary>
/// A normalized, sanitized operational event in the canonical schema (report §3.4):
/// EventTimestamp, ServiceName, Environment, EventType, EndpointGroup, StatusCode, DurationMs, ErrorFlag,
/// AuthenticationResult, DependencyName, RetryCount, CorrelationId.
/// </summary>
public sealed class OperationalEvent
{
    public const string CanonicalSchemaVersion = "event-v1";

    private OperationalEvent()
    {
    }

    public OperationalEvent(
        string eventId,
        ServiceDefinition service,
        DateTime eventTimestampUtc,
        EventType eventType,
        string endpointGroup,
        int? statusCode,
        double? durationMs,
        bool errorFlag,
        AuthenticationResult authenticationResult,
        string? dependencyName,
        int retryCount,
        string? correlationId,
        string? attributesJson,
        DateTime receivedAtUtc)
    {
        Id = Guid.NewGuid();
        EventId = eventId;
        ServiceDefinitionId = service.Id;
        ServiceName = service.Name;
        Environment = service.Environment;
        EventTimestampUtc = eventTimestampUtc;
        EventType = eventType;
        EndpointGroup = endpointGroup;
        StatusCode = statusCode;
        DurationMs = durationMs;
        ErrorFlag = errorFlag;
        AuthenticationResult = authenticationResult;
        DependencyName = dependencyName;
        RetryCount = retryCount;
        CorrelationId = correlationId;
        AttributesJson = attributesJson;
        SchemaVersion = CanonicalSchemaVersion;
        ReceivedAtUtc = receivedAtUtc;
        ProcessingState = EventProcessingState.Pending;
    }

    public Guid Id { get; private set; }

    /// <summary>Producer-supplied or deterministically derived identifier used for duplicate detection.</summary>
    public string EventId { get; private set; } = string.Empty;

    public Guid ServiceDefinitionId { get; private set; }

    public ServiceDefinition? Service { get; private set; }

    public string ServiceName { get; private set; } = string.Empty;

    public string Environment { get; private set; } = string.Empty;

    public DateTime EventTimestampUtc { get; private set; }

    public EventType EventType { get; private set; }

    public string EndpointGroup { get; private set; } = string.Empty;

    public int? StatusCode { get; private set; }

    public double? DurationMs { get; private set; }

    public bool ErrorFlag { get; private set; }

    public AuthenticationResult AuthenticationResult { get; private set; }

    public string? DependencyName { get; private set; }

    public int RetryCount { get; private set; }

    public string? CorrelationId { get; private set; }

    /// <summary>Sanitized optional attributes (secrets removed, identifiers masked). Never used as features.</summary>
    public string? AttributesJson { get; private set; }

    public string SchemaVersion { get; private set; } = CanonicalSchemaVersion;

    public DateTime ReceivedAtUtc { get; private set; }

    public EventProcessingState ProcessingState { get; private set; }

    public Guid? FeatureWindowId { get; private set; }

    /// <summary>True when the event arrived after its window had already been finalized (TC-10).</summary>
    public bool IsLate { get; private set; }

    public DateTime? IndexedAtUtc { get; private set; }

    public void MarkAggregated(Guid featureWindowId)
    {
        FeatureWindowId = featureWindowId;
        ProcessingState = EventProcessingState.Aggregated;
    }

    public void MarkLate(Guid featureWindowId)
    {
        FeatureWindowId = featureWindowId;
        ProcessingState = EventProcessingState.Late;
        IsLate = true;
    }

    public void MarkIndexed(DateTime indexedAtUtc) => IndexedAtUtc = indexedAtUtc;

    public FeatureInputEvent ToFeatureInput() =>
        new(EventId, EventType, EndpointGroup, StatusCode, DurationMs, ErrorFlag, AuthenticationResult, RetryCount);
}
