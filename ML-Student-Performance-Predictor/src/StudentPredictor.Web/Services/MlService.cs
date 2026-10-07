using System.Diagnostics;
using System.Text.Json;
using StudentPredictor.Web.Models;

namespace StudentPredictor.Web.Services;

/// <summary>
/// Bridge to the Python machine learning scripts. Each call starts a short-lived Python process and
/// exchanges JSON over stdin / stdout, so no second server has to be running.
/// </summary>
public class MlService(IConfiguration configuration, IWebHostEnvironment environment, ILogger<MlService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _python = configuration["MachineLearning:PythonExecutable"] ?? "python";
    private readonly string _scriptsFolder = Path.GetFullPath(Path.Combine(
        environment.ContentRootPath, configuration["MachineLearning:ScriptsFolder"] ?? "../../ml"));

    private string MetricsPath => Path.Combine(_scriptsFolder, "model", "metrics.json");
    private string ModelPath => Path.Combine(_scriptsFolder, "model", "model.joblib");

    public bool IsModelTrained => File.Exists(ModelPath) && File.Exists(MetricsPath);

    public async Task TrainAsync()
    {
        logger.LogInformation("Training model with {Script}", Path.Combine(_scriptsFolder, "train_model.py"));
        var output = await RunAsync("train_model.py", input: null);
        logger.LogInformation("Training finished:\n{Output}", output.Trim());
    }

    public async Task<List<PredictionResult>> PredictAsync(IReadOnlyList<StudentFeatures> students)
    {
        if (students.Count == 0) return [];
        var output = await RunAsync("predict.py", JsonSerializer.Serialize(students));
        return JsonSerializer.Deserialize<List<PredictionResult>>(output, Json)
               ?? throw new InvalidOperationException("The prediction script returned no data.");
    }

    public async Task<PredictionResult> PredictAsync(StudentFeatures student) =>
        (await PredictAsync([student]))[0];

    public ModelMetrics? GetMetrics()
    {
        if (!File.Exists(MetricsPath)) return null;
        return JsonSerializer.Deserialize<ModelMetrics>(File.ReadAllText(MetricsPath), Json);
    }

    private async Task<string> RunAsync(string script, string? input)
    {
        var startInfo = new ProcessStartInfo(_python, $"\"{Path.Combine(_scriptsFolder, script)}\"")
        {
            WorkingDirectory = _scriptsFolder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"Could not start '{_python}'.");
        if (input is not null) await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{script} failed (exit {process.ExitCode}): {await stderr}");
        return await stdout;
    }
}
