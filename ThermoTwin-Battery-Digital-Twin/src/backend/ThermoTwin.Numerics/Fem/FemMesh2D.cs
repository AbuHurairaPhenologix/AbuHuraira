using ThermoTwin.Numerics.Grid;

namespace ThermoTwin.Numerics.Fem;

/// <summary>A mesh vertex (a degree of freedom of the P1 space).</summary>
public readonly record struct FemNode(int Index, double X, double Y);

/// <summary>The four edges of the rectangular domain.</summary>
public enum MeshEdge
{
    West,
    East,
    South,
    North,
}

/// <summary>A boundary edge of one triangle: nodes A → B on side <see cref="Edge"/>.</summary>
public readonly record struct BoundarySegment(int A, int B, MeshEdge Edge, double Length);

/// <summary>
/// Linear (P1) triangle with vertices (a, b, c) in counter-clockwise order.
/// <para>
/// With the affine map x = x_a + J ξ from the reference triangle {ξ, η ≥ 0, ξ + η ≤ 1},
/// <code>
///   J = [x_b − x_a   x_c − x_a ;  y_b − y_a   y_c − y_a],    |T| = det J / 2,
/// </code>
/// the barycentric (hat) functions have constant gradients
/// <code>
///   ∇φ_a = (y_b − y_c, x_c − x_b)/det J,  ∇φ_b = (y_c − y_a, x_a − x_c)/det J,  ∇φ_c = (y_a − y_b, x_b − x_a)/det J,
/// </code>
/// which sum to zero (partition of unity). Hence the exact element integrals
/// <code>
///   K^e_ij = ∫_T k ∇φ_i·∇φ_j dx = k |T| ∇φ_i·∇φ_j,         M^e_ij = ∫_T φ_i φ_j dx = |T|/12 · (1 + δ_ij).
/// </code>
/// </para>
/// </summary>
public sealed class TriangleElement
{
    public TriangleElement(int index, FemNode a, FemNode b, FemNode c)
    {
        Index = index;
        Nodes = [a.Index, b.Index, c.Index];
        Jacobian = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
        if (Jacobian <= 0)
        {
            throw new ArgumentException($"Triangle {index} is degenerate or clockwise (det J = {Jacobian:E3}).");
        }

        Area = 0.5 * Jacobian;
        GradX = [(b.Y - c.Y) / Jacobian, (c.Y - a.Y) / Jacobian, (a.Y - b.Y) / Jacobian];
        GradY = [(c.X - b.X) / Jacobian, (a.X - c.X) / Jacobian, (b.X - a.X) / Jacobian];
        Xs = [a.X, b.X, c.X];
        Ys = [a.Y, b.Y, c.Y];
    }

    public int Index { get; }

    /// <summary>Global node indices (a, b, c), counter-clockwise.</summary>
    public int[] Nodes { get; }

    /// <summary>det J = 2|T| (positive for counter-clockwise orientation).</summary>
    public double Jacobian { get; }

    public double Area { get; }

    /// <summary>∂φ_i/∂x for the three local basis functions.</summary>
    public double[] GradX { get; }

    /// <summary>∂φ_i/∂y for the three local basis functions.</summary>
    public double[] GradY { get; }

    public double[] Xs { get; }

    public double[] Ys { get; }

    /// <summary>Element stiffness matrix k|T|∇φ_i·∇φ_j (3 × 3, symmetric, rows sum to zero).</summary>
    public double[,] Stiffness(double conductivity)
    {
        var k = new double[3, 3];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                k[i, j] = conductivity * Area * (GradX[i] * GradX[j] + GradY[i] * GradY[j]);
            }
        }

        return k;
    }

    /// <summary>Consistent element mass matrix |T|/12·(1 + δ_ij) (3 × 3, SPD, entries sum to |T|).</summary>
    public double[,] Mass()
    {
        var m = new double[3, 3];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                m[i, j] = Area / 12 * (i == j ? 2 : 1);
            }
        }

        return m;
    }

    /// <summary>Maps reference (barycentric) coordinates (λ_a, λ_b, λ_c) to physical coordinates.</summary>
    public (double X, double Y) Map(double la, double lb, double lc) =>
        (la * Xs[0] + lb * Xs[1] + lc * Xs[2], la * Ys[0] + lb * Ys[1] + lc * Ys[2]);

    /// <summary>Barycentric coordinates of a physical point (outside the triangle some are negative).</summary>
    public (double La, double Lb, double Lc) Barycentric(double x, double y)
    {
        var lb = ((x - Xs[0]) * (Ys[2] - Ys[0]) - (Xs[2] - Xs[0]) * (y - Ys[0])) / Jacobian;
        var lc = ((Xs[1] - Xs[0]) * (y - Ys[0]) - (x - Xs[0]) * (Ys[1] - Ys[0])) / Jacobian;
        return (1 - lb - lc, lb, lc);
    }
}

