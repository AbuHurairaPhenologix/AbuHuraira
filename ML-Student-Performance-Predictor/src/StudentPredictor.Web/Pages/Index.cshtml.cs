using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using StudentPredictor.Web.Data;
using StudentPredictor.Web.Models;
using StudentPredictor.Web.Services;

namespace StudentPredictor.Web.Pages;

public class IndexModel(AppDbContext db, MlService ml) : PageModel
{
    public static readonly string[] Categories = ["Excellent", "Good", "Average", "At Risk"];

    public int TotalStudents { get; private set; }
    public double AveragePredicted { get; private set; }
    public double AverageActual { get; private set; }
    public int AtRisk { get; private set; }
    public ModelMetrics? Metrics { get; private set; }
    public Dictionary<string, int> CategoryCounts { get; private set; } = [];
    public List<PredictionRecord> RecentPredictions { get; private set; } = [];
    public List<Student> AtRiskStudents { get; private set; } = [];

    /// <summary>Histogram of predicted scores in 5-point bins from 30 to 100 (edges line up with the category bands).</summary>
    public int[] ScoreHistogram { get; private set; } = new int[14];

    public async Task OnGetAsync()
    {
        var students = await db.Students.AsNoTracking().ToListAsync();
        TotalStudents = students.Count;
        AveragePredicted = students.Average(s => s.PredictedScore ?? 0);
        AverageActual = students.Average(s => s.FinalScore);
        AtRisk = students.Count(s => s.Category == "At Risk");
        CategoryCounts = Categories.ToDictionary(c => c, c => students.Count(s => s.Category == c));
        foreach (var s in students)
            ScoreHistogram[Math.Clamp((int)(((s.PredictedScore ?? 0) - 30) / 5), 0, 13)]++;

        AtRiskStudents = students.Where(s => s.Category == "At Risk")
            .OrderBy(s => s.PredictedScore).Take(5).ToList();
        RecentPredictions = await db.Predictions.AsNoTracking()
            .OrderByDescending(p => p.CreatedAt).Take(5).ToListAsync();
        Metrics = ml.GetMetrics();
    }
}
