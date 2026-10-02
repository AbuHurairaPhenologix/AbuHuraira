using AutoSphere.Application.Alerts;
using AutoSphere.Application.Common;
using AutoSphere.Application.Diagnostics;
using AutoSphere.Application.Health;
using AutoSphere.Application.Messaging;
using AutoSphere.Application.Ota;
using AutoSphere.Application.Simulation;
using AutoSphere.Application.Telemetry;
using AutoSphere.Application.Vehicles;
using AutoSphere.Domain.Vehicles;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AutoSphere.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TelemetryOptions>(configuration.GetSection(TelemetryOptions.SectionName));
        services.Configure<DiagnosticsOptions>(configuration.GetSection(DiagnosticsOptions.SectionName));
        services.Configure<OtaOptions>(configuration.GetSection(OtaOptions.SectionName));
        services.Configure<FaultInjectionOptions>(configuration.GetSection(FaultInjectionOptions.SectionName));
        services.Configure<HealthThresholds>(configuration.GetSection("Health"));

        services.AddValidatorsFromAssemblyContaining<RegisterVehicleValidator>(ServiceLifetime.Singleton);
        services.AddSingleton(TimeProvider.System);

        // Singletons: hold in-memory state shared across messages and requests.
        services.AddSingleton<VehicleRegistry>();
        services.AddSingleton<DiagnosticResponseAwaiter>();
        services.AddSingleton<TelemetryIngestionService>();
        services.AddSingleton<VehicleMessageDispatcher>();

        // Scoped: one unit of work per request or per inbound message.
        services.AddScoped<VehicleService>();
        services.AddScoped<VehicleStatusIngestionService>();
        services.AddScoped<VehicleHealthService>();
        services.AddScoped<TelemetryQueryService>();
        services.AddScoped<AlertService>();
        services.AddScoped<DiagnosticService>();
        services.AddScoped<DtcIngestionService>();
        services.AddScoped<SoftwarePackageService>();
        services.AddScoped<OtaCampaignService>();
        services.AddScoped<OtaStatusIngestionService>();
        services.AddScoped<FaultInjectionService>();
        return services;
    }
}
