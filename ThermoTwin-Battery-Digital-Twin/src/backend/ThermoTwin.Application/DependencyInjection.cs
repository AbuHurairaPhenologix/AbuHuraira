using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using ThermoTwin.Application.Experiments;
using ThermoTwin.Application.Scenarios;

namespace ThermoTwin.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IValidator<ScenarioDefinition>, ScenarioDefinitionValidator>();
        services.AddScoped<ExperimentService>();
        return services;
    }
}
