using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ThermoTwin.SimulationWorker;

public static class DependencyInjection
{
    public static IServiceCollection AddSimulationWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SimulationWorkerOptions>(configuration.GetSection(SimulationWorkerOptions.SectionName));
        services.AddSingleton<SimulationCoordinator>();
        services.AddSingleton<ExperimentQueue>();
        services.AddHostedService<LiveSimulationWorker>();
        services.AddHostedService<ExperimentWorker>();
        services.AddHostedService<DemoBootstrapper>();
        return services;
    }
}
