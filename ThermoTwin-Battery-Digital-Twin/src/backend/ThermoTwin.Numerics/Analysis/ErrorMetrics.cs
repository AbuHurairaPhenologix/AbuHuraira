namespace ThermoTwin.Numerics.Analysis;

/// <summary>Discrete error norms between an approximation and a reference field.</summary>
public static class ErrorMetrics
{
    /// <summary>Root-mean-square error √(Σ (a − b)² / n).</summary>
    public static double Rmse(ReadOnlySpan<double> approximation, ReadOnlySpan<double> reference)
    {
        var sum = 0.0;
        for (var i = 0; i < approximation.Length; i++)
        {
            var e = approximation[i] - reference[i];
            sum += e * e;
        }

        return Math.Sqrt(sum / approximation.Length);
    }

    /// <summary>Maximum absolute error ‖a − b‖∞.</summary>
    public static double MaxError(ReadOnlySpan<double> approximation, ReadOnlySpan<double> reference)
    {
        var max = 0.0;
        for (var i = 0; i < approximation.Length; i++)
        {
            max = Math.Max(max, Math.Abs(approximation[i] - reference[i]));
        }

        return max;
    }

    /// <summary>Relative L² error ‖a − b‖₂ / ‖b‖₂.</summary>
    public static double RelativeL2(ReadOnlySpan<double> approximation, ReadOnlySpan<double> reference)
    {
        double num = 0, den = 0;
        for (var i = 0; i < approximation.Length; i++)
        {
            var e = approximation[i] - reference[i];
            num += e * e;
            den += reference[i] * reference[i];
        }

        return den == 0 ? Math.Sqrt(num) : Math.Sqrt(num / den);
    }

    /// <summary>Observed order of accuracy p = log(e_coarse / e_fine) / log(h_coarse / h_fine).</summary>
    public static double ObservedOrder(double errorCoarse, double errorFine, double refinementRatio = 2.0) =>
        Math.Log(errorCoarse / errorFine) / Math.Log(refinementRatio);

    public static double[] Difference(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var d = new double[a.Length];
        for (var i = 0; i < a.Length; i++)
        {
            d[i] = a[i] - b[i];
        }

        return d;
    }
}
