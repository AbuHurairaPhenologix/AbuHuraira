using System.Buffers;
using System.Collections.Concurrent;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Physics;

namespace ThermoTwin.Numerics.Pde;

public enum TimeScheme
{
    /// <summary>θ = 0 — first order, conditionally stable.</summary>
    ExplicitEuler,

    /// <summary>θ = 1 — first order, unconditionally stable, strongly damping.</summary>
    ImplicitEuler,

    /// <summary>θ = ½ — second order, unconditionally stable (A-stable, not L-stable).</summary>
    CrankNicolson,
}

public static class TimeSchemeExtensions
{
    public static double Theta(this TimeScheme scheme) => scheme switch
    {
        TimeScheme.ExplicitEuler => 0.0,
        TimeScheme.ImplicitEuler => 1.0,
        TimeScheme.CrankNicolson => 0.5,
        _ => throw new ArgumentOutOfRangeException(nameof(scheme)),
    };

    public static int TemporalOrder(this TimeScheme scheme) => scheme == TimeScheme.CrankNicolson ? 2 : 1;
}

/// <summary>
/// θ-method solver for the depth-averaged battery heat equation
/// <code>
///   ρcₚ ∂T/∂t = k ∇²T + q(x, y, t) − H(u) (T − T_c),     H(u) = h(u)/δ
/// </code>
/// After spatial discretisation (∇²T ≈ A T + b) this is the linear ODE system
/// <code>
///   dT/dt = M(u) T + f(t),   M(u) = α A − (H(u)/ρcₚ) I,   f = α b + q/ρcₚ + (H(u)/ρcₚ) T_c
/// </code>
/// and one θ-step reads
/// <code>
///   (I − θΔt M) Tⁿ⁺¹ = (I + (1 − θ)Δt M) Tⁿ + Δt [θ fⁿ⁺¹ + (1 − θ) fⁿ].
/// </code>
/// The left-hand matrix is symmetric positive definite and banded, so it is factorised once per
/// cooling level with a banded Cholesky decomposition and re-used for every step.
/// </summary>
public sealed class HeatEquationSolver
{
    /// <summary>
    /// Bound on cached factorisations. An optimiser visits a continuum of cooling levels, so an unbounded
    /// cache would grow with every line-search trial; when the bound is hit the cache is simply flushed.
    /// </summary>
    private const int MaxCachedFactorisations = 256;

    private readonly ConcurrentDictionary<long, BandedCholesky> _factorisations = new();
    private readonly double _theta;
    private readonly double _alpha;
    private readonly double _rhoC;

