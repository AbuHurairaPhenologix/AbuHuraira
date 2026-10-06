using System.Diagnostics;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Physics;
using ThermoTwin.Numerics.Prediction;

namespace ThermoTwin.Numerics.Optimization;

public enum StageAlgorithm
{
    /// <summary>Projected BFGS (Bertsekas): quasi-Newton step on the free set, gradient projection on the ε-active set.</summary>
    ProjectedQuasiNewton,

    /// <summary>Projected gradient with Barzilai–Borwein step.</summary>
    ProjectedGradient,
}

public enum GradientMethod
{
    /// <summary>Discrete adjoint: one forward + one backward sweep per gradient.</summary>
    Adjoint,

    /// <summary>Finite differences: K extra forward solves (forward) or 2K (central) per gradient.</summary>
    FiniteDifference,
}

/// <summary>Algorithmic settings of the PDE-constrained optimiser.</summary>
/// <param name="PenaltySchedule">Moreau–Yosida continuation μ₁ &lt; μ₂ &lt; … (default 10², 10³, 10⁴, 10⁵).</param>
/// <param name="Tolerance">Stop a stage when ‖P(u − ∇Φ) − u‖∞ ≤ Tolerance.</param>
/// <param name="FiniteDifferenceStep">ε for <see cref="GradientMethod.FiniteDifference"/>.</param>
/// <param name="CentralDifferences">Central (2K solves) instead of forward (K solves) differences.</param>
public sealed record PdeOptimizerSettings(
    GradientMethod Gradient = GradientMethod.Adjoint,
    StageAlgorithm Algorithm = StageAlgorithm.ProjectedQuasiNewton,
    double[]? PenaltySchedule = null,
    int MaxIterationsPerStage = 40,
    double Tolerance = 1e-4,
    double FiniteDifferenceStep = 1e-4,
    bool CentralDifferences = false,
    bool ParallelFiniteDifferences = false)
{
    public double[] Penalties => PenaltySchedule ?? [1e2, 1e3, 1e4, 1e5];
}

/// <summary>One accepted projected-gradient iteration.</summary>
public sealed record PdeOptimizationIteration(
    int Iteration,
    double Mu,
    double Value,
    double Objective,
    double Penalty,
    double PeakTemperature,
    double ProjectedGradientNorm,
    double StepLength,
    long ForwardSolves,
    long AdjointSolves,
    double ElapsedMs);

/// <summary>
/// First-order (KKT) diagnostics of min Φ_μ(u) over the box U_ad = [0, 1]^K at a computed plan.
/// <para>
/// KKT conditions: ∇Φ_μ(u*) − ν_L + ν_U = 0, ν_L, ν_U ≥ 0, ν_L·u* = 0, ν_U·(1 − u*) = 0, which is equivalent to
/// the projected-gradient condition P_{U_ad}(u* − ∇Φ_μ(u*)) = u*. Hence at a stationary point
/// g_k ≥ 0 on the lower-active set, g_k ≤ 0 on the upper-active set and g_k = 0 on the inactive set.
/// </para>
/// <para>
/// State constraint: the Moreau–Yosida penalty gives the multiplier estimate η_iⁿ = 2μ·max(0, T_iⁿ − T_safe)/(N n),
/// non-negative and supported on the (approximate) active set {T ≥ T_safe} — complementarity holds up to O(1/μ).
/// </para>
/// </summary>
public sealed record KktDiagnostics(
    double Mu,
    double GradientNorm,
    double ProjectedGradientNorm,
    int ActiveLower,
    int ActiveUpper,
    int Inactive,
    double MaxInactiveGradient,
    double MinLowerMultiplier,
    double MinUpperMultiplier,
    double MaxStateViolation,
    int StateActivePoints,
    double StateMultiplierSum,
    double[] Gradient,
    double[] Controls);

