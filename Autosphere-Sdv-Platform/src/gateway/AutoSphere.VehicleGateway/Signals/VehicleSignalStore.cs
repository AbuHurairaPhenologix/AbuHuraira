using System.Collections.Concurrent;
using AutoSphere.Contracts.Messages;
using AutoSphere.VehicleSignals.Codec;

namespace AutoSphere.VehicleGateway.Signals;

/// <summary>Latest normalized value of one signal and the timeout after which it is stale.</summary>
public sealed record SignalSample(SignalValueDto Value, TimeSpan Timeout);

/// <summary>
/// Normalizes decoded CAN signals into VSS-addressed values and keeps the latest value per signal.
/// This is the gateway's local "vehicle state".
/// </summary>
public sealed class VehicleSignalStore
{
    private readonly ConcurrentDictionary<string, SignalSample> _latest = new(StringComparer.Ordinal);

    public void Update(DecodedMessage message, string sourceEcuId, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(message);
        foreach (var signal in message.Signals)
        {
            var value = new SignalValueDto(
                signal.Definition.VssPath,
                signal.Definition.Name,
                Math.Round(signal.PhysicalValue, 3),
                signal.Definition.Unit,
                sourceEcuId,
                message.Frame.Timestamp,
                signal.IsInRange ? SignalQuality.Valid : SignalQuality.OutOfRange,
                signal.Label);
            _latest[value.Path] = new SignalSample(value, timeout);
        }
    }

    public bool TryGet(string vssPath, out SignalValueDto value)
    {
        if (_latest.TryGetValue(vssPath, out var sample))
        {
            value = sample.Value;
            return true;
        }

        value = null!;
        return false;
    }

    /// <summary>Returns every known signal; values older than their timeout are marked <see cref="SignalQuality.Stale"/>.</summary>
    public IReadOnlyList<SignalValueDto> Snapshot(DateTimeOffset now) =>
        _latest.Values
            .Select(s => now - s.Value.Timestamp > s.Timeout && s.Value.Quality == SignalQuality.Valid
                ? s.Value with { Quality = SignalQuality.Stale }
                : s.Value)
            .OrderBy(v => v.Path, StringComparer.Ordinal)
            .ToList();
}
