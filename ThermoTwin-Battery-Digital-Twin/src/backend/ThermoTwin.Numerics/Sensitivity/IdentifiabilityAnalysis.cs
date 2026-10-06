using ThermoTwin.Numerics.LinearAlgebra;

namespace ThermoTwin.Numerics.Sensitivity;

/// <summary>Collinearity index of a parameter subset (Brun, Reichert &amp; Künsch 2001).</summary>
/// <param name="Index">γ_K = 1/√λ_min(S̃_Kᵀ S̃_K) with unit-norm columns; γ &gt; 10–15 indicates practical non-identifiability.</param>
public sealed record CollinearityIndex(string[] Parameters, double Index);

/// <summary>Local sensitivity and practical identifiability of the parameter vector at its nominal value.</summary>
/// <param name="ColumnNorms">‖S̃_j‖/√m — RMS change of the sensor data caused by a one-δ change of θ_j [K].</param>
/// <param name="SignalToNoise">ColumnNorms / σ_noise.</param>
/// <param name="Correlation">cos∠(S_j, S_k) — collinearity of the sensitivity directions (|·| → 1: confounded).</param>
/// <param name="SingularValues">Singular values of the column-normalised sensitivity matrix.</param>
/// <param name="ConditionNumber">σ_max/σ_min of the column-normalised matrix.</param>
/// <param name="CramerRaoStd">√diag(F⁻¹), F = SᵀS/σ² — the smallest achievable standard deviation of an unbiased estimate (units of θ).</param>
/// <param name="ParameterCorrelation">Correlation matrix of the estimates implied by F⁻¹.</param>
public sealed record SensitivityReport(
    string[] Parameters,
    string[] Units,
    double[] Nominal,
    double[] Uncertainty,
    int ObservationCount,
    double NoiseStd,
    double[] ColumnNorms,
    double[] SignalToNoise,
    double[][] Correlation,
    double[] SingularValues,
    double ConditionNumber,
    double[] CramerRaoStd,
    double[] CramerRaoRelative,
    double[][] ParameterCorrelation,
    IReadOnlyList<CollinearityIndex> Collinearity,
    double[][] ScaledSensitivity,
    double FiniteDifferenceConsistency);

