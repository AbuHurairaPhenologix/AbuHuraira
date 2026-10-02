using AutoSphere.CanBus;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.EcuSimulation;
using AutoSphere.VehicleSimulator;
using Microsoft.Extensions.Options;
using Serilog;

// Standalone ECU simulator: runs the simulated vehicle network in its own process and connects it
// to the gateway through SocketCAN (Linux, e.g. vcan0) or the UDP CAN bridge (any OS).
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "AutoSphere.VehicleSimulator"));

builder.Services.Configure<CanBusOptions>(builder.Configuration.GetSection(CanBusOptions.SectionName));
builder.Services.Configure<MqttBrokerOptions>(builder.Configuration.GetSection(MqttBrokerOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICanBus>(sp => CanBusFactory
    .CreateAsync(sp.GetRequiredService<IOptions<CanBusOptions>>().Value, sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<TimeProvider>())
    .GetAwaiter().GetResult());
builder.Services.AddVehicleSimulation(builder.Configuration);
builder.Services.AddHostedService<SimulationControlListener>();

await builder.Build().RunAsync();
