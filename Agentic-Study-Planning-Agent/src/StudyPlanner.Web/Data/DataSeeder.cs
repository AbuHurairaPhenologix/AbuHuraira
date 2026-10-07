using Microsoft.EntityFrameworkCore;
using StudyPlanner.Web.Models;
using StudyPlanner.Web.Services;

namespace StudyPlanner.Web.Data;

/// <summary>Creates the database with a realistic exam-season scenario and lets the agent build the first plans.</summary>
public class DataSeeder(AppDbContext db, PlanningAgentService agent, IWebHostEnvironment environment)
{
    public async Task SeedAsync()
    {
        Directory.CreateDirectory(Path.Combine(environment.ContentRootPath, "App_Data"));
        await db.Database.EnsureCreatedAsync();
        if (await db.Subjects.AnyAsync()) return;

        var today = PlanningAgentService.Today;
        db.Subjects.AddRange(
            new Subject { Name = "Linear Algebra", Difficulty = 4, Priority = "High", ExamDate = today.AddDays(5), Progress = 50, TotalHours = 20, ColorIndex = 0 },
            new Subject { Name = "Data Structures & Algorithms", Difficulty = 5, Priority = "Critical", ExamDate = today.AddDays(9), Progress = 45, TotalHours = 36, ColorIndex = 1 },
            new Subject { Name = "Database Systems", Difficulty = 3, Priority = "Medium", ExamDate = today.AddDays(12), Progress = 55, TotalHours = 20, ColorIndex = 2 },
            new Subject { Name = "Operating Systems", Difficulty = 4, Priority = "High", ExamDate = today.AddDays(4), Progress = 45, TotalHours = 16, ColorIndex = 3 },
            new Subject { Name = "Probability & Statistics", Difficulty = 3, Priority = "Medium", ExamDate = today.AddDays(16), Progress = 40, TotalHours = 24, ColorIndex = 4 },
            new Subject { Name = "Technical Writing", Difficulty = 1, Priority = "Low", ExamDate = today.AddDays(25), Progress = 60, TotalHours = 8, ColorIndex = 5 });

        // Weekday evenings after lectures, longer sessions at the weekend.
        (int Day, double Hours, string Start)[] week =
            [(0, 4, "18:00"), (1, 4, "18:00"), (2, 3.5, "18:30"), (3, 4, "18:00"), (4, 2.5, "17:00"), (5, 6, "10:00"), (6, 5, "11:00")];
        db.Availability.AddRange(week.Select(w => new DayAvailability { DayOfWeek = w.Day, Hours = w.Hours, StartTime = w.Start }));
        await db.SaveChangesAsync();

        await agent.RunAsync("Initial plan for the exam period");

        // A realistic follow-up: the student reports progress and the agent replans.
        var os = await db.Subjects.SingleAsync(s => s.Name == "Operating Systems");
        os.Progress = 60;
        await db.SaveChangesAsync();
        await agent.RunAsync("Progress updated: Operating Systems 45% → 60%");
    }
}
