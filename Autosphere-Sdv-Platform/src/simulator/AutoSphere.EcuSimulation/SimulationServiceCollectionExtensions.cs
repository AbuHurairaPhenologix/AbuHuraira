using AutoSphere.CanBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoSphere.EcuSimulation;

public static class SimulationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the simulated vehicle. Requires an <see cref="ICanBus"/> registration in the container.
    /// </summary>
    public static IServiceCollection AddVehicleSimulation(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SimulationOptions>(configuration.GetSection(SimulationOptions.SectionName));
        services.AddSingleton(sp => new VehicleSimulation(
            sp.GetRequiredService<IOptions<SimulationOptions>>().Value,
            sp.GetRequiredService<ICanBus>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetService<TimeProvider>()));
        services.AddSingleton(sp => sp.GetRequiredService<VehicleSimulation>().FaultInjector);
        services.AddHostedService<VehicleSimulationHostedService>();
        return services;
    }
}

/// <summary>Starts and stops the <see cref="VehicleSimulation"/> with the host.</summary>
public sealed class VehicleSimulationHostedService(VehicleSimulation simulation) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => simulation.StartAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken) => await simulation.DisposeAsync();
}
