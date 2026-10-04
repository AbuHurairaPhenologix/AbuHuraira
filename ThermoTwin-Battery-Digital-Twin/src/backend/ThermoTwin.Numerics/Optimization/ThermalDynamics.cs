using ThermoTwin.Numerics.Pde;

namespace ThermoTwin.Numerics.Optimization;

/// <summary>
/// Discrete controlled thermal dynamics in the common affine θ-scheme form shared by the full-order
/// finite-volume model and the POD reduced-order model:
/// <code>
///   L(u) xⁿ⁺¹ = R(u) xⁿ + Δt [θ gⁿ⁺¹ + (1 − θ) gⁿ],
///   L(u) = I − θΔt M(u),  R(u) = I + (1 − θ)Δt M(u),  M(u) = α Ã − σ(u) I,
///   gⁿ(u) = c + s(tⁿ) p + σ(u) d,
///   Tⁿ = T̄ + Φ xⁿ   (temperature field reconstructed from the state).
/// </code>
/// Ã is symmetric (the discrete Laplacian, or its Galerkin projection), so L and R are symmetric — which is
/// exactly what the discrete adjoint needs: Lᵀ = L and Rᵀ = R, so the backward sweep re-uses the forward
/// factorisations. Only σ(u) = H(u)/ρcₚ depends on the control, and it does so affinely: σ′ = dσ/du is constant.
/// </summary>
public interface IThermalDynamics
{
    string Name { get; }

    /// <summary>Dimension of the state x (n for the full model, r for the ROM).</summary>
    int StateDimension { get; }

    /// <summary>Number of reconstructed temperature values (n cells, or a screened subset for a restricted ROM).</summary>
    int FieldDimension { get; }

    /// <summary>Number of cells of the full domain — normalises the space–time penalty so that μ means the same for every model.</summary>
    int DomainCells { get; }

    double TimeStep { get; }

    double Theta { get; }

    /// <summary>σ(u) [1/s].</summary>
    double SinkRate(double coolingLevel);

    /// <summary>σ′ = dσ/du [1/s].</summary>
    double SinkSensitivity { get; }

    /// <summary>c — control-independent forcing (boundary data) [K/s].</summary>
    ReadOnlySpan<double> ConstantForcing { get; }

    /// <summary>d — multiplies σ(u) in the forcing (coolant temperature, minus the ROM reference state) [K].</summary>
    ReadOnlySpan<double> CoolantForcing { get; }

    /// <summary>x₀ from a temperature field (identity for the full model, Φᵀ(T − T̄) for the ROM).</summary>
    double[] Encode(ReadOnlySpan<double> field);

    /// <summary>p — forcing per unit load factor of a volumetric source shape q [W/m³] → (projected) q/ρcₚ [K/s].</summary>
    double[] ProjectSource(ReadOnlySpan<double> sourceShape);

    /// <summary>y = R(u) x.</summary>
    void ApplyExplicit(ReadOnlySpan<double> x, Span<double> y, double coolingLevel);

    /// <summary>Solves L(u) x = rhs in place.</summary>
    void SolveImplicit(Span<double> rhs, double coolingLevel);

    /// <summary>T = T̄ + Φ x.</summary>
    void Lift(ReadOnlySpan<double> state, Span<double> field);

    /// <summary>Φᵀ g — maps a gradient with respect to T into a gradient with respect to x.</summary>
    void LiftTranspose(ReadOnlySpan<double> fieldGradient, Span<double> stateGradient);
}

/// <summary>The high-fidelity model: the finite-volume θ-scheme of <see cref="HeatEquationSolver"/> itself.</summary>
public sealed class FullOrderThermalDynamics : IThermalDynamics
{
    private readonly HeatEquationSolver _solver;
    private readonly double[] _constant;
    private readonly double[] _coolant;
    private readonly double _rhoC;

    public FullOrderThermalDynamics(HeatEquationSolver solver)
    {
        _solver = solver;
        var alpha = solver.Model.Material.Diffusivity;
        _rhoC = solver.Model.Material.VolumetricHeatCapacity;
        _constant = solver.Laplacian.BoundaryVector.Select(b => alpha * b).ToArray();
        _coolant = new double[solver.Size];
        Array.Fill(_coolant, solver.Model.Cooling.CoolantTemperature);
    }

    public HeatEquationSolver Solver => _solver;

    public string Name => $"Full-order FVM ({_solver.Model.Grid.Nx}×{_solver.Model.Grid.Ny})";

    public int StateDimension => _solver.Size;

    public int FieldDimension => _solver.Size;

    public int DomainCells => _solver.Size;

    public double TimeStep => _solver.TimeStep;

    public double Theta => _solver.Theta;

    public double SinkSensitivity => _solver.SinkSensitivity;

    public ReadOnlySpan<double> ConstantForcing => _constant;

    public ReadOnlySpan<double> CoolantForcing => _coolant;

    public double SinkRate(double coolingLevel) => _solver.SinkRate(coolingLevel);

    public double[] Encode(ReadOnlySpan<double> field) => field.ToArray();

    public double[] ProjectSource(ReadOnlySpan<double> sourceShape)
    {
        var p = new double[sourceShape.Length];
        for (var k = 0; k < p.Length; k++)
        {
            p[k] = sourceShape[k] / _rhoC;
        }

        return p;
    }

    public void ApplyExplicit(ReadOnlySpan<double> x, Span<double> y, double coolingLevel) =>
        _solver.ApplyExplicitOperator(x, y, coolingLevel);

    public void SolveImplicit(Span<double> rhs, double coolingLevel) => _solver.SolveImplicitInPlace(rhs, coolingLevel);

    public void Lift(ReadOnlySpan<double> state, Span<double> field) => state.CopyTo(field);

    public void LiftTranspose(ReadOnlySpan<double> fieldGradient, Span<double> stateGradient) => fieldGradient.CopyTo(stateGradient);
}
