using System.Text.Json;
using AutoSphere.Infrastructure.Seeding;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;

namespace AutoSphere.Api.Configuration;

public static class RateLimitPolicies
{
    public const string Login = "login";
}

public static class SwaggerConfiguration
{
    public static IServiceCollection AddAutoSphereSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "AutoSphere API",
                Version = "v1",
                Description = "Software-Defined Vehicle platform: vehicle management, telemetry, UDS-inspired remote diagnostics, "
                              + "secure OTA updates and (simulation only) fault injection. Live data is pushed via SignalR at /hubs/vehicles.",
            });
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "JWT from POST /api/auth/login",
            });
            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
            });
            var xml = Path.Combine(AppContext.BaseDirectory, "AutoSphere.Api.xml");
            if (File.Exists(xml))
            {
                options.IncludeXmlComments(xml);
            }
        });
        return services;
    }

    public static WebApplication UseAutoSphereSwagger(this WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "AutoSphere API v1");
            options.DocumentTitle = "AutoSphere API";
        });
        return app;
    }
}

public static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                data = e.Value.Data,
            }),
        }, Json));
    }
}

public static class StartupExtensions
{
    /// <summary>The database container may still be starting (docker compose); retry for a while.</summary>
    public static async Task InitializeDatabaseWithRetryAsync(this IServiceProvider services, ILogger logger)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await services.InitializeDatabaseAsync();
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is System.Net.Sockets.SocketException or Npgsql.NpgsqlException or TimeoutException
                                           or InvalidOperationException { InnerException: Npgsql.NpgsqlException })
            {
                logger.LogWarning("Database not ready (attempt {Attempt}): {Error}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }
}
