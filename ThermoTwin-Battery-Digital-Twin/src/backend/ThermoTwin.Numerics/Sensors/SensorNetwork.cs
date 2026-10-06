using ThermoTwin.Numerics.Grid;

namespace ThermoTwin.Numerics.Sensors;

/// <summary>A point temperature sensor (thermistor) at physical position (X, Y) in metres.</summary>
public sealed record Sensor(string Id, double X, double Y);

/// <summary>
/// Sparse observation operator. Each sensor reads the bilinear interpolation of the cell-centred
/// temperature field, so the noise-free measurement vector is y = C·T with C a sparse
/// (sensors × cells) matrix holding four interpolation weights per row.
/// </summary>
public sealed class SensorNetwork
{
    private readonly IReadOnlyList<(int Index, double Weight)>[] _weights;

    public SensorNetwork(Grid2D grid, IReadOnlyList<Sensor> sensors)
    {
        Grid = grid;
        Sensors = sensors;
        _weights = sensors.Select(s => grid.InterpolationWeights(s.X, s.Y)).ToArray();
    }

    public Grid2D Grid { get; }

    public IReadOnlyList<Sensor> Sensors { get; }

    public int Count => Sensors.Count;

    /// <summary>Noise-free readings y = C·T.</summary>
    public double[] Observe(ReadOnlySpan<double> field)
    {
        var y = new double[Count];
        Observe(field, y);
        return y;
    }

    public void Observe(ReadOnlySpan<double> field, Span<double> destination)
    {
        for (var s = 0; s < Count; s++)
        {
            var value = 0.0;
            foreach (var (index, weight) in _weights[s])
            {
                value += weight * field[index];
            }

            destination[s] = value;
        }
    }

    /// <summary>Noisy readings y = C·T + ε, ε ~ N(0, σ²I).</summary>
    public double[] Measure(ReadOnlySpan<double> field, GaussianNoise noise)
    {
        var y = Observe(field);
        for (var s = 0; s < y.Length; s++)
        {
            y[s] += noise.Next();
        }

        return y;
    }

    /// <summary>
    /// Regular nx × ny sensor lattice inset from the edges — a realistic thermistor layout that
    /// deliberately does not place a sensor on top of every possible hotspot.
    /// </summary>
    public static IReadOnlyList<Sensor> Lattice(double lengthX, double lengthY, int nx, int ny, double insetFraction = 0.12)
    {
        var sensors = new List<Sensor>();
        var x0 = insetFraction * lengthX;
        var y0 = insetFraction * lengthY * 1.4;
        for (var j = 0; j < ny; j++)
        {
            for (var i = 0; i < nx; i++)
            {
                var x = nx == 1 ? lengthX / 2 : x0 + i * (lengthX - 2 * x0) / (nx - 1);
                var y = ny == 1 ? lengthY / 2 : y0 + j * (lengthY - 2 * y0) / (ny - 1);
                sensors.Add(new Sensor($"S{sensors.Count + 1:00}", x, y));
            }
        }

        return sensors;
    }

    /// <summary>Picks a sensor lattice with approximately <paramref name="count"/> sensors and the domain aspect ratio.</summary>
    public static IReadOnlyList<Sensor> Layout(double lengthX, double lengthY, int count)
    {
        var aspect = lengthX / lengthY;
        var ny = Math.Max(1, (int)Math.Round(Math.Sqrt(count / aspect)));
        var nx = Math.Max(1, (int)Math.Round((double)count / ny));
        return Lattice(lengthX, lengthY, nx, ny);
    }
}

/// <summary>Seeded Gaussian noise generator (Box–Muller) so experiments are reproducible.</summary>
public sealed class GaussianNoise
{
    private readonly Random _random;
    private double? _spare;

    public GaussianNoise(double standardDeviation, int seed)
    {
        StandardDeviation = standardDeviation;
        _random = new Random(seed);
    }

    public double StandardDeviation { get; }

    public double Next()
    {
        if (StandardDeviation == 0)
        {
            return 0;
        }

        if (_spare is { } spare)
        {
            _spare = null;
            return StandardDeviation * spare;
        }

        double u1;
        do
        {
            u1 = _random.NextDouble();
        }
        while (u1 <= double.Epsilon);

        var u2 = _random.NextDouble();
        var radius = Math.Sqrt(-2 * Math.Log(u1));
        _spare = radius * Math.Sin(2 * Math.PI * u2);
        return StandardDeviation * radius * Math.Cos(2 * Math.PI * u2);
    }
}
