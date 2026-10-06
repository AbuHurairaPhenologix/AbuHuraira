using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Physics;
using ThermoTwin.Numerics.Prediction;

namespace ThermoTwin.Numerics.Optimization;

/// <summary>Weights and limits of the optimal-control problem.</summary>
/// <param name="SafeTemperature">State constraint T(x, t) ≤ T_safe [°C].</param>
/// <param name="Segments">K — number of piecewise-constant control segments.</param>
/// <param name="SegmentDuration">Length of each segment [s] (a multiple of the model time step).</param>
/// <param name="SmoothnessWeight">w in w Σ (u_{k+1} − u_k)².</param>
public sealed record ControlProblemSettings(double SafeTemperature, int Segments, double SegmentDuration, double SmoothnessWeight)
{
    public double Horizon => Segments * SegmentDuration;
}

/// <summary>Forward state trajectory xⁿ, n = 0 … N, kept for the backward (adjoint) sweep.</summary>
public sealed class StateTrajectory
{
    public StateTrajectory(int steps, int dimension)
    {
        States = new double[steps + 1][];
        for (var n = 0; n <= steps; n++)
        {
            States[n] = new double[dimension];
        }
    }

    public double[][] States { get; }

    public int Steps => States.Length - 1;
}

/// <summary>One evaluation of Φ_μ(u) = J(u) + μ P(T(u)).</summary>
/// <param name="Value">Φ_μ.</param>
/// <param name="Objective">J = E/E_ref + w Σ(Δu)² (without the state-constraint penalty).</param>
/// <param name="Penalty">P = mean over the space–time grid of max(0, T − T_safe)² [K²].</param>
/// <param name="Gradient">∇Φ_μ (null until computed).</param>
public sealed record ObjectiveEvaluation(
    double[] Controls,
    double Mu,
    double Value,
    double Objective,
    double EnergyTerm,
    double SmoothnessTerm,
    double Penalty,
    double EnergyJoules,
    double PeakTemperature,
    double MaxViolation,
    int ViolatingPoints,
    ThermalForecast Forecast,
    StateTrajectory? Trajectory)
{
    public double[]? Gradient { get; internal set; }
}

/// <summary>
/// The reduced objective of the PDE-constrained cooling problem
/// <code>
///   min_{u ∈ U_ad}  J(T, u) = (1/E_ref) Σ_k P(u_k) Δt_k + w Σ_k (u_{k+1} − u_k)²
///   s.t.  ρcₚ ∂T/∂t − k∇²T + H(u)(T − T_c) = q          (state equation, discretised)
///         T(x, t) ≤ T_safe                                (pointwise state constraint)
///         U_ad = { u ∈ ℝ^K : 0 ≤ u_k ≤ 1 }                (box constraints)
/// </code>
/// The state constraint is relaxed by a Moreau–Yosida (quadratic-penalty) term on the space–time cylinder,
/// <code>
///   Φ_μ(u) = J(u) + μ P(u),     P(u) = (1/N) Σ_{n=1}^{N} (1/n) Σ_i max(0, T_iⁿ(u) − T_safe)²,
/// </code>
/// which is C¹ with a Lipschitz gradient (the squared hinge), so that a discrete adjoint gradient exists
/// everywhere; μ → ∞ recovers the constrained problem. The non-smooth "max over x" of the original penalty
/// is avoided: every space–time point carries its own constraint.
/// <para>
/// <b>Discrete adjoint.</b> With the residual eⁿ = L(uⁿ) xⁿ⁺¹ − R(uⁿ) xⁿ − Δt ĝⁿ(uⁿ) and the Lagrangian
/// 𝓛 = Φ_μ + Σ_n (λⁿ⁺¹)ᵀ eⁿ, stationarity in xⁿ gives the backward recursion
/// <code>
///   L(u^{N−1})ᵀ λᴺ = −μ ∂P/∂xᴺ,
///   L(u^{n−1})ᵀ λⁿ = R(uⁿ)ᵀ λⁿ⁺¹ − μ ∂P/∂xⁿ,     n = N−1, …, 1,
/// </code>
/// and the gradient is the explicit derivative plus the adjoint-weighted control derivative of the residual,
/// <code>
///   ∂Φ/∂u_k = ∂J/∂u_k + Σ_{n ∈ segment k} σ′Δt (λⁿ⁺¹)ᵀ [θ xⁿ⁺¹ + (1 − θ) xⁿ − d].
/// </code>
/// One forward and one backward sweep give all K partial derivatives — the cost is independent of K,
/// whereas finite differences need K (forward) or 2K (central) additional state solves.
/// </para>
/// </summary>
public sealed class PdeConstrainedObjective
{
    private readonly IThermalDynamics _dynamics;
    private readonly LoadProfile _load;
    private readonly CoolingModel _cooling;
    private readonly double[] _initialState;
    private readonly double[] _sourceForcing;
    private readonly double _startTime;
    private readonly int _steps;
    private readonly int _stepsPerSegment;
    private readonly double _referenceEnergy;
    private long _forwardSolves;
    private long _adjointSolves;

