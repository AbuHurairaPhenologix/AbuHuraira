using System.Text.Json;
using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Domain.Entities;
using AnomalyDetection.Domain.Features;
using AnomalyDetection.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AnomalyDetection.UnitTests;

internal static class TestSupport
{
    public static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    public static AnomalyDetectionDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AnomalyDetectionDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public static FakeTimeProvider Clock() => new(new DateTimeOffset(Now));

    public static IOptions<PipelineOptions> Pipeline(Action<PipelineOptions>? configure = null)
    {
        var o = new PipelineOptions();
        configure?.Invoke(o);
        return Options.Create(o);
    }

    public static ModelVersion Model(string version = "ocsvm-ops-v1-test", bool active = true, bool eligible = true, string schema = "ops-v1", double threshold = 1.0, string sha = "abc123")
    {
        var m = new ModelVersion(Guid.NewGuid(), version, "ocsvm", schema, Now.AddDays(-30), Now.AddDays(-1), 42, "{}", "{}", threshold, "max_f1", $"{version}/model.joblib", sha, eligible, null, Now.AddDays(-1), Now, "test");
        if (active)
        {
            m.Activate("test", Now);
        }

        return m;
    }

    public static FeatureWindow Window(ServiceDefinition service, DateTime start, double avgDuration = 250) =>
        new(service, start, start.AddMinutes(5), new FeatureVector(140, 0.02, avgDuration, avgDuration * 2.6, 0.015, 0, 0, 2.3), 140, Now);

    public static RegistryModelMetadata Metadata(ModelVersion m, bool exists = true) => new(
        m.ModelId, m.Version, m.Algorithm, m.FeatureSchemaVersion, FeatureSchema.OrderedFeatureNames.ToList(), m.TrainingPeriodStartUtc, m.TrainingPeriodEndUtc,
        m.RandomSeed, JsonDocument.Parse("{}").RootElement, JsonDocument.Parse("{}").RootElement, m.ValidationThreshold, m.ThresholdObjective,
        m.ArtifactPath, m.ArtifactSha256, m.ProductionEligible, null, m.TrainedAtUtc, exists, true, false);
}

/// <summary>Test double for the ML service port (tests only).</summary>
internal sealed class FakeMlClient : IMlScoringClient
{
    public Func<IReadOnlyList<ScoreItem>, MlCallResult<IReadOnlyList<ScoreResult>>>? OnScore { get; set; }

    public Func<string, MlCallResult<RegistryModelMetadata>>? OnActivate { get; set; }

    public Func<string, MlCallResult<RegistryModelMetadata>>? OnGet { get; set; }

    public List<string> Activated { get; } = [];

    public List<string> Deactivated { get; } = [];

    public int ScoreCalls { get; private set; }

    public Task<MlCallResult<IReadOnlyList<ScoreResult>>> ScoreBatchAsync(string modelVersion, string schemaVersion, IReadOnlyList<ScoreItem> items, CancellationToken cancellationToken)
    {
        ScoreCalls++;
        return Task.FromResult(OnScore?.Invoke(items) ?? MlCallResult<IReadOnlyList<ScoreResult>>.Fail(MlCallStatus.Unavailable, "not configured"));
    }

    public Task<MlCallResult<IReadOnlyList<RegistryModelMetadata>>> ListRegistryAsync(CancellationToken cancellationToken) =>
        Task.FromResult(MlCallResult<IReadOnlyList<RegistryModelMetadata>>.Ok([]));

    public Task<MlCallResult<RegistryModelMetadata>> GetModelAsync(string modelVersion, CancellationToken cancellationToken) =>
        Task.FromResult(OnGet?.Invoke(modelVersion) ?? MlCallResult<RegistryModelMetadata>.Fail(MlCallStatus.ModelNotFound, "missing"));

    public Task<MlCallResult<RegistryModelMetadata>> ActivateModelAsync(string modelVersion, CancellationToken cancellationToken)
    {
        Activated.Add(modelVersion);
        return Task.FromResult(OnActivate?.Invoke(modelVersion) ?? MlCallResult<RegistryModelMetadata>.Fail(MlCallStatus.Unavailable, "down"));
    }

    public Task<MlCallResult<bool>> DeactivateModelAsync(string modelVersion, CancellationToken cancellationToken)
    {
        Deactivated.Add(modelVersion);
        return Task.FromResult(MlCallResult<bool>.Ok(true));
    }

    public Task<MlCallResult<TrainingJobStatus>> StartTrainingAsync(TrainingJobRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(MlCallResult<TrainingJobStatus>.Ok(new TrainingJobStatus("job1", "queued", request.Algorithm, [], null, TestSupport.Now, null)));

    public Task<MlCallResult<TrainingJobStatus>> GetTrainingJobAsync(string jobId, CancellationToken cancellationToken) =>
        Task.FromResult(MlCallResult<TrainingJobStatus>.Fail(MlCallStatus.ModelNotFound, "missing"));

    /// <summary>Scores by average latency: windows slower than 600 ms exceed threshold 1.0.</summary>
    public static MlCallResult<IReadOnlyList<ScoreResult>> LatencyScorer(IReadOnlyList<ScoreItem> items, string version = "ocsvm-ops-v1-test") =>
        MlCallResult<IReadOnlyList<ScoreResult>>.Ok(items.Select(i =>
        {
            var score = i.Features[FeatureSchema.AvgDurationMs] / 600d;
            return new ScoreResult(i.WindowId, score, 1.0, score >= 1.0, version, "ops-v1", score >= 1.0 ? "Elevated p95 response time compared with the model's baseline." : "Within baseline.", []);
        }).ToList());
}
