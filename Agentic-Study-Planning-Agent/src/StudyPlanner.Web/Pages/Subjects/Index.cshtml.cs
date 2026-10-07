using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using StudyPlanner.Web.Data;
using StudyPlanner.Web.Models;
using StudyPlanner.Web.Services;

namespace StudyPlanner.Web.Pages.Subjects;

public class IndexModel(AppDbContext db, PlanningAgentService agent) : PlanPageModel(db, agent)
{
    public static readonly string[] Priorities = ["Low", "Medium", "High", "Critical"];

    [BindProperty]
    public Subject NewSubject { get; set; } = new() { ExamDate = PlanningAgentService.Today.AddDays(14) };

    public async Task OnGetAsync() => await LoadPlanAsync();

    public async Task<IActionResult> OnPostAddAsync()
    {
        ValidateSubject(NewSubject, ModelState, nameof(NewSubject));
        if (!ModelState.IsValid)
        {
            await LoadPlanAsync();
            return Page();
        }

        NewSubject.ColorIndex = SubjectColors.NextIndex(await Db.Subjects.Select(s => s.ColorIndex).ToListAsync());
        Db.Subjects.Add(NewSubject);
        await Db.SaveChangesAsync();
        await ReplanAsync($"Subject added: {NewSubject.Name}");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostProgressAsync(int id, int progress)
    {
        var subject = await Db.Subjects.FindAsync(id);
        if (subject is null) return NotFound();
        var old = subject.Progress;
        subject.Progress = Math.Clamp(progress, 0, 100);
        if (old == subject.Progress) return RedirectToPage();
        await Db.SaveChangesAsync();
        await ReplanAsync($"Progress updated: {subject.Name} {old}% → {subject.Progress}%");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var subject = await Db.Subjects.FindAsync(id);
        if (subject is null) return NotFound();
        Db.Subjects.Remove(subject);
        await Db.SaveChangesAsync();
        await ReplanAsync($"Subject removed: {subject.Name}");
        return RedirectToPage();
    }

    public static void ValidateSubject(Subject subject, ModelStateDictionary state, string prefix)
    {
        if (!Priorities.Contains(subject.Priority)) state.AddModelError($"{prefix}.Priority", "Choose a priority.");
        if (subject.ExamDate < PlanningAgentService.Today)
            state.AddModelError($"{prefix}.ExamDate", "The exam date cannot be in the past.");
    }
}
