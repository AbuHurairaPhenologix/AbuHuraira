using Microsoft.AspNetCore.Mvc;
using StudyPlanner.Web.Data;
using StudyPlanner.Web.Models;
using StudyPlanner.Web.Services;

namespace StudyPlanner.Web.Pages.Subjects;

public class EditModel(AppDbContext db, PlanningAgentService agent) : PlanPageModel(db, agent)
{
    [BindProperty]
    public Subject Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var subject = await Db.Subjects.FindAsync(id);
        if (subject is null) return NotFound();
        Input = subject;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var subject = await Db.Subjects.FindAsync(id);
        if (subject is null) return NotFound();
        IndexModel.ValidateSubject(Input, ModelState, nameof(Input));
        if (!ModelState.IsValid) return Page();

        var changes = new List<string>();
        if (subject.ExamDate != Input.ExamDate) changes.Add($"exam {subject.ExamDate:dd MMM} → {Input.ExamDate:dd MMM}");
        if (subject.Priority != Input.Priority) changes.Add($"priority {subject.Priority} → {Input.Priority}");
        if (subject.Difficulty != Input.Difficulty) changes.Add($"difficulty {subject.Difficulty} → {Input.Difficulty}");
        if (subject.Progress != Input.Progress) changes.Add($"progress {subject.Progress}% → {Input.Progress}%");
        if (subject.TotalHours != Input.TotalHours) changes.Add($"total hours {subject.TotalHours:0.#} → {Input.TotalHours:0.#}");
        if (subject.Name != Input.Name) changes.Add($"renamed to {Input.Name}");

        subject.Name = Input.Name;
        subject.ExamDate = Input.ExamDate;
        subject.Priority = Input.Priority;
        subject.Difficulty = Input.Difficulty;
        subject.Progress = Input.Progress;
        subject.TotalHours = Input.TotalHours;
        await Db.SaveChangesAsync();

        if (changes.Count > 0) await ReplanAsync($"Subject updated: {subject.Name} ({string.Join(", ", changes)})");
        return RedirectToPage("/Subjects/Index");
    }
}
