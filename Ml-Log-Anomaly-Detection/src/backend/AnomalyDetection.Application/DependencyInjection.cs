using AnomalyDetection.Application.Audit;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Application.Ingestion;
using AnomalyDetection.Application.Models;
using AnomalyDetection.Application.Queries;
using AnomalyDetection.Application.Reviews;
using AnomalyDetection.Application.Scoring;
using AnomalyDetection.Application.Search;
using AnomalyDetection.Application.Windowing;
using Microsoft.Extensions.DependencyInjection;

namespace AnomalyDetection.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddOptions<PipelineOptions>()
            .BindConfiguration(PipelineOptions.SectionName)
            .Validate(o =>
            {
                o.Validate();
                return true;
            })
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AuditService>();
        services.AddScoped<IngestionService>();
        services.AddScoped<WindowAggregationService>();
        services.AddScoped<ScoringService>();
        services.AddScoped<PipelineCoordinator>();
        services.AddScoped<ModelLifecycleService>();
        services.AddScoped<ReviewService>();
        services.AddScoped<EventInvestigationService>();
        services.AddScoped<IndexingService>();
        services.AddScoped<AnomalyQueryService>();
        services.AddScoped<WindowAndEventQueryService>();
        return services;
    }
}
