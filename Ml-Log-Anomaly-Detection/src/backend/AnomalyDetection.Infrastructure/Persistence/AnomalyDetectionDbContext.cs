using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AnomalyDetection.Infrastructure.Persistence;

/// <summary>PostgreSQL persistence (report §4.4). Table and column names use snake_case like the report's DDL.</summary>
public sealed class AnomalyDetectionDbContext(DbContextOptions<AnomalyDetectionDbContext> options)
    : DbContext(options), IApplicationDbContext
{
    public DbSet<ServiceDefinition> Services => Set<ServiceDefinition>();

    public DbSet<OperationalEvent> OperationalEvents => Set<OperationalEvent>();

    public DbSet<QuarantinedEvent> QuarantinedEvents => Set<QuarantinedEvent>();

    public DbSet<FeatureWindow> FeatureWindows => Set<FeatureWindow>();

    public DbSet<ModelVersion> ModelVersions => Set<ModelVersion>();

    public DbSet<ScoringRecord> ScoringRecords => Set<ScoringRecord>();

    public DbSet<AnomalyRecord> Anomalies => Set<AnomalyRecord>();

    public DbSet<AnomalyReview> AnomalyReviews => Set<AnomalyReview>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public void ResetTracking() => ChangeTracker.Clear();

    public bool IsUniqueViolation(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return true;
            }
        }

        return false;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AnomalyDetectionDbContext).Assembly);
    }
}
