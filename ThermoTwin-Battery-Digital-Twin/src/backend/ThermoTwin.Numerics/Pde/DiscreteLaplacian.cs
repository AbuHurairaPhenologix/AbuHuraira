using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Physics;

namespace ThermoTwin.Numerics.Pde;

/// <summary>
/// Five-point finite-volume Laplacian with boundary conditions folded in through ghost cells:
/// <code>
///   (∇²T)_ij ≈ (T_{i+1,j} − 2T_ij + T_{i−1,j}) / Δx² + (T_{i,j+1} − 2T_ij + T_{i,j−1}) / Δy²
/// </code>
/// so that ∇²T ≈ A·T + b, where A is symmetric negative (semi-)definite and b carries the
/// inhomogeneous boundary data.
/// <para>Ghost-cell closures for a boundary face at distance h/2 from the cell centre:</para>
/// <list type="bullet">
/// <item>Dirichlet T = T_b: T_ghost = 2T_b − T_P ⇒ contributes −2T_P/h² + 2T_b/h².</item>
/// <item>Neumann −k ∂T/∂n = g: T_ghost = T_P − g·h/k ⇒ contributes −g/(k·h).</item>
/// <item>Robin −k ∂T/∂n = h_c (T_face − T∞), T_face = (T_P + T_ghost)/2:
///   with β = h_c·h/k and γ = β/(1 + β/2) it contributes −γT_P/h² + γT∞/h².
///   γ → 2 recovers Dirichlet as h_c → ∞, γ → 0 recovers an insulated edge.</item>
/// </list>
/// </summary>
public sealed class DiscreteLaplacian
{
    private DiscreteLaplacian(SparseMatrix matrix, double[] boundaryVector)
    {
        Matrix = matrix;
        BoundaryVector = boundaryVector;
    }

    /// <summary>A — symmetric, ≤ 5 non-zeros per row.</summary>
    public SparseMatrix Matrix { get; }

    /// <summary>b — boundary contribution in K/m².</summary>
    public double[] BoundaryVector { get; }

    public static DiscreteLaplacian Assemble(Grid2D grid, BoundaryConditions boundaries, double conductivity)
    {
        var n = grid.CellCount;
        var builder = new SparseMatrix.Builder(n);
        var b = new double[n];
        var idx2 = 1.0 / (grid.Dx * grid.Dx);
        var idy2 = 1.0 / (grid.Dy * grid.Dy);

        for (var i = 0; i < grid.Nx; i++)
        {
            for (var j = 0; j < grid.Ny; j++)
            {
                var k = grid.Index(i, j);
                var diagonal = 0.0;

                void Neighbour(bool exists, int neighbourIndex, double invH2, double h, BoundaryCondition bc)
                {
                    if (exists)
                    {
                        builder.Add(k, neighbourIndex, invH2);
                        diagonal -= invH2;
                        return;
                    }

                    switch (bc.Kind)
                    {
                        case BoundaryKind.Dirichlet:
                            diagonal -= 2 * invH2;
                            b[k] += 2 * bc.Value * invH2;
                            break;
                        case BoundaryKind.Neumann:
                            b[k] += -bc.Value / (conductivity * h);
                            break;
                        case BoundaryKind.Robin:
                            var beta = bc.HeatTransferCoefficient * h / conductivity;
                            var gamma = beta / (1 + beta / 2);
                            diagonal -= gamma * invH2;
                            b[k] += gamma * bc.Value * invH2;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(bc), bc.Kind, "Unknown boundary kind.");
                    }
                }

                Neighbour(i > 0, i > 0 ? grid.Index(i - 1, j) : -1, idx2, grid.Dx, boundaries.West);
                Neighbour(i < grid.Nx - 1, i < grid.Nx - 1 ? grid.Index(i + 1, j) : -1, idx2, grid.Dx, boundaries.East);
                Neighbour(j > 0, j > 0 ? grid.Index(i, j - 1) : -1, idy2, grid.Dy, boundaries.South);
                Neighbour(j < grid.Ny - 1, j < grid.Ny - 1 ? grid.Index(i, j + 1) : -1, idy2, grid.Dy, boundaries.North);

                builder.Add(k, k, diagonal);
            }
        }

        return new DiscreteLaplacian(builder.Build(), b);
    }
}
