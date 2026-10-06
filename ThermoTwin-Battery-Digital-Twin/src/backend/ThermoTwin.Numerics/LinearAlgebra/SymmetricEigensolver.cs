namespace ThermoTwin.Numerics.LinearAlgebra;

/// <summary>Eigen-decomposition A = V Λ Vᵀ of a real symmetric matrix, eigenvalues sorted in descending order.</summary>
/// <param name="Values">λ₁ ≥ λ₂ ≥ … ≥ λₙ.</param>
/// <param name="Vectors">Orthonormal eigenvectors stored as columns of V (column i belongs to λᵢ).</param>
/// <param name="Sweeps">Jacobi sweeps used.</param>
/// <param name="OffDiagonalNorm">Frobenius norm of the remaining off-diagonal part (convergence measure).</param>
public sealed record SymmetricEigenDecomposition(double[] Values, DenseMatrix Vectors, int Sweeps, double OffDiagonalNorm)
{
    public double[] Vector(int index)
    {
        var v = new double[Vectors.Rows];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = Vectors[i, index];
        }

        return v;
    }
}

/// <summary>
/// Cyclic Jacobi eigenvalue algorithm for dense symmetric matrices.
/// <para>
/// Each rotation J(p, q, φ) annihilates the off-diagonal pair a_pq = a_qp of A ← JᵀAJ, with
/// <code>
///   θ = (a_qq − a_pp) / (2 a_pq),   t = tan φ = sgn(θ) / (|θ| + √(θ² + 1)),   c = 1/√(1 + t²),  s = t·c.
/// </code>
/// The off-diagonal Frobenius norm decreases monotonically and, after the first few sweeps, quadratically.
/// Jacobi is slower than Householder tridiagonalisation + QR for large n (≈ 2n³ flops per sweep), but it is
/// short, unconditionally robust and computes small eigenvalues to high <i>relative</i> accuracy — the
/// property that matters for the tail of a POD spectrum. It is used for the method-of-snapshots
/// correlation matrix (n ≲ 500), the diagonalisation of the reduced operator, and Fisher-information matrices.
/// </para>
/// </summary>
public static class SymmetricEigensolver
{
    /// <summary>
    /// Default solver: Householder tridiagonalisation + implicit QL for n &gt; 40 (O(n³) once), cyclic Jacobi
    /// for small matrices. Both are validated against each other in the test-suite.
    /// </summary>
    public static SymmetricEigenDecomposition Decompose(DenseMatrix matrix) =>
        matrix.Rows > 40 ? DecomposeTridiagonalQl(matrix) : DecomposeJacobi(matrix);

    /// <summary>
    /// Householder reduction to tridiagonal form Qᵀ A Q = T followed by the implicitly shifted QL iteration on T
    /// (Bowdler, Martin, Reinsch &amp; Wilkinson; EISPACK tred2/tql2). About (4/3 + 3)·n³ flops including the
    /// eigenvectors — an order of magnitude cheaper than Jacobi for the n ≈ 500 snapshot correlation matrices.
    /// </summary>
    public static SymmetricEigenDecomposition DecomposeTridiagonalQl(DenseMatrix matrix)
    {
        if (matrix.Rows != matrix.Columns)
        {
            throw new ArgumentException("Eigen-decomposition requires a square matrix.", nameof(matrix));
        }

        var n = matrix.Rows;
        var v = new double[n][];
        for (var i = 0; i < n; i++)
        {
            v[i] = new double[n];
            for (var j = 0; j < n; j++)
            {
                v[i][j] = 0.5 * (matrix[i, j] + matrix[j, i]);
            }
        }

        var d = new double[n];
        var e = new double[n];
        Tridiagonalise(v, d, e, n);
        var iterations = TridiagonalQl(v, d, e, n);

        var order = Enumerable.Range(0, n).OrderByDescending(i => d[i]).ToArray();
        var vectors = new DenseMatrix(n, n);
        for (var col = 0; col < n; col++)
        {
            for (var row = 0; row < n; row++)
            {
                vectors[row, col] = v[row][order[col]];
            }
        }

        return new SymmetricEigenDecomposition(order.Select(i => d[i]).ToArray(), vectors, iterations, 0);
    }

