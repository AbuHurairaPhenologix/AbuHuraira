namespace AnomalyDetection.Domain.Features;

/// <summary>Deterministic statistical helpers shared by feature generation (and mirrored in the Python module).</summary>
public static class FeatureStatistics
{
    /// <summary>
    /// Percentile using linear interpolation between closest ranks: position = (n − 1) · p.
    /// This is the same definition as NumPy's default <c>numpy.percentile(..., method="linear")</c>,
    /// so offline (Python) and online (.NET) features agree.
    /// </summary>
    public static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (percentile is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be between 0 and 100.");
        }

        if (values.Count == 0)
        {
            return 0d;
        }

        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var position = (sorted.Length - 1) * (percentile / 100d);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var fraction = position - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }

    /// <summary>
    /// Shannon entropy in bits: H = −Σ pᵢ · log₂(pᵢ), where pᵢ is the share of observations in category i.
    /// Returns 0 for an empty input or a single category.
    /// </summary>
    public static double ShannonEntropyBits<T>(IEnumerable<T> categories)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(categories);
        var counts = new Dictionary<T, int>();
        var total = 0;
        foreach (var category in categories)
        {
            counts[category] = counts.TryGetValue(category, out var c) ? c + 1 : 1;
            total++;
        }

        if (total == 0)
        {
            return 0d;
        }

        // Sum in a deterministic order so floating-point accumulation does not depend on hash ordering.
        var entropy = 0d;
        foreach (var count in counts.Values.OrderBy(v => v))
        {
            var p = (double)count / total;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }
}