    public HeatEquationSolver(ThermalModel model, TimeScheme scheme, double timeStep)
    {
        if (timeStep <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeStep), "Time step must be positive.");
        }

        Model = model;
        Scheme = scheme;
        TimeStep = timeStep;
        Laplacian = DiscreteLaplacian.Assemble(model.Grid, model.Boundaries, model.Material.Conductivity);
        _theta = scheme.Theta();
        _alpha = model.Material.Diffusivity;
        _rhoC = model.Material.VolumetricHeatCapacity;
    }

    public ThermalModel Model { get; }

    public TimeScheme Scheme { get; }

    public double TimeStep { get; }

    public DiscreteLaplacian Laplacian { get; }

    public int Size => Model.Grid.CellCount;

    /// <summary>
    /// Advances one step of length Δt.
    /// </summary>
    /// <param name="current">Tⁿ [°C]</param>
    /// <param name="next">Receives Tⁿ⁺¹ (may not alias <paramref name="current"/>).</param>
    /// <param name="sourceNow">qⁿ [W/m³]; empty span means q ≡ 0.</param>
    /// <param name="sourceNext">qⁿ⁺¹ [W/m³]; empty span means q ≡ 0.</param>
    /// <param name="coolingLevel">u ∈ [0, 1], held constant over the step.</param>
    /// <param name="homogeneous">
    /// When true, boundary data and coolant temperature are treated as zero. The response is then
    /// linear in the source, which the inverse solver uses to build its sensitivity matrix.
    /// </param>
    public void Step(
        ReadOnlySpan<double> current,
        Span<double> next,
        ReadOnlySpan<double> sourceNow,
        ReadOnlySpan<double> sourceNext,
        double coolingLevel,
        bool homogeneous = false)
    {
        var n = Size;
        var dt = TimeStep;
        var sink = Model.Cooling.VolumetricCoefficient(coolingLevel, Model.Material) / _rhoC; // H/ρc [1/s]
        var coolant = homogeneous ? 0.0 : Model.Cooling.CoolantTemperature;
        var b = Laplacian.BoundaryVector;

        var pool = ArrayPool<double>.Shared;
        var laplacianT = pool.Rent(n);
        try
        {
            var lap = laplacianT.AsSpan(0, n);
            Laplacian.Matrix.Multiply(current, lap);

            for (var k = 0; k < n; k++)
            {
                var bk = homogeneous ? 0.0 : b[k];
                var qn = sourceNow.IsEmpty ? 0.0 : sourceNow[k];
                var qn1 = sourceNext.IsEmpty ? 0.0 : sourceNext[k];

                // Forcing f = αb + q/ρc + (H/ρc)·T_c at both time levels.
                var fn = _alpha * bk + qn / _rhoC + sink * coolant;
                var fn1 = _alpha * bk + qn1 / _rhoC + sink * coolant;

                // M·Tⁿ = α A Tⁿ − (H/ρc) Tⁿ
                var mt = _alpha * lap[k] - sink * current[k];

                next[k] = current[k] + dt * ((1 - _theta) * (mt + fn) + _theta * fn1);
            }
        }
        finally
        {
            pool.Return(laplacianT);
        }

        if (_theta > 0)
        {
            GetFactorisation(coolingLevel).SolveInPlace(next);
        }
    }

    /// <summary>Applies M(u) to a vector (used by stability analysis and the CG cross-check).</summary>
    public double[] ApplySystemMatrix(ReadOnlySpan<double> x, double coolingLevel)
    {
        var y = new double[Size];
        Laplacian.Matrix.Multiply(x, y);
        var sink = Model.Cooling.VolumetricCoefficient(coolingLevel, Model.Material) / _rhoC;
        for (var k = 0; k < y.Length; k++)
        {
            y[k] = _alpha * y[k] - sink * x[k];
        }

        return y;
    }

    /// <summary>Applies the implicit left-hand operator (I − θΔt M) to a vector.</summary>
    public double[] ApplyImplicitOperator(ReadOnlySpan<double> x, double coolingLevel)
    {
        var mx = ApplySystemMatrix(x, coolingLevel);
        var y = new double[Size];
        for (var k = 0; k < y.Length; k++)
        {
            y[k] = x[k] - _theta * TimeStep * mx[k];
        }

        return y;
    }

    /// <summary>θ of the time scheme.</summary>
    public double Theta => _theta;

    /// <summary>σ(u) = H(u)/ρcₚ — the cold-plate sink rate [1/s].</summary>
    public double SinkRate(double coolingLevel) => Model.Cooling.VolumetricCoefficient(coolingLevel, Model.Material) / _rhoC;

    /// <summary>dσ/du = (h_max − h_min)/(δ ρcₚ) — constant because h(u) is affine on [0, 1] [1/s].</summary>
    public double SinkSensitivity =>
        (Model.Cooling.MaxHeatTransferCoefficient - Model.Cooling.MinHeatTransferCoefficient) / (Model.Material.Thickness * _rhoC);

    /// <summary>
    /// Solves L(u) x = b in place with L(u) = I − θΔt M(u) (identity for explicit Euler). L is symmetric,
    /// so the same factor also solves the transposed (adjoint) system Lᵀ λ = b.
    /// </summary>
    public void SolveImplicitInPlace(Span<double> rhs, double coolingLevel)
    {
        if (_theta > 0)
        {
            GetFactorisation(coolingLevel).SolveInPlace(rhs);
        }
    }

    /// <summary>y = R(u) x with R(u) = I + (1 − θ)Δt M(u) — the explicit half of the θ-step (symmetric, so R = Rᵀ).</summary>
    public void ApplyExplicitOperator(ReadOnlySpan<double> x, Span<double> y, double coolingLevel)
    {
        Laplacian.Matrix.Multiply(x, y);
        var sink = SinkRate(coolingLevel);
        var c = (1 - _theta) * TimeStep;
        for (var k = 0; k < y.Length; k++)
        {
            y[k] = x[k] + c * (_alpha * y[k] - sink * x[k]);
        }
    }

    private BandedCholesky GetFactorisation(double coolingLevel)
    {
        var key = (long)Math.Round(Math.Clamp(coolingLevel, 0, 1) * 1e9);
        if (_factorisations.Count > MaxCachedFactorisations)
        {
            _factorisations.Clear();
        }

        return _factorisations.GetOrAdd(key, _ =>
        {
            var sink = Model.Cooling.VolumetricCoefficient(coolingLevel, Model.Material) / _rhoC;
            // I − θΔt M = (1 + θΔt H/ρc) I − θΔt α A
            return Laplacian.Matrix.FactorShifted(1 + _theta * TimeStep * sink, -_theta * TimeStep * _alpha);
        });
    }

    /// <summary>Total thermal energy ∫ ρcₚ T dV of a field [J] (relative to 0 °C).</summary>
    public double ThermalEnergy(ReadOnlySpan<double> field)
    {
        var volume = Model.Grid.CellArea * Model.Material.Thickness;
        var sum = 0.0;
        foreach (var t in field)
        {
            sum += t;
        }

        return _rhoC * volume * sum;
    }
}
