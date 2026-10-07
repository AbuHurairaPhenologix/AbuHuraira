using System.Text.Json.Serialization;

namespace StudentPredictor.Web.Models;

/// <summary>The five model inputs. Property names match the Python feature names.</summary>
public record StudentFeatures(
    [property: JsonPropertyName("attendance")] double Attendance,
    [property: JsonPropertyName("study_hours")] double StudyHours,
    [property: JsonPropertyName("assignment_avg")] double AssignmentAvg,
    [property: JsonPropertyName("quiz_avg")] double QuizAvg,
    [property: JsonPropertyName("previous_exam")] double PreviousExam);

public record Contribution(string Feature, string Label, double Value, double Weight, double Points);

public record PredictionResult(
    double PredictedScore,
    string Category,
    double Lower,
    double Upper,
    double Intercept,
    List<Contribution> Contributions);

public record FeatureStatistic(string Feature, string Label, double Mean, double Std, double Min, double Max, double Correlation);

public record Coefficient(string Feature, string Label, double Weight);

public record CorrelationMatrix(List<string> Features, List<List<double>> Values);

public record TestPrediction(double Actual, double Predicted);

/// <summary>Evaluation output written by ml/train_model.py (model/metrics.json).</summary>
public record ModelMetrics(
    DateTime TrainedAt,
    string Algorithm,
    int Rows,
    int TrainRows,
    int TestRows,
    double Mae,
    double Rmse,
    double R2,
    double BaselineMae,
    double CategoryAccuracy,
    double MeanError,
    double ErrorStd,
    double Intercept,
    List<Coefficient> Coefficients,
    List<FeatureStatistic> Statistics,
    CorrelationMatrix CorrelationMatrix,
    List<TestPrediction> TestPredictions);
