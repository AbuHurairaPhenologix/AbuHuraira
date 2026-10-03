using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.LinearAlgebra;

namespace ThermoTwin.Numerics.Analysis;

/// <summary>A detected localised heat-generation anomaly.</summary>
/// <param name="X">Excess-weighted centroid x [m].</param>
/// <param name="Y">Excess-weighted centroid y [m].</param>
/// <param name="PeakValue">Peak value of the analysed field.</param>
/// <param name="Background">Robust background level (median).</param>
/// <param name="Area">Area of the connected region above the detection threshold [m²].</param>
/// <param name="Prominence">(peak − background) / background — how strongly the hotspot stands out.</param>
public sealed record Hotspot(double X, double Y, double PeakValue, double Background, double Area, double Prominence, int CellCount);

/// <summary>
/// Hotspot localisation on a scalar field (reconstructed source or temperature):
/// <list type="number">
/// <item>background b = median of the field (robust to the anomaly itself);</item>
/// <item>threshold τ = b + κ·(max − b) with κ = ½ (half-maximum above background);</item>
/// <item>flood-fill the 4-connected region above τ that contains the global maximum;</item>
/// <item>centroid weighted by the excess (f − τ) over that region.</item>
/// </list>
/// </summary>
public static class HotspotDetector
{
    public static Hotspot? Detect(Grid2D grid, ReadOnlySpan<double> field, double relativeThreshold = 0.5,
        double minimumProminence = 0.15)
    {
        var sorted = field.ToArray();
        Array.Sort(sorted);
        var background = sorted[sorted.Length / 2];
        var peakIndex = Vector.ArgMax(field);
        var peak = field[peakIndex];
        var prominence = (peak - background) / Math.Max(Math.Abs(background), 1e-9);
        if (peak <= background || prominence < minimumProminence)
        {
            return null;
        }

        var threshold = background + relativeThreshold * (peak - background);
        var visited = new bool[field.Length];
        var stack = new Stack<int>();
        stack.Push(peakIndex);
        visited[peakIndex] = true;

        double wSum = 0, xSum = 0, ySum = 0;
        var cells = 0;
        var values = field.ToArray();
        while (stack.Count > 0)
        {
            var k = stack.Pop();
            var (i, j) = grid.Coordinates(k);
            var w = values[k] - threshold;
            wSum += w;
            xSum += w * grid.X(i);
            ySum += w * grid.Y(j);
            cells++;

            foreach (var (ni, nj) in new[] { (i - 1, j), (i + 1, j), (i, j - 1), (i, j + 1) })
            {
                if (ni < 0 || nj < 0 || ni >= grid.Nx || nj >= grid.Ny)
                {
                    continue;
                }

                var nk = grid.Index(ni, nj);
                if (!visited[nk] && values[nk] > threshold)
                {
                    visited[nk] = true;
                    stack.Push(nk);
                }
            }
        }

        return new Hotspot(xSum / wSum, ySum / wSum, peak, background, cells * grid.CellArea, prominence, cells);
    }

    /// <summary>Location of the field maximum (cell centre).</summary>
    public static (double X, double Y, double Value) Maximum(Grid2D grid, ReadOnlySpan<double> field)
    {
        var k = Vector.ArgMax(field);
        var (x, y) = grid.CellCentre(k);
        return (x, y, field[k]);
    }
}
