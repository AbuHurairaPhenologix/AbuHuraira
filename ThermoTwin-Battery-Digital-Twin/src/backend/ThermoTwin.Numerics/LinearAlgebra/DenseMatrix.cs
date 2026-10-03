namespace ThermoTwin.Numerics.LinearAlgebra;

/// <summary>Row-major dense matrix used for the (small) regularised normal equations.</summary>
public sealed class DenseMatrix
{
    private readonly double[] _data;

    public DenseMatrix(int rows, int columns)
    {
        Rows = rows;
        Columns = columns;
        _data = new double[rows * columns];
    }

    public int Rows { get; }

    public int Columns { get; }

    public double this[int row, int column]
    {
        get => _data[row * Columns + column];
        set => _data[row * Columns + column] = value;
    }

    public DenseMatrix Clone()
    {
        var copy = new DenseMatrix(Rows, Columns);
        Array.Copy(_data, copy._data, _data.Length);
        return copy;
    }

    public double Trace()
    {
        var t = 0.0;
        for (var i = 0; i < Math.Min(Rows, Columns); i++)
        {
            t += this[i, i];
        }

        return t;
    }

    public double[] Multiply(ReadOnlySpan<double> x)
    {
        var y = new double[Rows];
        for (var i = 0; i < Rows; i++)
        {
            var sum = 0.0;
            var offset = i * Columns;
            for (var j = 0; j < Columns; j++)
            {
                sum += _data[offset + j] * x[j];
            }

            y[i] = sum;
        }

        return y;
    }

    /// <summary>this += scale · v vᵀ (rank-one update, used to accumulate AᵀA row by row).</summary>
    public void AddOuterProduct(ReadOnlySpan<double> v, double scale = 1.0)
    {
        for (var i = 0; i < Rows; i++)
        {
            var vi = scale * v[i];
            if (vi == 0.0)
            {
                continue;
            }

            var offset = i * Columns;
            for (var j = 0; j < Columns; j++)
            {
                _data[offset + j] += vi * v[j];
            }
        }
    }

    /// <summary>Returns this + scale · other.</summary>
    public DenseMatrix Add(DenseMatrix other, double scale)
    {
        var result = Clone();
        for (var k = 0; k < _data.Length; k++)
        {
            result._data[k] += scale * other._data[k];
        }

        return result;
    }

    /// <summary>Returns Mᵀ M for a (sparse-ish) operator stored densely.</summary>
    public DenseMatrix GramMatrix()
    {
        var g = new DenseMatrix(Columns, Columns);
        for (var r = 0; r < Rows; r++)
        {
            var offset = r * Columns;
            for (var i = 0; i < Columns; i++)
            {
                var a = _data[offset + i];
                if (a == 0.0)
                {
                    continue;
                }

                for (var j = 0; j < Columns; j++)
                {
                    g._data[i * Columns + j] += a * _data[offset + j];
                }
            }
        }

        return g;
    }

    /// <summary>Solves the SPD system this · x = b by dense Cholesky factorisation.</summary>
    public double[] SolveSpd(ReadOnlySpan<double> b) => DenseCholesky.Factor(this).Solve(b);
}

/// <summary>Dense Cholesky factorisation A = L Lᵀ, factor once / solve many.</summary>
public sealed class DenseCholesky
{
    private readonly double[] _l;

    private DenseCholesky(int size, double[] l)
    {
        Size = size;
        _l = l;
    }

    public int Size { get; }

    public static DenseCholesky Factor(DenseMatrix a)
    {
        if (a.Rows != a.Columns)
        {
            throw new InvalidOperationException("Cholesky requires a square matrix.");
        }

        var n = a.Rows;
        var l = new double[n * n];
        for (var j = 0; j < n; j++)
        {
            var diag = a[j, j];
            for (var k = 0; k < j; k++)
            {
                diag -= l[j * n + k] * l[j * n + k];
            }

            if (diag <= 0.0)
            {
                throw new InvalidOperationException($"Matrix is not positive definite (pivot {diag:E3} at row {j}).");
            }

            var ljj = Math.Sqrt(diag);
            l[j * n + j] = ljj;
            for (var i = j + 1; i < n; i++)
            {
                var sum = a[i, j];
                for (var k = 0; k < j; k++)
                {
                    sum -= l[i * n + k] * l[j * n + k];
                }

                l[i * n + j] = sum / ljj;
            }
        }

        return new DenseCholesky(n, l);
    }

    public double[] Solve(ReadOnlySpan<double> b)
    {
        var n = Size;
        var x = b.ToArray();
        for (var i = 0; i < n; i++)
        {
            var sum = x[i];
            for (var k = 0; k < i; k++)
            {
                sum -= _l[i * n + k] * x[k];
            }

            x[i] = sum / _l[i * n + i];
        }

        for (var i = n - 1; i >= 0; i--)
        {
            var sum = x[i];
            for (var k = i + 1; k < n; k++)
            {
                sum -= _l[k * n + i] * x[k];
            }

            x[i] = sum / _l[i * n + i];
        }

        return x;
    }
}
