namespace StudentPredictor.Web.Models;

/// <summary>A student from the dataset, with the model's prediction stored next to the real result.</summary>
public class Student
{
    public int Id { get; set; }
    public string StudentNumber { get; set; } = "";
    public string Name { get; set; } = "";
    public string Programme { get; set; } = "";

    public double Attendance { get; set; }
    public double StudyHours { get; set; }
    public double AssignmentAvg { get; set; }
    public double QuizAvg { get; set; }
    public double PreviousExam { get; set; }

    /// <summary>Actual final exam score from the dataset.</summary>
    public double FinalScore { get; set; }

    public double? PredictedScore { get; set; }
    public string? Category { get; set; }

    public StudentFeatures ToFeatures() => new(Attendance, StudyHours, AssignmentAvg, QuizAvg, PreviousExam);
}

/// <summary>A prediction requested through the prediction form.</summary>
public class PredictionRecord
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string StudentName { get; set; } = "";

    public double Attendance { get; set; }
    public double StudyHours { get; set; }
    public double AssignmentAvg { get; set; }
    public double QuizAvg { get; set; }
    public double PreviousExam { get; set; }

    public double PredictedScore { get; set; }
    public string Category { get; set; } = "";
    public double Lower { get; set; }
    public double Upper { get; set; }

    /// <summary>Per-feature contributions returned by the model, stored as JSON.</summary>
    public string ContributionsJson { get; set; } = "[]";

    public StudentFeatures ToFeatures() => new(Attendance, StudyHours, AssignmentAvg, QuizAvg, PreviousExam);
}
