using AnomalyDetection.Domain.Entities;
using AnomalyDetection.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AnomalyDetection.Infrastructure.Persistence.Configurations;

internal static class Converters
{
    public static readonly ValueConverter<EventType, string> EventTypeToken =
        new(v => v.ToToken(), v => EnumTokens.ParseEventType(v));

    public static readonly ValueConverter<AuthenticationResult, string> AuthToken =
        new(v => v.ToToken(), v => EnumTokens.ParseAuthenticationResult(v));
}

internal sealed class ServiceDefinitionConfiguration : IEntityTypeConfiguration<ServiceDefinition>
{
    public void Configure(EntityTypeBuilder<ServiceDefinition> b)
    {
        b.ToTable("service_definition");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Environment).HasMaxLength(32).IsRequired();
        b.HasIndex(x => new { x.Name, x.Environment }).IsUnique();
    }
}

internal sealed class OperationalEventConfiguration : IEntityTypeConfiguration<OperationalEvent>
{
    public void Configure(EntityTypeBuilder<OperationalEvent> b)
    {
        b.ToTable("operational_event");
        b.HasKey(x => x.Id);
        b.Property(x => x.EventId).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.EventId).IsUnique();
        b.Property(x => x.ServiceName).HasMaxLength(100).IsRequired();
        b.Property(x => x.Environment).HasMaxLength(32).IsRequired();
        b.Property(x => x.EventType).HasConversion(Converters.EventTypeToken).HasMaxLength(32);
        b.Property(x => x.EndpointGroup).HasMaxLength(200).IsRequired();
        b.Property(x => x.AuthenticationResult).HasConversion(Converters.AuthToken).HasMaxLength(16);
        b.Property(x => x.DependencyName).HasMaxLength(100);
        b.Property(x => x.CorrelationId).HasMaxLength(128);
        b.Property(x => x.AttributesJson).HasColumnType("jsonb");
        b.Property(x => x.SchemaVersion).HasMaxLength(16);
        b.Property(x => x.ProcessingState).HasConversion<string>().HasMaxLength(16);
        b.HasOne(x => x.Service).WithMany().HasForeignKey(x => x.ServiceDefinitionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FeatureWindow>().WithMany().HasForeignKey(x => x.FeatureWindowId).OnDelete(DeleteBehavior.SetNull);

        // Access patterns (report §4.4): service + time range, correlation lookup, pipeline queues.
        b.HasIndex(x => new { x.ServiceDefinitionId, x.EventTimestampUtc });
        b.HasIndex(x => new { x.ServiceName, x.Environment, x.EventTimestampUtc });
        b.HasIndex(x => x.CorrelationId);
        b.HasIndex(x => x.FeatureWindowId);
        b.HasIndex(x => new { x.ProcessingState, x.EventTimestampUtc });
        b.HasIndex(x => x.ReceivedAtUtc).HasFilter("indexed_at_utc IS NULL").HasDatabaseName("ix_operational_event_unindexed");
    }
}

internal sealed class QuarantinedEventConfiguration : IEntityTypeConfiguration<QuarantinedEvent>
{
    public void Configure(EntityTypeBuilder<QuarantinedEvent> b)
    {
        b.ToTable("quarantined_event");
        b.HasKey(x => x.Id);
        b.Property(x => x.Source).HasMaxLength(32).IsRequired();
        b.Property(x => x.EventId).HasMaxLength(128);
        b.Property(x => x.ServiceName).HasMaxLength(100);
        b.Property(x => x.ReasonCodes).HasMaxLength(1000).IsRequired();
        b.Property(x => x.SanitizedPayloadJson).HasColumnType("jsonb").IsRequired();
        b.HasIndex(x => x.ReceivedAtUtc);
    }
}

internal sealed class FeatureWindowConfiguration : IEntityTypeConfiguration<FeatureWindow>
{
    public void Configure(EntityTypeBuilder<FeatureWindow> b)
    {
        b.ToTable("feature_window");
        b.HasKey(x => x.WindowId);
        b.Ignore(x => x.Features);
        b.Property(x => x.ServiceName).HasMaxLength(100).IsRequired();
        b.Property(x => x.Environment).HasMaxLength(32).IsRequired();
        b.Property(x => x.FeatureSchemaVersion).HasMaxLength(16).IsRequired();
        b.Property(x => x.ScoringStatus).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.LastScoringError).HasMaxLength(1000);
        b.HasOne(x => x.Service).WithMany().HasForeignKey(x => x.ServiceDefinitionId).OnDelete(DeleteBehavior.Restrict);

        // A window is finalized at most once per service, time range and schema (idempotent aggregation).
        b.HasIndex(x => new { x.ServiceDefinitionId, x.WindowStartUtc, x.WindowEndUtc, x.FeatureSchemaVersion }).IsUnique();
        b.HasIndex(x => new { x.ServiceName, x.Environment, x.WindowStartUtc });
        b.HasIndex(x => new { x.ScoringStatus, x.NextScoringAttemptUtc });
        b.HasIndex(x => x.WindowStartUtc);
    }
}

