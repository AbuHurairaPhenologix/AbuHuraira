using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AnomalyDetection.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c> to create migrations; no database connection is opened.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AnomalyDetectionDbContext>
{
    public AnomalyDetectionDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
                               ?? "Host=localhost;Database=anomaly_design_time;Username=design;Password=design";
        var options = new DbContextOptionsBuilder<AnomalyDetectionDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AnomalyDetectionDbContext(options);
    }
}
