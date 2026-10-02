using AutoSphere.SharedKernel.Diagnostics;

namespace AutoSphere.Domain.Alerts;

public enum AlertSource
{
    /// <summary>Raised by the vehicle gateway (edge rule).</summary>
    Vehicle = 0,

    /// <summary>Raised by the backend (e.g. vehicle offline, critical DTC, failed OTA).</summary>
    Backend = 1,
}

/// <summary>An alert condition with its raise/clear/acknowledge lifecycle.</summary>
public sealed class Alert
{
    private Alert()
    {
    }

    public Guid Id { get; private set; }

    public Guid VehicleKey { get; private set; }

    /// <summary>Stable key of the condition; at most one active alert exists per key and vehicle.</summary>
    public string AlertKey { get; private set; } = string.Empty;

    public AlertSource Source { get; private set; }

    public DtcSeverity Severity { get; private set; }

    /// <summary>Category name (Thermal, Communication, …).</summary>
    public string Category { get; private set; } = string.Empty;

    public string Message { get; private set; } = string.Empty;

    public string? EcuId { get; private set; }

    public string? SignalPath { get; private set; }

    public double? Value { get; private set; }

    public double? Threshold { get; private set; }

    public DateTimeOffset RaisedAt { get; private set; }

    public DateTimeOffset? ClearedAt { get; private set; }

    public DateTimeOffset? AcknowledgedAt { get; private set; }

    public string? AcknowledgedBy { get; private set; }

    public bool IsActive => ClearedAt is null;

    public static Alert Raise(Guid vehicleKey, string alertKey, AlertSource source, DtcSeverity severity, string category, string message,
        DateTimeOffset at, string? ecuId = null, string? signalPath = null, double? value = null, double? threshold = null) => new()
    {
        Id = Guid.NewGuid(),
        VehicleKey = vehicleKey,
        AlertKey = alertKey,
        Source = source,
        Severity = severity,
        Category = category,
        Message = message,
        RaisedAt = at,
        EcuId = ecuId,
        SignalPath = signalPath,
        Value = value,
        Threshold = threshold,
    };

    /// <summary>Updates an active alert (e.g. warning escalated to critical).</summary>
    public void Update(DtcSeverity severity, string message, double? value, double? threshold)
    {
        if (severity > Severity)
        {
            // An escalation must be acknowledged again.
            AcknowledgedAt = null;
            AcknowledgedBy = null;
        }

        Severity = severity;
        Message = message;
        Value = value;
        Threshold = threshold;
    }

    public void Clear(DateTimeOffset at) => ClearedAt ??= at;

    public void Acknowledge(string user, DateTimeOffset at)
    {
        AcknowledgedAt = at;
        AcknowledgedBy = user;
    }
}
