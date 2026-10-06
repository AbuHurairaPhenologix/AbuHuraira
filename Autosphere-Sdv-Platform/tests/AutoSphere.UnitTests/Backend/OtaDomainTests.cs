using System.Security.Cryptography;
using AutoSphere.Domain.Common;
using AutoSphere.Domain.Ota;
using AutoSphere.SharedKernel.Ota;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.SharedKernel.Versioning;

namespace AutoSphere.UnitTests.Backend;

public sealed class OtaStateMachineTests
{
    [Theory]
    [InlineData(OtaUpdateStatus.Created, OtaUpdateStatus.Pending, true)]
    [InlineData(OtaUpdateStatus.Pending, OtaUpdateStatus.Downloading, true)]
    [InlineData(OtaUpdateStatus.Verifying, OtaUpdateStatus.Failed, true)]
    [InlineData(OtaUpdateStatus.Verifying, OtaUpdateStatus.RollingBack, false)]
    [InlineData(OtaUpdateStatus.HealthChecking, OtaUpdateStatus.Succeeded, true)]
    [InlineData(OtaUpdateStatus.HealthChecking, OtaUpdateStatus.RollingBack, true)]
    [InlineData(OtaUpdateStatus.RollingBack, OtaUpdateStatus.RolledBack, true)]
    [InlineData(OtaUpdateStatus.Succeeded, OtaUpdateStatus.Failed, false)]
    [InlineData(OtaUpdateStatus.Installing, OtaUpdateStatus.Succeeded, false)]
    [InlineData(OtaUpdateStatus.Failed, OtaUpdateStatus.Failed, false)]
    public void Direct_transitions(OtaUpdateStatus from, OtaUpdateStatus to, bool allowed) =>
        Assert.Equal(allowed, OtaStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(OtaUpdateStatus.Pending, OtaUpdateStatus.Installing, true)]
    [InlineData(OtaUpdateStatus.Downloading, OtaUpdateStatus.RolledBack, true)]
    [InlineData(OtaUpdateStatus.HealthChecking, OtaUpdateStatus.Downloading, false)]
    [InlineData(OtaUpdateStatus.RolledBack, OtaUpdateStatus.Succeeded, false)]
    public void Reachability_allows_coalesced_reports_but_never_moves_backwards(OtaUpdateStatus from, OtaUpdateStatus to, bool reachable) =>
        Assert.Equal(reachable, OtaStateMachine.IsReachable(from, to));
}

