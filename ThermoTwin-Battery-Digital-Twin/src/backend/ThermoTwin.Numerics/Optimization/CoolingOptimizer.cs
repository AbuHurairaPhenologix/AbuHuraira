using ThermoTwin.Numerics.Prediction;

namespace ThermoTwin.Numerics.Optimization;

public sealed record CoolingOptimizationOptions(
    double SafeTemperature = 45.0,
    int Segments = 6,
    double SegmentDuration = 100.0,
    double SmoothnessWeight = 0.02,
    double[]? PenaltySchedule = null,
    int MaxIterationsPerStage = 30,
    double FiniteDifferenceStep = 1e-3,
    double FeasibilityTolerance = 0.02)
{
    public double[] Penalties => PenaltySchedule ?? [10, 300, 1e4];
}

public sealed record OptimizationIteration(int Iteration, double Penalty, double Objective, double EnergyTerm, double Violation, double PeakTemperature);

/// <summary>Outcome of a constrained cooling optimisation.</summary>
/// <param name="Plan">Optimal u_k.</param>
/// <param name="Objective">J(u*) = E(u*)/E_ref + w Σ(Δu)² (penalty term excluded).</param>
/// <param name="CoolingEnergy">E(u*) = Σ P(u_k) Δt [J].</param>
/// <param name="PeakTemperature">max_t max_x T over the horizon [°C].</param>
/// <param name="MaxViolation">max(0, peak − T_safe) [K].</param>
/// <param name="Evaluations">Forward PDE (or ROM) solves performed.</param>
/// <param name="AdjointSolves">Backward adjoint solves (0 for finite-difference optimisers).</param>
/// <param name="Kkt">First-order optimality diagnostics at the returned plan (PDE-constrained optimiser only).</param>
/// <param name="Model">Name of the dynamics the plan was optimised on.</param>
public sealed record CoolingOptimizationResult(
    CoolingPlan Plan,
    double Objective,
    double CoolingEnergy,
    double PeakTemperature,
    double MaxViolation,
    bool Feasible,
    int Evaluations,
    IReadOnlyList<OptimizationIteration> History,
    ThermalForecast Forecast,
    int AdjointSolves = 0,
    KktDiagnostics? Kkt = null,
    string Model = "Full-order FVM");

/// <summary>Strategy interface so the digital twin can switch between optimisers.</summary>
public interface ICoolingOptimizer
{
    string Name { get; }

    CoolingOptimizationResult Optimize(
        ReadOnlySpan<double> initialState,
        ReadOnlySpan<double> sourceShape,
        double startTime,
        CoolingOptimizationOptions options,
        double[]? initialGuess = null);
}

/// <summary>
/// Constrained cooling optimisation
/// <code>
///   min_u   J(u) = Σ_k P(u_k)Δt / E_ref + w Σ_k (u_{k+1} − u_k)²
///   s.t.    max_x T(x, t; u) ≤ T_safe     for all t in the horizon
///           0 ≤ u_k ≤ 1
/// </code>
/// solved by a quadratic exterior-penalty method
/// <code>
///   Φ_μ(u) = J(u) + μ · (1/N) Σ_n max(0, T_max(t_n; u) − T_safe)²
/// </code>
/// with an increasing penalty schedule μ₁ &lt; μ₂ &lt; …. Each sub-problem is minimised by projected
/// gradient descent with Armijo backtracking; ∇Φ is obtained by finite differences, each requiring
/// one forward PDE solve (evaluated in parallel). The box constraint is enforced by projection
/// P(u) = clamp(u, 0, 1). A final feasibility repair raises all levels uniformly (bisection) if the
/// penalty solution still violates T_safe by more than the tolerance.
/// </summary>
public sealed class CoolingOptimizer : ICoolingOptimizer
{
    private readonly ThermalPredictor _predictor;

    public CoolingOptimizer(ThermalPredictor predictor) => _predictor = predictor;

    public string Name => "Penalty + finite-difference projected gradient (max-temperature penalty)";

