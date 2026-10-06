using System.Collections.Concurrent;
using AutoSphere.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoSphere.Application.Vehicles;

/// <summary>
/// Cache of registered vehicle ids → database keys. Messages from unregistered vehicles are rejected,
/// and the high-frequency telemetry path never needs a database round trip.
/// </summary>
public sealed class VehicleRegistry(IServiceScopeFactory scopeFactory)
{
    private readonly ConcurrentDictionary<string, Guid> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _unknownUntil = new(StringComparer.OrdinalIgnoreCase);

    public async Task<Guid?> FindAsync(string vehicleId, CancellationToken cancellationToken)
    {
        if (_known.TryGetValue(vehicleId, out var key))
        {
            return key;
        }

        // Negative cache: avoid hitting the database for every message of an unknown vehicle.
        if (_unknownUntil.TryGetValue(vehicleId, out var until) && until > DateTimeOffset.UtcNow)
        {
            return null;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAutoSphereDbContext>();
        var id = vehicleId.ToUpperInvariant();
        var found = await db.Vehicles.AsNoTracking().Where(v => v.VehicleId == id).Select(v => (Guid?)v.Id).FirstOrDefaultAsync(cancellationToken);
        if (found is { } value)
        {
            _known[vehicleId] = value;
            return value;
        }

        _unknownUntil[vehicleId] = DateTimeOffset.UtcNow.AddSeconds(30);
        return null;
    }

    public void Register(string vehicleId, Guid key)
    {
        _known[vehicleId] = key;
        _unknownUntil.TryRemove(vehicleId, out _);
    }

    public void Remove(string vehicleId) => _known.TryRemove(vehicleId, out _);

    public IReadOnlyCollection<string> KnownVehicleIds => _known.Keys.ToList();
}