/// <summary>
/// Solver for the PDE-constrained cooling problem of <see cref="PdeConstrainedObjective"/>: Moreau–Yosida
/// penalty continuation μ₁ &lt; μ₂ &lt; …, and for every μ a bound-constrained minimisation of Φ_μ over U_ad = [0, 1]^K.
/// <para>
/// <b>Stage solver (default): projected quasi-Newton</b> (Bertsekas 1982). With the ε-active set
/// A = {k : u_k ≤ ε, g_k &gt; 0} ∪ {k : u_k ≥ 1 − ε, g_k &lt; 0} and a BFGS inverse-Hessian approximation H,
/// <code>
///   d_F = −H_FF g_F  (free variables),     d_A = −H_AA g_A  (active variables),     u(α) = P_{U_ad}(u + α d),
/// </code>
/// with Armijo backtracking Φ(u(α)) ≤ Φ(u) + 10⁻⁴ ∇Φᵀ(u(α) − u). H is updated by BFGS when sᵀy &gt; 0 and reset
/// when μ changes. <b>Alternative:</b> projected gradient u(α) = P(u − α∇Φ) with a Barzilai–Borwein step.
/// </para>
/// <para>
/// The gradient comes from the discrete adjoint (default) or from finite differences (validation baseline).
/// A final uniform-shift bisection repairs any residual O(1/μ) violation of the pointwise state constraint.
/// </para>
/// </summary>
public sealed class AdjointCoolingOptimizer : ICoolingOptimizer
{
    private readonly IThermalDynamics _dynamics;
    private readonly LoadProfile _load;
    private readonly CoolingModel _cooling;

    public AdjointCoolingOptimizer(IThermalDynamics dynamics, LoadProfile load, CoolingModel cooling, PdeOptimizerSettings? settings = null)
    {
        _dynamics = dynamics;
        _load = load;
        _cooling = cooling;
        Settings = settings ?? new PdeOptimizerSettings();
    }

    public PdeOptimizerSettings Settings { get; }

    public IThermalDynamics Dynamics => _dynamics;

    public string Name => $"PDE-constrained projected gradient ({Settings.Gradient} gradient, {_dynamics.Name})";

    /// <summary>Iteration log of the most recent <see cref="Optimize"/> call.</summary>
    public IReadOnlyList<PdeOptimizationIteration> LastHistory { get; private set; } = [];

    /// <summary>Builds the objective for a horizon (exposed for diagnostics and experiments).</summary>
    public PdeConstrainedObjective CreateObjective(ReadOnlySpan<double> initialState, ReadOnlySpan<double> sourceShape, double startTime,
        CoolingOptimizationOptions options) =>
        new(_dynamics, _load, _cooling, initialState, sourceShape, startTime,
            new ControlProblemSettings(options.SafeTemperature, options.Segments, options.SegmentDuration, options.SmoothnessWeight));

