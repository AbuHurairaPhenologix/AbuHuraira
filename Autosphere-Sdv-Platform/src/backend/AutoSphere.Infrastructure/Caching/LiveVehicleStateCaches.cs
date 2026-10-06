using System.Collections.Concurrent;
using System.Text.Json;
using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Telemetry;
using AutoSphere.Contracts.Mqtt;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AutoSphere.Infrastructure.Caching;

/// <summary>
/// Redis-backed live state: survives API restarts and can be shared by several API instances.
/// Each vehicle's latest state is one key with a time-to-live, so data of decommissioned vehicles expires.
/// </summary>
public sealed class RedisLiveVehicleStateCache(IConnectionMultiplexer redis, ILogger<RedisLiveVehicleStateCache> logger) : ILiveVehicleStateCache
{
    private static readonly TimeSpan TimeToLive = TimeSpan.FromHours(1);

    public async Task<VehicleLiveState?> GetAsync(string vehicleId, CancellationToken cancellationToken)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(Key(vehicleId));
            return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<VehicleLiveState>((string)value!, AutoSphereJson.Options);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Redis read failed for {VehicleId}", vehicleId);
            return null;
        }
    }

    public async Task SetAsync(VehicleLiveState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            await redis.GetDatabase().StringSetAsync(Key(state.VehicleId), JsonSerializer.Serialize(state, AutoSphereJson.Options), TimeToLive);
        }
        catch (RedisException ex)
        {
            // Live data is best effort; the next telemetry message overwrites it anyway.
            logger.LogWarning(ex, "Redis write failed for {VehicleId}", state.VehicleId);
        }
    }

    private static RedisKey Key(string vehicleId) => $"autosphere:vehicle:{vehicleId.ToUpperInvariant()}:live";
}

/// <summary>Process-local live state, used when no Redis connection string is configured (tests, minimal local runs).</summary>
public sealed class InMemoryLiveVehicleStateCache : ILiveVehicleStateCache
{
    private readonly ConcurrentDictionary<string, VehicleLiveState> _states = new(StringComparer.OrdinalIgnoreCase);

    public Task<VehicleLiveState?> GetAsync(string vehicleId, CancellationToken cancellationToken) =>
        Task.FromResult(_states.GetValueOrDefault(vehicleId));

    public Task SetAsync(VehicleLiveState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        _states[state.VehicleId] = state;
        return Task.CompletedTask;
    }
}
