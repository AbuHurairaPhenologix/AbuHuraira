using AutoSphere.CanBus;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.EcuSimulation;
using AutoSphere.VehicleGateway.Can;
using AutoSphere.VehicleGateway.Configuration;
using AutoSphere.VehicleGateway.Instrumentation;
using AutoSphere.VehicleGateway.Messaging;
using AutoSphere.VehicleGateway.Monitoring;
using AutoSphere.VehicleGateway.Network;
using AutoSphere.VehicleGateway.Ota;
using AutoSphere.VehicleGateway.Publishing;
using AutoSphere.VehicleGateway.RemoteDiagnostics;
using AutoSphere.VehicleGateway.Signals;
using AutoSphere.VehicleGateway.Simulation;
using AutoSphere.VehicleSignals.Codec;
using AutoSphere.VehicleSignals.Database;
using Microsoft.Extensions.Options;

namespace AutoSphere.VehicleGateway;

public static class GatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers the complete vehicle edge gateway. When <c>Simulation:Enabled</c> is true the simulated
    /// ECUs are hosted in the same process on the same CAN bus (the default development set-up).
    /// </summary>
    public static IServiceCollection AddVehicleGateway(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GatewayOptions>(configuration.GetSection(GatewayOptions.SectionName));
        services.Configure<CanBusOptions>(configuration.GetSection(CanBusOptions.SectionName));
        services.Configure<MqttBrokerOptions>(configuration.GetSection(MqttBrokerOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICanBus>(sp => CanBusFactory
            .CreateAsync(sp.GetRequiredService<IOptions<CanBusOptions>>().Value, sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<TimeProvider>())
            .GetAwaiter().GetResult());
        services.AddSingleton(_ => CanDatabaseLoader.Default);
        services.AddSingleton<CanSignalCodec>();
        services.AddSingleton<ICanSignalDecoder>(sp => sp.GetRequiredService<CanSignalCodec>());

        services.AddSingleton<GatewayMetrics>();
        services.AddSingleton<VehicleSignalStore>();
        services.AddSingleton<EcuNetworkMonitor>();
        services.AddSingleton<CanConnectionState>();
        services.AddSingleton<StatusSignal>();
        services.AddSingleton<DiagnosticClientPool>();
        services.AddSingleton<UdsDiagnosticService>();
        services.AddSingleton<DiagnosticRequestHandler>();
        services.AddSingleton<OtaTrustAnchor>();
        services.AddSingleton<OtaUpdateAgent>();
        services.AddSingleton<FaultCommandHandler>();

        // Hosted services that other components also call into are registered once as singletons.
        services.AddSingleton<GatewayMqttClient>();
        services.AddHostedService(sp => sp.GetRequiredService<GatewayMqttClient>());
        services.AddSingleton<DtcMonitorService>();
        services.AddHostedService(sp => sp.GetRequiredService<DtcMonitorService>());

        var simulation = configuration.GetSection(SimulationOptions.SectionName).Get<SimulationOptions>();
        if (simulation?.Enabled == true)
        {
            services.AddVehicleSimulation(configuration);
            services.PostConfigure<SimulationOptions>(o =>
            {
                // In-process simulation shares the gateway's security-access secret unless configured separately.
                if (string.IsNullOrWhiteSpace(o.SecurityAccessSecret))
                {
                    o.SecurityAccessSecret = configuration[$"{GatewayOptions.SectionName}:{nameof(GatewayOptions.SecurityAccessSecret)}"] ?? string.Empty;
                }

                o.VehicleId = configuration[$"{GatewayOptions.SectionName}:{nameof(GatewayOptions.VehicleId)}"] ?? o.VehicleId;
            });
        }

        services.AddHostedService<CanReceiverService>();
        services.AddHostedService<NetworkSupervisionService>();
        services.AddHostedService<TelemetryPublisherService>();
        services.AddHostedService<StatusPublisherService>();
        services.AddHostedService<EdgeAlertMonitor>();
        services.AddHostedService<CommandDispatcher>();
        return services;
    }
}
