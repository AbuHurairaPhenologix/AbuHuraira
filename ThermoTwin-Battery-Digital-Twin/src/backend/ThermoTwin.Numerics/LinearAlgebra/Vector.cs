namespace ThermoTwin.Numerics.LinearAlgebra;

/// <summary>Small set of BLAS-1 style helpers on spans.</summary>
public static class Vector
{
    public static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    public static double Norm2(ReadOnlySpan<double> a) => Math.Sqrt(Dot(a, a));

    public static double NormInf(ReadOnlySpan<double> a)
    {
        var max = 0.0;
        foreach (var v in a)
        {
            max = Math.Max(max, Math.Abs(v));
        }

        return max;
    }

    public static double Max(ReadOnlySpan<double> a)
    {
        var max = double.NegativeInfinity;
        foreach (var v in a)
        {
            max = Math.Max(max, v);
        }

        return max;
    }

    public static double Min(ReadOnlySpan<double> a)
    {
        var min = double.PositiveInfinity;
        foreach (var v in a)
        {
            min = Math.Min(min, v);
        }

        return min;
    }

    public static double Mean(ReadOnlySpan<double> a)
    {
        var sum = 0.0;
        foreach (var v in a)
        {
            sum += v;
        }

        return sum / a.Length;
    }

    public static int ArgMax(ReadOnlySpan<double> a)
    {
        var best = 0;
        for (var i = 1; i < a.Length; i++)
        {
            if (a[i] > a[best])
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>y += a·x</summary>
    public static void Axpy(double a, ReadOnlySpan<double> x, Span<double> y)
    {
        for (var i = 0; i < x.Length; i++)
        {
            y[i] += a * x[i];
        }
    }

    public static double[] Subtract(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var r = new double[a.Length];
        for (var i = 0; i < a.Length; i++)
        {
            r[i] = a[i] - b[i];
        }

        return r;
    }

    public static double[] LogSpace(double start, double stop, int count)
    {
        var result = new double[count];
        var a = Math.Log10(start);
        var b = Math.Log10(stop);
        for (var i = 0; i < count; i++)
        {
            result[i] = Math.Pow(10, a + (b - a) * i / (count - 1));
        }

        return result;
    }
}
