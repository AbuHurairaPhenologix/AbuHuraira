namespace StudyPlanner.Web.Models;

// ---- request sent to planner/agent.py ----

public record AgentSubject(int Id, string Name, int Difficulty, string Priority, string ExamDate, int Progress, double TotalHours);

public record AgentAvailability(int DayOfWeek, double Hours, string StartTime);

public record AgentAllocation(int SubjectId, double Hours);

public record AgentPrevious(List<AgentAllocation> Allocations, List<AgentSubject> Subjects);

public record AgentRequest(
    string Today,
    string Trigger,
    List<AgentAvailability> Availability,
    List<AgentSubject> Subjects,
    AgentPrevious? Previous);

// ---- result returned by the agent ----

public record AgentStep(string Action, string Status, string Summary, List<string> Details);

public record PlanSummary(
    double AvailableHours,
    double AllocatedHours,
    double WeeklyDemand,
    double WorkloadRatio,
    string? TopSubject,
    double TopScore,
    int ConflictCount,
    int SessionCount);

public record SubjectPlan(
    int Id,
    string Name,
    string Status,
    int DaysLeft,
    double RemainingHours,
    double WeeklyDemand,
    double PriorityWeight,
    double DifficultyWeight,
    double UrgencyWeight,
    double Score,
    double AllocatedHours,
    double Coverage);

public record StudySession(int SubjectId, string Subject, string Date, string Start, string End, double Hours);

public record Conflict(string Type, string Severity, string Message, string Resolution);

public record PlanChange(int SubjectId, string Subject, double Before, double After, double Delta, string Reason);

public record AgentResult(
    string GeneratedFor,
    List<string> Days,
    List<double> Capacity,
    PlanSummary Summary,
    List<SubjectPlan> Subjects,
    List<StudySession> Sessions,
    List<Conflict> Conflicts,
    List<AgentStep> Steps,
    List<PlanChange> Changes);
