using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Users;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.Infrastructure.BackgroundJobs;
using AutoSphere.Infrastructure.Caching;
using AutoSphere.Infrastructure.Identity;
using AutoSphere.Infrastructure.Messaging;
using AutoSphere.Infrastructure.Ota;
using AutoSphere.Infrastructure.Persistence;
using AutoSphere.Infrastructure.Seeding;
using AutoSphere.SharedKernel.Vehicles;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace AutoSphere.Infrastructure;

public enum DatabaseProvider
{
    PostgreSql,
    Sqlite,
}

public static class DependencyInjection
{
    public const string DatabaseConnectionName = "AutoSphere";
    public const string RedisConnectionName = "Redis";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        AddPersistence(services, configuration);
        AddIdentity(services, configuration);
        AddCaching(services, configuration);

        services.Configure<MqttBrokerOptions>(configuration.GetSection(MqttBrokerOptions.SectionName));
        services.AddSingleton<MqttVehicleGateway>();
        services.AddSingleton<IVehicleCommandPublisher>(sp => sp.GetRequiredService<MqttVehicleGateway>());
        services.AddHostedService(sp => sp.GetRequiredService<MqttVehicleGateway>());

        services.Configure<OtaSigningOptions>(configuration.GetSection(OtaSigningOptions.SectionName));
        services.AddSingleton<IPackageSigner, EcdsaPackageSigner>();

        services.AddSingleton<TelemetrySampleWriter>();
        services.AddSingleton<ITelemetrySampleSink>(sp => sp.GetRequiredService<TelemetrySampleWriter>());
        services.AddHostedService(sp => sp.GetRequiredService<TelemetrySampleWriter>());
        services.AddHostedService<TelemetryRetentionService>();
        services.AddHostedService<VehicleHealthMonitorService>();
        services.AddHostedService<OtaDeploymentTimeoutService>();

        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.SectionName));
        services.AddScoped<DatabaseInitializer>();
        return services;
    }

    /// <summary>Health checks for the infrastructure the API depends on.</summary>
    public static IHealthChecksBuilder AddInfrastructureChecks(this IHealthChecksBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddCheck<MqttHealthCheck>("mqtt", Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, ["ready"]);
        builder.AddCheck<GatewayConnectionHealthCheck>("vehicle-gateways", Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, ["ready"]);
        if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString(RedisConnectionName)))
        {
            builder.AddCheck<RedisHealthCheck>("redis", Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, ["ready"]);
        }

        return builder;
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration.GetValue("Database:Provider", DatabaseProvider.PostgreSql);
        var connectionString = configuration.GetConnectionString(DatabaseConnectionName)
                               ?? throw new InvalidOperationException($"ConnectionStrings:{DatabaseConnectionName} is not configured.");
        services.AddDbContext<AutoSphereDbContext>(options =>
        {
            if (provider == DatabaseProvider.Sqlite)
            {
                options.UseSqlite(connectionString);
            }
            else
            {
                options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3));
            }

            options.UseSnakeCaseNamingConvention();
        });
        services.AddScoped<IAutoSphereDbContext>(sp => sp.GetRequiredService<AutoSphereDbContext>());
    }

    private static void AddIdentity(IServiceCollection services, IConfiguration configuration)
    {
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.User.RequireUniqueEmail = true;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AutoSphereDbContext>();

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddSingleton<JwtSigningKeyProvider>();
        services.AddSingleton<JwtTokenService>();
        services.AddScoped<IIdentityService, IdentityService>();
    }

    private static void AddCaching(IServiceCollection services, IConfiguration configuration)
    {
        var redis = configuration.GetConnectionString(RedisConnectionName);
        if (string.IsNullOrWhiteSpace(redis))
        {
            services.AddSingleton<ILiveVehicleStateCache, InMemoryLiveVehicleStateCache>();
            return;
        }

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redis);
            options.AbortOnConnectFail = false; // start even if Redis is still booting; StackExchange.Redis reconnects
            return ConnectionMultiplexer.Connect(options);
        });
        services.AddSingleton<ILiveVehicleStateCache, RedisLiveVehicleStateCache>();
    }
}

public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"Redis ping {latency.TotalMilliseconds:0.0} ms");
        }
        catch (RedisException ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis is not reachable.", ex);
        }
    }
}

/// <summary>Reports whether registered vehicles currently have a connected gateway.</summary>
public sealed class GatewayConnectionHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoSphereDbContext>();
        var total = await db.Vehicles.CountAsync(cancellationToken);
        var online = await db.Vehicles.CountAsync(v => v.Connectivity == ConnectivityStatus.Online, cancellationToken);
        var data = new Dictionary<string, object> { ["registered"] = total, ["online"] = online };
        return total == 0 || online > 0
            ? HealthCheckResult.Healthy($"{online} of {total} vehicle gateways online.", data)
            : HealthCheckResult.Degraded("No vehicle gateway is connected.", data: data);
    }
}
