namespace AnomalyDetection.Domain.Entities;

/// <summary>
/// An input record that failed validation. Invalid records are routed to quarantine rather than silently dropped
/// so that logging regressions can be detected (report §4.3). The stored payload is sanitized first.
/// </summary>
public sealed class QuarantinedEvent
{
    private QuarantinedEvent()
    {
    }

    public QuarantinedEvent(string source, string? eventId, string? serviceName, IReadOnlyList<string> reasonCodes, string sanitizedPayloadJson, DateTime receivedAtUtc)
    {
        Id = Guid.NewGuid();
        Source = source;
        EventId = eventId;
        ServiceName = serviceName;
        ReasonCodes = string.Join(',', reasonCodes);
        SanitizedPayloadJson = sanitizedPayloadJson;
        ReceivedAtUtc = receivedAtUtc;
    }

    public Guid Id { get; private set; }

    /// <summary>Where the record came from: <c>api</c> or <c>middleware</c>.</summary>
    public string Source { get; private set; } = string.Empty;

    public string? EventId { get; private set; }

    public string? ServiceName { get; private set; }

    /// <summary>Comma-separated machine-readable reasons, e.g. <c>invalid_timestamp,negative_duration</c>.</summary>
    public string ReasonCodes { get; private set; } = string.Empty;

    public string SanitizedPayloadJson { get; private set; } = "{}";

    public DateTime ReceivedAtUtc { get; private set; }
}
