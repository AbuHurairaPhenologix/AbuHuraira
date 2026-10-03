using ThermoTwin.Numerics.LinearAlgebra;

namespace ThermoTwin.Numerics.Pde;

/// <summary>Result of a linear stability analysis of a θ-scheme for dT/dt = M T.</summary>
/// <param name="FourierNumber">r = α Δt (1/Δx² + 1/Δy²).</param>
/// <param name="SpectralRadiusBound">Gershgorin bound on ρ(M) [1/s].</param>
/// <param name="SpectralRadiusEstimate">Power-iteration estimate of ρ(M) [1/s].</param>
/// <param name="ExplicitCriticalTimeStep">Largest stable explicit step 2/ρ(M) [s].</param>
/// <param name="ClassicalExplicitLimit">Textbook bound 1 / (2α(1/Δx² + 1/Δy²)) [s].</param>
/// <param name="AmplificationFactor">max |G(Δt λ)| over the spectrum, G(z) = (1 + (1−θ)z)/(1 − θz).</param>
/// <param name="StiffModeAmplification">G at the stiffest mode — near −1 for Crank–Nicolson (slowly damped oscillation).</param>
/// <param name="IsStable">|G| ≤ 1 for every eigenmode.</param>
public sealed record StabilityReport(
    TimeScheme Scheme,
    double TimeStep,
    double FourierNumber,
    double SpectralRadiusBound,
    double SpectralRadiusEstimate,
    double ExplicitCriticalTimeStep,
    double ClassicalExplicitLimit,
    double AmplificationFactor,
    double StiffModeAmplification,
    bool IsStable);

/// <summary>
/// Von Neumann / matrix stability analysis. M is symmetric negative definite, so its eigenvalues
/// lie in [−ρ(M), 0) and the θ-scheme amplifies mode λ by G(z) = (1 + (1 − θ) z)/(1 − θ z), z = Δt λ.
/// Explicit Euler (θ = 0) therefore requires Δt ≤ 2/ρ(M); θ ≥ ½ is unconditionally stable.
/// </summary>
public static class StabilityAnalyzer
{
    public static StabilityReport Analyze(HeatEquationSolver solver, double coolingLevel = 0)
    {
        var grid = solver.Model.Grid;
        var alpha = solver.Model.Material.Diffusivity;
        var dt = solver.TimeStep;
        var theta = solver.Scheme.Theta();
        var sink = solver.Model.Cooling.VolumetricCoefficient(coolingLevel, solver.Model.Material)
                   / solver.Model.Material.VolumetricHeatCapacity;

        var fourier = alpha * dt * (1 / (grid.Dx * grid.Dx) + 1 / (grid.Dy * grid.Dy));
        var bound = alpha * solver.Laplacian.Matrix.GershgorinRadius() + sink;
        var estimate = EstimateSpectralRadius(v => solver.ApplySystemMatrix(v, coolingLevel), grid.CellCount);
        var rho = Math.Max(estimate, 1e-300);

        static double G(double z, double th) => (1 + (1 - th) * z) / (1 - th * z);

        // |G| is monotone in z between its extremes, so checking the spectrum ends suffices.
        var stiff = G(-dt * rho, theta);
        var amplification = Math.Max(Math.Abs(stiff), Math.Abs(G(0, theta)));

        return new StabilityReport(
            solver.Scheme,
            dt,
            fourier,
            bound,
            estimate,
            2 / rho,
            1 / (2 * alpha * (1 / (grid.Dx * grid.Dx) + 1 / (grid.Dy * grid.Dy))),
            amplification,
            stiff,
            amplification <= 1 + 1e-12);
    }

    /// <summary>Power iteration on the symmetric operator M for its largest-magnitude eigenvalue.</summary>
    public static double EstimateSpectralRadius(Func<double[], double[]> apply, int size, int iterations = 400)
    {
        // A checkerboard start vector has a large component along the highest-frequency mode.
        var v = new double[size];
        var rng = new Random(7);
        for (var k = 0; k < size; k++)
        {
            v[k] = (k % 2 == 0 ? 1 : -1) + 0.01 * rng.NextDouble();
        }

        var lambda = 0.0;
        for (var it = 0; it < iterations; it++)
        {
            var norm = Vector.Norm2(v);
            for (var k = 0; k < size; k++)
            {
                v[k] /= norm;
            }

            var w = apply(v);
            lambda = Math.Abs(Vector.Dot(v, w));
            v = w;
        }

        return lambda;
    }
}
