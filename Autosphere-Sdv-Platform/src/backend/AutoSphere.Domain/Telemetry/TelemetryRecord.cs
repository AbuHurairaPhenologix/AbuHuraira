namespace AutoSphere.Domain.Telemetry;

/// <summary>
/// One sampled signal value. AutoSphere persists at most one sample per signal and second
/// (see docs/thesis/performance-testing.md for the sampling and retention strategy).
/// </summary>
public sealed class TelemetryRecord
{
    private TelemetryRecord()
    {
    }

    public long Id { get; private set; }

    public Guid VehicleKey { get; private set; }

    /// <summary>VSS-inspired signal path, e.g. <c>Vehicle.Speed</c>.</summary>
    public string SignalPath { get; private set; } = string.Empty;

    public double Value { get; private set; }

    /// <summary>Time the CAN frame carrying the value was received by the gateway.</summary>
    public DateTimeOffset Timestamp { get; private set; }

    public static TelemetryRecord Create(Guid vehicleKey, string signalPath, double value, DateTimeOffset timestamp) => new()
    {
        VehicleKey = vehicleKey,
        SignalPath = signalPath,
        Value = value,
        Timestamp = timestamp,
    };
}
