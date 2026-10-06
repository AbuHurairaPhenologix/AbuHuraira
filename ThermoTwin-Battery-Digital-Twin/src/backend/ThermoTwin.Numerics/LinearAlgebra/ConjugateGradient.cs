namespace ThermoTwin.Numerics.LinearAlgebra;

/// <summary>
/// Unpreconditioned conjugate gradient for SPD systems (c₀ I + c₁ A) x = b.
/// The production solvers use the direct banded Cholesky factorisation; CG is kept as an
/// independent iterative cross-check of the implicit systems in the test-suite.
/// </summary>
public static class ConjugateGradient
{
    public static (double[] Solution, int Iterations, double Residual) Solve(
        Func<double[], double[]> applyOperator,
        ReadOnlySpan<double> b,
        double tolerance = 1e-12,
        int maxIterations = 10_000)
    {
        var n = b.Length;
        var x = new double[n];
        var r = b.ToArray();
        var p = (double[])r.Clone();
        var rr = Vector.Dot(r, r);
        var bNorm = Math.Max(Vector.Norm2(b), double.Epsilon);

        var iteration = 0;
        while (iteration < maxIterations && Math.Sqrt(rr) / bNorm > tolerance)
        {
            var ap = applyOperator(p);
            var alpha = rr / Vector.Dot(p, ap);
            Vector.Axpy(alpha, p, x);
            Vector.Axpy(-alpha, ap, r);
            var rrNew = Vector.Dot(r, r);
            var beta = rrNew / rr;
            for (var i = 0; i < n; i++)
            {
                p[i] = r[i] + beta * p[i];
            }

            rr = rrNew;
            iteration++;
        }

        return (x, iteration, Math.Sqrt(rr) / bNorm);
    }
}
