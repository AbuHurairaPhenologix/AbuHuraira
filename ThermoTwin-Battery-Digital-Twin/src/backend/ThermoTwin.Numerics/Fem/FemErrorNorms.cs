namespace ThermoTwin.Numerics.Fem;

/// <summary>
/// Continuous error norms of a P1 finite-element function against an exact solution, evaluated with the
/// 6-point degree-4 Dunavant quadrature on every triangle (exact for polynomials of degree ≤ 4, so the
/// quadrature error is far below the O(h²) discretisation error being measured).
/// <code>
///   ‖T − T_h‖_{L²(Ω)}  = ( Σ_T ∫_T (T − T_h)² dx )^{1/2}          expected O(h²)
///   |T − T_h|_{H¹(Ω)}  = ( Σ_T ∫_T |∇T − ∇T_h|² dx )^{1/2}       expected O(h)
/// </code>
/// </summary>
public static class FemErrorNorms
{
    // Barycentric points and weights (weights sum to 1; multiply by |T|).
    private static readonly (double A, double B, double C, double W)[] Rule =
    [
        (0.108103018168070, 0.445948490915965, 0.445948490915965, 0.223381589678011),
        (0.445948490915965, 0.108103018168070, 0.445948490915965, 0.223381589678011),
        (0.445948490915965, 0.445948490915965, 0.108103018168070, 0.223381589678011),
        (0.816847572980459, 0.091576213509771, 0.091576213509771, 0.109951743655322),
        (0.091576213509771, 0.816847572980459, 0.091576213509771, 0.109951743655322),
        (0.091576213509771, 0.091576213509771, 0.816847572980459, 0.109951743655322),
    ];

    /// <summary>∫_Ω f dx by element-wise quadrature (used to check the rule itself).</summary>
    public static double Integrate(FemMesh2D mesh, Func<double, double, double> f)
    {
        var sum = 0.0;
        foreach (var e in mesh.Elements)
        {
            foreach (var (a, b, c, w) in Rule)
            {
                var (x, y) = e.Map(a, b, c);
                sum += w * e.Area * f(x, y);
            }
        }

        return sum;
    }

    public static double L2Error(FemMesh2D mesh, ReadOnlySpan<double> nodal, Func<double, double, double> exact)
    {
        var sum = 0.0;
        foreach (var e in mesh.Elements)
        {
            var ta = nodal[e.Nodes[0]];
            var tb = nodal[e.Nodes[1]];
            var tc = nodal[e.Nodes[2]];
            foreach (var (a, b, c, w) in Rule)
            {
                var (x, y) = e.Map(a, b, c);
                var err = exact(x, y) - (a * ta + b * tb + c * tc);
                sum += w * e.Area * err * err;
            }
        }

        return Math.Sqrt(sum);
    }

    public static double H1SeminormError(FemMesh2D mesh, ReadOnlySpan<double> nodal, Func<double, double, (double Dx, double Dy)> exactGradient)
    {
        var sum = 0.0;
        foreach (var e in mesh.Elements)
        {
            var gx = 0.0;
            var gy = 0.0;
            for (var i = 0; i < 3; i++)
            {
                gx += nodal[e.Nodes[i]] * e.GradX[i];
                gy += nodal[e.Nodes[i]] * e.GradY[i];
            }

            foreach (var (a, b, c, w) in Rule)
            {
                var (x, y) = e.Map(a, b, c);
                var (dx, dy) = exactGradient(x, y);
                sum += w * e.Area * ((dx - gx) * (dx - gx) + (dy - gy) * (dy - gy));
            }
        }

        return Math.Sqrt(sum);
    }

    /// <summary>‖f‖_{L²(Ω)} of an exact function (used to report relative errors).</summary>
    public static double L2Norm(FemMesh2D mesh, Func<double, double, double> f) =>
        Math.Sqrt(Integrate(mesh, (x, y) => f(x, y) * f(x, y)));
}