    public CoolingOptimizationResult Optimize(
        ReadOnlySpan<double> initialState,
        ReadOnlySpan<double> sourceShape,
        double startTime,
        CoolingOptimizationOptions options,
        double[]? initialGuess = null)
    {
        var objective = CreateObjective(initialState, sourceShape, startTime, options);
        var sw = Stopwatch.StartNew();
        var history = new List<PdeOptimizationIteration>();
        var levels = initialGuess is { Length: > 0 } guess && guess.Length == options.Segments
            ? guess.Select(v => Math.Clamp(v, 0, 1)).ToArray()
            : Enumerable.Repeat(0.5, options.Segments).ToArray();

        ObjectiveEvaluation? last = null;
        var iteration = 0;
        foreach (var mu in Settings.Penalties)
        {
            var current = objective.Evaluate(levels, mu, keepTrajectory: Settings.Gradient == GradientMethod.Adjoint);
            var gradient = Gradient(objective, current);
            double[]? previousLevels = null;
            double[]? previousGradient = null;
            var k = levels.Length;
            DenseMatrix? inverseHessian = null;

            for (var it = 0; it < Settings.MaxIterationsPerStage; it++)
            {
                var projected = ProjectedGradientNorm(levels, gradient);
                if (projected <= Settings.Tolerance)
                {
                    break;
                }

                var gMax = Math.Max(Vector.NormInf(gradient), 1e-300);
                var direction = new double[k];
                double step;
                if (Settings.Algorithm == StageAlgorithm.ProjectedQuasiNewton)
                {
                    inverseHessian ??= ScaledIdentity(k, 0.5 / gMax);
                    var epsilon = Math.Min(1e-3, projected);
                    var active = new bool[k];
                    for (var j = 0; j < k; j++)
                    {
                        active[j] = (levels[j] <= epsilon && gradient[j] > 0) || (levels[j] >= 1 - epsilon && gradient[j] < 0);
                    }

                    for (var i = 0; i < k; i++)
                    {
                        if (active[i])
                        {
                            direction[i] = -inverseHessian[i, i] * gradient[i];
                            continue;
                        }

                        var sum = 0.0;
                        for (var j = 0; j < k; j++)
                        {
                            if (!active[j])
                            {
                                sum += inverseHessian[i, j] * gradient[j];
                            }
                        }

                        direction[i] = -sum;
                    }

                    step = 1.0;
                }
                else
                {
                    for (var j = 0; j < k; j++)
                    {
                        direction[j] = -gradient[j];
                    }

                    step = 0.5 / gMax;
                    if (previousLevels is not null && previousGradient is not null)
                    {
                        double ss = 0, sy = 0;
                        for (var j = 0; j < k; j++)
                        {
                            var sj = levels[j] - previousLevels[j];
                            var yj = gradient[j] - previousGradient[j];
                            ss += sj * sj;
                            sy += sj * yj;
                        }

                        if (sy > 1e-300)
                        {
                            step = Math.Clamp(ss / sy, 1e-6 / gMax, 1.0 / gMax);
                        }
                    }
                }

                var accepted = false;
                var candidate = levels;
                var trial = current;
                for (var ls = 0; ls < 30; ls++)
                {
                    var a = step;
                    candidate = levels.Select((v, j) => Math.Clamp(v + a * direction[j], 0, 1)).ToArray();
                    var predicted = 0.0;
                    for (var j = 0; j < k; j++)
                    {
                        predicted += gradient[j] * (candidate[j] - levels[j]);
                    }

                    if (predicted >= 0)
                    {
                        step *= 0.5;
                        continue;
                    }

                    trial = objective.Evaluate(candidate, mu, keepTrajectory: Settings.Gradient == GradientMethod.Adjoint);
                    if (trial.Value <= current.Value + 1e-4 * predicted)
                    {
                        accepted = true;
                        break;
                    }

                    step *= 0.5;
                }

                if (!accepted)
                {
                    if (Settings.Algorithm == StageAlgorithm.ProjectedQuasiNewton && previousLevels is not null)
                    {
                        // The quasi-Newton model failed: restart from a scaled steepest-descent metric before giving up.
                        inverseHessian = ScaledIdentity(k, 0.5 / gMax);
                        previousLevels = null;
                        continue;
                    }

                    break;
                }

                var change = levels.Zip(candidate, (x, y) => Math.Abs(x - y)).Max();
                var newGradient = Gradient(objective, trial);
                if (inverseHessian is not null)
                {
                    BfgsUpdate(inverseHessian, levels, candidate, gradient, newGradient);
                }

                previousLevels = levels;
                previousGradient = gradient;
                levels = candidate;
                current = trial;
                gradient = newGradient;
                history.Add(new PdeOptimizationIteration(++iteration, mu, current.Value, current.Objective, current.Penalty, current.PeakTemperature,
                    ProjectedGradientNorm(levels, gradient), step, objective.ForwardSolves, objective.AdjointSolves, sw.Elapsed.TotalMilliseconds));
                if (change < 1e-9)
                {
                    break;
                }
            }

            last = current;
            last.Gradient ??= gradient;
        }

        // Residual O(1/μ) violation of the pointwise constraint: raise all levels uniformly (bisection).
        var final = objective.Evaluate(levels, 0);
        if (final.MaxViolation > options.FeasibilityTolerance)
        {
            levels = Repair(objective, levels, options.FeasibilityTolerance);
            final = objective.Evaluate(levels, 0);
        }

        var forwardSolves = (int)objective.ForwardSolves;
        var adjointSolves = (int)objective.AdjointSolves;
        LastHistory = history;
        var kkt = Diagnose(objective, levels, Settings.Penalties[^1]);

        return new CoolingOptimizationResult(
            new CoolingPlan(options.SegmentDuration, levels),
            final.Objective,
            final.EnergyJoules,
            final.PeakTemperature,
            final.MaxViolation,
            final.MaxViolation <= options.FeasibilityTolerance,
            forwardSolves,
            history.Select(h => new OptimizationIteration(h.Iteration, h.Mu, h.Value, h.Objective, Math.Sqrt(h.Penalty), h.PeakTemperature)).ToArray(),
            final.Forecast,
            adjointSolves,
            kkt,
            _dynamics.Name);
    }

