using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.LinearAlgebra;

namespace ThermoTwin.Numerics.Inverse;

/// <summary>
/// Finite-dimensional parameterisation of the unknown heat source:
/// <code>
///   q(x, y) = Σ_j q_j φ_j(x, y)
/// </code>
/// where φ_j are bilinear "hat" functions on a coarse mx × my node lattice covering the cell.
/// Reducing ~800 grid unknowns to ~100 smooth basis coefficients is the first (implicit)
/// regularisation; Tikhonov regularisation is the second.
/// </summary>
public sealed class SourceBasis
{
    private readonly (int Node, double Weight)[][] _cellWeights;

    public SourceBasis(Grid2D grid, int nodesX, int nodesY)
    {
        if (nodesX < 2 || nodesY < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(nodesX), "The source basis needs at least 2×2 nodes.");
        }

        Grid = grid;
        NodesX = nodesX;
        NodesY = nodesY;
        SpacingX = grid.LengthX / (nodesX - 1);
        SpacingY = grid.LengthY / (nodesY - 1);
        _cellWeights = new (int, double)[grid.CellCount][];

        for (var k = 0; k < grid.CellCount; k++)
        {
            var (x, y) = grid.CellCentre(k);
            var gx = x / SpacingX;
            var gy = y / SpacingY;
            var a = Math.Min((int)Math.Floor(gx), nodesX - 2);
            var b = Math.Min((int)Math.Floor(gy), nodesY - 2);
            var tx = gx - a;
            var ty = gy - b;
            _cellWeights[k] =
            [
                (NodeIndex(a, b), (1 - tx) * (1 - ty)),
                (NodeIndex(a + 1, b), tx * (1 - ty)),
                (NodeIndex(a, b + 1), (1 - tx) * ty),
                (NodeIndex(a + 1, b + 1), tx * ty),
            ];
        }
    }

    public Grid2D Grid { get; }

    public int NodesX { get; }

    public int NodesY { get; }

    public double SpacingX { get; }

    public double SpacingY { get; }

    public int Count => NodesX * NodesY;

    public int NodeIndex(int a, int b) => b * NodesX + a;

    public (double X, double Y) NodePosition(int node) => (node % NodesX * SpacingX, node / NodesX * SpacingY);

    /// <summary>Grid field of a single basis function φ_j (scaled by <paramref name="amplitude"/>).</summary>
    public double[] BasisField(int node, double amplitude = 1.0)
    {
        var field = new double[Grid.CellCount];
        for (var k = 0; k < field.Length; k++)
        {
            foreach (var (n, w) in _cellWeights[k])
            {
                if (n == node)
                {
                    field[k] += amplitude * w;
                }
            }
        }

        return field;
    }

    /// <summary>q(x_k) = Σ_j c_j φ_j(x_k) evaluated at every cell centre.</summary>
    public double[] Expand(ReadOnlySpan<double> coefficients, double scale = 1.0)
    {
        var field = new double[Grid.CellCount];
        for (var k = 0; k < field.Length; k++)
        {
            var sum = 0.0;
            foreach (var (n, w) in _cellWeights[k])
            {
                sum += w * coefficients[n];
            }

            field[k] = scale * sum;
        }

        return field;
    }

    /// <summary>Builds the regularisation operator L for the chosen smoothness prior.</summary>
    public DenseMatrix RegularizationOperator(RegularizationKind kind)
    {
        var n = Count;
        switch (kind)
        {
            case RegularizationKind.Identity:
            {
                var l = new DenseMatrix(n, n);
                for (var i = 0; i < n; i++)
                {
                    l[i, i] = 1;
                }

                return l;
            }

            case RegularizationKind.Gradient:
            {
                // First differences along x and y: penalises ||∇q||².
                var rows = (NodesX - 1) * NodesY + NodesX * (NodesY - 1);
                var l = new DenseMatrix(rows, n);
                var r = 0;
                for (var b = 0; b < NodesY; b++)
                {
                    for (var a = 0; a < NodesX - 1; a++, r++)
                    {
                        l[r, NodeIndex(a, b)] = -1;
                        l[r, NodeIndex(a + 1, b)] = 1;
                    }
                }

                for (var b = 0; b < NodesY - 1; b++)
                {
                    for (var a = 0; a < NodesX; a++, r++)
                    {
                        l[r, NodeIndex(a, b)] = -1;
                        l[r, NodeIndex(a, b + 1)] = 1;
                    }
                }

                return l;
            }

            case RegularizationKind.Laplacian:
            {
                // Five-point Laplacian with mirrored (zero-normal-derivative) edges: penalises curvature.
                var l = new DenseMatrix(n, n);
                for (var b = 0; b < NodesY; b++)
                {
                    for (var a = 0; a < NodesX; a++)
                    {
                        var row = NodeIndex(a, b);
                        void Link(int aa, int bb)
                        {
                            if (aa < 0 || aa >= NodesX || bb < 0 || bb >= NodesY)
                            {
                                return;
                            }

                            l[row, NodeIndex(aa, bb)] += 1;
                            l[row, row] -= 1;
                        }

                        Link(a - 1, b);
                        Link(a + 1, b);
                        Link(a, b - 1);
                        Link(a, b + 1);
                    }
                }

                return l;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }
}

public enum RegularizationKind
{
    /// <summary>Zeroth order: L = I, penalises ||q||².</summary>
    Identity,

    /// <summary>First order: L = ∇_h, penalises ||∇q||² (favours smooth sources).</summary>
    Gradient,

    /// <summary>Second order: L = Δ_h, penalises curvature.</summary>
    Laplacian,
}
