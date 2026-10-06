using AnomalyDetection.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AnomalyDetection.Application.Abstractions;

/// <summary>Persistence port implemented by the PostgreSQL DbContext in Infrastructure.</summary>
public interface IApplicationDbContext
{
    DbSet<ServiceDefinition> Services { get; }

    DbSet<OperationalEvent> OperationalEvents { get; }

    DbSet<QuarantinedEvent> QuarantinedEvents { get; }

    DbSet<FeatureWindow> FeatureWindows { get; }

    DbSet<ModelVersion> ModelVersions { get; }

    DbSet<ScoringRecord> ScoringRecords { get; }

    DbSet<AnomalyRecord> Anomalies { get; }

    DbSet<AnomalyReview> AnomalyReviews { get; }

    DbSet<AuditEvent> AuditEvents { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards tracked changes (used after a failed unit of work, e.g. a unique-constraint race).</summary>
    void ResetTracking();

    /// <summary>True when the exception represents a unique-constraint violation.</summary>
    bool IsUniqueViolation(Exception exception);
}
