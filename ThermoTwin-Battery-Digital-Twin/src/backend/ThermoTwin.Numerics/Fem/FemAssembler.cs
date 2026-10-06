using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Physics;

namespace ThermoTwin.Numerics.Fem;

/// <summary>Assembled finite-element operators of the heat equation on a <see cref="FemMesh2D"/>.</summary>
/// <param name="Mass">M_ij = ∫_Ω φ_i φ_j dx (consistent mass, unweighted) [m²].</param>
/// <param name="Stiffness">K_ij = ∫_Ω k ∇φ_i·∇φ_j dx [W/K].</param>
/// <param name="RobinMass">R_ij = Σ_{Robin edges} ∫_Γ h_e φ_i φ_j ds [W/(m·K)].</param>
/// <param name="BoundaryLoad">r_i = Σ_{Robin} ∫_Γ h_e T∞ φ_i ds − Σ_{Neumann} ∫_Γ g φ_i ds [W/m].</param>
/// <param name="LumpedMass">M·𝟙 — the area associated with each node (Σ = |Ω|).</param>
/// <param name="IsDirichlet">Nodes on a Dirichlet edge (strongly imposed).</param>
/// <param name="DirichletValues">Prescribed temperature at Dirichlet nodes (0 elsewhere).</param>
public sealed record FemMatrices(
    SparseMatrix Mass,
    SparseMatrix Stiffness,
    SparseMatrix RobinMass,
    double[] BoundaryLoad,
    double[] LumpedMass,
    bool[] IsDirichlet,
    double[] DirichletValues)
{
    public int DirichletCount => IsDirichlet.Count(d => d);
}

/// <summary>
/// Galerkin assembly of the weak form of the depth-averaged heat equation.
/// <para>
/// <b>Strong form.</b> ρcₚ ∂T/∂t − ∇·(k∇T) + H(u)(T − T_c) = q in Ω, with Dirichlet T = T_b on Γ_D,
/// Neumann −k ∂T/∂n = g on Γ_N and Robin −k ∂T/∂n = h_e(T − T∞) on Γ_R.
/// </para>
/// <para>
/// <b>Weak form.</b> Multiplying by a test function v ∈ V₀ = {v ∈ H¹(Ω) : v = 0 on Γ_D} and integrating
/// the diffusion term by parts (Green's formula, −∫∇·(k∇T)v = ∫k∇T·∇v − ∫_∂Ω k ∂T/∂n v):
/// <code>
///   ∫ ρcₚ ∂ₜT v + ∫ k∇T·∇v + ∫ H(u) T v + ∫_Γ_R h_e T v
///       = ∫ q v + ∫ H(u) T_c v + ∫_Γ_R h_e T∞ v − ∫_Γ_N g v      ∀ v ∈ V₀.
/// </code>
/// </para>
/// <para>
/// <b>Galerkin discretisation.</b> With T_h = Σ T_j(t) φ_j and v = φ_i (P1 hat functions):
/// <code>
///   ρcₚ M dT/dt + (K + H(u) M + R) T = M q + H(u) T_c M𝟙 + r,
/// </code>
/// where q is represented by its nodal interpolant (so ∫ q φ_i ≈ (M q)_i, second-order accurate).
/// Every matrix is symmetric; M is positive definite and K + R positive semi-definite (definite as soon as
/// Γ_R or Γ_D is non-empty), which makes the θ-scheme matrix SPD.
/// </para>
/// </summary>
public static class FemAssembler
{
    public static FemMatrices Assemble(FemMesh2D mesh, BoundaryConditions boundaries, double conductivity)
    {
        var n = mesh.NodeCount;
        var mass = new SparseMatrix.Builder(n);
        var stiffness = new SparseMatrix.Builder(n);
        var robin = new SparseMatrix.Builder(n);
        var load = new double[n];

        foreach (var element in mesh.Elements)
        {
            var ke = element.Stiffness(conductivity);
            var me = element.Mass();
            for (var a = 0; a < 3; a++)
            {
                for (var b = 0; b < 3; b++)
                {
                    stiffness.Add(element.Nodes[a], element.Nodes[b], ke[a, b]);
                    mass.Add(element.Nodes[a], element.Nodes[b], me[a, b]);
                }
            }
        }

        foreach (var segment in mesh.BoundarySegments)
        {
            var bc = Condition(boundaries, segment.Edge);
            var length = segment.Length;
            switch (bc.Kind)
            {
                case BoundaryKind.Robin:
                {
                    // ∫_e h φ_a φ_b ds = h ℓ/6 · [2 1; 1 2],   ∫_e h T∞ φ_a ds = h T∞ ℓ/2
                    var h = bc.HeatTransferCoefficient;
                    robin.Add(segment.A, segment.A, h * length / 3);
                    robin.Add(segment.B, segment.B, h * length / 3);
                    robin.Add(segment.A, segment.B, h * length / 6);
                    robin.Add(segment.B, segment.A, h * length / 6);
                    load[segment.A] += h * bc.Value * length / 2;
                    load[segment.B] += h * bc.Value * length / 2;
                    break;
                }

                case BoundaryKind.Neumann:
                    // −k ∂T/∂n = g is the outward flux: it enters the right-hand side as −∫ g φ_i ds.
                    load[segment.A] -= bc.Value * length / 2;
                    load[segment.B] -= bc.Value * length / 2;
                    break;

                case BoundaryKind.Dirichlet:
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(boundaries), bc.Kind, "Unknown boundary kind.");
            }
        }

        var isDirichlet = new bool[n];
        var dirichletValues = new double[n];
        foreach (var edge in Enum.GetValues<MeshEdge>())
        {
            var bc = Condition(boundaries, edge);
            if (bc.Kind != BoundaryKind.Dirichlet)
            {
                continue;
            }

            foreach (var node in mesh.EdgeNodes(edge))
            {
                isDirichlet[node] = true;
                dirichletValues[node] = bc.Value;
            }
        }

        var m = mass.Build();
        return new FemMatrices(m, stiffness.Build(), robin.Build(), load, m.RowSums(), isDirichlet, dirichletValues);
    }

    public static BoundaryCondition Condition(BoundaryConditions boundaries, MeshEdge edge) => edge switch
    {
        MeshEdge.West => boundaries.West,
        MeshEdge.East => boundaries.East,
        MeshEdge.South => boundaries.South,
        MeshEdge.North => boundaries.North,
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };
}
