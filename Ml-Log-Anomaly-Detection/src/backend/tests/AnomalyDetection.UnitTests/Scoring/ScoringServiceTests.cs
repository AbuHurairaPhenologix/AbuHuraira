using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Scoring;
using AnomalyDetection.Domain.Entities;
using AnomalyDetection.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnomalyDetection.UnitTests.Scoring;

public sealed class ScoringServiceTests
{
    private readonly ServiceDefinition _service = new("orders-api", "production", TestSupport.Now);

    private (ScoringService Service, Infrastructure.Persistence.AnomalyDetectionDbContext Db, FakeMlClient Ml, ModelVersion Model) Setup(bool withModel = true)
    {
        var db = TestSupport.NewDb();
        var ml = new FakeMlClient();
        var model = TestSupport.Model();
        db.Services.Add(_service);
        if (withModel)
        {
            db.ModelVersions.Add(model);
        }

        db.FeatureWindows.Add(TestSupport.Window(_service, TestSupport.Now.AddHours(-1)));
        db.FeatureWindows.Add(TestSupport.Window(_service, TestSupport.Now.AddHours(-1).AddMinutes(5), avgDuration: 900));
        db.SaveChanges();
        var svc = new ScoringService(db, ml, TestSupport.Clock(), TestSupport.Pipeline(), NullLogger<ScoringService>.Instance);
        return (svc, db, ml, model);
    }

