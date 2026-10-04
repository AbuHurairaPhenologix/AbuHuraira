using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Optimization;
using ThermoTwin.Numerics.Pde;

namespace ThermoTwin.Numerics.ReducedOrder;

/// <summary>
/// POD–Galerkin reduced-order model of the finite-volume heat equation.
/// <para>
/// <b>Projection.</b> Insert T ≈ T̄ + Φ_r a into the semi-discrete system dT/dt = (αA − σ(u)I)T + αb + s(t)q/ρcₚ + σ(u)T_c
/// and require the residual to be orthogonal to span(Φ_r) (Galerkin condition, Φ_rᵀΦ_r = I):
/// <code>
///   da/dt = (α A_r − σ(u) I_r) a + α Φ_rᵀ(A T̄ + b) + s(t) Φ_rᵀ q/ρcₚ + σ(u) Φ_rᵀ(T_c𝟙 − T̄),     A_r = Φ_rᵀ A Φ_r.
/// </code>
/// A_r is symmetric negative semi-definite (A is), so it is diagonalised once offline, A_r = V Λ Vᵀ, and the
/// model is written in the rotated basis Ψ = Φ_r V (still orthonormal, same subspace). In these coordinates
/// M_r(u) = αΛ − σ(u)I is <i>diagonal</i> for every cooling level, so a θ-step costs O(r) without any
/// factorisation, and the operators are trivially symmetric for the adjoint. The same θ-scheme as the full
/// model is used, so the ROM is the exact Galerkin projection of the <i>discrete</i> full-order model.
/// </para>
/// <para>
/// <b>Cost.</b> Each step costs O(r) for the dynamics plus O(nr) to reconstruct T for the pointwise state
/// constraint; the full model costs O(np) for the banded solve (p = half-bandwidth). The reconstruction
/// therefore bounds the achievable speed-up.
/// </para>
/// </summary>
public sealed class ReducedThermalModel : IThermalDynamics
{
    private readonly double[][] _psi;      // r rotated modes, each of length n
    private readonly double[] _mean;
    private readonly double[] _lambda;     // α Λ_i (≤ 0) [1/s]
    private readonly double[] _constant;   // c_r
    private readonly double[] _coolant;    // d_r
    private readonly HeatEquationSolver _solver;
    private readonly double _theta;
    private readonly double _rhoC;
    private readonly int[]? _outputCells;     // screened constraint cells (null = whole field)
    private readonly double[][]? _psiOut;     // Ψ restricted to the output cells
    private readonly double[]? _meanOut;

    private ReducedThermalModel(ReducedThermalModel source, int[] cells)
    {
        Basis = source.Basis;
        Modes = source.Modes;
        ReducedSpectrum = source.ReducedSpectrum;
        _psi = source._psi;
        _mean = source._mean;
        _lambda = source._lambda;
        _constant = source._constant;
        _coolant = source._coolant;
        _solver = source._solver;
        _theta = source._theta;
        _rhoC = source._rhoC;
        _outputCells = cells;
        _psiOut = _psi.Select(mode => cells.Select(c => mode[c]).ToArray()).ToArray();
        _meanOut = cells.Select(c => _mean[c]).ToArray();
    }

    public ReducedThermalModel(PodBasis basis, int modes, HeatEquationSolver solver)
    {
        if (modes < 1 || modes > basis.Rank)
        {
            throw new ArgumentOutOfRangeException(nameof(modes), $"The basis has {basis.Rank} modes.");
        }

        if (basis.Dimension != solver.Size)
        {
            throw new ArgumentException("Basis and solver grids differ.", nameof(basis));
        }

        Basis = basis;
        Modes = modes;
        _solver = solver;
        _theta = solver.Theta;
        _rhoC = solver.Model.Material.VolumetricHeatCapacity;
        _mean = basis.Mean;
        var n = solver.Size;
        var alpha = solver.Model.Material.Diffusivity;
        var a = solver.Laplacian.Matrix;

        // A_r = Φᵀ A Φ
        var aphi = new double[modes][];
        for (var i = 0; i < modes; i++)
        {
            aphi[i] = new double[n];
            a.Multiply(basis.Modes[i], aphi[i]);
        }

        var reduced = new DenseMatrix(modes, modes);
        for (var i = 0; i < modes; i++)
        {
            for (var j = 0; j <= i; j++)
            {
                var value = 0.5 * (Vector.Dot(basis.Modes[i], aphi[j]) + Vector.Dot(basis.Modes[j], aphi[i]));
                reduced[i, j] = value;
                reduced[j, i] = value;
            }
        }

        var eigen = SymmetricEigensolver.Decompose(reduced);
        _lambda = eigen.Values.Select(l => alpha * l).ToArray();
        _psi = new double[modes][];
        for (var i = 0; i < modes; i++)
        {
            _psi[i] = new double[n];
            for (var j = 0; j < modes; j++)
            {
                Vector.Axpy(eigen.Vectors[j, i], basis.Modes[j], _psi[i]);
            }
        }

        // c_r = α Ψᵀ(A T̄ + b),  d_r = Ψᵀ(T_c 𝟙 − T̄)
        var aMean = new double[n];
        a.Multiply(_mean, aMean);
        var b = solver.Laplacian.BoundaryVector;
        var forcing = new double[n];
        var coolant = new double[n];
        for (var k = 0; k < n; k++)
        {
            forcing[k] = alpha * (aMean[k] + b[k]);
            coolant[k] = solver.Model.Cooling.CoolantTemperature - _mean[k];
        }

        _constant = Project(forcing);
        _coolant = Project(coolant);
        ReducedSpectrum = [.. _lambda];
    }

