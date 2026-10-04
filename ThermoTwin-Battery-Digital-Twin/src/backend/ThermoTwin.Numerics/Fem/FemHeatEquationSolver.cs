using System.Collections.Concurrent;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Physics;

namespace ThermoTwin.Numerics.Fem;

/// <summary>
/// Galerkin P1 finite-element solver for the transient battery heat equation — an independent second
/// discretisation of the same PDE solved by the finite-volume <see cref="HeatEquationSolver"/>.
/// <para>
/// Semi-discrete system (see <see cref="FemAssembler"/>):
/// <code>
///   ρcₚ M Ṫ + K_t(u) T = F(t, u),     K_t(u) = K + R + H(u) M,     F = M q + H(u) T_c M𝟙 + r.
/// </code>
/// θ-method in time (θ = ½ Crank–Nicolson by default):
/// <code>
///   [ρcₚ M + θΔt K_t] Tⁿ⁺¹ = [ρcₚ M − (1 − θ)Δt K_t] Tⁿ + Δt [θ Fⁿ⁺¹ + (1 − θ) Fⁿ].
/// </code>
/// The left-hand matrix is SPD. Dirichlet nodes are eliminated symmetrically: their rows and columns are
/// replaced by the identity and the known values are moved to the right-hand side, which keeps the
/// system SPD so that the banded Cholesky factorisation can be reused for every step.
/// Unlike the finite-volume scheme, θ = 0 is not "explicit" here: the consistent mass matrix must still be
/// inverted (a lumped mass would make it explicit at the price of first-order mass-lumping error).
/// </para>
/// </summary>
public sealed class FemHeatEquationSolver
{
    private const int MaxCachedFactorisations = 64;

    private readonly ConcurrentDictionary<long, (BandedCholesky Factor, SparseMatrix Lhs)> _factorisations = new();
    private readonly SparseMatrix _diffusion; // K + R
    private readonly double _theta;
    private readonly double _rhoC;

    public FemHeatEquationSolver(ThermalModel model, TimeScheme scheme, double timeStep)
        : this(FemMesh2D.FromGrid(model.Grid), model, scheme, timeStep)
    {
    }

    public FemHeatEquationSolver(FemMesh2D mesh, ThermalModel model, TimeScheme scheme, double timeStep)
    {
        if (timeStep <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeStep), "Time step must be positive.");
        }

        Mesh = mesh;
        Model = model;
        Scheme = scheme;
        TimeStep = timeStep;
        Matrices = FemAssembler.Assemble(mesh, model.Boundaries, model.Material.Conductivity);
        _diffusion = SparseMatrix.LinearCombination(1, Matrices.Stiffness, 1, Matrices.RobinMass);
        _theta = scheme.Theta();
        _rhoC = model.Material.VolumetricHeatCapacity;
    }

    public FemMesh2D Mesh { get; }

    public ThermalModel Model { get; }

    public TimeScheme Scheme { get; }

    public double TimeStep { get; }

    public FemMatrices Matrices { get; }

    public int Size => Mesh.NodeCount;

    /// <summary>Non-zeros of the θ-scheme system matrix (sparsity diagnostic).</summary>
    public int SystemNonZeros => GetFactorisation(0).Lhs.NonZeroCount;

    /// <summary>Half-bandwidth of the system matrix (ny + 2 for the structured mesh).</summary>
    public int SystemHalfBandwidth => _diffusion.HalfBandwidth();

    /// <summary>Advances one step. Sources are nodal values q(x_i, t) [W/m³]; empty span means q ≡ 0.</summary>
    public void Step(
        ReadOnlySpan<double> current,
        Span<double> next,
        ReadOnlySpan<double> sourceNow,
        ReadOnlySpan<double> sourceNext,
        double coolingLevel)
    {
        var n = Size;
        var dt = TimeStep;
        var h = Model.Cooling.VolumetricCoefficient(coolingLevel, Model.Material); // H(u) [W/(m³K)]
        var coolant = Model.Cooling.CoolantTemperature;
        var isDirichlet = Matrices.IsDirichlet;
        var g = Matrices.DirichletValues;

        // w = (ρc − (1−θ)Δt H) Tⁿ + Δt (θ qⁿ⁺¹ + (1−θ) qⁿ), so that M·w carries the mass and source terms.
        var w = new double[n];
        var tn = new double[n];
        for (var i = 0; i < n; i++)
        {
            tn[i] = isDirichlet[i] ? g[i] : current[i];
            var qn = sourceNow.IsEmpty ? 0.0 : sourceNow[i];
            var qn1 = sourceNext.IsEmpty ? 0.0 : sourceNext[i];
            w[i] = (_rhoC - (1 - _theta) * dt * h) * tn[i] + dt * (_theta * qn1 + (1 - _theta) * qn);
        }

        var rhs = new double[n];
        Matrices.Mass.Multiply(w, rhs);
        if (_theta < 1)
        {
            var kt = new double[n];
            _diffusion.Multiply(tn, kt);
            Vector.Axpy(-(1 - _theta) * dt, kt, rhs);
        }

        for (var i = 0; i < n; i++)
        {
            rhs[i] += dt * (h * coolant * Matrices.LumpedMass[i] + Matrices.BoundaryLoad[i]);
        }

        var (factor, lhs) = GetFactorisation(coolingLevel);
        if (Matrices.DirichletCount > 0)
        {
            // Symmetric elimination: rhs_free −= A_{free,D} g_D, rhs_D = g_D.
            var coupling = new double[n];
            lhs.MultiplyColumns(g, coupling, isDirichlet);
            for (var i = 0; i < n; i++)
            {
                rhs[i] = isDirichlet[i] ? g[i] : rhs[i] - coupling[i];
            }
        }

        factor.SolveInPlace(rhs);
        rhs.CopyTo(next);
    }

    /// <summary>Total thermal energy ∫ ρcₚ T_h dV = ρcₚ δ Σ_i T_i ∫φ_i [J] (relative to 0 °C).</summary>
    public double ThermalEnergy(ReadOnlySpan<double> nodal) =>
        _rhoC * Model.Material.Thickness * Vector.Dot(Matrices.LumpedMass, nodal);

    private (BandedCholesky Factor, SparseMatrix Lhs) GetFactorisation(double coolingLevel)
    {
        var key = (long)Math.Round(Math.Clamp(coolingLevel, 0, 1) * 1e9);
        if (_factorisations.Count > MaxCachedFactorisations)
        {
            _factorisations.Clear();
        }

        return _factorisations.GetOrAdd(key, _ =>
        {
            var h = Model.Cooling.VolumetricCoefficient(coolingLevel, Model.Material);
            // ρc M + θΔt (K + R + H M) = (ρc + θΔt H) M + θΔt (K + R)
            var lhs = SparseMatrix.LinearCombination(_rhoC + _theta * TimeStep * h, Matrices.Mass, _theta * TimeStep, _diffusion);
            return (lhs.FactorCholesky(Matrices.IsDirichlet), lhs);
        });
    }
}