    private static void Tridiagonalise(double[][] v, double[] d, double[] e, int n)
    {
        for (var j = 0; j < n; j++)
        {
            d[j] = v[n - 1][j];
        }

        for (var i = n - 1; i > 0; i--)
        {
            var scale = 0.0;
            var h = 0.0;
            for (var k = 0; k < i; k++)
            {
                scale += Math.Abs(d[k]);
            }

            if (scale == 0.0)
            {
                e[i] = d[i - 1];
                for (var j = 0; j < i; j++)
                {
                    d[j] = v[i - 1][j];
                    v[i][j] = 0.0;
                    v[j][i] = 0.0;
                }
            }
            else
            {
                // Householder vector.
                for (var k = 0; k < i; k++)
                {
                    d[k] /= scale;
                    h += d[k] * d[k];
                }

                var f = d[i - 1];
                var g = Math.Sqrt(h);
                if (f > 0)
                {
                    g = -g;
                }

                e[i] = scale * g;
                h -= f * g;
                d[i - 1] = f - g;
                for (var j = 0; j < i; j++)
                {
                    e[j] = 0.0;
                }

                // Similarity transformation of the remaining columns.
                for (var j = 0; j < i; j++)
                {
                    f = d[j];
                    v[j][i] = f;
                    g = e[j] + v[j][j] * f;
                    for (var k = j + 1; k <= i - 1; k++)
                    {
                        g += v[k][j] * d[k];
                        e[k] += v[k][j] * f;
                    }

                    e[j] = g;
                }

                f = 0.0;
                for (var j = 0; j < i; j++)
                {
                    e[j] /= h;
                    f += e[j] * d[j];
                }

                var hh = f / (h + h);
                for (var j = 0; j < i; j++)
                {
                    e[j] -= hh * d[j];
                }

                for (var j = 0; j < i; j++)
                {
                    f = d[j];
                    g = e[j];
                    for (var k = j; k <= i - 1; k++)
                    {
                        v[k][j] -= f * e[k] + g * d[k];
                    }

                    d[j] = v[i - 1][j];
                    v[i][j] = 0.0;
                }
            }

            d[i] = h;
        }

        // Accumulate the orthogonal transformation Q.
        for (var i = 0; i < n - 1; i++)
        {
            v[n - 1][i] = v[i][i];
            v[i][i] = 1.0;
            var h = d[i + 1];
            if (h != 0.0)
            {
                for (var k = 0; k <= i; k++)
                {
                    d[k] = v[k][i + 1] / h;
                }

                for (var j = 0; j <= i; j++)
                {
                    var g = 0.0;
                    for (var k = 0; k <= i; k++)
                    {
                        g += v[k][i + 1] * v[k][j];
                    }

                    for (var k = 0; k <= i; k++)
                    {
                        v[k][j] -= g * d[k];
                    }
                }
            }

            for (var k = 0; k <= i; k++)
            {
                v[k][i + 1] = 0.0;
            }
        }

        for (var j = 0; j < n; j++)
        {
            d[j] = v[n - 1][j];
            v[n - 1][j] = 0.0;
        }

        v[n - 1][n - 1] = 1.0;
        e[0] = 0.0;
    }

    private static int TridiagonalQl(double[][] v, double[] d, double[] e, int n)
    {
        for (var i = 1; i < n; i++)
        {
            e[i - 1] = e[i];
        }

        e[n - 1] = 0.0;
        var f = 0.0;
        var tst1 = 0.0;
        var eps = Math.Pow(2.0, -52.0);
        var totalIterations = 0;
        for (var l = 0; l < n; l++)
        {
            // Find a negligible sub-diagonal element.
            tst1 = Math.Max(tst1, Math.Abs(d[l]) + Math.Abs(e[l]));
            var m = l;
            while (m < n)
            {
                if (Math.Abs(e[m]) <= eps * tst1)
                {
                    break;
                }

                m++;
            }

            if (m > l)
            {
                var iteration = 0;
                do
                {
                    if (++iteration > 60)
                    {
                        throw new InvalidOperationException("Tridiagonal QL iteration did not converge.");
                    }

                    totalIterations++;

                    // Implicit Wilkinson-type shift.
                    var g = d[l];
                    var p = (d[l + 1] - g) / (2.0 * e[l]);
                    var r = Hypot(p, 1.0);
                    if (p < 0)
                    {
                        r = -r;
                    }

                    d[l] = e[l] / (p + r);
                    d[l + 1] = e[l] * (p + r);
                    var dl1 = d[l + 1];
                    var h = g - d[l];
                    for (var i = l + 2; i < n; i++)
                    {
                        d[i] -= h;
                    }

                    f += h;

                    // Implicit QL sweep with Givens rotations.
                    p = d[m];
                    double c = 1.0, c2 = c, c3 = c;
                    var el1 = e[l + 1];
                    double s = 0.0, s2 = 0.0;
                    for (var i = m - 1; i >= l; i--)
                    {
                        c3 = c2;
                        c2 = c;
                        s2 = s;
                        g = c * e[i];
                        h = c * p;
                        r = Hypot(p, e[i]);
                        e[i + 1] = s * r;
                        s = e[i] / r;
                        c = p / r;
                        p = c * d[i] - s * g;
                        d[i + 1] = h + s * (c * g + s * d[i]);
                        for (var k = 0; k < n; k++)
                        {
                            h = v[k][i + 1];
                            v[k][i + 1] = s * v[k][i] + c * h;
                            v[k][i] = c * v[k][i] - s * h;
                        }
                    }

                    p = -s * s2 * c3 * el1 * e[l] / dl1;
                    e[l] = s * p;
                    d[l] = c * p;
                }
                while (Math.Abs(e[l]) > eps * tst1);
            }

            d[l] += f;
            e[l] = 0.0;
        }

        return totalIterations;
    }