/// <summary>
/// Local sensitivity analysis of the parameter-to-observation map y(θ):
/// <code>
///   S = ∂y/∂θ |_{θ₀}  (m × p, central differences),       S̃ = S·diag(δθ)   (scaled by prior uncertainty),
///   F = SᵀS / σ²       (Fisher information for y = y(θ) + ε, ε ~ N(0, σ²I)),     Cov(θ̂) ⪰ F⁻¹  (Cramér–Rao).
/// </code>
/// Practical identifiability requires (i) sensitivities well above the noise level and (ii) linearly
/// independent sensitivity directions: nearly collinear columns mean that a change of one parameter can be
/// compensated by another, so the data cannot tell them apart (large condition number, large collinearity
/// index, large CRB). The analysis is <i>local</i> (linearisation at θ₀) and assumes the model is exact.
/// </summary>
public static class IdentifiabilityAnalysis
{
    public static SensitivityReport Analyze(SensorResponseModel model, IReadOnlyList<ParameterSpec> specs, double noiseStd)
    {
        var p = specs.Count;
        var nominal = specs.ToDictionary(s => s.Parameter, s => s.Nominal);
        var columns = new double[p][];
        double consistency = 0;
        Parallel.For(0, p, j =>
        {
            columns[j] = CentralDifference(model, nominal, specs[j], specs[j].FiniteDifferenceStep);
        });

        // FD consistency: compare with a column computed at half the step (relative difference of norms of S_j).
        var halfStep = CentralDifference(model, nominal, specs[0], specs[0].FiniteDifferenceStep / 2);
        consistency = Vector.Norm2(Vector.Subtract(halfStep, columns[0])) / Math.Max(Vector.Norm2(columns[0]), 1e-300);

        var m = model.ObservationCount;
        var s = new DenseMatrix(m, p);
        var scaled = new DenseMatrix(m, p);
        for (var j = 0; j < p; j++)
        {
            for (var i = 0; i < m; i++)
            {
                s[i, j] = columns[j][i];
                scaled[i, j] = columns[j][i] * specs[j].Uncertainty;
            }
        }

        var norms = Enumerable.Range(0, p).Select(j => Vector.Norm2(scaled.Column(j))).ToArray();
        var rms = norms.Select(n => n / Math.Sqrt(m)).ToArray();
        var correlation = new double[p][];
        for (var a = 0; a < p; a++)
        {
            correlation[a] = new double[p];
            for (var b = 0; b < p; b++)
            {
                correlation[a][b] = Vector.Dot(scaled.Column(a), scaled.Column(b)) / (norms[a] * norms[b]);
            }
        }

        // Column-normalised Gram matrix = correlation matrix → singular values of the normalised S.
        var corrMatrix = new DenseMatrix(p, p);
        for (var a = 0; a < p; a++)
        {
            for (var b = 0; b < p; b++)
            {
                corrMatrix[a, b] = correlation[a][b];
            }
        }

        var eigen = SymmetricEigensolver.DecomposeJacobi(corrMatrix);
        var singular = eigen.Values.Select(l => Math.Sqrt(Math.Max(l, 0))).ToArray();

        // Fisher information and Cramér–Rao bound (in physical units).
        var fisher = s.GramMatrix();
        for (var a = 0; a < p; a++)
        {
            for (var b = 0; b < p; b++)
            {
                fisher[a, b] /= noiseStd * noiseStd;
            }
        }

        var covariance = fisher.InverseSpd();
        var crb = Enumerable.Range(0, p).Select(j => Math.Sqrt(covariance[j, j])).ToArray();
        var paramCorrelation = Enumerable.Range(0, p)
            .Select(a => Enumerable.Range(0, p).Select(b => covariance[a, b] / (crb[a] * crb[b])).ToArray())
            .ToArray();

        var collinearity = new List<CollinearityIndex>();
        for (var mask = 1; mask < 1 << p; mask++)
        {
            var subset = Enumerable.Range(0, p).Where(j => (mask & (1 << j)) != 0).ToArray();
            if (subset.Length < 2)
            {
                continue;
            }

            var sub = new DenseMatrix(subset.Length, subset.Length);
            for (var a = 0; a < subset.Length; a++)
            {
                for (var b = 0; b < subset.Length; b++)
                {
                    sub[a, b] = correlation[subset[a]][subset[b]];
                }
            }

            var lambdaMin = SymmetricEigensolver.DecomposeJacobi(sub).Values[^1];
            collinearity.Add(new CollinearityIndex(subset.Select(j => specs[j].Symbol).ToArray(), 1 / Math.Sqrt(Math.Max(lambdaMin, 1e-300))));
        }

        return new SensitivityReport(
            specs.Select(x => x.Symbol).ToArray(),
            specs.Select(x => x.Unit).ToArray(),
            specs.Select(x => x.Nominal).ToArray(),
            specs.Select(x => x.Uncertainty).ToArray(),
            m,
            noiseStd,
            rms,
            rms.Select(v => v / noiseStd).ToArray(),
            correlation,
            singular,
            singular[0] / Math.Max(singular[^1], 1e-300),
            crb,
            crb.Select((v, j) => v / Math.Abs(specs[j].Nominal)).ToArray(),
            paramCorrelation,
            collinearity.OrderByDescending(c => c.Index).ToArray(),
            scaled.ToRows(),
            consistency);
    }

    /// <summary>Central-difference column ∂y/∂θ_j ≈ (y(θ + h e_j) − y(θ − h e_j)) / 2h.</summary>
    public static double[] CentralDifference(SensorResponseModel model, IReadOnlyDictionary<ThermalParameter, double> theta, ParameterSpec spec, double step)
    {
        var plus = new Dictionary<ThermalParameter, double>(theta) { [spec.Parameter] = theta[spec.Parameter] + step };
        var minus = new Dictionary<ThermalParameter, double>(theta) { [spec.Parameter] = theta[spec.Parameter] - step };
        var yp = model.Simulate(plus);
        var ym = model.Simulate(minus);
        var d = new double[yp.Length];
        for (var i = 0; i < d.Length; i++)
        {
            d[i] = (yp[i] - ym[i]) / (2 * step);
        }

        return d;
    }
}