    public PdeConstrainedObjective(
        IThermalDynamics dynamics,
        LoadProfile load,
        CoolingModel cooling,
        ReadOnlySpan<double> initialField,
        ReadOnlySpan<double> sourceShape,
        double startTime,
        ControlProblemSettings settings)
    {
        _dynamics = dynamics;
        _load = load;
        _cooling = cooling;
        Settings = settings;
        _initialState = dynamics.Encode(initialField);
        _sourceForcing = dynamics.ProjectSource(sourceShape);
        _startTime = startTime;
        _stepsPerSegment = (int)Math.Round(settings.SegmentDuration / dynamics.TimeStep);
        if (_stepsPerSegment < 1 || Math.Abs(_stepsPerSegment * dynamics.TimeStep - settings.SegmentDuration) > 1e-9 * settings.SegmentDuration)
        {
            throw new ArgumentException("The segment duration must be a positive multiple of the model time step.", nameof(settings));
        }

        _steps = _stepsPerSegment * settings.Segments;
        _referenceEnergy = Math.Max(cooling.RatedPower * settings.Horizon, 1e-9);
    }

    public ControlProblemSettings Settings { get; }

    public IThermalDynamics Dynamics => _dynamics;

    public int Steps => _steps;

    public double ReferenceEnergy => _referenceEnergy;

    /// <summary>Number of forward state solves (full trajectories) performed so far.</summary>
    public long ForwardSolves => Interlocked.Read(ref _forwardSolves);

    /// <summary>Number of backward adjoint solves performed so far.</summary>
    public long AdjointSolves => Interlocked.Read(ref _adjointSolves);

    /// <summary>Segment index of time step n.</summary>
    public int SegmentOf(int step) => Math.Min(step / _stepsPerSegment, Settings.Segments - 1);

    /// <summary>Forward solve and objective evaluation. Keeps the trajectory when <paramref name="keepTrajectory"/> is set (needed for the adjoint).</summary>
    public ObjectiveEvaluation Evaluate(double[] controls, double mu, bool keepTrajectory = false)
    {
        if (controls.Length != Settings.Segments)
        {
            throw new ArgumentException($"Expected {Settings.Segments} controls.", nameof(controls));
        }

        Interlocked.Increment(ref _forwardSolves);
        var r = _dynamics.StateDimension;
        var nField = _dynamics.FieldDimension;
        var dt = _dynamics.TimeStep;
        var theta = _dynamics.Theta;
        var c = _dynamics.ConstantForcing;
        var d = _dynamics.CoolantForcing;
        var safe = Settings.SafeTemperature;

        var trajectory = keepTrajectory ? new StateTrajectory(_steps, r) : null;
        var current = (double[])_initialState.Clone();
        var next = new double[r];
        var field = new double[nField];
        if (trajectory is not null)
        {
            Array.Copy(current, trajectory.States[0], r);
        }

        var times = new double[_steps + 1];
        var maxT = new double[_steps + 1];
        var meanT = new double[_steps + 1];
        _dynamics.Lift(current, field);
        times[0] = _startTime;
        maxT[0] = Vector.Max(field);
        meanT[0] = Vector.Mean(field);

        var penalty = 0.0;
        var maxViolation = 0.0;
        var violating = 0;
        var energy = 0.0;
        for (var n = 0; n < _steps; n++)
        {
            var u = controls[SegmentOf(n)];
            var t = _startTime + n * dt;
            var sNow = _load.At(t);
            var sNext = _load.At(t + dt);
            var sigma = _dynamics.SinkRate(u);

            _dynamics.ApplyExplicit(current, next, u);
            for (var i = 0; i < r; i++)
            {
                var gNow = c[i] + sNow * _sourceForcing[i] + sigma * d[i];
                var gNext = c[i] + sNext * _sourceForcing[i] + sigma * d[i];
                next[i] += dt * (theta * gNext + (1 - theta) * gNow);
            }

            _dynamics.SolveImplicit(next, u);
            (current, next) = (next, current);
            if (trajectory is not null)
            {
                Array.Copy(current, trajectory.States[n + 1], r);
            }
            energy += _cooling.Power(u) * dt;

            _dynamics.Lift(current, field);
            var max = double.NegativeInfinity;
            var sum = 0.0;
            var stepPenalty = 0.0;
            for (var i = 0; i < nField; i++)
            {
                var ti = field[i];
                max = Math.Max(max, ti);
                sum += ti;
                var excess = ti - safe;
                if (excess > 0)
                {
                    stepPenalty += excess * excess;
                    violating++;
                }
            }

            penalty += stepPenalty / _dynamics.DomainCells;
            maxViolation = Math.Max(maxViolation, max - safe);
            times[n + 1] = t + dt;
            maxT[n + 1] = max;
            meanT[n + 1] = sum / nField;
        }

        penalty /= _steps;
        var (energyTerm, smoothTerm) = ControlCost(controls, energy);
        var objective = energyTerm + smoothTerm;
        var peakIndex = 1;
        for (var n = 2; n <= _steps; n++)
        {
            if (maxT[n] > maxT[peakIndex])
            {
                peakIndex = n;
            }
        }

        var forecast = new ThermalForecast(_startTime, times, maxT, meanT, field.ToArray(), maxT[peakIndex], times[peakIndex], energy);
        return new ObjectiveEvaluation((double[])controls.Clone(), mu, objective + mu * penalty, objective, energyTerm, smoothTerm, penalty,
            energy, maxT[peakIndex], Math.Max(0, maxViolation), violating, forecast, trajectory);
    }

