using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyPlanner.Web.Data;
using StudyPlanner.Web.Models;
using StudyPlanner.Web.Services;

namespace StudyPlanner.Web.Pages;

public class ActivityModel(AppDbContext db, PlanningAgentService agent) : PlanPageModel(db, agent)
{
    public List<PlanRun> Runs { get; private set; } = [];
    public PlanRun? Selected { get; private set; }
    public AgentResult? SelectedResult { get; private set; }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        await LoadPlanAsync();
        Runs = await Db.PlanRuns.AsNoTracking().OrderByDescending(r => r.Id).Take(30).ToListAsync();
        Selected = id is null ? Runs.FirstOrDefault() : await Db.PlanRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (id is not null && Selected is null) return NotFound();
        SelectedResult = Selected is null ? null : PlanningAgentService.ReadResult(Selected);
        return Page();
    }
}
