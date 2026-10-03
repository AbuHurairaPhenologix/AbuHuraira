using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ThermoTwin.Application.Abstractions;
using ThermoTwin.Infrastructure.Persistence;

namespace ThermoTwin.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("ThermoTwin") ?? "Data Source=App_Data/thermotwin.db";
        services.AddDbContext<ThermoTwinDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<ISimulationRunRepository, SimulationRunRepository>();
        services.AddScoped<IExperimentRepository, ExperimentRepository>();
        return services;
    }

    /// <summary>Creates the SQLite file and schema if missing.</summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ThermoTwinDbContext>();
        var dataSource = db.Database.GetDbConnection().DataSource;
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await db.Database.EnsureCreatedAsync(cancellationToken);
    }
}