public sealed class OtaDeploymentTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public void Successful_update_records_full_history_and_installed_version()
    {
        var deployment = CreateDeployment();

        deployment.TransitionTo(OtaUpdateStatus.Pending, 0, "sent", Now);
        foreach (var (status, progress) in new[] { (OtaUpdateStatus.Downloading, 5), (OtaUpdateStatus.Verifying, 20), (OtaUpdateStatus.Installing, 50),
                     (OtaUpdateStatus.Installing, 60), (OtaUpdateStatus.Restarting, 78), (OtaUpdateStatus.HealthChecking, 82), (OtaUpdateStatus.Succeeded, 100) })
        {
            Assert.True(deployment.ApplyVehicleReport(status, progress, status.ToString(), status == OtaUpdateStatus.Succeeded ? "1.1.0" : null, null, Now));
        }

        Assert.Equal(OtaUpdateStatus.Succeeded, deployment.Status);
        Assert.Equal("1.1.0", deployment.InstalledVersion);
        Assert.NotNull(deployment.CompletedAt);
        Assert.Equal(9, deployment.Events.Count);
    }

    [Fact]
    public void Duplicate_and_out_of_order_reports_are_ignored()
    {
        var deployment = CreateDeployment();
        deployment.TransitionTo(OtaUpdateStatus.Pending, 0, "sent", Now);
        deployment.ApplyVehicleReport(OtaUpdateStatus.Installing, 50, "flashing", null, null, Now);

        Assert.False(deployment.ApplyVehicleReport(OtaUpdateStatus.Installing, 50, "duplicate", null, null, Now));
        Assert.False(deployment.ApplyVehicleReport(OtaUpdateStatus.Downloading, 5, "late", null, null, Now));
        Assert.Equal(OtaUpdateStatus.Installing, deployment.Status);
    }

    [Fact]
    public void Terminal_deployment_cannot_change()
    {
        var deployment = CreateDeployment();
        deployment.TransitionTo(OtaUpdateStatus.Pending, 0, "sent", Now);
        deployment.ApplyVehicleReport(OtaUpdateStatus.Failed, 25, "checksum", null, "Checksum", Now);

        Assert.False(deployment.ApplyVehicleReport(OtaUpdateStatus.Succeeded, 100, "?", "1.1.0", null, Now));
        Assert.Throws<DomainException>(() => deployment.TransitionTo(OtaUpdateStatus.Pending, 0, "retry", Now));
        Assert.Equal("1.0.0", deployment.InstalledVersion); // failed before installation: unchanged
    }

    [Fact]
    public void Rolled_back_deployment_reports_previous_version()
    {
        var deployment = CreateDeployment();
        deployment.TransitionTo(OtaUpdateStatus.Pending, 0, "sent", Now);
        deployment.ApplyVehicleReport(OtaUpdateStatus.HealthChecking, 82, "observing", null, null, Now);
        deployment.ApplyVehicleReport(OtaUpdateStatus.RollingBack, 88, "rollback", null, "crash loop", Now);
        deployment.ApplyVehicleReport(OtaUpdateStatus.RolledBack, 100, "restored", "1.0.0", null, Now);

        Assert.Equal(OtaUpdateStatus.RolledBack, deployment.Status);
        Assert.Equal("1.0.0", deployment.InstalledVersion);
        Assert.Equal("crash loop", deployment.FailureReason);
    }

    [Fact]
    public void Campaign_rejects_unsigned_packages_and_non_newer_versions()
    {
        var (unsigned, _) = SoftwarePackage.Create("p", EcuType.BatteryManagementSystem, SoftwareVersion.Parse("1.1.0"), SoftwareVersion.Parse("1.0.0"), [1, 2, 3], "admin", null, Now);
        Assert.Throws<DomainException>(() => OtaCampaign.Create("c", unsigned, "admin", Now));

        var package = SignedPackage();
        var campaign = OtaCampaign.Create("c", package, "admin", Now);
        Assert.Throws<DomainException>(() => campaign.AddDeployment(Guid.NewGuid(), "AUTO-001", "BMS-001", "1.1.0", package, false, Now));
    }

    [Fact]
    public void Signed_package_manifest_verifies_with_the_public_key()
    {
        var package = SignedPackage();
        using var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(_key.ExportSubjectPublicKeyInfo(), out _);

        Assert.True(PackageIntegrity.VerifySignature(package.ToManifest(), package.Signature, publicKey));
        Assert.True(PackageIntegrity.ChecksumMatches(package.Payload, package.PayloadSha256));
    }

    [Fact]
    public void Package_rules_are_enforced()
    {
        Assert.Throws<DomainException>(() => SoftwarePackage.Create("p", EcuType.BatteryManagementSystem, SoftwareVersion.Parse("1.0.0"),
            SoftwareVersion.Parse("1.1.0"), [1], "admin", null, Now));
        Assert.Throws<DomainException>(() => SoftwarePackage.Create("p", EcuType.CentralGateway, SoftwareVersion.Parse("1.1.0"),
            SoftwareVersion.Parse("1.0.0"), [1], "admin", null, Now));
        Assert.Throws<DomainException>(() => SoftwarePackage.Create("p", EcuType.BatteryManagementSystem, SoftwareVersion.Parse("1.1.0"),
            SoftwareVersion.Parse("1.0.0"), [], "admin", null, Now));
    }

    public void Dispose() => _key.Dispose();

    private SoftwarePackage SignedPackage()
    {
        var (package, manifest) = SoftwarePackage.Create("BMS 1.1.0", EcuType.BatteryManagementSystem, SoftwareVersion.Parse("1.1.0"),
            SoftwareVersion.Parse("1.0.0"), [1, 2, 3, 4], "admin", null, Now);
        package.ApplySignature(PackageIntegrity.Sign(manifest, _key), "test-key");
        return package;
    }

    private OtaDeployment CreateDeployment()
    {
        var package = SignedPackage();
        return OtaCampaign.Create("c", package, "admin", Now).AddDeployment(Guid.NewGuid(), "AUTO-001", "BMS-001", "1.0.0", package, false, Now);
    }
}
