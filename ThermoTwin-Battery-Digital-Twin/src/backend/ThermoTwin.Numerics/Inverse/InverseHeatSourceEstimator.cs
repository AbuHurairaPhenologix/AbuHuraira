using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Physics;
using ThermoTwin.Numerics.Sensors;

namespace ThermoTwin.Numerics.Inverse;

public enum LambdaSelection
{
    /// <summary>Use the supplied λ unchanged.</summary>
    Fixed,

    /// <summary>Corner of the L-curve (maximum curvature of log‖Aq−d‖ vs log‖Lq‖).</summary>
    LCurve,

    /// <summary>Morozov discrepancy principle: ‖Aq−d‖² = τ·m·σ².</summary>
    Discrepancy,

    /// <summary>Generalised cross-validation: minimise m‖Aq−d‖² / (m − tr H_λ)².</summary>
    Gcv,
}

/// <summary>Result of one regularised inversion.</summary>
/// <param name="Coefficients">q_j at the basis nodes [kW/m³].</param>
/// <param name="SourceField">Reconstructed q(x, y) on the model grid at full load [W/m³].</param>
/// <param name="TemperatureField">State estimate T̂(x, y, t_now) [°C].</param>
/// <param name="Lambda">Absolute regularisation parameter λ.</param>
/// <param name="RelativeLambda">λ / (tr(AᵀA)/tr(LᵀL)) — scale-free value reported to users.</param>
/// <param name="ResidualNorm">‖A q − d‖ [K].</param>
/// <param name="SolutionSeminorm">‖L q‖.</param>
/// <param name="MeasurementCount">m — number of scalar sensor samples assimilated.</param>
public sealed record InverseSolution(
    double[] Coefficients,
    double[] SourceField,
    double[] TemperatureField,
    double Lambda,
    double RelativeLambda,
    double ResidualNorm,
    double SolutionSeminorm,
    int MeasurementCount,
    LambdaSelection Selection);

public sealed record LCurvePoint(double RelativeLambda, double ResidualNorm, double SolutionSeminorm, double Curvature, double Gcv);

public sealed record LCurveResult(IReadOnlyList<LCurvePoint> Points, int CornerIndex)
{
    public LCurvePoint Corner => Points[CornerIndex];
}

/// <summary>
/// Recursive inverse heat-source estimator.
/// <para>
/// <b>Forward model.</b> The heat equation is linear in T, so with the unknown source written as
/// q(x, y, t) = s(t) Σ_j q_j φ_j(x, y) the temperature splits into
/// <code>
///   T(t) = T₀(t) + Σ_j q_j T_j(t)
/// </code>
/// T₀ — response to the known initial state, boundary data, coolant and cooling history with q = 0;
/// T_j — response to basis source s(t)·φ_j from zero initial state with homogeneous data.
/// </para>
/// <para>
/// <b>Measurements.</b> At every sample time the sensors give y = C T + ε, hence
/// <code>
///   d := y − C T₀ = A q + ε,     A[(t,s), j] = (C T_j(t))_s
/// </code>
/// Each new time step appends one block row to A. Instead of storing A, the estimator advances the
/// basis responses alongside the plant and accumulates the normal-equation quantities
/// AᵀA, Aᵀd and dᵀd recursively, so the cost per step is independent of the history length.
/// </para>
/// <para>
/// <b>Tikhonov regularisation.</b>
/// <code>
///   q* = argmin ‖A q − d‖² + λ ‖L q‖²   ⇔   (AᵀA + λ LᵀL) q* = Aᵀd
/// </code>
/// which is solved by dense Cholesky (n ≈ 100 unknowns).
/// </para>
/// </summary>
public sealed class InverseHeatSourceEstimator
{
    /// <summary>Coefficients are expressed in kW/m³ so that A has well-scaled entries.</summary>
    public const double CoefficientScale = 1000.0;

