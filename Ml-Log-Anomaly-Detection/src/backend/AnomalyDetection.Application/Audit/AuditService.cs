using System.Text.Json;
using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Domain.Entities;

namespace AnomalyDetection.Application.Audit;

/// <summary>Writes audit events. Metadata must contain only safe, non-secret values.</summary>
public sealed class AuditService(IApplicationDbContext db, TimeProvider clock)
{
    /// <summary>Adds an audit entry to the current unit of work (saved with the caller's changes).</summary>
    public void Record(string action, string actor, string targetType, string? targetId, string result, object? metadata = null)
    {
        var json = metadata is null ? "{}" : JsonSerializer.Serialize(metadata);
        db.AuditEvents.Add(new AuditEvent(action, actor, targetType, targetId, result, json, clock.GetUtcNow().UtcDateTime));
    }

    /// <summary>Adds and immediately persists an audit entry (used for denials and failures).</summary>
    public async Task RecordNowAsync(string action, string actor, string targetType, string? targetId, string result, object? metadata, CancellationToken cancellationToken)
    {
        Record(action, actor, targetType, targetId, result, metadata);
        await db.SaveChangesAsync(cancellationToken);
    }
}
