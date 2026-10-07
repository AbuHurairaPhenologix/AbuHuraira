using StudyPlanner.Web.Data;
using StudyPlanner.Web.Models;
using StudyPlanner.Web.Services;

namespace StudyPlanner.Web.Pages;

public class IndexModel(AppDbContext db, PlanningAgentService agent) : PlanPageModel(db, agent)
{
    public List<Subject> UpcomingDeadlines { get; private set; } = [];
    public List<StudySession> TodaySessions { get; private set; } = [];

    public async Task OnGetAsync()
    {
        await LoadPlanAsync();
        var today = PlanningAgentService.Today;
        UpcomingDeadlines = Subjects.Where(s => s.ExamDate >= today).OrderBy(s => s.ExamDate).Take(5).ToList();
        TodaySessions = Result?.Sessions.Where(s => s.Date == today.ToString("yyyy-MM-dd")).ToList() ?? [];
    }
}
