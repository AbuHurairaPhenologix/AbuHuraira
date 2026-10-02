namespace AutoSphere.EcuSimulation.Plant;

/// <summary>A looping target-speed profile followed by the plant's driver model.</summary>
public sealed class DriveCycle
{
    private readonly (double DurationS, double SpeedKmh)[] _segments;
    private readonly double _period;

    public DriveCycle(IEnumerable<(double DurationS, double SpeedKmh)> segments)
    {
        _segments = segments.ToArray();
        if (_segments.Length == 0 || _segments.Any(s => s.DurationS <= 0))
        {
            throw new ArgumentException("A drive cycle needs at least one segment with a positive duration.", nameof(segments));
        }

        _period = _segments.Sum(s => s.DurationS);
    }

    /// <summary>
    /// Default demo profile: motorway-like cruising around 72 km/h with moderate speed changes,
    /// so the dashboard shows realistic, slowly varying values.
    /// </summary>
    public static DriveCycle Default { get; } = new([(45, 72), (20, 82), (25, 64), (30, 72), (15, 90), (20, 72)]);

    /// <summary>Stationary vehicle (useful for tests).</summary>
    public static DriveCycle Stationary { get; } = new([(60, 0)]);

    public double TargetSpeedKmh(double timeSeconds)
    {
        var t = timeSeconds % _period;
        foreach (var (duration, speed) in _segments)
        {
            if (t < duration)
            {
                // Small sinusoidal variation imitates a human driver holding speed.
                return Math.Max(0, speed + (speed > 0 ? 1.2 * Math.Sin(timeSeconds / 4.0) : 0));
            }

            t -= duration;
        }

        return _segments[^1].SpeedKmh;
    }
}
