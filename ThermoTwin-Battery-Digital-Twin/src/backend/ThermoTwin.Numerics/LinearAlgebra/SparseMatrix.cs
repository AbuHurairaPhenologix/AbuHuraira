namespace ThermoTwin.Numerics.LinearAlgebra;

/// <summary>
/// Compressed-sparse-row (CSR) matrix. Used for the discrete Laplacian, whose
/// five-point stencil gives at most five non-zeros per row.
/// </summary>
public sealed class SparseMatrix
{
    private readonly int[] _rowPointers;
    private readonly int[] _columns;
    private readonly double[] _values;

    private SparseMatrix(int size, int[] rowPointers, int[] columns, double[] values)
    {
        Size = size;
        _rowPointers = rowPointers;
        _columns = columns;
        _values = values;
    }

    public int Size { get; }

    public int NonZeroCount => _values.Length;

    public double this[int row, int column]
    {
        get
        {
            for (var k = _rowPointers[row]; k < _rowPointers[row + 1]; k++)
            {
                if (_columns[k] == column)
                {
                    return _values[k];
                }
            }

            return 0.0;
        }
    }

    /// <summary>y = A x</summary>
    public void Multiply(ReadOnlySpan<double> x, Span<double> y)
    {
        for (var row = 0; row < Size; row++)
        {
            var sum = 0.0;
            for (var k = _rowPointers[row]; k < _rowPointers[row + 1]; k++)
            {
                sum += _values[k] * x[_columns[k]];
            }

            y[row] = sum;
        }
    }

    /// <summary>Gershgorin bound on the spectral radius: max_i Σ_j |a_ij|.</summary>
    public double GershgorinRadius()
    {
        var max = 0.0;
        for (var row = 0; row < Size; row++)
        {
            var sum = 0.0;
            for (var k = _rowPointers[row]; k < _rowPointers[row + 1]; k++)
            {
                sum += Math.Abs(_values[k]);
            }

            max = Math.Max(max, sum);
        }

        return max;
    }

    /// <summary>Returns the half-bandwidth p such that a_ij = 0 whenever |i - j| &gt; p.</summary>
    public int HalfBandwidth()
    {
        var p = 0;
        for (var row = 0; row < Size; row++)
        {
            for (var k = _rowPointers[row]; k < _rowPointers[row + 1]; k++)
            {
                p = Math.Max(p, Math.Abs(row - _columns[k]));
            }
        }

        return p;
    }

    /// <summary>Builds the symmetric banded matrix c₀·I + c₁·A used by implicit time stepping.</summary>
    public BandedCholesky FactorShifted(double identityCoefficient, double matrixCoefficient)
    {
        var p = HalfBandwidth();
        var band = new BandedSymmetricMatrix(Size, p);
        for (var row = 0; row < Size; row++)
        {
            band[row, row] = identityCoefficient;
            for (var k = _rowPointers[row]; k < _rowPointers[row + 1]; k++)
            {
                var col = _columns[k];
                if (col <= row)
                {
                    band[row, col] += matrixCoefficient * _values[k];
                }
            }
        }

        return BandedCholesky.Factor(band);
    }

    /// <summary>
    /// Banded Cholesky factor of this (symmetric positive definite) matrix. Rows and columns flagged in
    /// <paramref name="identityRows"/> are replaced by those of the identity — the symmetric elimination
    /// of Dirichlet degrees of freedom used by the finite-element solver.
    /// </summary>
    public BandedCholesky FactorCholesky(bool[]? identityRows = null)
    {
        var p = HalfBandwidth();
        var band = new BandedSymmetricMatrix(Size, p);
        for (var row = 0; row < Size; row++)
        {
            if (identityRows is not null && identityRows[row])
            {
                band[row, row] = 1.0;
                continue;
            }

            for (var k = _rowPointers[row]; k < _rowPointers[row + 1]; k++)
            {
                var col = _columns[k];
                if (col <= row && (identityRows is null || !identityRows[col]))
                {
                    band[row, col] += _values[k];
                }
            }
        }

        return BandedCholesky.Factor(band);
    }

    /// <summary>y = A x restricted to the columns flagged in <paramref name="columns"/> (other entries of x ignored).</summary>
    public void MultiplyColumns(ReadOnlySpan<double> x, Span<double> y, bool[] columns)
    {
        for (var row = 0; row < Size; row++)
        {
            var sum = 0.0;
            for (var k = _rowPointers[row]; k < _rowPointers[row + 1]; k++)
            {
                var col = _columns[k];
                if (columns[col])
                {
                    sum += _values[k] * x[col];
                }
            }

            y[row] = sum;
        }
    }

