using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StudentPredictor.Web.Data;
using StudentPredictor.Web.Models;
using StudentPredictor.Web.Services;

namespace StudentPredictor.Web.Pages;

public class ModelStatsModel(MlService ml, DataSeeder seeder) : PageModel
{
    public ModelMetrics? Metrics { get; private set; }

    [TempData] public string? Message { get; set; }

    public void OnGet() => Metrics = ml.GetMetrics();

    /// <summary>Retrains the model from the CSV dataset and re-scores every student.</summary>
    public async Task<IActionResult> OnPostRetrainAsync()
    {
        await ml.TrainAsync();
        await seeder.ScoreAllStudentsAsync();
        var metrics = ml.GetMetrics();
        Message = $"Model retrained: MAE {metrics?.Mae:0.00}, RMSE {metrics?.Rmse:0.00}, R² {metrics?.R2:0.000}. All students re-scored.";
        return RedirectToPage();
    }

    /// <summary>Error histogram (predicted − actual) in 2-point bins from −14 to +14, labelled by bin centre.</summary>
    public (string[] Labels, int[] Counts) ErrorHistogram()
    {
        const int binWidth = 2, min = -14, bins = 14;
        var counts = new int[bins];
        foreach (var t in Metrics?.TestPredictions ?? [])
            counts[Math.Clamp((int)Math.Floor((t.Predicted - t.Actual - min) / binWidth), 0, bins - 1)]++;
        var labels = Enumerable.Range(0, bins).Select(i => $"{min + i * binWidth + binWidth / 2:+0;−0;0}").ToArray();
        return (labels, counts);
    }

    /// <summary>Background colour for a correlation cell: blue for positive, red for negative, stronger = more opaque.</summary>
    public static string HeatColor(double r) =>
        r >= 0 ? $"rgba(57,135,229,{0.08 + 0.72 * r:0.00})" : $"rgba(208,59,59,{0.08 + 0.72 * -r:0.00})";
}