    private static double Hypot(double a, double b)
    {
        double ax = Math.Abs(a), bx = Math.Abs(b);
        if (ax > bx)
        {
            var t = bx / ax;
            return ax * Math.Sqrt(1 + t * t);
        }

        if (bx > 0)
        {
            var t = ax / bx;
            return bx * Math.Sqrt(1 + t * t);
        }

        return 0;
    }

    /// <summary>Cyclic Jacobi (see class remarks): slower, but simple and highly accurate for small matrices.</summary>
    public static SymmetricEigenDecomposition DecomposeJacobi(DenseMatrix matrix, double tolerance = 1e-14, int maxSweeps = 60)
    {
        if (matrix.Rows != matrix.Columns)
        {
            throw new ArgumentException("Eigen-decomposition requires a square matrix.", nameof(matrix));
        }

        var n = matrix.Rows;
        var a = new double[n * n];
        var v = new double[n * n];
        var frobenius = 0.0;
        for (var i = 0; i < n; i++)
        {
            v[i * n + i] = 1.0;
            for (var j = 0; j < n; j++)
            {
                // Symmetrise defensively: round-off in an assembled AᵀA is not exactly symmetric.
                var value = 0.5 * (matrix[i, j] + matrix[j, i]);
                a[i * n + j] = value;
                frobenius += value * value;
            }
        }

        var threshold = tolerance * Math.Sqrt(Math.Max(frobenius, double.Epsilon));
        var sweeps = 0;
        var off = OffDiagonalNorm(a, n);
        while (off > threshold && sweeps < maxSweeps)
        {
            sweeps++;
            for (var p = 0; p < n - 1; p++)
            {
                for (var q = p + 1; q < n; q++)
                {
                    var apq = a[p * n + q];
                    if (Math.Abs(apq) < 1e-300)
                    {
                        continue;
                    }

                    var app = a[p * n + p];
                    var aqq = a[q * n + q];
                    var theta = (aqq - app) / (2 * apq);
                    var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0)
                    {
                        t = 1;
                    }

                    var c = 1 / Math.Sqrt(t * t + 1);
                    var s = t * c;

                    // Columns p and q:  A ← A J
                    for (var k = 0; k < n; k++)
                    {
                        var akp = a[k * n + p];
                        var akq = a[k * n + q];
                        a[k * n + p] = c * akp - s * akq;
                        a[k * n + q] = s * akp + c * akq;
                    }

                    // Rows p and q:  A ← Jᵀ A
                    var rowP = p * n;
                    var rowQ = q * n;
                    for (var k = 0; k < n; k++)
                    {
                        var apk = a[rowP + k];
                        var aqk = a[rowQ + k];
                        a[rowP + k] = c * apk - s * aqk;
                        a[rowQ + k] = s * apk + c * aqk;
                    }

                    a[p * n + q] = 0;
                    a[q * n + p] = 0;

                    // Accumulate eigenvectors:  V ← V J
                    for (var k = 0; k < n; k++)
                    {
                        var vkp = v[k * n + p];
                        var vkq = v[k * n + q];
                        v[k * n + p] = c * vkp - s * vkq;
                        v[k * n + q] = s * vkp + c * vkq;
                    }
                }
            }

            off = OffDiagonalNorm(a, n);
        }

        var order = Enumerable.Range(0, n).OrderByDescending(i => a[i * n + i]).ToArray();
        var values = order.Select(i => a[i * n + i]).ToArray();
        var vectors = new DenseMatrix(n, n);
        for (var col = 0; col < n; col++)
        {
            var src = order[col];
            for (var row = 0; row < n; row++)
            {
                vectors[row, col] = v[row * n + src];
            }
        }

        return new SymmetricEigenDecomposition(values, vectors, sweeps, off);
    }

    private static double OffDiagonalNorm(double[] a, int n)
    {
        var sum = 0.0;
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                sum += 2 * a[i * n + j] * a[i * n + j];
            }
        }

        return Math.Sqrt(sum);
    }
}