    /// <summary>KKT diagnostics at <paramref name="controls"/> for penalty μ (uses one adjoint gradient).</summary>
    public static KktDiagnostics Diagnose(PdeConstrainedObjective objective, double[] controls, double mu, double activeTolerance = 1e-8)
    {
        var evaluation = objective.EvaluateWithGradient(controls, mu);
        var g = evaluation.Gradient!;
        int lower = 0, upper = 0, inactive = 0;
        double maxInactive = 0, minLower = double.PositiveInfinity, minUpper = double.PositiveInfinity;
        for (var k = 0; k < controls.Length; k++)
        {
            if (controls[k] <= activeTolerance)
            {
                lower++;
                minLower = Math.Min(minLower, g[k]);
            }
            else if (controls[k] >= 1 - activeTolerance)
            {
                upper++;
                minUpper = Math.Min(minUpper, -g[k]);
            }
            else
            {
                inactive++;
                maxInactive = Math.Max(maxInactive, Math.Abs(g[k]));
            }
        }

        var n = objective.Dynamics.DomainCells;
        var etaScale = 2 * mu / (objective.Steps * (double)n);
        var multiplierSum = evaluation.ViolatingPoints == 0 ? 0 : etaScale * MultiplierMass(objective, evaluation);
        return new KktDiagnostics(
            mu,
            Vector.NormInf(g),
            ProjectedGradientNorm(controls, g),
            lower,
            upper,
            inactive,
            maxInactive,
            double.IsPositiveInfinity(minLower) ? 0 : minLower,
            double.IsPositiveInfinity(minUpper) ? 0 : minUpper,
            evaluation.MaxViolation,
            evaluation.ViolatingPoints,
            multiplierSum,
            g.Select(v => Math.Round(v, 8)).ToArray(),
            controls.Select(v => Math.Round(v, 6)).ToArray());
    }

    private static DenseMatrix ScaledIdentity(int size, double scale)
    {
        var m = new DenseMatrix(size, size);
        for (var i = 0; i < size; i++)
        {
            m[i, i] = scale;
        }

        return m;
    }

    /// <summary>BFGS update of the inverse Hessian: H ← (I − ρsyᵀ) H (I − ρysᵀ) + ρssᵀ, ρ = 1/yᵀs (skipped unless yᵀs &gt; 0).</summary>
    private static void BfgsUpdate(DenseMatrix h, double[] u0, double[] u1, double[] g0, double[] g1)
    {
        var n = u0.Length;
        var s = new double[n];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            s[i] = u1[i] - u0[i];
            y[i] = g1[i] - g0[i];
        }

        var sy = Vector.Dot(s, y);
        if (sy <= 1e-12 * Vector.Norm2(s) * Vector.Norm2(y))
        {
            return;
        }

        var rho = 1 / sy;
        var hy = h.Multiply(y);
        var yhy = Vector.Dot(y, hy);
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                h[i, j] += -rho * (hy[i] * s[j] + s[i] * hy[j]) + (rho * rho * yhy + rho) * s[i] * s[j];
            }
        }
    }

    /// <summary>‖P_{[0,1]}(u − g) − u‖∞ — zero exactly at a KKT point of the box-constrained problem.</summary>
    public static double ProjectedGradientNorm(double[] u, double[] g)
    {
        var max = 0.0;
        for (var k = 0; k < u.Length; k++)
        {
            max = Math.Max(max, Math.Abs(Math.Clamp(u[k] - g[k], 0, 1) - u[k]));
        }

        return max;
    }

    private double[] Gradient(PdeConstrainedObjective objective, ObjectiveEvaluation evaluation) =>
        Settings.Gradient == GradientMethod.Adjoint
            ? objective.AdjointGradient(evaluation)
            : evaluation.Gradient = objective.FiniteDifferenceGradient(evaluation.Controls, evaluation.Mu, Settings.FiniteDifferenceStep,
                Settings.CentralDifferences, evaluation.Value, Settings.ParallelFiniteDifferences);

    private static double MultiplierMass(PdeConstrainedObjective objective, ObjectiveEvaluation evaluation)
    {
        var trajectory = evaluation.Trajectory!;
        var field = new double[objective.Dynamics.FieldDimension];
        var safe = objective.Settings.SafeTemperature;
        var sum = 0.0;
        for (var n = 1; n < trajectory.States.Length; n++)
        {
            objective.Dynamics.Lift(trajectory.States[n], field);
            foreach (var t in field)
            {
                sum += Math.Max(0, t - safe);
            }
        }

        return sum;
    }

    private static double[] Repair(PdeConstrainedObjective objective, double[] levels, double tolerance)
    {
        double[] Shift(double delta) => levels.Select(v => Math.Clamp(v + delta, 0, 1)).ToArray();
        bool Feasible(double delta) => objective.Evaluate(Shift(delta), 0).MaxViolation <= tolerance;

        if (!Feasible(1))
        {
            return Shift(1);
        }

        double lo = 0, hi = 1;
        for (var it = 0; it < 25; it++)
        {
            var mid = 0.5 * (lo + hi);
            if (Feasible(mid))
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return Shift(hi);
    }
}