    [Fact]
    public async Task Scores_windows_persists_threshold_and_creates_anomaly_only_above_threshold()
    {
        var (svc, db, ml, model) = Setup();
        ml.OnScore = items => FakeMlClient.LatencyScorer(items);

        var result = await svc.RunOnceAsync(CancellationToken.None);

        Assert.Equal(ScoringRunOutcome.Scored, result.Outcome);
        Assert.Equal(2, result.WindowsScored);
        Assert.Equal(1, result.AnomaliesCreated);
        var records = await db.ScoringRecords.ToListAsync();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal(1.0, r.Threshold));
        Assert.All(records, r => Assert.Equal(model.ModelId, r.ModelId));
        var anomaly = await db.Anomalies.SingleAsync();
        Assert.Equal(1.5, anomaly.Score, 9);
        Assert.Equal(1.0, anomaly.Threshold);
        Assert.Equal(model.Version, anomaly.ModelVersion);
        Assert.Equal(ReviewState.Unreviewed, anomaly.ReviewState);
        Assert.All(await db.FeatureWindows.ToListAsync(), w => Assert.Equal(ScoringStatus.Scored, w.ScoringStatus));
    }

    [Fact]
    public async Task TC04_ml_unavailable_defers_scoring_with_backoff_and_nothing_is_lost()
    {
        var (svc, db, ml, _) = Setup();
        ml.OnScore = _ => MlCallResult<IReadOnlyList<ScoreResult>>.Fail(MlCallStatus.Unavailable, "TimeoutRejectedException: timeout");

        var result = await svc.RunOnceAsync(CancellationToken.None);

        Assert.Equal(ScoringRunOutcome.Deferred, result.Outcome);
        var windows = await db.FeatureWindows.ToListAsync();
        Assert.All(windows, w =>
        {
            Assert.Equal(ScoringStatus.Deferred, w.ScoringStatus);
            Assert.Equal(1, w.ScoringAttempts);
            Assert.True(w.NextScoringAttemptUtc > TestSupport.Now);
            Assert.Contains("Unavailable", w.LastScoringError);
        });
        Assert.Empty(db.ScoringRecords);

        // Not retried before the back-off elapses...
        Assert.Equal(ScoringRunOutcome.NothingToScore, (await svc.RunOnceAsync(CancellationToken.None)).Outcome);
        Assert.Equal(1, ml.ScoreCalls);
    }

    [Fact]
    public async Task Deferred_windows_are_scored_after_recovery()
    {
        var db = TestSupport.NewDb();
        var ml = new FakeMlClient();
        var clock = TestSupport.Clock();
        db.Services.Add(_service);
        db.ModelVersions.Add(TestSupport.Model());
        db.FeatureWindows.Add(TestSupport.Window(_service, TestSupport.Now.AddHours(-1)));
        db.SaveChanges();
        var svc = new ScoringService(db, ml, clock, TestSupport.Pipeline(), NullLogger<ScoringService>.Instance);

        ml.OnScore = _ => MlCallResult<IReadOnlyList<ScoreResult>>.Fail(MlCallStatus.Unavailable, "down");
        await svc.RunOnceAsync(CancellationToken.None);
        ml.OnScore = items => FakeMlClient.LatencyScorer(items);
        clock.Advance(TimeSpan.FromMinutes(1));
        var result = await svc.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.WindowsScored);
        Assert.Equal(ScoringStatus.Scored, (await db.FeatureWindows.SingleAsync()).ScoringStatus);
    }

    [Fact]
    public async Task No_active_model_leaves_windows_pending()
    {
        var (svc, db, ml, _) = Setup(withModel: false);
        var result = await svc.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ScoringRunOutcome.NoActiveModel, result.Outcome);
        Assert.Equal(0, ml.ScoreCalls);
        Assert.All(await db.FeatureWindows.ToListAsync(), w => Assert.Equal(ScoringStatus.Pending, w.ScoringStatus));
    }

    [Fact]
    public async Task Model_not_active_in_ml_service_triggers_reconciliation_from_database()
    {
        var (svc, db, ml, model) = Setup();
        ml.OnScore = _ => MlCallResult<IReadOnlyList<ScoreResult>>.Fail(MlCallStatus.ModelNotActive, "409");
        var result = await svc.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ScoringRunOutcome.Deferred, result.Outcome);
        Assert.Equal([model.Version], ml.Activated);
        Assert.All(await db.FeatureWindows.ToListAsync(), w => Assert.Equal(0, w.ScoringAttempts));
    }

    [Fact]
    public async Task After_ml_restart_activation_is_reconciled_and_the_batch_is_scored_in_the_same_run()
    {
        var (svc, db, ml, model) = Setup();
        var calls = 0;
        ml.OnScore = items => ++calls == 1
            ? MlCallResult<IReadOnlyList<ScoreResult>>.Fail(MlCallStatus.ModelNotActive, "409")
            : FakeMlClient.LatencyScorer(items);
        ml.OnActivate = _ => MlCallResult<RegistryModelMetadata>.Ok(TestSupport.Metadata(model));

        var result = await svc.RunOnceAsync(CancellationToken.None);

        Assert.Equal(ScoringRunOutcome.Scored, result.Outcome);
        Assert.Equal(2, result.WindowsScored);
        Assert.Equal([model.Version], ml.Activated);
    }

    [Fact]
    public async Task Rejected_contract_marks_windows_rejected_for_explicit_requeue()
    {
        var (svc, db, ml, _) = Setup();
        ml.OnScore = _ => MlCallResult<IReadOnlyList<ScoreResult>>.Fail(MlCallStatus.Rejected, "422 schema");
        await svc.RunOnceAsync(CancellationToken.None);
        Assert.All(await db.FeatureWindows.ToListAsync(), w => Assert.Equal(ScoringStatus.Rejected, w.ScoringStatus));
    }

    [Fact]
    public async Task Inconsistent_ml_response_is_not_persisted()
    {
        var (svc, db, ml, _) = Setup();
        ml.OnScore = items => FakeMlClient.LatencyScorer(items, version: "some-other-model");
        await svc.RunOnceAsync(CancellationToken.None);
        Assert.Empty(db.ScoringRecords);
        Assert.All(await db.FeatureWindows.ToListAsync(), w => Assert.Equal(ScoringStatus.Rejected, w.ScoringStatus));
    }

    [Fact]
    public async Task Reprocessing_is_idempotent_per_window_and_model()
    {
        var (svc, db, ml, _) = Setup();
        ml.OnScore = items => FakeMlClient.LatencyScorer(items);
        await svc.RunOnceAsync(CancellationToken.None);
        foreach (var w in db.FeatureWindows)
        {
            w.RequeueScoring(TestSupport.Now);
        }

        await db.SaveChangesAsync();
        await svc.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, await db.ScoringRecords.CountAsync());
        Assert.Equal(1, await db.Anomalies.CountAsync());
    }
}

public sealed class FeatureWindowSchedulingTests
{
    [Fact]
    public void Deferral_uses_capped_exponential_backoff()
    {
        var w = TestSupport.Window(new ServiceDefinition("a", "b", TestSupport.Now), TestSupport.Now);
        var now = TestSupport.Now;
        var delays = new List<TimeSpan>();
        for (var i = 0; i < 8; i++)
        {
            w.DeferScoring("down", now, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(300));
            delays.Add(w.NextScoringAttemptUtc!.Value - now);
        }

        Assert.Equal(TimeSpan.FromSeconds(15), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(30), delays[1]);
        Assert.Equal(TimeSpan.FromSeconds(60), delays[2]);
        Assert.Equal(TimeSpan.FromSeconds(300), delays[^1]);
        Assert.Equal(8, w.ScoringAttempts);
    }
}
