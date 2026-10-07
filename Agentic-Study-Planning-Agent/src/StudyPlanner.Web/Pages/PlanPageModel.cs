using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using StudyPlanner.Web.Data;
using StudyPlanner.Web.Models;
using StudyPlanner.Web.Services;

namespace StudyPlanner.Web.Pages;

/// <summary>Base for pages that show the current plan: loads the latest agent run and the subject colours.</summary>
public abstract class PlanPageModel(AppDbContext db, PlanningAgentService agent) : PageModel
{
    protected AppDbContext Db { get; } = db;
    protected PlanningAgentService Agent { get; } = agent;

    public PlanRun? Run { get; protected set; }
    public AgentResult? Result { get; protected set; }
    public List<Subject> Subjects { get; protected set; } = [];

    protected async Task LoadPlanAsync()
    {
        Run = await Agent.GetCurrentPlanAsync();
        Result = Run is null ? null : PlanningAgentService.ReadResult(Run);
        Subjects = await Db.Subjects.AsNoTracking().OrderBy(s => s.ExamDate).ToListAsync();
    }

    /// <summary>Lets the agent recalculate the plan after an input changed and reports what it did.</summary>
    protected async Task ReplanAsync(string trigger)
    {
        var run = await Agent.RunAsync(trigger);
        var result = PlanningAgentService.ReadResult(run);
        TempData["AgentMessage"] = $"{trigger}. The agent replanned the week: {result.Changes.Count} allocation change(s), " +
                                   $"{run.AllocatedHours:0.#} h allocated, {run.ConflictCount} conflict(s) handled.";
    }

    public string ColorOf(int subjectId)
    {
        var subject = Subjects.FirstOrDefault(s => s.Id == subjectId);
        return subject is null ? SubjectColors.Removed : SubjectColors.Get(subject.ColorIndex);
    }

    public static string PriorityCss(string priority) => priority.ToLowerInvariant();

    public static string WorkloadLabel(double ratio) => ratio switch
    {
        <= 0.8 => "Light",
        <= 1.0 => "Balanced",
        <= 1.3 => "Heavy",
        _ => "Overloaded",
    };
}
