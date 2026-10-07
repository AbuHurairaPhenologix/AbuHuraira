using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StudyPlanner.Web.Data;
using StudyPlanner.Web.Models;

namespace StudyPlanner.Web.Services;

/// <summary>
/// Runs the Python planning agent. Every change to subjects or availability calls <see cref="RunAsync"/>,
/// so the plan is always recalculated from the current inputs and the previous plan.
/// </summary>
public class PlanningAgentService(AppDbContext db, IConfiguration configuration, IWebHostEnvironment environment,
    ILogger<PlanningAgentService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    public async Task<PlanRun?> GetLatestRunAsync() =>
        await db.PlanRuns.AsNoTracking().OrderByDescending(r => r.Id).FirstOrDefaultAsync();

    public static AgentResult ReadResult(PlanRun run) =>
        JsonSerializer.Deserialize<AgentResult>(run.ResultJson, Json)!;

    /// <summary>Returns the latest plan, replanning first if the planning window has moved to a new day.</summary>
    public async Task<PlanRun?> GetCurrentPlanAsync()
    {
        var latest = await GetLatestRunAsync();
        if (latest is not null && latest.GeneratedFor != Today)
            latest = await RunAsync($"New day: planning window moved from {latest.GeneratedFor:dd MMM} to {Today:dd MMM}");
        return latest;
    }

    public async Task<PlanRun> RunAsync(string trigger)
    {
        var subjects = await db.Subjects.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
        var availability = await db.Availability.AsNoTracking().OrderBy(a => a.DayOfWeek).ToListAsync();
        var previous = await GetLatestRunAsync();

        var agentSubjects = subjects.Select(ToAgentSubject).ToList();
        var request = new AgentRequest(
            Today.ToString("yyyy-MM-dd"),
            trigger,
            availability.Select(a => new AgentAvailability(a.DayOfWeek, a.Hours, a.StartTime)).ToList(),
            agentSubjects,
            previous is null ? null : new AgentPrevious(
                ReadResult(previous).Subjects.Select(s => new AgentAllocation(s.Id, s.AllocatedHours)).ToList(),
                JsonSerializer.Deserialize<List<AgentSubject>>(previous.InputSubjectsJson, Json) ?? []));

        var output = await RunPythonAsync(JsonSerializer.Serialize(request, Json));
        var result = JsonSerializer.Deserialize<AgentResult>(output, Json)
                     ?? throw new InvalidOperationException("The planning agent returned no data.");

        var run = new PlanRun
        {
            CreatedAt = DateTime.Now,
            Trigger = trigger,
            IsReplan = previous is not null,
            GeneratedFor = Today,
            AvailableHours = result.Summary.AvailableHours,
            AllocatedHours = result.Summary.AllocatedHours,
            WorkloadRatio = result.Summary.WorkloadRatio,
            TopSubject = result.Summary.TopSubject,
            ConflictCount = result.Summary.ConflictCount,
            ResultJson = output,
            InputSubjectsJson = JsonSerializer.Serialize(agentSubjects, Json),
        };
        db.PlanRuns.Add(run);
        await db.SaveChangesAsync();
        logger.LogInformation("Agent run #{Id} ({Trigger}): {Allocated} h allocated, {Conflicts} conflicts",
            run.Id, trigger, run.AllocatedHours, run.ConflictCount);
        return run;
    }

    private static AgentSubject ToAgentSubject(Subject s) =>
        new(s.Id, s.Name, s.Difficulty, s.Priority, s.ExamDate.ToString("yyyy-MM-dd"), s.Progress, s.TotalHours);

    private async Task<string> RunPythonAsync(string input)
    {
        var python = configuration["Agent:PythonExecutable"] ?? "python";
        var script = Path.GetFullPath(Path.Combine(environment.ContentRootPath,
            configuration["Agent:Script"] ?? "../../planner/agent.py"));

        var startInfo = new ProcessStartInfo(python, $"\"{script}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{python}'.");
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"agent.py failed (exit {process.ExitCode}): {await stderr}");
        return await stdout;
    }
}
