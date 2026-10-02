using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Common;
using AutoSphere.Application.Vehicles;
using AutoSphere.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AutoSphere.Application.Telemetry;

public sealed record TelemetryHistoryQuery(IReadOnlyList<string> Signals, DateTimeOffset? From, DateTimeOffset? To, int MaxPoints = 300);

/// <summary>Read side for live and historical telemetry.</summary>
public sealed class TelemetryQueryService(
    IAutoSphereDbContext db,
    VehicleRegistry registry,
    ILiveVehicleStateCache cache,
    IOptions<TelemetryOptions> options,
    TimeProvider timeProvider)
{
    public async Task<VehicleTelemetryDto?> GetLatestAsync(string vehicleId, CancellationToken cancellationToken)
    {
        _ = await RequireVehicleAsync(vehicleId, cancellationToken);
        var state = await cache.GetAsync(vehicleId.ToUpperInvariant(), cancellationToken);
        return state is null ? null : VehicleTelemetryDto.From(state);
    }

    /// <summary>Returns down-sampled series (bucket averages) for the requested signals.</summary>
    public async Task<IReadOnlyList<TelemetrySeriesDto>> GetHistoryAsync(string vehicleId, TelemetryHistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var vehicleKey = await RequireVehicleAsync(vehicleId, cancellationToken);
        var to = query.To ?? timeProvider.GetUtcNow();
        var from = query.From ?? to.AddMinutes(-10);
        if (from >= to)
        {
            throw new DomainException("'from' must be earlier than 'to'.");
        }

        if (to - from > TimeSpan.FromHours(options.Value.MaxHistoryHours))
        {
            throw new DomainException($"History queries are limited to {options.Value.MaxHistoryHours} hours.");
        }

        if (query.Signals.Count is 0 or > 10)
        {
            throw new DomainException("Request between 1 and 10 signals.");
        }

        var maxPoints = Math.Clamp(query.MaxPoints, 10, 2000);
        var result = new List<TelemetrySeriesDto>();
        foreach (var path in query.Signals.Distinct(StringComparer.Ordinal))
        {
            var samples = await db.TelemetryRecords.AsNoTracking()
                .Where(r => r.VehicleKey == vehicleKey && r.SignalPath == path && r.Timestamp >= from && r.Timestamp <= to)
                .OrderBy(r => r.Timestamp)
                .Select(r => new TelemetryPointDto(r.Timestamp, r.Value))
                .ToListAsync(cancellationToken);
            result.Add(new TelemetrySeriesDto(path, Downsample(samples, from, to, maxPoints)));
        }

        return result;
    }

    /// <summary>Bucket-average down-sampling; keeps the series shape while bounding the payload size.</summary>
    public static IReadOnlyList<TelemetryPointDto> Downsample(IReadOnlyList<TelemetryPointDto> samples, DateTimeOffset from, DateTimeOffset to, int maxPoints)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count <= maxPoints)
        {
            return samples;
        }

        var bucketTicks = Math.Max(1, (to - from).Ticks / maxPoints);
        return samples
            .GroupBy(s => (s.Timestamp - from).Ticks / bucketTicks)
            .OrderBy(g => g.Key)
            .Select(g => new TelemetryPointDto(from.AddTicks((g.Key * bucketTicks) + (bucketTicks / 2)), Math.Round(g.Average(s => s.Value), 3)))
            .ToList();
    }

    private async Task<Guid> RequireVehicleAsync(string vehicleId, CancellationToken cancellationToken) =>
        await registry.FindAsync(vehicleId, cancellationToken) ?? throw NotFoundException.For("Vehicle", vehicleId);
}
