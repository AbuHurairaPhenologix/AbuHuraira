using StudyPlanner.Web.Data;
using StudyPlanner.Web.Services;

namespace StudyPlanner.Web.Pages;

public class ScheduleModel(AppDbContext db, PlanningAgentService agent) : PlanPageModel(db, agent)
{
    public async Task OnGetAsync() => await LoadPlanAsync();
}