    public PodBasis Basis { get; }

    /// <summary>r — number of retained modes.</summary>
    public int Modes { get; }

    /// <summary>Eigenvalues αΛ_i of the reduced diffusion operator [1/s] (all ≤ 0 by symmetry/semi-definiteness).</summary>
    public double[] ReducedSpectrum { get; }

    public string Name => _outputCells is null ? $"POD–Galerkin ROM (r = {Modes})" : $"POD–Galerkin ROM (r = {Modes}, {_outputCells.Length} screened constraint cells)";

    public int StateDimension => Modes;

    public int FieldDimension => _outputCells?.Length ?? _solver.Size;

    public int DomainCells => _solver.Size;

    /// <summary>Cells at which the reconstructed temperature is evaluated (null = all cells).</summary>
    public IReadOnlyList<int>? OutputCells => _outputCells;

    /// <summary>
    /// The same ROM with the temperature reconstruction (and hence the state constraint) restricted to a screened
    /// set of cells. The O(nr) reconstruction of the full field dominates the cost of a ROM step; when the
    /// constraint can only become active in a known hot region, evaluating it there reduces the cost to O(|C|r).
    /// The penalty stays normalised by the full cell count, so μ keeps its meaning.
    /// </summary>
    public ReducedThermalModel WithOutputCells(IEnumerable<int> cells) => new(this, [.. cells.Distinct().Order()]);

    /// <summary>Full-field reconstruction T = T̄ + Ψz regardless of any output restriction.</summary>
    public double[] LiftFull(ReadOnlySpan<double> state)
    {
        var field = (double[])_mean.Clone();
        for (var i = 0; i < Modes; i++)
        {
            Vector.Axpy(state[i], _psi[i], field);
        }

        return field;
    }

    public double TimeStep => _solver.TimeStep;

    public double Theta => _theta;

    public double SinkSensitivity => _solver.SinkSensitivity;

    public ReadOnlySpan<double> ConstantForcing => _constant;

    public ReadOnlySpan<double> CoolantForcing => _coolant;

    public double SinkRate(double coolingLevel) => _solver.SinkRate(coolingLevel);

    public double[] Encode(ReadOnlySpan<double> field) => Project(Vector.Subtract(field, _mean));

    public double[] ProjectSource(ReadOnlySpan<double> sourceShape)
    {
        var p = Project(sourceShape);
        for (var i = 0; i < p.Length; i++)
        {
            p[i] /= _rhoC;
        }

        return p;
    }

    public void ApplyExplicit(ReadOnlySpan<double> x, Span<double> y, double coolingLevel)
    {
        var sigma = SinkRate(coolingLevel);
        var c = (1 - _theta) * TimeStep;
        for (var i = 0; i < Modes; i++)
        {
            y[i] = (1 + c * (_lambda[i] - sigma)) * x[i];
        }
    }

    public void SolveImplicit(Span<double> rhs, double coolingLevel)
    {
        var sigma = SinkRate(coolingLevel);
        var c = _theta * TimeStep;
        for (var i = 0; i < Modes; i++)
        {
            rhs[i] /= 1 - c * (_lambda[i] - sigma);
        }
    }

    public void Lift(ReadOnlySpan<double> state, Span<double> field)
    {
        var modes = _psiOut ?? _psi;
        (_meanOut ?? _mean).CopyTo(field);
        for (var i = 0; i < Modes; i++)
        {
            var a = state[i];
            var psi = modes[i];
            for (var k = 0; k < field.Length; k++)
            {
                field[k] += a * psi[k];
            }
        }
    }

    public void LiftTranspose(ReadOnlySpan<double> fieldGradient, Span<double> stateGradient)
    {
        var modes = _psiOut ?? _psi;
        for (var i = 0; i < Modes; i++)
        {
            stateGradient[i] = Vector.Dot(modes[i], fieldGradient);
        }
    }

    private double[] Project(ReadOnlySpan<double> field)
    {
        var a = new double[Modes];
        for (var i = 0; i < Modes; i++)
        {
            a[i] = Vector.Dot(_psi[i], field);
        }

        return a;
    }
}
