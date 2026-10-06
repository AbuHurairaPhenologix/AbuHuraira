using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ThermoTwin.Domain.Entities;

namespace ThermoTwin.Infrastructure.Persistence;

public sealed class ThermoTwinDbContext : DbContext
{
    public ThermoTwinDbContext(DbContextOptions<ThermoTwinDbContext> options)
        : base(options)
    {
    }

    public DbSet<SimulationRun> SimulationRuns => Set<SimulationRun>();

    public DbSet<SimulationSnapshot> SimulationSnapshots => Set<SimulationSnapshot>();

    public DbSet<ExperimentRecord> Experiments => Set<ExperimentRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite cannot order by DateTimeOffset natively; persist as UTC ticks.
        var offsetConverter = new ValueConverter<DateTimeOffset, long>(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        var nullableOffsetConverter = new ValueConverter<DateTimeOffset?, long?>(
            v => v.HasValue ? v.Value.UtcTicks : null,
            v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);

        modelBuilder.Entity<SimulationRun>(e =>
        {
            e.ToTable("simulation_runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(200).IsRequired();
            e.Property(r => r.ScenarioKey).HasMaxLength(100).IsRequired();
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.CreatedAt).HasConversion(offsetConverter);
            e.Property(r => r.StartedAt).HasConversion(nullableOffsetConverter);
            e.Property(r => r.CompletedAt).HasConversion(nullableOffsetConverter);
            e.Property(r => r.FailureReason).HasMaxLength(2000);
            e.Ignore(r => r.IsActive);
            e.HasIndex(r => r.CreatedAt);
        });

        modelBuilder.Entity<SimulationSnapshot>(e =>
        {
            e.ToTable("simulation_snapshots");
            e.HasKey(s => s.Id);
            e.Property(s => s.Risk).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(s => new { s.RunId, s.Time });
            e.HasOne<SimulationRun>().WithMany().HasForeignKey(s => s.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExperimentRecord>(e =>
        {
            e.ToTable("experiments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.CreatedAt).HasConversion(offsetConverter);
            e.Property(x => x.Summary).HasMaxLength(1000);
            e.HasIndex(x => new { x.Kind, x.CreatedAt });
        });
    }
}
