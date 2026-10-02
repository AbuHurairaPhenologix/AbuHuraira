using System.Text.Json;
using AutoSphere.Domain.Alerts;
using AutoSphere.Domain.Diagnostics;
using AutoSphere.Domain.Ota;
using AutoSphere.Domain.Simulation;
using AutoSphere.Domain.Telemetry;
using AutoSphere.Domain.Vehicles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AutoSphere.Infrastructure.Persistence.Configurations;

internal sealed class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        builder.ToTable("vehicles");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.VehicleId).HasMaxLength(64).IsRequired();
        builder.Property(v => v.Vin).HasMaxLength(17).IsRequired();
        builder.Property(v => v.Model).HasMaxLength(128).IsRequired();
        builder.Property(v => v.GatewayId).HasMaxLength(32);
        builder.Property(v => v.GatewaySoftwareVersion).HasMaxLength(32);
        builder.Property(v => v.HealthSummary).HasMaxLength(2000);
        builder.HasIndex(v => v.VehicleId).IsUnique();
        builder.HasIndex(v => v.Vin).IsUnique();
        builder.HasMany(v => v.Ecus).WithOne().HasForeignKey(e => e.VehicleKey).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(v => v.Ecus).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class EcuConfiguration : IEntityTypeConfiguration<Ecu>
{
    public void Configure(EntityTypeBuilder<Ecu> builder)
    {
        builder.ToTable("ecus");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.EcuId).HasMaxLength(32).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(128).IsRequired();
        builder.Property(e => e.SoftwareVersion).HasMaxLength(32).IsRequired();
        builder.Property(e => e.HardwareVersion).HasMaxLength(32);
        builder.HasIndex(e => new { e.VehicleKey, e.EcuId }).IsUnique();
    }
}

internal sealed class TelemetryRecordConfiguration : IEntityTypeConfiguration<TelemetryRecord>
{
    public void Configure(EntityTypeBuilder<TelemetryRecord> builder)
    {
        builder.ToTable("telemetry_records");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.SignalPath).HasMaxLength(128).IsRequired();