    public CoolingOptimizationResult Optimize(
        ReadOnlySpan<double> initialState,
        ReadOnlySpan<double> sourceShape,
        double startTime,
        CoolingOptimizationOptions options,
        double[]? initialGuess = null)
    {
        var state = initialState.ToArray();
        var shape = sourceShape.ToArray();
        var cooling = _predictor.Solver.Model.Cooling;
        var horizon = options.Segments * options.SegmentDuration;
        var referenceEnergy = Math.Max(cooling.RatedPower * horizon, 1e-9);
        var evaluations = 0;
        var history = new List<OptimizationIteration>();

        (double Phi, double J, double Energy, double Violation, ThermalForecast Forecast) Evaluate(double[] u, double mu)
        {
            Interlocked.Increment(ref evaluations);
            var forecast = _predictor.Predict(state, shape, startTime, new CoolingPlan(options.SegmentDuration, u));
            var violation = 0.0;
            for (var n = 1; n < forecast.MaxTemperature.Length; n++)
            {
                var excess = Math.Max(0, forecast.MaxTemperature[n] - options.SafeTemperature);
                violation += excess * excess;
            }

            violation /= forecast.MaxTemperature.Length - 1;
            var smooth = 0.0;
            for (var k = 0; k + 1 < u.Length; k++)
            {
                smooth += (u[k + 1] - u[k]) * (u[k + 1] - u[k]);
            }

            var energyTerm = forecast.CoolingEnergy / referenceEnergy;
            var j = energyTerm + options.SmoothnessWeight * smooth;
            return (j + mu * violation, j, forecast.CoolingEnergy, violation, forecast);
        }

        var levels = initialGuess is { Length: > 0 } guess && guess.Length == options.Segments
            ? guess.Select(v => Math.Clamp(v, 0, 1)).ToArray()
            : Enumerable.Repeat(0.5, options.Segments).ToArray();

        var iteration = 0;
        foreach (var mu in options.Penalties)
        {
            var current = Evaluate(levels, mu);
            for (var it = 0; it < options.MaxIterationsPerStage; it++)
            {
                var gradient = Gradient(levels, mu, current.Phi);
                var gMax = gradient.Max(Math.Abs);
                if (gMax < 1e-10)
                {
                    break;
                }

                // Projected gradient step with Armijo backtracking.
                var step = 0.5 / gMax;
                var accepted = false;
                double[] candidate = levels;
                (double Phi, double J, double Energy, double Violation, ThermalForecast Forecast) trial = current;
                for (var ls = 0; ls < 12; ls++)
                {
                    candidate = levels.Select((v, k) => Math.Clamp(v - step * gradient[k], 0, 1)).ToArray();
                    trial = Evaluate(candidate, mu);
                    var decrease = 0.0;
                    for (var k = 0; k < levels.Length; k++)
                    {
                        decrease += gradient[k] * (levels[k] - candidate[k]);
                    }

                    if (trial.Phi <= current.Phi - 1e-4 * decrease)
                    {
                        accepted = true;
                        break;
                    }

                    step *= 0.5;
                }

                if (!accepted)
                {
                    break;
                }

                var change = levels.Zip(candidate, (a, b) => Math.Abs(a - b)).Max();
                levels = candidate;
                current = trial;
                history.Add(new OptimizationIteration(++iteration, mu, current.Phi, current.J,
                    Math.Sqrt(current.Violation), current.Forecast.PeakTemperature));
                if (change < 1e-4)
                {
                    break;
                }
            }
        }

        var final = Evaluate(levels, 0);
        if (final.Forecast.PeakTemperature - options.SafeTemperature > options.FeasibilityTolerance)
        {
            levels = RepairFeasibility(levels, options, state, shape, startTime);
            final = Evaluate(levels, 0);
        }

        var peak = MaxAfterStart(final.Forecast);
        var maxViolation = Math.Max(0, peak - options.SafeTemperature);
        return new CoolingOptimizationResult(
            new CoolingPlan(options.SegmentDuration, levels),
            final.J,
            final.Energy,
            peak,
            maxViolation,
            maxViolation <= options.FeasibilityTolerance,
            evaluations,
            history,
            final.Forecast);

        double[] Gradient(double[] u, double mu, double phi0)
        {
            var h = options.FiniteDifferenceStep;
            var g = new double[u.Length];
            Parallel.For(0, u.Length, k =>
            {
                var shifted = (double[])u.Clone();
                var forward = u[k] + h <= 1;
                shifted[k] = forward ? u[k] + h : u[k] - h;
                var phi = Evaluate(shifted, mu).Phi;
                g[k] = forward ? (phi - phi0) / h : (phi0 - phi) / h;
            });
            return g;
        }
    }

    /// <summary>Evaluates a fixed plan (used to compare strategies on equal terms).</summary>
    public (ThermalForecast Forecast, double Objective, double PeakTemperature, double Violation) EvaluatePlan(
        ReadOnlySpan<double> initialState, ReadOnlySpan<double> sourceShape, double startTime, CoolingPlan plan,
        CoolingOptimizationOptions options)
    {
        var forecast = _predictor.Predict(initialState, sourceShape, startTime, plan);
        var referenceEnergy = _predictor.Solver.Model.Cooling.RatedPower * plan.Horizon;
        var smooth = 0.0;
        for (var k = 0; k + 1 < plan.Levels.Length; k++)
        {
            smooth += Math.Pow(plan.Levels[k + 1] - plan.Levels[k], 2);
        }

        var peak = MaxAfterStart(forecast);
        return (forecast, forecast.CoolingEnergy / referenceEnergy + options.SmoothnessWeight * smooth, peak,
            Math.Max(0, peak - options.SafeTemperature));
    }

    /// <summary>Smallest constant level that keeps the forecast below T_safe (bisection), or 1 if none does.</summary>
    public double MinimumFeasibleConstantLevel(ReadOnlySpan<double> initialState, ReadOnlySpan<double> sourceShape,
        double startTime, CoolingOptimizationOptions options)
    {
        var state = initialState.ToArray();
        var shape = sourceShape.ToArray();
        bool Feasible(double u) =>
            MaxAfterStart(_predictor.Predict(state, shape, startTime,
                CoolingPlan.Constant(u, options.Segments, options.SegmentDuration))) <= options.SafeTemperature;

        if (Feasible(0))
        {
            return 0;
        }

        if (!Feasible(1))
        {
            return 1;
        }

        double lo = 0, hi = 1;
        for (var it = 0; it < 30; it++)
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

        return hi;
    }

    private double[] RepairFeasibility(double[] levels, CoolingOptimizationOptions options, double[] state, double[] shape, double startTime)
    {
        double[] Shift(double delta) => levels.Select(v => Math.Clamp(v + delta, 0, 1)).ToArray();
        bool Feasible(double delta) =>
            MaxAfterStart(_predictor.Predict(state, shape, startTime, new CoolingPlan(options.SegmentDuration, Shift(delta))))
            <= options.SafeTemperature + options.FeasibilityTolerance;

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

    private static double MaxAfterStart(ThermalForecast forecast)
    {
        var max = double.NegativeInfinity;
        for (var n = 1; n < forecast.MaxTemperature.Length; n++)
        {
            max = Math.Max(max, forecast.MaxTemperature[n]);
        }

        return max;
    }
}
