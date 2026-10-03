namespace ThermoTwin.Numerics.Grid;

/// <summary>
/// Uniform cell-centred grid on the rectangle [0, Lx] × [0, Ly].
/// Cell (i, j) has centre ((i + ½)Δx, (j + ½)Δy).
/// <para>
/// Unknowns are ordered x-major / y-fast: k = i·Ny + j. Neighbours in y are ±1 apart and
/// neighbours in x are ±Ny apart, so the five-point Laplacian has half-bandwidth Ny. Choosing
/// the shorter side as the fast index keeps the banded Cholesky factor small.
/// </para>
/// </summary>
public sealed record Grid2D
{
    public Grid2D(int nx, int ny, double lengthX, double lengthY)
    {
        if (nx < 2 || ny < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(nx), "A grid needs at least 2×2 cells.");
        }

        if (lengthX <= 0 || lengthY <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthX), "Domain lengths must be positive.");
        }

        Nx = nx;
        Ny = ny;
        LengthX = lengthX;
        LengthY = lengthY;
    }

    public int Nx { get; }

    public int Ny { get; }

    public double LengthX { get; }

    public double LengthY { get; }

    public double Dx => LengthX / Nx;

    public double Dy => LengthY / Ny;

    public int CellCount => Nx * Ny;

    public double CellArea => Dx * Dy;

    public int Index(int i, int j) => i * Ny + j;

    public (int I, int J) Coordinates(int index) => (index / Ny, index % Ny);

    public double X(int i) => (i + 0.5) * Dx;

    public double Y(int j) => (j + 0.5) * Dy;

    public (double X, double Y) CellCentre(int index)
    {
        var (i, j) = Coordinates(index);
        return (X(i), Y(j));
    }

    public double[] CreateField(double value = 0.0)
    {
        var field = new double[CellCount];
        Array.Fill(field, value);
        return field;
    }

    public double[] CreateField(Func<double, double, double> f)
    {
        var field = new double[CellCount];
        for (var i = 0; i < Nx; i++)
        {
            for (var j = 0; j < Ny; j++)
            {
                field[Index(i, j)] = f(X(i), Y(j));
            }
        }

        return field;
    }

    /// <summary>Converts a field to rows (j = 0 is the bottom edge) of columns (i) for serialisation and display.</summary>
    public double[][] ToRows(ReadOnlySpan<double> field)
    {
        var rows = new double[Ny][];
        for (var j = 0; j < Ny; j++)
        {
            rows[j] = new double[Nx];
            for (var i = 0; i < Nx; i++)
            {
                rows[j][i] = field[Index(i, j)];
            }
        }

        return rows;
    }

    /// <summary>
    /// Restricts a field from a grid refined by an integer factor onto this grid by
    /// cell averaging (the finite-volume restriction operator).
    /// </summary>
    public double[] RestrictFrom(Grid2D fine, ReadOnlySpan<double> fineField)
    {
        if (fine.Nx % Nx != 0 || fine.Ny % Ny != 0 || fine.Nx / Nx != fine.Ny / Ny)
        {
            throw new ArgumentException("Fine grid must be an integer refinement of this grid.", nameof(fine));
        }

        var r = fine.Nx / Nx;
        var coarse = new double[CellCount];
        for (var i = 0; i < Nx; i++)
        {
            for (var j = 0; j < Ny; j++)
            {
                var sum = 0.0;
                for (var a = 0; a < r; a++)
                {
                    for (var b = 0; b < r; b++)
                    {
                        sum += fineField[fine.Index(i * r + a, j * r + b)];
                    }
                }

                coarse[Index(i, j)] = sum / (r * r);
            }
        }

        return coarse;
    }

    /// <summary>
    /// Bilinear interpolation weights of point (x, y) from surrounding cell centres.
    /// Points within half a cell of the boundary are clamped to the nearest centre line.
    /// </summary>
    public IReadOnlyList<(int Index, double Weight)> InterpolationWeights(double x, double y)
    {
        var gx = Math.Clamp(x / Dx - 0.5, 0, Nx - 1);
        var gy = Math.Clamp(y / Dy - 0.5, 0, Ny - 1);
        var i0 = Math.Min((int)Math.Floor(gx), Nx - 2);
        var j0 = Math.Min((int)Math.Floor(gy), Ny - 2);
        var tx = gx - i0;
        var ty = gy - j0;

        return
        [
            (Index(i0, j0), (1 - tx) * (1 - ty)),
            (Index(i0 + 1, j0), tx * (1 - ty)),
            (Index(i0, j0 + 1), (1 - tx) * ty),
            (Index(i0 + 1, j0 + 1), tx * ty),
        ];
    }

    public double Interpolate(ReadOnlySpan<double> field, double x, double y)
    {
        var value = 0.0;
        foreach (var (index, weight) in InterpolationWeights(x, y))
        {
            value += weight * field[index];
        }

        return value;
    }
}