    /// <summary>Forward + backward (adjoint) sweep: returns the evaluation with <see cref="ObjectiveEvaluation.Gradient"/> set.</summary>
    public ObjectiveEvaluation EvaluateWithGradient(double[] controls, double mu)
    {
        var evaluation = Evaluate(controls, mu, keepTrajectory: true);
        AdjointGradient(evaluation);
        return evaluation;
    }

    /// <summary>
    /// Discrete adjoint gradient for an evaluation that kept its trajectory. Cost: one backward sweep with
    /// the same symmetric operators (Lᵀ = L, Rᵀ = R) as the forward solve.
    /// </summary>
    public double[] AdjointGradient(ObjectiveEvaluation evaluation)
    {
        if (evaluation.Gradient is { } cached)
        {
            return cached;
        }

        var trajectory = evaluation.Trajectory ?? throw new InvalidOperationException("The evaluation did not keep its state trajectory.");
        Interlocked.Increment(ref _adjointSolves);
        var controls = evaluation.Controls;
        var mu = evaluation.Mu;
        var r = _dynamics.StateDimension;
        var nField = _dynamics.FieldDimension;
        var dt = _dynamics.TimeStep;
        var theta = _dynamics.Theta;
        var sigmaPrime = _dynamics.SinkSensitivity;
        var d = _dynamics.CoolantForcing;
        var safe = Settings.SafeTemperature;
        var penaltyScale = 2.0 * mu / (_steps * (double)_dynamics.DomainCells); // ∂(μP)/∂T_iⁿ = penaltyScale · max(0, T_iⁿ − T_safe)

        var gradient = ExplicitGradient(controls);
        var lambdaNext = new double[r]; // λⁿ⁺¹
        var lambda = new double[r];     // λⁿ
        var field = new double[nField];
        var fieldGrad = new double[nField];
        var stateGrad = new double[r];

        for (var n = _steps; n >= 1; n--)
        {
            // Right-hand side:  R(uⁿ)ᵀ λⁿ⁺¹ − μ ∂P/∂xⁿ   (no λᴺ⁺¹ term at the final time).
            if (n < _steps)
            {
                _dynamics.ApplyExplicit(lambdaNext, lambda, controls[SegmentOf(n)]);
            }
            else
            {
                Array.Clear(lambda);
            }

            _dynamics.Lift(trajectory.States[n], field);
            var any = false;
            for (var i = 0; i < nField; i++)
            {
                var excess = field[i] - safe;
                fieldGrad[i] = excess > 0 ? penaltyScale * excess : 0.0;
                any |= excess > 0;
            }

            if (any)
            {
                _dynamics.LiftTranspose(fieldGrad, stateGrad);
                Vector.Axpy(-1.0, stateGrad, lambda);
            }

            // L(u^{n−1})ᵀ λⁿ = rhs
            var uPrev = controls[SegmentOf(n - 1)];
            _dynamics.SolveImplicit(lambda, uPrev);

            // Control derivative of the residual of step n−1 (which maps x^{n−1} → xⁿ, weighted by λⁿ):
            //   σ′Δt (λⁿ)ᵀ [θ xⁿ + (1 − θ) x^{n−1} − d]
            var xn = trajectory.States[n];
            var xp = trajectory.States[n - 1];
            var dot = 0.0;
            for (var i = 0; i < r; i++)
            {
                dot += lambda[i] * (theta * xn[i] + (1 - theta) * xp[i] - d[i]);
            }

            gradient[SegmentOf(n - 1)] += sigmaPrime * dt * dot;
            (lambda, lambdaNext) = (lambdaNext, lambda);
        }

        evaluation.Gradient = gradient;
        return gradient;
    }

