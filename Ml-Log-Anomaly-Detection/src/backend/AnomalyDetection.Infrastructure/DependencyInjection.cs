using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Infrastructure.Health;
using AnomalyDetection.Infrastructure.Ml;
using AnomalyDetection.Infrastructure.Persistence;
using AnomalyDetection.Infrastructure.Queue;
using AnomalyDetection.Infrastructure.Search;
using AnomalyDetection.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace AnomalyDetection.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.AddDbContext<AnomalyDetectionDbContext>(o => o
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<AnomalyDetectionDbContext>());

        // ML service: options, service tokens and resilient HTTP clients (report §4.7: timeouts, retries and circuit
        // breaking belong in the calling backend).
        services.AddOptions<MlServiceOptions>().BindConfiguration(MlServiceOptions.SectionName);
        services.AddSingleton<ServiceTokenIssuer>();
        services.AddScoped<IMlScoringClient, MlScoringClient>();

        services.AddHttpClient(MlScoringClient.ResilientClientName, ConfigureMlBaseAddress)
            .AddResilienceHandler("ml-scoring", (pipeline, context) =>
            {
                var o = context.ServiceProvider.GetRequiredService<IOptions<MlServiceOptions>>().Value;
                pipeline
                    .AddTimeout(TimeSpan.FromSeconds(o.TotalTimeoutSeconds))
                    .AddRetry(new HttpRetryStrategyOptions
                    {
                        // Only transient failures (network errors, 408, 429, 5xx) are retried; 4xx contract errors are not.
                        MaxRetryAttempts = o.MaxRetryAttempts,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        Delay = TimeSpan.FromMilliseconds(500),
                    })
                    .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                    {
                        FailureRatio = 0.5,
                        MinimumThroughput = 4,
                        SamplingDuration = TimeSpan.FromSeconds(Math.Max(30, 2 * o.AttemptTimeoutSeconds)),
                        BreakDuration = TimeSpan.FromSeconds(o.CircuitBreakDurationSeconds),
                    })
                    .AddTimeout(TimeSpan.FromSeconds(o.AttemptTimeoutSeconds));
            });

        services.AddHttpClient(MlScoringClient.AdminClientName, (sp, client) =>
        {
            ConfigureMlBaseAddress(sp, client);
            client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<MlServiceOptions>>().Value.AdminTimeoutSeconds);
        });
        services.AddHttpClient(MlServiceHealthCheck.ProbeClientName, ConfigureMlBaseAddress);

        // OpenSearch.
        services.AddOptions<OpenSearchOptions>().BindConfiguration(OpenSearchOptions.SectionName);
        services.AddHttpClient(OpenSearchEventSearchService.ClientName, (sp, client) =>
        {
            var o = sp.GetRequiredService<IOptions<OpenSearchOptions>>().Value;
            client.BaseAddress = new Uri(o.Url.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
            if (!string.IsNullOrEmpty(o.Username))
            {
                var raw = System.Text.Encoding.UTF8.GetBytes($"{o.Username}:{o.Password}");
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
            }
        });
        services.AddSingleton<IEventSearchService, OpenSearchEventSearchService>();

        // In-process event capture queue + workers.
        services.AddSingleton<ChannelEventQueue>();
        services.AddSingleton<IEventQueue>(sp => sp.GetRequiredService<ChannelEventQueue>());
        services.AddSingleton<WorkerStatusRegistry>();
        services.AddOptions<ModelBootstrapOptions>().BindConfiguration(ModelBootstrapOptions.SectionName);

        var pipeline = configuration.GetSection(PipelineOptions.SectionName).Get<PipelineOptions>() ?? new PipelineOptions();
        services.AddHostedService<EventQueueWorker>();
        if (pipeline.EnableBackgroundWorkers)
        {
            services.AddHostedService<WindowAggregationWorker>();
            services.AddHostedService<ScoringWorker>();
            services.AddHostedService<IndexingWorker>();
            services.AddHostedService<ModelBootstrapWorker>();
        }

        services.AddHealthChecks()
            .AddDbContextCheck<AnomalyDetectionDbContext>("postgresql", HealthStatus.Unhealthy, ["ready", "dependency"])
            .AddCheck<OpenSearchHealthCheck>("opensearch", HealthStatus.Degraded, ["ready", "dependency"])
            .AddCheck<MlServiceHealthCheck>("ml-service", HealthStatus.Degraded, ["ready", "dependency"]);

        return services;
    }

    private static void ConfigureMlBaseAddress(IServiceProvider sp, HttpClient client)
    {
        var o = sp.GetRequiredService<IOptions<MlServiceOptions>>().Value;
        client.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");

        // The resilience pipeline owns timeouts; disable HttpClient's own 100 s default for the resilient client.
        client.Timeout = Timeout.InfiniteTimeSpan;
    }
}
