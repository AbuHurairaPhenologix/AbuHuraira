using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyPlanner.Web.Data;
using StudyPlanner.Web.Models;
using StudyPlanner.Web.Services;

namespace StudyPlanner.Web.Pages;

public class PlanModel(AppDbContext db, PlanningAgentService agent) : PlanPageModel(db, agent)
{
    public static readonly string[] DayNames = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    /// <summary>The fixed decision cycle, in the order the agent executes it.</summary>
    public static readonly (string Action, string Purpose)[] Cycle =
    [
        ("Analyze Subjects", "Remaining work, days to exam, hours needed this week"),
        ("Calculate Priority", "Priority × Difficulty × Urgency"),
        ("Check Available Time", "Free hours per day, workload ratio"),
        ("Detect Conflicts", "Deadline overload, clashes, low progress"),
        ("Allocate Study Hours", "Reserve for exams, share the rest by score"),
        ("Generate Weekly Plan", "Least-slack-first sessions with breaks"),
        ("Replan Schedule", "Compare with the previous plan, explain changes"),
    ];

    [BindProperty]
    public List<DayAvailability> Availability { get; set; } = [];

    public async Task OnGetAsync()
    {
        await LoadPlanAsync();
        Availability = await Db.Availability.AsNoTracking().OrderBy(a => a.DayOfWeek).ToListAsync();
    }

    public async Task<IActionResult> OnPostRunAsync()
    {
        await ReplanAsync("Manual run requested from the planner");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAvailabilityAsync()
    {
        var stored = await Db.Availability.ToDictionaryAsync(a => a.DayOfWeek);
        var changes = new List<string>();
        foreach (var day in Availability.Where(d => d.DayOfWeek is >= 0 and <= 6))
        {
            var hours = Math.Round(Math.Clamp(day.Hours, 0, 14) * 2) / 2;  // half-hour steps
            var start = TimeOnly.TryParse(day.StartTime, out var t) ? t.ToString("HH:mm") : "18:00";
            if (!stored.TryGetValue(day.DayOfWeek, out var entity))
            {
                entity = new DayAvailability { DayOfWeek = day.DayOfWeek };
                Db.Availability.Add(entity);
            }
            if (entity.Hours != hours) changes.Add($"{DayNames[day.DayOfWeek][..3]} {entity.Hours:0.#} h → {hours:0.#} h");
            else if (entity.StartTime != start) changes.Add($"{DayNames[day.DayOfWeek][..3]} starts {start}");
            entity.Hours = hours;
            entity.StartTime = start;
        }
        await Db.SaveChangesAsync();

        if (changes.Count > 0) await ReplanAsync($"Availability changed: {string.Join(", ", changes)}");
        return RedirectToPage();
    }
}