    private readonly HeatEquationSolver _solver;
    private readonly SensorNetwork _sensors;
    private readonly LoadProfile _load;
    private readonly double[][] _basisSources;
    private double[] _homogeneous;
    private double[][] _basisStates;
    private readonly DenseMatrix _ata;
    private readonly double[] _atd;
    private double _dtd;
    private readonly Dictionary<RegularizationKind, DenseMatrix> _regularizationGram = [];

    public InverseHeatSourceEstimator(
        HeatEquationSolver solver,
        SourceBasis basis,
        SensorNetwork sensors,
        LoadProfile load,
        ReadOnlySpan<double> initialTemperature,
        double startTime = 0)
    {
        if (!ReferenceEquals(solver.Model.Grid, basis.Grid) && solver.Model.Grid != basis.Grid)
        {
            throw new ArgumentException("Basis and solver must share the same grid.", nameof(basis));
        }

        _solver = solver;
        Basis = basis;
        _sensors = sensors;
        _load = load;
        Time = startTime;
        _homogeneous = initialTemperature.ToArray();
        _basisSources = Enumerable.Range(0, basis.Count).Select(j => basis.BasisField(j, CoefficientScale)).ToArray();
        _basisStates = Enumerable.Range(0, basis.Count).Select(_ => new double[solver.Size]).ToArray();
        _ata = new DenseMatrix(basis.Count, basis.Count);
        _atd = new double[basis.Count];
    }

    public SourceBasis Basis { get; }

    public double Time { get; private set; }

    public int MeasurementCount { get; private set; }

    public int Unknowns => Basis.Count;

    /// <summary>
    /// Advances T₀ and every T_j by one step with the applied cooling level, then assimilates the
    /// sensor readings taken at the new time.
    /// </summary>
    public void Assimilate(double coolingLevel, ReadOnlySpan<double> measurements)
    {
        var dt = _solver.TimeStep;
        var sNow = _load.At(Time);
        var sNext = _load.At(Time + dt);

        var nextHom = new double[_solver.Size];
        _solver.Step(_homogeneous, nextHom, [], [], coolingLevel);
        _homogeneous = nextHom;

        var next = new double[_basisStates.Length][];
        Parallel.For(0, _basisStates.Length, j =>
        {
            var src = _basisSources[j];
            var qn = new double[src.Length];
            var qn1 = new double[src.Length];
            for (var k = 0; k < src.Length; k++)
            {
                qn[k] = sNow * src[k];
                qn1[k] = sNext * src[k];
            }

            var result = new double[_solver.Size];
            _solver.Step(_basisStates[j], result, qn, qn1, coolingLevel, homogeneous: true);
            next[j] = result;
        });
        _basisStates = next;
        Time += dt;

        // Append the block row for this sample time.
        var baseline = _sensors.Observe(_homogeneous);
        var responses = _basisStates.Select(state => _sensors.Observe(state)).ToArray();
        var row = new double[Unknowns];
        for (var s = 0; s < _sensors.Count; s++)
        {
            for (var j = 0; j < Unknowns; j++)
            {
                row[j] = responses[j][s];
            }

            var d = measurements[s] - baseline[s];
            _ata.AddOuterProduct(row);
            Vector.Axpy(d, row, _atd);
            _dtd += d * d;
        }

        MeasurementCount += _sensors.Count;
    }

    public double RegularizationScale(RegularizationKind kind) =>
        _ata.Trace() / Math.Max(Gram(kind).Trace(), double.Epsilon);

