using AutoSphere.VehicleGateway;
using Serilog;

// AutoSphere Vehicle Edge Gateway: CAN → decode/normalize → supervise → MQTT, plus remote diagnostics and OTA.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "AutoSphere.VehicleGateway"));

builder.Services.AddVehicleGateway(builder.Configuration);

await builder.Build().RunAsync();
