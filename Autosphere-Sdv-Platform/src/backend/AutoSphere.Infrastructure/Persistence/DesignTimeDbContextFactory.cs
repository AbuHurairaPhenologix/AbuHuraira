using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AutoSphere.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> to create PostgreSQL migrations; no database connection is opened.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AutoSphereDbContext>
{
    public AutoSphereDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AutoSphereDbContext>()
            .UseNpgsql("Host=localhost;Database=autosphere;Username=autosphere")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AutoSphereDbContext(options);
    }
}