    /// <summary>Solves (AᵀA + λ LᵀL) q = Aᵀd for a relative λ.</summary>
    public InverseSolution Solve(double relativeLambda, RegularizationKind kind = RegularizationKind.Gradient,
        LambdaSelection selection = LambdaSelection.Fixed)
    {
        if (MeasurementCount == 0)
        {
            throw new InvalidOperationException("No measurements have been assimilated yet.");
        }

        var gram = Gram(kind);
        var lambda = relativeLambda * RegularizationScale(kind);
        var q = _ata.Add(gram, lambda).SolveSpd(_atd);

        var source = Basis.Expand(q, CoefficientScale);
        var temperature = (double[])_homogeneous.Clone();
        for (var j = 0; j < Unknowns; j++)
        {
            Vector.Axpy(q[j], _basisStates[j], temperature);
        }

        return new InverseSolution(
            q,
            source,
            temperature,
            lambda,
            relativeLambda,
            ResidualNorm(q),
            Math.Sqrt(Math.Max(0, Vector.Dot(q, gram.Multiply(q)))),
            MeasurementCount,
            selection);
    }

    /// <summary>
    /// Current state estimate T̂ = T₀ + Σ_j q_j T_j for given coefficients — cheap enough to
    /// refresh every step between (more expensive) inverse solves.
    /// </summary>
    public double[] EstimateTemperature(ReadOnlySpan<double> coefficients)
    {
        var temperature = (double[])_homogeneous.Clone();
        for (var j = 0; j < Unknowns; j++)
        {
            Vector.Axpy(coefficients[j], _basisStates[j], temperature);
        }

        return temperature;
    }

    /// <summary>‖Aq − d‖ evaluated from the accumulated normal equations: qᵀAᵀAq − 2qᵀAᵀd + dᵀd.</summary>
    public double ResidualNorm(ReadOnlySpan<double> q)
    {
        var r2 = Vector.Dot(q, _ata.Multiply(q)) - 2 * Vector.Dot(q, _atd) + _dtd;
        return Math.Sqrt(Math.Max(0, r2));
    }

    /// <summary>Traces the L-curve and locates its corner by maximum Menger curvature in log–log space.</summary>
    public LCurveResult ComputeLCurve(RegularizationKind kind, double minRelativeLambda = 1e-8,
        double maxRelativeLambda = 1e1, int points = 36)
    {
        var lambdas = Vector.LogSpace(minRelativeLambda, maxRelativeLambda, points);
        var raw = lambdas.Select(l =>
        {
            var s = Solve(l, kind);
            return (Lambda: l, s.ResidualNorm, s.SolutionSeminorm, Gcv: Gcv(l, kind));
        }).ToArray();

        var curvature = new double[raw.Length];
        for (var i = 1; i < raw.Length - 1; i++)
        {
            curvature[i] = MengerCurvature(
                (Math.Log10(raw[i - 1].ResidualNorm), Math.Log10(Math.Max(raw[i - 1].SolutionSeminorm, 1e-300))),
                (Math.Log10(raw[i].ResidualNorm), Math.Log10(Math.Max(raw[i].SolutionSeminorm, 1e-300))),
                (Math.Log10(raw[i + 1].ResidualNorm), Math.Log10(Math.Max(raw[i + 1].SolutionSeminorm, 1e-300))));
        }

        var corner = 1;
        for (var i = 2; i < raw.Length - 1; i++)
        {
            if (curvature[i] > curvature[corner])
            {
                corner = i;
            }
        }

        return new LCurveResult(
            raw.Select((p, i) => new LCurvePoint(p.Lambda, p.ResidualNorm, p.SolutionSeminorm, curvature[i], p.Gcv)).ToArray(),
            corner);
    }

