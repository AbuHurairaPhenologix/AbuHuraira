namespace ThermoTwin.Numerics.LinearAlgebra;

/// <summary>
/// Symmetric banded matrix storing only the lower band: entry (i, j) with
/// 0 ≤ i − j ≤ p is kept at <c>data[i·(p+1) + (i − j)]</c>.
/// </summary>
public sealed class BandedSymmetricMatrix
{
    internal readonly double[] Data;

    public BandedSymmetricMatrix(int size, int halfBandwidth)
    {
        Size = size;
        HalfBandwidth = halfBandwidth;
        Data = new double[size * (halfBandwidth + 1)];
    }

    public int Size { get; }

    public int HalfBandwidth { get; }

    public double this[int row, int column]
    {
        get
        {
            if (column > row)
            {
                (row, column) = (column, row);
            }

            var d = row - column;
            return d > HalfBandwidth ? 0.0 : Data[row * (HalfBandwidth + 1) + d];
        }
        set
        {
            if (column > row)
            {
                (row, column) = (column, row);
            }

            var d = row - column;
            if (d > HalfBandwidth)
            {
                throw new ArgumentOutOfRangeException(nameof(column), "Entry lies outside the band.");
            }

            Data[row * (HalfBandwidth + 1) + d] = value;
        }
    }
}

/// <summary>
/// Cholesky factorisation A = L Lᵀ of a symmetric positive-definite banded matrix.
/// Cost is O(n·p²) to factor and O(n·p) per solve, versus O(n³) / O(n²) dense —
/// for the 40×20 battery grid (n = 800, p = 20) a solve costs ≈ 3·10⁴ flops.
/// The factor is immutable after construction, so one instance can be shared across threads.
/// </summary>
public sealed class BandedCholesky
{
    private readonly double[] _l;
    private readonly int _p;

    private BandedCholesky(int size, int halfBandwidth, double[] lower)
    {
        Size = size;
        _p = halfBandwidth;
        _l = lower;
    }

    public int Size { get; }

    public static BandedCholesky Factor(BandedSymmetricMatrix matrix)
    {
        var n = matrix.Size;
        var p = matrix.HalfBandwidth;
        var w = p + 1;
        var l = (double[])matrix.Data.Clone();

        for (var j = 0; j < n; j++)
        {
            // Diagonal: l_jj = sqrt(a_jj − Σ_k l_jk²)
            var kStart = Math.Max(0, j - p);
            var diag = l[j * w];
            for (var k = kStart; k < j; k++)
            {
                var ljk = l[j * w + (j - k)];
                diag -= ljk * ljk;
            }

            if (diag <= 0.0)
            {
                throw new InvalidOperationException(
                    $"Matrix is not positive definite (pivot {diag:E3} at row {j}).");
            }

            var ljj = Math.Sqrt(diag);
            l[j * w] = ljj;

            // Column below the diagonal: l_ij = (a_ij − Σ_k l_ik l_jk) / l_jj
            var iEnd = Math.Min(n - 1, j + p);
            for (var i = j + 1; i <= iEnd; i++)
            {
                var sum = l[i * w + (i - j)];
                var start = Math.Max(0, i - p);
                for (var k = start; k < j; k++)
                {
                    sum -= l[i * w + (i - k)] * l[j * w + (j - k)];
                }

                l[i * w + (i - j)] = sum / ljj;
            }
        }

        return new BandedCholesky(n, p, l);
    }

    /// <summary>Solves A x = b in place (b is overwritten with x).</summary>
    public void SolveInPlace(Span<double> b)
    {
        var n = Size;
        var p = _p;
        var w = p + 1;

        // Forward substitution: L y = b
        for (var i = 0; i < n; i++)
        {
            var sum = b[i];
            var start = Math.Max(0, i - p);
            var rowOffset = i * w + i;
            for (var k = start; k < i; k++)
            {
                sum -= _l[rowOffset - k] * b[k];
            }

            b[i] = sum / _l[i * w];
        }

        // Backward substitution: Lᵀ x = y
        for (var i = n - 1; i >= 0; i--)
        {
            var sum = b[i];
            var end = Math.Min(n - 1, i + p);
            for (var k = i + 1; k <= end; k++)
            {
                sum -= _l[k * w + (k - i)] * b[k];
            }

            b[i] = sum / _l[i * w];
        }
    }
}
