using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Audit;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Application.Models;
using AnomalyDetection.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnomalyDetection.UnitTests.Models;

public sealed class ModelLifecycleServiceTests
{
    private static (ModelLifecycleService Svc, Infrastructure.Persistence.AnomalyDetectionDbContext Db, FakeMlClient Ml) Setup(params ModelVersion[] models)
    {
        var db = TestSupport.NewDb();
        db.ModelVersions.AddRange(models);
        db.SaveChanges();
        var ml = new FakeMlClient();
        var clock = TestSupport.Clock();
        return (new ModelLifecycleService(db, ml, new AuditService(db, clock), clock, NullLogger<ModelLifecycleService>.Instance), db, ml);
    }

    [Fact]
    public async Task Activation_verifies_artifact_deactivates_previous_and_audits()
    {
        var previous = TestSupport.Model("lof-ops-v1-a");
        var candidate = TestSupport.Model("ocsvm-ops-v1-b", active: false);
        var (svc, db, ml) = Setup(previous, candidate);
        ml.OnActivate = v => MlCallResult<RegistryModelMetadata>.Ok(TestSupport.Metadata(candidate));

        var result = await svc.ActivateAsync(candidate.ModelId, "admin", CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.True((await db.ModelVersions.SingleAsync(m => m.ModelId == candidate.ModelId)).IsActive);
        Assert.False((await db.ModelVersions.SingleAsync(m => m.ModelId == previous.ModelId)).IsActive);
        Assert.Contains(previous.Version, ml.Deactivated);
        Assert.Contains(db.AuditEvents, a => a.Action == AuditActions.ModelActivate && a.Result == AuditResults.Success && a.Actor == "admin");
        Assert.Contains(db.AuditEvents, a => a.Action == AuditActions.ModelDeactivate && a.TargetId == previous.ModelId.ToString());
    }

    [Fact]
    public async Task Benchmark_reference_model_cannot_be_activated()
    {
        var rf = TestSupport.Model("rf-ops-v1-a", active: false, eligible: false);
        var (svc, db, ml) = Setup(rf);
        var result = await svc.ActivateAsync(rf.ModelId, "admin", CancellationToken.None);
        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Contains("not production eligible", result.Error);
        Assert.Empty(ml.Activated);
        Assert.Contains(db.AuditEvents, a => a.Action == AuditActions.ModelActivate && a.Result == AuditResults.Rejected);
    }

    [Fact]
    public async Task Incompatible_schema_is_rejected()
    {
        var m = TestSupport.Model("ocsvm-ops-v2-a", active: false, schema: "ops-v2");
        var (svc, _, ml) = Setup(m);
        var result = await svc.ActivateAsync(m.ModelId, "admin", CancellationToken.None);
        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Contains("incompatible", result.Error);
        Assert.Empty(ml.Activated);
    }

    [Fact]
    public async Task Missing_or_changed_artifact_blocks_activation()
    {
        var m = TestSupport.Model("ocsvm-ops-v1-a", active: false);
        var (svc, db, ml) = Setup(m);

        ml.OnActivate = _ => MlCallResult<RegistryModelMetadata>.Fail(MlCallStatus.Unavailable, "HTTP 503: model_artifact_unavailable");
        Assert.Equal(OperationStatus.Unavailable, (await svc.ActivateAsync(m.ModelId, "admin", CancellationToken.None)).Status);

        ml.OnActivate = _ => MlCallResult<RegistryModelMetadata>.Ok(TestSupport.Metadata(TestSupport.Model("ocsvm-ops-v1-a", sha: "different")));
        var mismatch = await svc.ActivateAsync(m.ModelId, "admin", CancellationToken.None);
        Assert.Equal(OperationStatus.Invalid, mismatch.Status);
        Assert.False((await db.ModelVersions.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task Registration_accepts_only_registry_labels_never_paths()
    {
        var (svc, db, ml) = Setup();
        var result = await svc.RegisterAsync("../../artifacts/models/evil.joblib", "admin", CancellationToken.None);
        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Contains(db.AuditEvents, a => a.Action == AuditActions.ModelRegister && a.Result == AuditResults.Rejected);

        var notFound = await svc.RegisterAsync("ocsvm-ops-v1-missing", "admin", CancellationToken.None);
        Assert.Equal(OperationStatus.NotFound, notFound.Status);
    }

    [Fact]
    public async Task Registration_stores_metadata_inactive_and_audits()
    {
        var template = TestSupport.Model("ocsvm-ops-v1-new", active: false);
        var (svc, db, ml) = Setup();
        ml.OnGet = _ => MlCallResult<RegistryModelMetadata>.Ok(TestSupport.Metadata(template));
        var result = await svc.RegisterAsync("ocsvm-ops-v1-new", "admin", CancellationToken.None);
        Assert.True(result.IsOk);
        var stored = await db.ModelVersions.SingleAsync();
        Assert.False(stored.IsActive);
        Assert.Equal(template.ModelId, stored.ModelId);
        Assert.Equal(template.ValidationThreshold, stored.ValidationThreshold);
        Assert.Contains(db.AuditEvents, a => a.Action == AuditActions.ModelRegister && a.Result == AuditResults.Success);
        Assert.Equal(OperationStatus.Conflict, (await svc.RegisterAsync("ocsvm-ops-v1-new", "admin", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Deactivation_is_audited()
    {
        var m = TestSupport.Model();
        var (svc, db, ml) = Setup(m);
        var result = await svc.DeactivateAsync(m.ModelId, "admin", CancellationToken.None);
        Assert.True(result.IsOk);
        Assert.False((await db.ModelVersions.SingleAsync()).IsActive);
        Assert.Contains(m.Version, ml.Deactivated);
        Assert.Contains(db.AuditEvents, a => a.Action == AuditActions.ModelDeactivate);
    }

    [Fact]
    public async Task Retraining_validates_algorithm_and_source()
    {
        var (svc, db, _) = Setup();
        Assert.Equal(OperationStatus.Invalid, (await svc.RetrainAsync(new RetrainRequest("deep_net", "benchmark", null, null), "admin", CancellationToken.None)).Status);
        Assert.Equal(OperationStatus.Invalid, (await svc.RetrainAsync(new RetrainRequest("lof", "s3://bucket", null, null), "admin", CancellationToken.None)).Status);
        Assert.Equal(OperationStatus.Invalid, (await svc.RetrainAsync(new RetrainRequest("lof", "feature-windows", TestSupport.Now.AddDays(-1), TestSupport.Now), "admin", CancellationToken.None)).Status);
        var ok = await svc.RetrainAsync(new RetrainRequest("ocsvm", "benchmark", null, null), "admin", CancellationToken.None);
        Assert.True(ok.IsOk);
        Assert.Contains(db.AuditEvents, a => a.Action == AuditActions.ModelRetrain && a.Result == AuditResults.Success);
    }
}
