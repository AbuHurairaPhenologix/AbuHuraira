using System.ComponentModel.DataAnnotations;

namespace StudyPlanner.Web.Models;

public class Subject
{
    public int Id { get; set; }

    [Required, StringLength(60)]
    public string Name { get; set; } = "";

    /// <summary>1 = easy … 5 = very hard.</summary>
    [Range(1, 5)]
    public int Difficulty { get; set; } = 3;

    /// <summary>Low, Medium, High or Critical.</summary>
    [Required]
    public string Priority { get; set; } = "Medium";

    [Display(Name = "Exam / deadline")]
    public DateOnly ExamDate { get; set; }

    /// <summary>Share of the syllabus already covered (0–100 %).</summary>
    [Range(0, 100)]
    public int Progress { get; set; }

    /// <summary>Estimated total study hours the whole subject needs.</summary>
    [Range(1, 200), Display(Name = "Total study hours")]
    public double TotalHours { get; set; } = 20;

    /// <summary>Fixed colour slot, so a subject keeps its colour when others are added or removed.</summary>
    public int ColorIndex { get; set; }
}

/// <summary>Hours a student can study on one weekday.</summary>
public class DayAvailability
{
    /// <summary>0 = Monday … 6 = Sunday (same convention as Python's date.weekday()).</summary>
    [Key]
    public int DayOfWeek { get; set; }

    [Range(0, 14)]
    public double Hours { get; set; }

    /// <summary>"HH:mm" — when the first study session of the day starts.</summary>
    public string StartTime { get; set; } = "18:00";
}

/// <summary>One run of the planning agent: its trigger, its full JSON output and the inputs it saw.</summary>
public class PlanRun
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Trigger { get; set; } = "";
    public bool IsReplan { get; set; }
    public DateOnly GeneratedFor { get; set; }

    public double AvailableHours { get; set; }
    public double AllocatedHours { get; set; }
    public double WorkloadRatio { get; set; }
    public string? TopSubject { get; set; }
    public int ConflictCount { get; set; }

    public string ResultJson { get; set; } = "{}";
    public string InputSubjectsJson { get; set; } = "[]";
}