internal sealed class ModelVersionConfiguration : IEntityTypeConfiguration<ModelVersion>
{
    public void Configure(EntityTypeBuilder<ModelVersion> b)
    {
        b.ToTable("model_version");
        b.HasKey(x => x.ModelId);
        b.Property(x => x.ModelId).ValueGeneratedNever();
        b.Property(x => x.Version).HasColumnName("model_version").HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.Version).IsUnique();
        b.Property(x => x.Algorithm).HasMaxLength(32).IsRequired();
        b.Property(x => x.FeatureSchemaVersion).HasMaxLength(16).IsRequired();
        b.Property(x => x.LibraryVersionsJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.ParametersJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.ValidationMetricsJson).HasColumnType("jsonb");
        b.Property(x => x.ThresholdObjective).HasMaxLength(64);
        b.Property(x => x.ArtifactPath).HasMaxLength(256).IsRequired();
        b.Property(x => x.ArtifactSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.RegisteredBy).HasMaxLength(128);
        b.Property(x => x.ActivatedBy).HasMaxLength(128);
        b.Property(x => x.DeactivatedBy).HasMaxLength(128);

        // At most one active model per feature schema, enforced by the database.
        b.HasIndex(x => x.FeatureSchemaVersion).IsUnique().HasFilter("is_active").HasDatabaseName("ux_model_version_one_active_per_schema");
    }
}

internal sealed class ScoringRecordConfiguration : IEntityTypeConfiguration<ScoringRecord>
{
    public void Configure(EntityTypeBuilder<ScoringRecord> b)
    {
        b.ToTable("scoring_record");
        b.HasKey(x => x.Id);
        b.Property(x => x.ModelVersion).HasMaxLength(64).IsRequired();
        b.Property(x => x.FeatureSchemaVersion).HasMaxLength(16).IsRequired();
        b.Property(x => x.ReasonSummary).HasMaxLength(2000);
        b.Property(x => x.ReasonsJson).HasColumnType("jsonb").IsRequired();
        b.HasOne(x => x.Window).WithMany().HasForeignKey(x => x.WindowId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Model).WithMany().HasForeignKey(x => x.ModelId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.WindowId, x.ModelId }).IsUnique();
        b.HasIndex(x => new { x.ModelId, x.ScoredAtUtc });
    }
}

internal sealed class AnomalyRecordConfiguration : IEntityTypeConfiguration<AnomalyRecord>
{
    public void Configure(EntityTypeBuilder<AnomalyRecord> b)
    {
        b.ToTable("anomaly_record");
        b.HasKey(x => x.AnomalyId);
        b.Property(x => x.ModelVersion).HasMaxLength(64).IsRequired();
        b.Property(x => x.ReviewState).HasConversion<string>().HasMaxLength(30).HasDefaultValue(ReviewState.Unreviewed).HasSentinel((ReviewState)(-1));
        b.Property(x => x.ReasonSummary).HasMaxLength(2000);
        b.Property(x => x.ServiceName).HasMaxLength(100).IsRequired();
        b.Property(x => x.Environment).HasMaxLength(32).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne(x => x.Window).WithMany().HasForeignKey(x => x.WindowId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Model).WithMany().HasForeignKey(x => x.ModelId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ScoringRecord>().WithOne().HasForeignKey<AnomalyRecord>(x => x.ScoringRecordId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Reviews).WithOne().HasForeignKey(r => r.AnomalyId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(x => x.Reviews).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Access patterns: review state, model version, score, service + time range.
        b.HasIndex(x => new { x.WindowId, x.ModelId }).IsUnique();
        b.HasIndex(x => new { x.ReviewState, x.CreatedAtUtc });
        b.HasIndex(x => x.ModelId);
        b.HasIndex(x => x.Score);
        b.HasIndex(x => new { x.ServiceName, x.Environment, x.WindowStartUtc });
        b.HasIndex(x => x.CreatedAtUtc);
    }
}

internal sealed class AnomalyReviewConfiguration : IEntityTypeConfiguration<AnomalyReview>
{
    public void Configure(EntityTypeBuilder<AnomalyReview> b)
    {
        b.ToTable("anomaly_review");
        b.HasKey(x => x.Id);
        b.Property(x => x.PreviousState).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Note).HasMaxLength(AnomalyReview.MaxNoteLength);
        b.Property(x => x.Reviewer).HasMaxLength(128).IsRequired();
        b.HasIndex(x => new { x.AnomalyId, x.CreatedAtUtc });
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("audit_event");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(64).IsRequired();
        b.Property(x => x.Actor).HasMaxLength(128).IsRequired();
        b.Property(x => x.TargetType).HasMaxLength(64).IsRequired();
        b.Property(x => x.TargetId).HasMaxLength(128);
        b.Property(x => x.Result).HasMaxLength(16).IsRequired();
        b.Property(x => x.MetadataJson).HasColumnType("jsonb").IsRequired();
        b.HasIndex(x => x.OccurredAtUtc);
        b.HasIndex(x => new { x.Action, x.OccurredAtUtc });
    }
}
