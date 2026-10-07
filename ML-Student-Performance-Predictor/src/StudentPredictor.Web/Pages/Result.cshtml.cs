using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using StudentPredictor.Web.Data;
using StudentPredictor.Web.Models;
using StudentPredictor.Web.Services;

namespace StudentPredictor.Web.Pages;

public class ResultModel(AppDbContext db, MlService ml) : PageModel
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PredictionRecord? Prediction { get; private set; }
    public List<PredictionRecord> History { get; private set; } = [];
    public List<FeatureImpact> Impacts { get; private set; } = [];
    public List<string> Recommendations { get; private set; } = [];
    public double Intercept { get; private set; }
    public double AverageStudentScore { get; private set; }
    public double Rmse { get; private set; }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        History = await db.Predictions.AsNoTracking().OrderByDescending(p => p.CreatedAt).ToListAsync();
        Prediction = id is null ? History.FirstOrDefault() : History.FirstOrDefault(p => p.Id == id);
        if (id is not null && Prediction is null) return NotFound();
        if (Prediction is null) return Page();

        var metrics = ml.GetMetrics();
        if (metrics is null) return Page();
        Rmse = metrics.Rmse;
        Intercept = metrics.Intercept;
        var stats = metrics.Statistics.ToDictionary(s => s.Feature);
        var contributions = JsonSerializer.Deserialize<List<Contribution>>(Prediction.ContributionsJson, Json) ?? [];

        // Impact = how many points this feature adds compared with an average student: w · (x − mean).
        Impacts = contributions.Select(c =>
        {
            var s = stats[c.Feature];
            return new FeatureImpact(c.Label, c.Value, s.Mean, s.Std, (c.Value - s.Mean) / s.Std,
                c.Weight * (c.Value - s.Mean), c.Points);
        }).ToList();
        AverageStudentScore = metrics.Intercept + contributions.Sum(c => c.Weight * stats[c.Feature].Mean);
        Recommendations = BuildRecommendations(Prediction, metrics.Coefficients.ToDictionary(c => c.Feature, c => c.Weight));
        return Page();
    }

    private static List<string> BuildRecommendations(PredictionRecord p, Dictionary<string, double> weights)
    {
        var tips = new List<string>();
        if (p.Attendance < 75) tips.Add($"Attendance is {p.Attendance:0}%. Raising it to 85% is worth about {weights["attendance"] * (85 - p.Attendance):0.0} points.");
        if (p.StudyHours < 8) tips.Add($"Only {p.StudyHours:0.#} study hours per week. Each extra weekly hour adds roughly {weights["study_hours"]:0.0} points; aim for 10–12 hours.");
        if (p.AssignmentAvg < 60) tips.Add("Assignment average is below 60. Book office hours and submit drafts early for feedback.");
        if (p.QuizAvg < 55) tips.Add("Quiz average is low. Weekly revision of lecture material should lift quiz results.");
        if (p.PreviousExam < 50) tips.Add("Weak previous exam result. Review the earlier topics that later material builds on.");
        if (p.Category == "At Risk") tips.Add("Flag this student to the academic advisor for an early-support meeting.");
        if (tips.Count == 0) tips.Add("All indicators are on track. Keep the current study routine up to the exam.");
        return tips;
    }

    public record FeatureImpact(string Label, double Value, double Mean, double Std, double ZScore, double ImpactPoints,
        double ContributionPoints);
}