/// <summary>
/// Structured triangulation of the rectangle [0, Lx] × [0, Ly]: an nx × ny array of rectangles, each split
/// along its south-west → north-east diagonal into two counter-clockwise triangles. The rectangles coincide
/// with the cells of the finite-volume grid <see cref="Grid2D"/>, so both discretisations share the same h.
/// <para>
/// Nodes are numbered x-major / y-fast, k = i·(ny + 1) + j. A P1 node couples to its six neighbours
/// (i ± 1, j), (i, j ± 1), (i + 1, j + 1), (i − 1, j − 1), so the stiffness and mass matrices have at most
/// seven non-zeros per row and half-bandwidth ny + 2.
/// </para>
/// </summary>
public sealed class FemMesh2D
{
    private readonly FemNode[] _nodes;
    private readonly TriangleElement[] _elements;
    private readonly BoundarySegment[] _boundary;

    public FemMesh2D(int nx, int ny, double lengthX, double lengthY)
    {
        if (nx < 1 || ny < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(nx), "A mesh needs at least 1×1 rectangles.");
        }

        if (lengthX <= 0 || lengthY <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthX), "Domain lengths must be positive.");
        }

        Nx = nx;
        Ny = ny;
        LengthX = lengthX;
        LengthY = lengthY;

        _nodes = new FemNode[(nx + 1) * (ny + 1)];
        for (var i = 0; i <= nx; i++)
        {
            for (var j = 0; j <= ny; j++)
            {
                var k = NodeIndex(i, j);
                _nodes[k] = new FemNode(k, i * Hx, j * Hy);
            }
        }

        var elements = new List<TriangleElement>(2 * nx * ny);
        for (var i = 0; i < nx; i++)
        {
            for (var j = 0; j < ny; j++)
            {
                var n00 = _nodes[NodeIndex(i, j)];
                var n10 = _nodes[NodeIndex(i + 1, j)];
                var n01 = _nodes[NodeIndex(i, j + 1)];
                var n11 = _nodes[NodeIndex(i + 1, j + 1)];
                elements.Add(new TriangleElement(elements.Count, n00, n10, n11));
                elements.Add(new TriangleElement(elements.Count, n00, n11, n01));
            }
        }

        _elements = [.. elements];

        var boundary = new List<BoundarySegment>(2 * (nx + ny));
        for (var i = 0; i < nx; i++)
        {
            boundary.Add(new BoundarySegment(NodeIndex(i, 0), NodeIndex(i + 1, 0), MeshEdge.South, Hx));
            boundary.Add(new BoundarySegment(NodeIndex(i + 1, ny), NodeIndex(i, ny), MeshEdge.North, Hx));
        }

        for (var j = 0; j < ny; j++)
        {
            boundary.Add(new BoundarySegment(NodeIndex(0, j + 1), NodeIndex(0, j), MeshEdge.West, Hy));
            boundary.Add(new BoundarySegment(NodeIndex(nx, j), NodeIndex(nx, j + 1), MeshEdge.East, Hy));
        }

        _boundary = [.. boundary];
    }

    /// <summary>Mesh with the same rectangles as a finite-volume grid.</summary>
    public static FemMesh2D FromGrid(Grid2D grid) => new(grid.Nx, grid.Ny, grid.LengthX, grid.LengthY);

    public int Nx { get; }

    public int Ny { get; }

    public double LengthX { get; }

    public double LengthY { get; }

    public double Hx => LengthX / Nx;

    public double Hy => LengthY / Ny;

    /// <summary>Mesh size h = longest triangle edge (the diagonal).</summary>
    public double MeshSize => Math.Sqrt(Hx * Hx + Hy * Hy);

    public int NodeCount => _nodes.Length;

    public int ElementCount => _elements.Length;

    public IReadOnlyList<FemNode> Nodes => _nodes;

    public IReadOnlyList<TriangleElement> Elements => _elements;

    public IReadOnlyList<BoundarySegment> BoundarySegments => _boundary;

    public int NodeIndex(int i, int j) => i * (Ny + 1) + j;

    public (int I, int J) NodeCoordinates(int index) => (index / (Ny + 1), index % (Ny + 1));

    /// <summary>Nodes lying on the given edge (corners included).</summary>
    public IEnumerable<int> EdgeNodes(MeshEdge edge) => edge switch
    {
        MeshEdge.West => Enumerable.Range(0, Ny + 1).Select(j => NodeIndex(0, j)),
        MeshEdge.East => Enumerable.Range(0, Ny + 1).Select(j => NodeIndex(Nx, j)),
        MeshEdge.South => Enumerable.Range(0, Nx + 1).Select(i => NodeIndex(i, 0)),
        MeshEdge.North => Enumerable.Range(0, Nx + 1).Select(i => NodeIndex(i, Ny)),
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };

    /// <summary>Nodal interpolant I_h f (values of f at the vertices).</summary>
    public double[] CreateNodalField(Func<double, double, double> f)
    {
        var field = new double[NodeCount];
        foreach (var node in _nodes)
        {
            field[node.Index] = f(node.X, node.Y);
        }

        return field;
    }

    public double[] CreateNodalField(double value)
    {
        var field = new double[NodeCount];
        Array.Fill(field, value);
        return field;
    }

    /// <summary>Element containing (x, y) — O(1) for the structured mesh. Points on the boundary are clamped inside.</summary>
    public TriangleElement Locate(double x, double y)
    {
        var gx = Math.Clamp(x / Hx, 0, Nx - 1e-12);
        var gy = Math.Clamp(y / Hy, 0, Ny - 1e-12);
        var i = Math.Min((int)gx, Nx - 1);
        var j = Math.Min((int)gy, Ny - 1);
        var tx = gx - i;
        var ty = gy - j;
        var lower = tx >= ty; // below the SW–NE diagonal
        return _elements[2 * (i * Ny + j) + (lower ? 0 : 1)];
    }

    /// <summary>Point evaluation of the P1 function T_h(x, y) = Σ T_i φ_i(x, y).</summary>
    public double Evaluate(ReadOnlySpan<double> nodal, double x, double y)
    {
        var e = Locate(x, y);
        var (la, lb, lc) = e.Barycentric(x, y);
        return la * nodal[e.Nodes[0]] + lb * nodal[e.Nodes[1]] + lc * nodal[e.Nodes[2]];
    }

    /// <summary>Evaluates the P1 function at the cell centres of a finite-volume grid (for FVM–FEM comparison).</summary>
    public double[] SampleAtCellCentres(ReadOnlySpan<double> nodal, Grid2D grid)
    {
        var values = new double[grid.CellCount];
        for (var k = 0; k < values.Length; k++)
        {
            var (x, y) = grid.CellCentre(k);
            values[k] = Evaluate(nodal, x, y);
        }

        return values;
    }

    /// <summary>Converts a nodal field to rows (j = 0 bottom) of columns (i) for display.</summary>
    public double[][] ToRows(ReadOnlySpan<double> nodal)
    {
        var rows = new double[Ny + 1][];
        for (var j = 0; j <= Ny; j++)
        {
            rows[j] = new double[Nx + 1];
            for (var i = 0; i <= Nx; i++)
            {
                rows[j][i] = nodal[NodeIndex(i, j)];
            }
        }

        return rows;
    }
}
