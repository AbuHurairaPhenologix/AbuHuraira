using ThermoTwin.Numerics.LinearAlgebra;

namespace ThermoTwin.Numerics.Sensitivity;

/// <summary>One accepted Levenberg–Marquardt iteration.</summary>
public sealed record EstimationIteration(int Iteration, double ResidualNorm, double Damping, double[] Parameters);

/// <summary>Result of a bounded nonlinear least-squares parameter estimation.</summary>
/// <param name="Estimate">θ̂ in the order of the estimated parameters.</param>
/// <param name="StandardErrors">√diag((JᵀJ)⁻¹ σ̂²) at θ̂ — the linearised (asymptotic) standard errors.</param>
/// <param name="NoiseEstimate">σ̂ = ‖r(θ̂)‖/√(m − p).</param>
/// <param name="AtBound">Parameters that ended on a bound (estimates there are not interior optima).</param>
public sealed record EstimationResult(
    string[] Parameters,
    double[] Truth,
    double[] Initial,
    double[] Estimate,
    double[] StandardErrors,
    double[] RelativeErrors,
    bool[] AtBound,
    double InitialResidual,
    double FinalResidual,
    double NoiseEstimate,
    int Iterations,
    int ModelEvaluations,
    IReadOnlyList<EstimationIteration> History);

/// <summary>
/// Box-constrained Levenberg–Marquardt for min_θ ½‖y(θ) − d‖², ℓ ≤ θ ≤ u, with the parameters scaled by
/// their a-priori uncertainty (ϑ = θ/δθ) for conditioning:
/// <code>
///   (JᵀJ + λ diag(JᵀJ)) Δ = −Jᵀ r,     θ⁺ = P_[ℓ,u](θ + Δ),
/// </code>
/// accepting the step (and decreasing λ) if the residual decreases, otherwise increasing λ. J is obtained by
/// central differences of the PDE model. Only a subset of the parameters may be estimated (the others are
/// held at their nominal values), which is how the identifiability study is turned into an estimation design.
/// </summary>
public static class BoundedLevenbergMarquardt
{
    public static EstimationResult Estimate(
        SensorResponseModel model,
        IReadOnlyList<ParameterSpec> specs,
        IReadOnlyCollection<ThermalParameter> estimated,
        double[] data,
        IReadOnlyDictionary<ThermalParameter, double> initial,
        IReadOnlyDictionary<ThermalParameter, double> truth,
        int maxIterations = 30,
        double tolerance = 1e-8)
    {
        var active = specs.Where(s => estimated.Contains(s.Parameter)).ToArray();
        var p = active.Length;
        var theta = specs.ToDictionary(s => s.Parameter, s => initial.TryGetValue(s.Parameter, out var v) ? v : s.Nominal);
        var evaluations = 0;

        double[] Residual(Dictionary<ThermalParameter, double> t)
        {
            Interlocked.Increment(ref evaluations);
            var y = model.Simulate(t);
            return Vector.Subtract(y, data);
        }

        var r = Residual(theta);
        var cost = Vector.Norm2(r);
        var initialCost = cost;
        var lambda = 1e-2;
        var history = new List<EstimationIteration> { new(0, cost, lambda, active.Select(a => theta[a.Parameter]).ToArray()) };
        var iterations = 0;
        DenseMatrix? jtj = null;

        for (var it = 0; it < maxIterations; it++)
        {
            // Jacobian in scaled variables ϑ_j = θ_j/δθ_j:  ∂r/∂ϑ_j = δθ_j ∂y/∂θ_j.
            var columns = new double[p][];
            var current = theta;
            Parallel.For(0, p, j =>
            {
                var col = IdentifiabilityAnalysis.CentralDifference(model, current, active[j], active[j].FiniteDifferenceStep);
                Interlocked.Add(ref evaluations, 2);
                for (var i = 0; i < col.Length; i++)
                {
                    col[i] *= active[j].Uncertainty;
                }

                columns[j] = col;
            });

            jtj = new DenseMatrix(p, p);
            var jtr = new double[p];
            for (var a = 0; a < p; a++)
            {
                jtr[a] = Vector.Dot(columns[a], r);
                for (var b = 0; b < p; b++)
                {
                    jtj[a, b] = Vector.Dot(columns[a], columns[b]);
                }
            }

            var improved = false;
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var damped = jtj.Clone();
                for (var a = 0; a < p; a++)
                {
                    damped[a, a] += lambda * Math.Max(jtj[a, a], 1e-12);
                }

                var step = damped.SolveSpd(jtr.Select(v => -v).ToArray());
                var trial = new Dictionary<ThermalParameter, double>(theta);
                for (var a = 0; a < p; a++)
                {
                    var spec = active[a];
                    trial[spec.Parameter] = Math.Clamp(theta[spec.Parameter] + step[a] * spec.Uncertainty, spec.Lower, spec.Upper);
                }

                var trialR = Residual(trial);
                var trialCost = Vector.Norm2(trialR);
                if (trialCost < cost)
                {
                    var relative = (cost - trialCost) / cost;
                    theta = trial;
                    r = trialR;
                    cost = trialCost;
                    lambda = Math.Max(lambda / 3, 1e-9);
                    improved = true;
                    iterations++;
                    history.Add(new EstimationIteration(iterations, cost, lambda, active.Select(x => theta[x.Parameter]).ToArray()));
                    if (relative < tolerance)
                    {
                        it = maxIterations;
                    }

                    break;
                }

                lambda *= 4;
            }

            if (!improved)
            {
                break;
            }
        }

        var m = data.Length;
        var sigma = cost / Math.Sqrt(Math.Max(1, m - p));
        var errors = new double[p];
        if (jtj is not null)
        {
            try
            {
                var cov = jtj.InverseSpd();
                for (var a = 0; a < p; a++)
                {
                    errors[a] = Math.Sqrt(Math.Max(cov[a, a], 0)) * sigma * active[a].Uncertainty;
                }
            }
            catch (InvalidOperationException)
            {
                Array.Fill(errors, double.PositiveInfinity);
            }
        }

        var estimate = active.Select(a => theta[a.Parameter]).ToArray();
        var trueValues = active.Select(a => truth[a.Parameter]).ToArray();
        return new EstimationResult(
            active.Select(a => a.Symbol).ToArray(),
            trueValues,
            active.Select(a => initial.TryGetValue(a.Parameter, out var v) ? v : a.Nominal).ToArray(),
            estimate,
            errors,
            estimate.Select((v, j) => (v - trueValues[j]) / Math.Abs(trueValues[j])).ToArray(),
            active.Select(a => theta[a.Parameter] <= a.Lower + 1e-12 || theta[a.Parameter] >= a.Upper - 1e-12).ToArray(),
            initialCost,
            cost,
            sigma,
            iterations,
            evaluations,
            history);
    }
}
