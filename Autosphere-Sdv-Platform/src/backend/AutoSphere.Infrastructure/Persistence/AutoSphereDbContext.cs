using AutoSphere.Application.Abstractions;
using AutoSphere.Domain.Alerts;
using AutoSphere.Domain.Diagnostics;
using AutoSphere.Domain.Ota;
using AutoSphere.Domain.Simulation;
using AutoSphere.Domain.Telemetry;
using AutoSphere.Domain.Vehicles;
using AutoSphere.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AutoSphere.Infrastructure.Persistence;

/// <summary>EF Core context for PostgreSQL (production) and SQLite (tests / zero-dependency local runs).</summary>
public sealed class AutoSphereDbContext(DbContextOptions<AutoSphereDbContext> options)
    : IdentityDbContext<ApplicationUser>(options), IAutoSphereDbContext
{
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();

    public DbSet<Ecu> Ecus => Set<Ecu>();

    public DbSet<TelemetryRecord> TelemetryRecords => Set<TelemetryRecord>();

    public DbSet<DiagnosticTroubleCode> DiagnosticTroubleCodes => Set<DiagnosticTroubleCode>();

    public DbSet<DiagnosticSession> DiagnosticSessions => Set<DiagnosticSession>();

    public DbSet<Alert> Alerts => Set<Alert>();

    public DbSet<SoftwarePackage> SoftwarePackages => Set<SoftwarePackage>();

    public DbSet<OtaCampaign> OtaCampaigns => Set<OtaCampaign>();

    public DbSet<OtaDeployment> OtaDeployments => Set<OtaDeployment>();

    public DbSet<VehicleHealthSnapshot> VehicleHealthSnapshots => Set<VehicleHealthSnapshot>();

    public DbSet<FaultInjectionRecord> FaultInjections => Set<FaultInjectionRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AutoSphereDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Enums are stored as readable strings: the database stays self-explanatory for analysis.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);

        if (Database.IsSqlite())
        {
            // SQLite has no native timestamp type that supports ordering/comparison of DateTimeOffset.
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
            configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }
}
