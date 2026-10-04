using ThermoTwin.Numerics.LinearAlgebra;

namespace ThermoTwin.Numerics.ReducedOrder;

/// <summary>A set of temperature snapshots T(t_j) collected from high-fidelity simulations.</summary>
public sealed class SnapshotSet
{
    private readonly List<double[]> _snapshots = [];

    public SnapshotSet(int dimension) => Dimension = dimension;

    public int Dimension { get; }

    public int Count => _snapshots.Count;

    public IReadOnlyList<double[]> Snapshots => _snapshots;

    public void Add(ReadOnlySpan<double> field)
    {
        if (field.Length != Dimension)
        {
            throw new ArgumentException($"Snapshot has {field.Length} entries, expected {Dimension}.", nameof(field));
        }

        _snapshots.Add(field.ToArray());
    }

    public double[] Mean()
    {
        var mean = new double[Dimension];
        foreach (var s in _snapshots)
        {
            Vector.Axpy(1.0 / _snapshots.Count, s, mean);
        }

        return mean;
    }
}

/// <summary>
/// Proper Orthogonal Decomposition of a snapshot set by the <b>method of snapshots</b> (Sirovich 1987).
/// <para>
/// With centred snapshots X = [x₁ − T̄, …, x_m − T̄] ∈ ℝ^{n×m} (m ≪ n), POD seeks the r-dimensional
/// subspace that minimises the mean squared projection error
/// <code>
///   min_{Φ_r ᵀΦ_r = I}  (1/m) Σ_j ‖x_j − T̄ − Φ_r Φ_rᵀ (x_j − T̄)‖²  =  Σ_{i &gt; r} λ_i,
/// </code>
/// whose solution is spanned by the leading eigenvectors of the covariance (1/m) X Xᵀ (n × n). The method of
/// snapshots instead solves the m × m eigenproblem of the correlation matrix C = (1/m) XᵀX,
/// C v_i = λ_i v_i, and recovers the modes φ_i = X v_i / √(m λ_i) — the same non-zero spectrum because
/// XXᵀ and XᵀX share their non-zero eigenvalues (they are the squared singular values of X/√m).
/// </para>
/// <para>
/// Inner product: on the uniform cell-centred grid the discrete L²(Ω) inner product is ΔxΔy·uᵀv, a constant
/// multiple of the Euclidean one, so the modes are identical; they are normalised to be Euclidean-orthonormal.
/// After the eigen-solve the modes are re-orthonormalised by two passes of modified Gram–Schmidt to remove
/// the loss of orthogonality caused by small λ_i.
/// </para>
/// </summary>
public sealed class PodBasis
{
    private PodBasis(double[] mean, double[][] modes, double[] eigenvalues, int snapshotCount, int sweeps)
    {
        Mean = mean;
        Modes = modes;
        Eigenvalues = eigenvalues;
        SnapshotCount = snapshotCount;
        JacobiSweeps = sweeps;
        var total = eigenvalues.Where(l => l > 0).Sum();
        var cumulative = 0.0;
        CumulativeEnergy = eigenvalues.Select(l => (cumulative += Math.Max(l, 0)) / Math.Max(total, double.Epsilon)).ToArray();
    }

    /// <summary>Reference state T̄ (snapshot mean).</summary>
    public double[] Mean { get; }

    /// <summary>Orthonormal POD modes φ_i (rank ≤ m − 1), ordered by decreasing energy.</summary>
    public double[][] Modes { get; }

    /// <summary>λ_i of C = XᵀX/m in decreasing order (all m of them, including the near-zero tail) [K²].</summary>
    public double[] Eigenvalues { get; }

    /// <summary>E(r) = Σ_{i≤r} λ_i / Σ_i λ_i — fraction of snapshot variance ("energy") captured by r modes.</summary>
    public double[] CumulativeEnergy { get; }

    public int SnapshotCount { get; }

    public int JacobiSweeps { get; }

    public int Dimension => Mean.Length;

    public int Rank => Modes.Length;

    /// <summary>Smallest r with E(r) ≥ fraction.</summary>
    public int ModesForEnergy(double fraction)
    {
        for (var i = 0; i < CumulativeEnergy.Length; i++)
        {
            if (CumulativeEnergy[i] >= fraction)
            {
                return Math.Min(i + 1, Rank);
            }
        }

        return Rank;
    }

    /// <summary>Computes the POD of <paramref name="snapshots"/> keeping at most <paramref name="maxModes"/> modes.</summary>
    public static PodBasis Compute(SnapshotSet snapshots, int maxModes = 60, double relativeCutoff = 1e-13)
    {
        var m = snapshots.Count;
        var n = snapshots.Dimension;
        if (m < 2)
        {
            throw new ArgumentException("POD needs at least two snapshots.", nameof(snapshots));
        }

        var mean = snapshots.Mean();
        var centred = snapshots.Snapshots.Select(s => Vector.Subtract(s, mean)).ToArray();

        // Correlation matrix C_ij = (x_i − T̄)ᵀ(x_j − T̄) / m — symmetric positive semi-definite.
        var correlation = new DenseMatrix(m, m);
        Parallel.For(0, m, i =>
        {
            for (var j = 0; j <= i; j++)
            {
                var value = Vector.Dot(centred[i], centred[j]) / m;
                correlation[i, j] = value;
                correlation[j, i] = value;
            }
        });

        var eigen = SymmetricEigensolver.Decompose(correlation);
        var values = eigen.Values;
        var cutoff = relativeCutoff * Math.Max(values[0], double.Epsilon);
        var modes = new List<double[]>();
        for (var i = 0; i < m && modes.Count < maxModes; i++)
        {
            if (values[i] <= cutoff)
            {
                break;
            }

            var phi = new double[n];
            var scale = 1.0 / Math.Sqrt(m * values[i]);
            for (var j = 0; j < m; j++)
            {
                Vector.Axpy(eigen.Vectors[j, i] * scale, centred[j], phi);
            }

            // Two passes of modified Gram–Schmidt against the previous modes.
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var previous in modes)
                {
                    Vector.Axpy(-Vector.Dot(previous, phi), previous, phi);
                }
            }

            var norm = Vector.Norm2(phi);
            if (norm < 1e-10)
            {
                break;
            }

            for (var k = 0; k < n; k++)
            {
                phi[k] /= norm;
            }

            modes.Add(phi);
        }

        return new PodBasis(mean, [.. modes], values.Select(v => Math.Max(v, 0)).ToArray(), m, eigen.Sweeps);
    }

    /// <summary>Coefficients a = Φ_rᵀ(T − T̄).</summary>
    public double[] Project(ReadOnlySpan<double> field, int r)
    {
        var a = new double[r];
        var centred = Vector.Subtract(field, Mean);
        for (var i = 0; i < r; i++)
        {
            a[i] = Vector.Dot(Modes[i], centred);
        }

        return a;
    }

    /// <summary>T = T̄ + Φ_r a.</summary>
    public double[] Reconstruct(ReadOnlySpan<double> coefficients)
    {
        var field = (double[])Mean.Clone();
        for (var i = 0; i < coefficients.Length; i++)
        {
            Vector.Axpy(coefficients[i], Modes[i], field);
        }

        return field;
    }

    /// <summary>Best-approximation (orthogonal projection) error ‖T − Π_r T‖₂ of a field onto the r-mode affine subspace.</summary>
    public double ProjectionError(ReadOnlySpan<double> field, int r) =>
        Vector.Norm2(Vector.Subtract(field, Reconstruct(Project(field, r))));
}
