using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Physics;

namespace ThermoTwin.Numerics.Prediction;

/// <summary>Piecewise-constant cooling schedule u(t) over a prediction horizon.</summary>
/// <param name="SegmentDuration">Length of each segment [s].</param>
/// <param name="Levels">u_k ∈ [0, 1] for each segment.</param>
public sealed record CoolingPlan(double SegmentDuration, double[] Levels)
{
    public double Horizon => SegmentDuration * Levels.Length;

    public double LevelAt(double elapsed)
    {
        var k = (int)Math.Floor(elapsed / SegmentDuration + 1e-9);
        return Levels[Math.Clamp(k, 0, Levels.Length - 1)];
    }

    public static CoolingPlan Constant(double level, int segments, double segmentDuration) =>
        new(segmentDuration, Enumerable.Repeat(level, segments).ToArray());
}

/// <summary>Forecast of the thermal state over a horizon.</summary>
public sealed record ThermalForecast(
    double StartTime,
    double[] Times,
    double[] MaxTemperature,
    double[] MeanTemperature,
    double[] FinalField,
    double PeakTemperature,
    double PeakTime,
    double CoolingEnergy);

/// <summary>
/// Forward prediction: integrates the heat equation from the current (estimated) state with the
/// estimated source q̂(x, y), the known future load profile s(t) and a candidate cooling plan.
/// This is the "simulate forward" half of model-predictive control.
/// </summary>
public sealed class ThermalPredictor
{
    private readonly HeatEquationSolver _solver;
    private readonly LoadProfile _load;

    public ThermalPredictor(HeatEquationSolver solver, LoadProfile load)
    {
        _solver = solver;
        _load = load;
    }

    public HeatEquationSolver Solver => _solver;

    public ThermalForecast Predict(ReadOnlySpan<double> initialState, ReadOnlySpan<double> sourceShape, double startTime, CoolingPlan plan)
    {
        var dt = _solver.TimeStep;
        var steps = Math.Max(1, (int)Math.Round(plan.Horizon / dt));
        var n = _solver.Size;
        var current = initialState.ToArray();
        var next = new double[n];
        var qNow = new double[n];
        var qNext = new double[n];
        var shape = sourceShape.ToArray();

        var times = new double[steps + 1];
        var maxT = new double[steps + 1];
        var meanT = new double[steps + 1];
        times[0] = startTime;
        maxT[0] = Vector.Max(current);
        meanT[0] = Vector.Mean(current);
        var energy = 0.0;

        for (var step = 0; step < steps; step++)
        {
            var t = startTime + step * dt;
            var u = plan.LevelAt(step * dt);
            var sNow = _load.At(t);
            var sNext = _load.At(t + dt);
            for (var k = 0; k < n; k++)
            {
                qNow[k] = sNow * shape[k];
                qNext[k] = sNext * shape[k];
            }

            _solver.Step(current, next, qNow, qNext, u);
            (current, next) = (next, current);
            energy += _solver.Model.Cooling.Power(u) * dt;

            times[step + 1] = t + dt;
            maxT[step + 1] = Vector.Max(current);
            meanT[step + 1] = Vector.Mean(current);
        }

        var peakIndex = Vector.ArgMax(maxT);
        return new ThermalForecast(startTime, times, maxT, meanT, current, maxT[peakIndex], times[peakIndex], energy);
    }
}