        // Serves the only query pattern: one signal of one vehicle over a time window.
        builder.HasIndex(t => new { t.VehicleKey, t.SignalPath, t.Timestamp });
        builder.HasIndex(t => t.Timestamp); // retention job
        builder.HasOne<Vehicle>().WithMany().HasForeignKey(t => t.VehicleKey).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DiagnosticTroubleCodeConfiguration : IEntityTypeConfiguration<DiagnosticTroubleCode>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<DiagnosticTroubleCode> builder)
    {
        builder.ToTable("diagnostic_trouble_codes");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.EcuId).HasMaxLength(32).IsRequired();
        builder.Property(d => d.Code).HasMaxLength(5).IsFixedLength().IsRequired();
        builder.Property(d => d.Description).HasMaxLength(256).IsRequired();
        builder.Property(d => d.FaultCategory).HasMaxLength(128).IsRequired();

        // Freeze-frame values are stored as a JSON document; they are always read together with the DTC.
        builder.Property(d => d.Snapshot)
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<List<DtcSnapshotValue>>(v, Json) ?? new List<DtcSnapshotValue>(),
                new ValueComparer<List<DtcSnapshotValue>>((a, b) => a!.SequenceEqual(b!), v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x)), v => v.ToList()))
            .HasColumnName("snapshot_json");
        builder.Ignore(d => d.IsClearable);
        builder.HasIndex(d => new { d.VehicleKey, d.Status });
        builder.HasIndex(d => new { d.VehicleKey, d.EcuId, d.Code });
        builder.HasOne<Vehicle>().WithMany().HasForeignKey(d => d.VehicleKey).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DiagnosticSessionConfiguration : IEntityTypeConfiguration<DiagnosticSession>
{
    public void Configure(EntityTypeBuilder<DiagnosticSession> builder)
    {
        builder.ToTable("diagnostic_sessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Operation).HasMaxLength(32).IsRequired();
        builder.Property(s => s.EcuId).HasMaxLength(32);
        builder.Property(s => s.RequestedBy).HasMaxLength(64).IsRequired();
        builder.Property(s => s.Error).HasMaxLength(2000);
        builder.Property(s => s.Diagnosis).HasMaxLength(2000);
        builder.HasIndex(s => new { s.VehicleKey, s.RequestedAt });
        builder.HasOne<Vehicle>().WithMany().HasForeignKey(s => s.VehicleKey).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> builder)
    {
        builder.ToTable("alerts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.AlertKey).HasMaxLength(128).IsRequired();
        builder.Property(a => a.Category).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Message).HasMaxLength(1000).IsRequired();
        builder.Property(a => a.EcuId).HasMaxLength(32);
        builder.Property(a => a.SignalPath).HasMaxLength(128);
        builder.Property(a => a.AcknowledgedBy).HasMaxLength(64);
        builder.Ignore(a => a.IsActive);
        builder.HasIndex(a => new { a.VehicleKey, a.RaisedAt });
        builder.HasIndex(a => new { a.VehicleKey, a.AlertKey, a.ClearedAt });
        builder.HasOne<Vehicle>().WithMany().HasForeignKey(a => a.VehicleKey).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SoftwarePackageConfiguration : IEntityTypeConfiguration<SoftwarePackage>
{
    public void Configure(EntityTypeBuilder<SoftwarePackage> builder)
    {
        builder.ToTable("software_packages");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Name).HasMaxLength(128).IsRequired();
        builder.Property(p => p.Version).HasMaxLength(32).IsRequired();
        builder.Property(p => p.MinimumCompatibleVersion).HasMaxLength(32).IsRequired();
        builder.Property(p => p.PayloadSha256).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(p => p.SigningKeyId).HasMaxLength(64).IsRequired();
        builder.Property(p => p.CreatedBy).HasMaxLength(64).IsRequired();
        builder.Property(p => p.ReleaseNotes).HasMaxLength(2000);
        builder.Property(p => p.FirmwareDescription).HasMaxLength(256);
        builder.Ignore(p => p.ParsedVersion);
        builder.HasIndex(p => new { p.TargetEcuType, p.Version }).IsUnique();
    }
}

internal sealed class OtaCampaignConfiguration : IEntityTypeConfiguration<OtaCampaign>
{
    public void Configure(EntityTypeBuilder<OtaCampaign> builder)
    {
        builder.ToTable("ota_campaigns");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasMaxLength(128).IsRequired();
        builder.Property(c => c.CreatedBy).HasMaxLength(64).IsRequired();
        builder.Ignore(c => c.IsCompleted);
        builder.HasOne<SoftwarePackage>().WithMany().HasForeignKey(c => c.PackageId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(c => c.Deployments).WithOne().HasForeignKey(d => d.CampaignId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Deployments).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class OtaDeploymentConfiguration : IEntityTypeConfiguration<OtaDeployment>
{
    public void Configure(EntityTypeBuilder<OtaDeployment> builder)
    {
        builder.ToTable("ota_deployments");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.VehicleId).HasMaxLength(64).IsRequired();
        builder.Property(d => d.EcuId).HasMaxLength(32).IsRequired();
        builder.Property(d => d.FromVersion).HasMaxLength(32).IsRequired();
        builder.Property(d => d.ToVersion).HasMaxLength(32).IsRequired();
        builder.Property(d => d.InstalledVersion).HasMaxLength(32);
        builder.Property(d => d.LastMessage).HasMaxLength(1000);
        builder.Property(d => d.FailureReason).HasMaxLength(1000);
        builder.Ignore(d => d.IsSuccessful);
        builder.HasIndex(d => new { d.VehicleKey, d.CreatedAt });
        builder.HasIndex(d => d.Status);
        builder.HasOne<Vehicle>().WithMany().HasForeignKey(d => d.VehicleKey).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<SoftwarePackage>().WithMany().HasForeignKey(d => d.PackageId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(d => d.Events).WithOne().HasForeignKey(e => e.DeploymentId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(d => d.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class OtaDeploymentEventConfiguration : IEntityTypeConfiguration<OtaDeploymentEvent>
{
    public void Configure(EntityTypeBuilder<OtaDeploymentEvent> builder)
    {
        builder.ToTable("ota_deployment_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Message).HasMaxLength(1000).IsRequired();
        builder.HasIndex(e => new { e.DeploymentId, e.Timestamp });
    }
}

internal sealed class VehicleHealthSnapshotConfiguration : IEntityTypeConfiguration<VehicleHealthSnapshot>
{
    public void Configure(EntityTypeBuilder<VehicleHealthSnapshot> builder)
    {
        builder.ToTable("vehicle_health_snapshots");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Summary).HasMaxLength(2000).IsRequired();
        builder.HasIndex(s => new { s.VehicleKey, s.Timestamp });
        builder.HasOne<Vehicle>().WithMany().HasForeignKey(s => s.VehicleKey).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class FaultInjectionRecordConfiguration : IEntityTypeConfiguration<FaultInjectionRecord>
{
    public void Configure(EntityTypeBuilder<FaultInjectionRecord> builder)
    {
        builder.ToTable("fault_injections");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.Action).HasMaxLength(16).IsRequired();
        builder.Property(f => f.TargetEcuId).HasMaxLength(32);
        builder.Property(f => f.RequestedBy).HasMaxLength(64).IsRequired();
        builder.Property(f => f.Result).HasMaxLength(1000);
        builder.Property(f => f.HandledBy).HasMaxLength(32);
        builder.HasIndex(f => new { f.VehicleKey, f.RequestedAt });
        builder.HasOne<Vehicle>().WithMany().HasForeignKey(f => f.VehicleKey).OnDelete(DeleteBehavior.Cascade);
    }
}
