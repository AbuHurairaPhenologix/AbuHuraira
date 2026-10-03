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