    /// <summary>Returns a·A + b·B (patterns are merged, so the operands need not share a sparsity pattern).</summary>
    public static SparseMatrix LinearCombination(double a, SparseMatrix first, double b, SparseMatrix second)
    {
        if (first.Size != second.Size)
        {
            throw new ArgumentException("Matrices must have the same size.", nameof(second));
        }

        var n = first.Size;
        var rowPointers = new int[n + 1];
        var columns = new List<int>(first.NonZeroCount + second.NonZeroCount);
        var values = new List<double>(first.NonZeroCount + second.NonZeroCount);
        for (var row = 0; row < n; row++)
        {
            int i = first._rowPointers[row], iEnd = first._rowPointers[row + 1];
            int j = second._rowPointers[row], jEnd = second._rowPointers[row + 1];
            while (i < iEnd || j < jEnd)
            {
                var ci = i < iEnd ? first._columns[i] : int.MaxValue;
                var cj = j < jEnd ? second._columns[j] : int.MaxValue;
                if (ci == cj)
                {
                    columns.Add(ci);
                    values.Add(a * first._values[i++] + b * second._values[j++]);
                }
                else if (ci < cj)
                {
                    columns.Add(ci);
                    values.Add(a * first._values[i++]);
                }
                else
                {
                    columns.Add(cj);
                    values.Add(b * second._values[j++]);
                }
            }

            rowPointers[row + 1] = columns.Count;
        }

        return new SparseMatrix(n, rowPointers, [.. columns], [.. values]);
    }

    /// <summary>Largest |a_ij − a_ji| — a symmetry check used by assertions and tests.</summary>
    public double SymmetryDefect()
    {
        var max = 0.0;
        for (var row = 0; row < Size; row++)
        {
            for (var k = _rowPointers[row]; k < _rowPointers[row + 1]; k++)
            {
                max = Math.Max(max, Math.Abs(_values[k] - this[_columns[k], row]));
            }
        }

        return max;
    }

    /// <summary>Row sums A·𝟙.</summary>
    public double[] RowSums()
    {
        var sums = new double[Size];
        for (var row = 0; row < Size; row++)
        {
            for (var k = _rowPointers[row]; k < _rowPointers[row + 1]; k++)
            {
                sums[row] += _values[k];
            }
        }

        return sums;
    }

    /// <summary>Quadratic form xᵀ A x.</summary>
    public double QuadraticForm(ReadOnlySpan<double> x)
    {
        var sum = 0.0;
        for (var row = 0; row < Size; row++)
        {
            var r = 0.0;
            for (var k = _rowPointers[row]; k < _rowPointers[row + 1]; k++)
            {
                r += _values[k] * x[_columns[k]];
            }

            sum += x[row] * r;
        }

        return sum;
    }

    /// <summary>Dense copy (small matrices only — used by projections and tests).</summary>
    public DenseMatrix ToDense()
    {
        var dense = new DenseMatrix(Size, Size);
        for (var row = 0; row < Size; row++)
        {
            for (var k = _rowPointers[row]; k < _rowPointers[row + 1]; k++)
            {
                dense[row, _columns[k]] += _values[k];
            }
        }

        return dense;
    }

    public sealed class Builder
    {
        private readonly int _size;
        private readonly List<(int Row, int Column, double Value)> _entries = [];

        public Builder(int size) => _size = size;

        public void Add(int row, int column, double value) => _entries.Add((row, column, value));

        public SparseMatrix Build()
        {
            var merged = _entries
                .GroupBy(e => (e.Row, e.Column))
                .Select(g => (g.Key.Row, g.Key.Column, Value: g.Sum(e => e.Value)))
                .OrderBy(e => e.Row).ThenBy(e => e.Column)
                .ToArray();

            var rowPointers = new int[_size + 1];
            foreach (var entry in merged)
            {
                rowPointers[entry.Row + 1]++;
            }

            for (var i = 0; i < _size; i++)
            {
                rowPointers[i + 1] += rowPointers[i];
            }

            return new SparseMatrix(
                _size,
                rowPointers,
                merged.Select(e => e.Column).ToArray(),
                merged.Select(e => e.Value).ToArray());
        }
    }
}
