using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StudentPredictor.Web.Models;
using StudentPredictor.Web.Services;

namespace StudentPredictor.Web.Data;

/// <summary>Creates the database, imports the CSV dataset, trains the model and scores every student.</summary>
public class DataSeeder(AppDbContext db, MlService ml, IConfiguration configuration, IWebHostEnvironment environment,
    ILogger<DataSeeder> logger)
{
    public async Task SeedAsync()
    {
        Directory.CreateDirectory(Path.Combine(environment.ContentRootPath, "App_Data"));
        await db.Database.EnsureCreatedAsync();

        if (!await db.Students.AnyAsync())
        {
            var students = ReadDataset();
            db.Students.AddRange(students);
            await db.SaveChangesAsync();
            logger.LogInformation("Imported {Count} students from the dataset", students.Count);
        }

        if (!ml.IsModelTrained) await ml.TrainAsync();

        if (await db.Students.AnyAsync(s => s.PredictedScore == null)) await ScoreAllStudentsAsync();

        if (!await db.Predictions.AnyAsync()) await AddSamplePredictionsAsync();
    }

    /// <summary>Runs the current model over every student in the dataset.</summary>
    public async Task ScoreAllStudentsAsync()
    {
        var students = await db.Students.OrderBy(s => s.Id).ToListAsync();
        var results = await ml.PredictAsync(students.Select(s => s.ToFeatures()).ToList());
        foreach (var (student, result) in students.Zip(results))
        {
            student.PredictedScore = result.PredictedScore;
            student.Category = result.Category;
        }
        await db.SaveChangesAsync();
        logger.LogInformation("Scored {Count} students with the trained model", students.Count);
    }

    public async Task<PredictionRecord> SavePredictionAsync(string studentName, StudentFeatures features, DateTime createdAt)
    {
        var result = await ml.PredictAsync(features);
        var record = new PredictionRecord
        {
            CreatedAt = createdAt,
            StudentName = studentName,
            Attendance = features.Attendance,
            StudyHours = features.StudyHours,
            AssignmentAvg = features.AssignmentAvg,
            QuizAvg = features.QuizAvg,
            PreviousExam = features.PreviousExam,
            PredictedScore = result.PredictedScore,
            Category = result.Category,
            Lower = result.Lower,
            Upper = result.Upper,
            ContributionsJson = JsonSerializer.Serialize(result.Contributions),
        };
        db.Predictions.Add(record);
        await db.SaveChangesAsync();
        return record;
    }

    private async Task AddSamplePredictionsAsync()
    {
        var now = DateTime.Now;
        (string Name, StudentFeatures Features, int HoursAgo)[] samples =
        [
            ("Sara Weber", new StudentFeatures(96, 18, 90, 88, 84), 50),
            ("Omar Iqbal", new StudentFeatures(68, 4, 55, 49, 47), 30),
            ("Lena Fischer", new StudentFeatures(85, 11, 74, 70, 66), 20),
            ("Daniel Rossi", new StudentFeatures(74, 7, 63, 58, 61), 6),
        ];
        foreach (var (name, features, hoursAgo) in samples)
            await SavePredictionAsync(name, features, now.AddHours(-hoursAgo));
    }

    private List<Student> ReadDataset()
    {
        var folder = Path.GetFullPath(Path.Combine(environment.ContentRootPath,
            configuration["MachineLearning:ScriptsFolder"] ?? "../../ml"));
        var lines = File.ReadAllLines(Path.Combine(folder, "data", "students.csv"));
        var header = lines[0].Split(',');
        int Col(string name) => Array.IndexOf(header, name);
        double Num(string[] row, string name) => double.Parse(row[Col(name)], CultureInfo.InvariantCulture);

        return lines.Skip(1).Where(l => l.Length > 0).Select(line =>
        {
            var row = line.Split(',');
            return new Student
            {
                StudentNumber = row[Col("student_id")],
                Name = row[Col("name")],
                Programme = row[Col("programme")],
                Attendance = Num(row, "attendance"),
                StudyHours = Num(row, "study_hours"),
                AssignmentAvg = Num(row, "assignment_avg"),
                QuizAvg = Num(row, "quiz_avg"),
                PreviousExam = Num(row, "previous_exam"),
                FinalScore = Num(row, "final_score"),
            };
        }).ToList();
    }
}