    /// <summary>
    /// Finite-difference gradient (reference / validation method). Central: (Φ(u + εe_k) − Φ(u − εe_k))/2ε,
    /// 2K solves, O(ε²). Forward: (Φ(u + εe_k) − Φ(u))/ε, K solves, O(ε). Steps are taken one-sided near the bounds.
    /// </summary>
    public double[] FiniteDifferenceGradient(double[] controls, double mu, double epsilon, bool central, double? valueAtControls = null, bool parallel = false)
    {
        var k = controls.Length;
        var g = new double[k];
        var phi0 = central ? 0.0 : valueAtControls ?? Evaluate(controls, mu).Value;

        void Component(int j)
        {
            var plus = (double[])controls.Clone();
            var minus = (double[])controls.Clone();
            var up = controls[j] + epsilon <= 1;
            var down = controls[j] - epsilon >= 0;
            if (central && up && down)
            {
                plus[j] += epsilon;
                minus[j] -= epsilon;
                g[j] = (Evaluate(plus, mu).Value - Evaluate(minus, mu).Value) / (2 * epsilon);
            }
            else if (central)
            {
                // One-sided second-order formula on the feasible side.
                var s = up ? epsilon : -epsilon;
                plus[j] += s;
                minus[j] += 2 * s;
                var f0 = Evaluate(controls, mu).Value;
                g[j] = (-3 * f0 + 4 * Evaluate(plus, mu).Value - Evaluate(minus, mu).Value) / (2 * s);
            }
            else
            {
                var s = up ? epsilon : -epsilon;
                plus[j] += s;
                g[j] = (Evaluate(plus, mu).Value - phi0) / s;
            }
        }

        if (parallel)
        {
            Parallel.For(0, k, Component);
        }
        else
        {
            for (var j = 0; j < k; j++)
            {
                Component(j);
            }
        }

        return g;
    }

    /// <summary>J(u) split into energy E/E_ref and smoothness w Σ(Δu)².</summary>
    public (double Energy, double Smoothness) ControlCost(double[] controls, double energyJoules)
    {
        var smooth = 0.0;
        for (var k = 0; k + 1 < controls.Length; k++)
        {
            smooth += (controls[k + 1] - controls[k]) * (controls[k + 1] - controls[k]);
        }

        return (energyJoules / _referenceEnergy, Settings.SmoothnessWeight * smooth);
    }

    /// <summary>∂J/∂u_k of the explicit (state-independent) terms: energy 3P_rated u_k² n_k Δt / E_ref and smoothness.</summary>
    private double[] ExplicitGradient(double[] controls)
    {
        var k = controls.Length;
        var g = new double[k];
        var segmentTime = _stepsPerSegment * _dynamics.TimeStep;
        for (var j = 0; j < k; j++)
        {
            var u = Math.Clamp(controls[j], 0, 1);
            g[j] = 3 * _cooling.RatedPower * u * u * segmentTime / _referenceEnergy;
            var w = Settings.SmoothnessWeight;
            if (j > 0)
            {
                g[j] += 2 * w * (controls[j] - controls[j - 1]);
            }

            if (j + 1 < k)
            {
                g[j] -= 2 * w * (controls[j + 1] - controls[j]);
            }
        }

        return g;
    }
}
