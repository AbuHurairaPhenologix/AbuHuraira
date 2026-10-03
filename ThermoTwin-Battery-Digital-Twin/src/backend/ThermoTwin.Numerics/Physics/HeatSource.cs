using ThermoTwin.Numerics.Grid;

namespace ThermoTwin.Numerics.Physics;

/// <summary>
/// Piecewise-linear electrical load profile s(t) = (I(t) / I_ref)². Because both bulk Joule heating
/// and defect heating are resistive (∝ I²), every volumetric heat source in the cell scales with s(t).
/// The battery management system measures I(t), so s(t) is a known input to the estimator.
/// </summary>
public sealed class LoadProfile
{
    private readonly (double Time, double Factor)[] _points;

    public LoadProfile(IEnumerable<(double Time, double Factor)> points)
    {
        _points = points.OrderBy(p => p.Time).ToArray();
        if (_points.Length == 0)
        {
            throw new ArgumentException("A load profile needs at least one point.", nameof(points));
        }
    }

    public IReadOnlyList<(double Time, double Factor)> Points => _points;

    public static LoadProfile Constant(double factor = 1.0) => new([(0, factor)]);

    /// <summary>
    /// Fast-charge CC–CV profile: short ramp-up, constant-current phase, then a constant-voltage
    /// taper in which current (and thus I²R heat) decays.
    /// </summary>
    public static LoadProfile FastChargeCcCv(double ccEnd = 1200, double end = 1800, double taperFactor = 0.25) =>
        new([(0, 0.0), (30, 1.0), (ccEnd, 1.0), (ccEnd + 0.5 * (end - ccEnd), 0.5), (end, taperFactor)]);

    public double At(double t)
    {
        if (t <= _points[0].Time)
        {
            return _points[0].Factor;
        }

        for (var k = 1; k < _points.Length; k++)
        {
            if (t <= _points[k].Time)
            {
                var (t0, f0) = _points[k - 1];
                var (t1, f1) = _points[k];
                return f0 + (f1 - f0) * (t - t0) / (t1 - t0);
            }
        }

        return _points[^1].Factor;
    }
}

/// <summary>Gaussian volumetric heat source q(x, y) = Q·exp(−r² / 2σ²).</summary>
public sealed record GaussianHotspot(double X, double Y, double PeakPower, double Radius)
{
    public double Evaluate(double x, double y)
    {
        var dx = x - X;
        var dy = y - Y;
        return PeakPower * Math.Exp(-(dx * dx + dy * dy) / (2 * Radius * Radius));
    }
}

/// <summary>
/// Ground-truth volumetric heat generation q(x, y, t) = s(t) · [q_joule + Σ hotspots(x, y)] in W/m³.
/// Only the simulator ("the physical battery") knows this; the digital twin must infer it.
/// </summary>
public sealed class HeatSourceModel
{
    public HeatSourceModel(double uniformJouleHeating, IReadOnlyList<GaussianHotspot> hotspots, LoadProfile load)
    {
        UniformJouleHeating = uniformJouleHeating;
        Hotspots = hotspots;
        Load = load;
    }

    public double UniformJouleHeating { get; }

    public IReadOnlyList<GaussianHotspot> Hotspots { get; }

    public LoadProfile Load { get; }

    /// <summary>Time-independent spatial shape q(x, y) at full load (s = 1).</summary>
    public double[] SpatialField(Grid2D grid) =>
        grid.CreateField((x, y) => UniformJouleHeating + Hotspots.Sum(h => h.Evaluate(x, y)));

    public void Evaluate(Grid2D grid, double t, Span<double> destination, double[]? spatial = null)
    {
        spatial ??= SpatialField(grid);
        var s = Load.At(t);
        for (var k = 0; k < destination.Length; k++)
        {
            destination[k] = s * spatial[k];
        }
    }
}
