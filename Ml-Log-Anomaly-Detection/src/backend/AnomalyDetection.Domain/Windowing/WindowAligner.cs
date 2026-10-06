namespace AnomalyDetection.Domain.Windowing;

/// <summary>A fixed, epoch-aligned observation window [Start, End).</summary>
public readonly record struct ObservationWindow(DateTime StartUtc, DateTime EndUtc)
{
    public bool Contains(DateTime timestampUtc) => timestampUtc >= StartUtc && timestampUtc < EndUtc;
}

/// <summary>
/// Aligns timestamps to fixed observation windows (report §3.7). Supported sizes are 1, 5 (default) and 15 minutes.
/// Windows are aligned to the Unix epoch so every component computes the same boundaries.
/// </summary>
public static class WindowAligner
{
    public static readonly IReadOnlyList<int> SupportedWindowSizesMinutes = [1, 5, 15];

    public const int DefaultWindowSizeMinutes = 5;

    public static bool IsSupported(int windowSizeMinutes) => SupportedWindowSizesMinutes.Contains(windowSizeMinutes);

    public static ObservationWindow Align(DateTime timestampUtc, int windowSizeMinutes)
    {
        if (!IsSupported(windowSizeMinutes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowSizeMinutes),
                windowSizeMinutes,
                "Window size must be 1, 5 or 15 minutes.");
        }

        if (timestampUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(timestampUtc));
        }

        var size = TimeSpan.FromMinutes(windowSizeMinutes);
        var ticks = timestampUtc.Ticks - (timestampUtc.Ticks % size.Ticks);
        var start = new DateTime(ticks, DateTimeKind.Utc);
        return new ObservationWindow(start, start + size);
    }
}