    /// <summary>
    /// Morozov discrepancy principle: choose λ such that ‖A q_λ − d‖² = τ m σ². The residual is
    /// monotone in λ, so bisection on log λ converges.
    /// </summary>
    public double DiscrepancyLambda(RegularizationKind kind, double noiseStd, double tau = 1.0)
    {
        var target = Math.Sqrt(tau * MeasurementCount) * noiseStd;
        double lo = -10, hi = 2;
        if (Solve(Math.Pow(10, lo), kind).ResidualNorm >= target)
        {
            return Math.Pow(10, lo);
        }

        if (Solve(Math.Pow(10, hi), kind).ResidualNorm <= target)
        {
            return Math.Pow(10, hi);
        }

        for (var it = 0; it < 50; it++)
        {
            var mid = 0.5 * (lo + hi);
            if (Solve(Math.Pow(10, mid), kind).ResidualNorm < target)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return Math.Pow(10, 0.5 * (lo + hi));
    }

    /// <summary>
    /// Generalised cross-validation function
    /// <code>
    ///   GCV(λ) = m ‖A q_λ − d‖² / (m − tr H_λ)²,   H_λ = (AᵀA + λLᵀL)⁻¹ AᵀA
    /// </code>
    /// tr H_λ is the effective number of parameters resolved by the data; it is computed from the
    /// accumulated normal equations with one Cholesky factorisation and n triangular solves.
    /// </summary>
    public double Gcv(double relativeLambda, RegularizationKind kind)
    {
        var lambda = relativeLambda * RegularizationScale(kind);
        var factor = DenseCholesky.Factor(_ata.Add(Gram(kind), lambda));
        var trace = 0.0;
        var column = new double[Unknowns];
        for (var j = 0; j < Unknowns; j++)
        {
            for (var i = 0; i < Unknowns; i++)
            {
                column[i] = _ata[i, j];
            }

            trace += factor.Solve(column)[j];
        }

        var q = factor.Solve(_atd);
        var r = ResidualNorm(q);
        var dof = Math.Max(MeasurementCount - trace, 1e-9);
        return MeasurementCount * r * r / (dof * dof);
    }

    /// <summary>λ minimising GCV over a logarithmic grid followed by golden-section refinement.</summary>
    public double GcvLambda(RegularizationKind kind, double minRelativeLambda = 1e-8, double maxRelativeLambda = 1e1)
    {
        var grid = Vector.LogSpace(minRelativeLambda, maxRelativeLambda, 28);
        var values = grid.Select(l => Gcv(l, kind)).ToArray();
        var best = Array.IndexOf(values, values.Min());
        var lo = Math.Log10(grid[Math.Max(0, best - 1)]);
        var hi = Math.Log10(grid[Math.Min(grid.Length - 1, best + 1)]);
        var phi = (Math.Sqrt(5) - 1) / 2;
        for (var it = 0; it < 25; it++)
        {
            var a = hi - phi * (hi - lo);
            var b = lo + phi * (hi - lo);
            if (Gcv(Math.Pow(10, a), kind) < Gcv(Math.Pow(10, b), kind))
            {
                hi = b;
            }
            else
            {
                lo = a;
            }
        }

        return Math.Pow(10, 0.5 * (lo + hi));
    }

    /// <summary>Solves with an automatically selected λ.</summary>
    public InverseSolution SolveAuto(LambdaSelection selection, RegularizationKind kind, double noiseStd, double fixedLambda)
    {
        var lambda = selection switch
        {
            LambdaSelection.LCurve => ComputeLCurve(kind).Corner.RelativeLambda,
            LambdaSelection.Discrepancy => DiscrepancyLambda(kind, noiseStd),
            LambdaSelection.Gcv => GcvLambda(kind),
            _ => fixedLambda,
        };
        return Solve(lambda, kind, selection);
    }

    private DenseMatrix Gram(RegularizationKind kind)
    {
        if (!_regularizationGram.TryGetValue(kind, out var gram))
        {
            gram = Basis.RegularizationOperator(kind).GramMatrix();
            _regularizationGram[kind] = gram;
        }

        return gram;
    }

    private static double MengerCurvature((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        // κ = 4·Area / (|ab|·|bc|·|ca|), signed so that a convex "corner" is positive.
        var cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        var ab = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
        var bc = Math.Sqrt(Math.Pow(c.X - b.X, 2) + Math.Pow(c.Y - b.Y, 2));
        var ca = Math.Sqrt(Math.Pow(a.X - c.X, 2) + Math.Pow(a.Y - c.Y, 2));
        var denominator = ab * bc * ca;
        return denominator < 1e-14 ? 0 : 2 * cross / denominator;
    }
}
