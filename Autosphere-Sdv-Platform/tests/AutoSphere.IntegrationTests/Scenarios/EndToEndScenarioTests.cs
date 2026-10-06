using System.Net;
using System.Net.Http.Json;
using AutoSphere.Application.Alerts;
using AutoSphere.Application.Diagnostics;
using AutoSphere.Application.Ota;
using AutoSphere.Application.Simulation;
using AutoSphere.Application.Telemetry;
using AutoSphere.Application.Vehicles;
using AutoSphere.Domain.Diagnostics;
using AutoSphere.IntegrationTests.Infrastructure;
using AutoSphere.SharedKernel.Ota;
using AutoSphere.SharedKernel.Simulation;
using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.IntegrationTests.Scenarios;

/// <summary>
/// The end-to-end scenarios required by the thesis, executed against the complete platform
/// (simulated ECUs → CAN → gateway → MQTT → backend → database/SignalR).
/// </summary>
public sealed class EndToEndScenarioTests(PlatformFixture platform)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Start vehicle → receive telemetry → detect ECUs → inject battery overheat → DTC → alert → Critical →
    /// run diagnostics → resolve fault → Healthy → clear DTC.
    /// </summary>
    [Fact]
    public async Task Battery_overheat_is_detected_diagnosed_and_resolved()
    {
        using var admin = await platform.CreateClientAsync("admin", PlatformFixture.AdminPassword);
        using var engineer = await platform.CreateClientAsync("engineer", PlatformFixture.EngineerPassword);

        // Vehicle online, telemetry flowing, ECUs detected.
        var vehicle = await Vehicle(engineer);
        Assert.Equal(ConnectivityStatus.Online, vehicle.Connectivity);
        Assert.All(vehicle.Ecus, e => Assert.Equal(EcuStatus.Online, e.Status));
        Assert.NotNull(await engineer.GetFromJsonAsync<VehicleTelemetryDto>("/api/vehicles/AUTO-001/telemetry/latest", PlatformFixture.Json, Ct));

        // Inject the fault.
        var injected = await admin.PostAsJsonAsync("/api/vehicles/AUTO-001/faults", new FaultInjectionRequest(FaultType.BatteryOverheat), PlatformFixture.Json, Ct);
        Assert.Equal(HttpStatusCode.Accepted, injected.StatusCode);

        // The BMS confirms P0A7E, the backend raises alerts and the vehicle becomes critical.
        await PlatformFixture.WaitUntilAsync(async () => (await Vehicle(engineer)).Health.Status == HealthStatus.Critical
                                                         && (await Dtcs(engineer)).Any(d => d.Code == "P0A7E" && d.Status == DtcRecordStatus.Active),
            TimeSpan.FromSeconds(60), "battery overheat did not make the vehicle critical");
        // The DTC is "pending" first; once debounced it is confirmed and its freeze frame is captured.
        await PlatformFixture.WaitUntilAsync(async () => (await Dtcs(engineer)).Single(d => d.Code == "P0A7E") is { Confirmed: true, Snapshot.Count: > 0 },
            TimeSpan.FromSeconds(15), "P0A7E was not confirmed with a snapshot");
        var dtc = (await Dtcs(engineer)).Single(d => d.Code == "P0A7E");
        Assert.Equal("BMS-001", dtc.EcuId);
        Assert.Contains(dtc.Snapshot, s => s.Name == "BatteryTemperature" && s.Value >= 60);
        var alerts = await engineer.GetFromJsonAsync<IReadOnlyList<AlertDto>>("/api/vehicles/AUTO-001/alerts?activeOnly=true", PlatformFixture.Json, Ct);
        Assert.Contains(alerts!, a => a.AlertKey == "battery-temperature");
        Assert.Contains(alerts!, a => a.AlertKey == "dtc-BMS-001-P0A7E");

        // Remote diagnostics identifies the fault; an active DTC cannot be cleared.
        var scan = await (await engineer.PostAsync("/api/vehicles/AUTO-001/diagnostics/scan", null, Ct)).Content
            .ReadFromJsonAsync<DiagnosticSessionDto>(PlatformFixture.Json, Ct);
        Assert.Equal(DiagnosticSessionStatus.Completed, scan!.Status);
        Assert.Contains("Battery Thermal Fault", scan.Diagnosis, StringComparison.Ordinal);
        var refused = await engineer.PostAsJsonAsync("/api/vehicles/AUTO-001/dtcs/clear", new ClearDtcsRequest(null, "P0A7E"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // Resolve the root cause: the pack cools down, the DTC heals and the vehicle returns to healthy.
        await admin.PostAsJsonAsync("/api/vehicles/AUTO-001/faults", new FaultInjectionRequest(FaultType.BatteryOverheat, Contracts.Messages.FaultAction.Clear), PlatformFixture.Json, Ct);
        await PlatformFixture.WaitUntilAsync(async () => (await Vehicle(engineer)).Health.Status == HealthStatus.Healthy,
            TimeSpan.FromSeconds(120), "vehicle did not return to healthy after the fault was resolved");
        Assert.Equal(DtcRecordStatus.Resolved, (await Dtcs(engineer)).Single(d => d.Code == "P0A7E").Status);

        var cleared = await engineer.PostAsJsonAsync("/api/vehicles/AUTO-001/dtcs/clear", new ClearDtcsRequest(null, "P0A7E"), Ct);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        await PlatformFixture.WaitUntilAsync(async () => (await Dtcs(engineer)).All(d => d.Code != "P0A7E"),
            TimeSpan.FromSeconds(15), "cleared DTC still reported");
    }

    /// <summary>Vehicle runs 1.0.0 → upload 1.1.0 → deploy → validate → install → health check → 1.1.0 confirmed.</summary>
    [Fact]
    public async Task Ota_update_is_verified_installed_and_confirmed()
    {
        using var admin = await platform.CreateClientAsync("admin", PlatformFixture.AdminPassword);
        var before = Ecu(await Vehicle(admin), "BCM-001").SoftwareVersion;
        var next = Bump(before, 1);
        var package = await CreatePackageAsync(admin, EcuType.BodyControlModule, next, FirmwareBootBehavior.Normal);

        var deployment = await DeployAsync(admin, package.Id);

        Assert.Equal(OtaUpdateStatus.Succeeded, deployment.Status);
        Assert.Equal(next, deployment.InstalledVersion);
        Assert.Contains(deployment.Events, e => e.Status == OtaUpdateStatus.Verifying && e.Message.Contains("Signature", StringComparison.Ordinal));
        Assert.Contains(deployment.Events, e => e.Status == OtaUpdateStatus.HealthChecking);
        Assert.Equal(next, Ecu(await Vehicle(admin), "BCM-001").SoftwareVersion);
    }

    /// <summary>Corrupted package → verification fails → installation prevented → version unchanged.</summary>
    [Fact]
    public async Task Corrupted_ota_package_is_rejected_before_installation()
    {
        using var admin = await platform.CreateClientAsync("admin", PlatformFixture.AdminPassword);
        var before = Ecu(await Vehicle(admin), "VCU-001").SoftwareVersion;
        var package = await CreatePackageAsync(admin, EcuType.VehicleControlUnit, Bump(before, 3), FirmwareBootBehavior.Normal);

        var response = await admin.PostAsJsonAsync("/api/vehicles/AUTO-001/faults",
            new FaultInjectionRequest(FaultType.CorruptedOtaPackage, PackageId: package.Id), PlatformFixture.Json, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var deploymentId = (await admin.GetFromJsonAsync<IReadOnlyList<OtaDeploymentDto>>("/api/ota/deployments?vehicleId=AUTO-001", PlatformFixture.Json, Ct))!
            .First(d => d.PackageId == package.Id).Id;
        var deployment = await WaitForCompletionAsync(admin, deploymentId);

        Assert.Equal(OtaUpdateStatus.Failed, deployment.Status);
        Assert.Contains("Checksum", deployment.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(deployment.Events, e => e.Status == OtaUpdateStatus.Installing);
        Assert.Equal(before, Ecu(await Vehicle(admin), "VCU-001").SoftwareVersion);
    }

    /// <summary>A build that crashes after boot fails the health check and is rolled back automatically.</summary>
    [Fact]
    public async Task Failing_post_install_health_check_triggers_rollback()
    {
        using var admin = await platform.CreateClientAsync("admin", PlatformFixture.AdminPassword);
        var before = Ecu(await Vehicle(admin), "MCU-001").SoftwareVersion;
        var package = await CreatePackageAsync(admin, EcuType.MotorControlUnit, Bump(before, 7), FirmwareBootBehavior.CrashLoop);

        var deployment = await DeployAsync(admin, package.Id);

        Assert.Equal(OtaUpdateStatus.RolledBack, deployment.Status);
        Assert.Equal(before, deployment.InstalledVersion);
        Assert.Contains(deployment.Events, e => e.Status == OtaUpdateStatus.RollingBack);
        Assert.Equal(before, Ecu(await Vehicle(admin), "MCU-001").SoftwareVersion);
        await PlatformFixture.WaitUntilAsync(async () => Ecu(await Vehicle(admin), "MCU-001").Status == EcuStatus.Online,
            TimeSpan.FromSeconds(20), "MCU did not come back online after the rollback");
    }

    private static async Task<VehicleDetailsDto> Vehicle(HttpClient client) =>
        (await client.GetFromJsonAsync<VehicleDetailsDto>("/api/vehicles/AUTO-001", PlatformFixture.Json, Ct))!;

    private static async Task<IReadOnlyList<DtcRecordDto>> Dtcs(HttpClient client) =>
        (await client.GetFromJsonAsync<IReadOnlyList<DtcRecordDto>>("/api/vehicles/AUTO-001/dtcs", PlatformFixture.Json, Ct))!;

    private static EcuDto Ecu(VehicleDetailsDto vehicle, string ecuId) => vehicle.Ecus.Single(e => e.EcuId == ecuId);

    /// <summary>A newer version that does not collide with the seeded demo packages.</summary>
    private static string Bump(string version, int minorStep)
    {
        var parsed = SharedKernel.Versioning.SoftwareVersion.Parse(version);
        return new SharedKernel.Versioning.SoftwareVersion(parsed.Major, parsed.Minor + minorStep, 0).ToString();
    }

    private static async Task<SoftwarePackageDto> CreatePackageAsync(HttpClient admin, EcuType ecuType, string version, FirmwareBootBehavior behavior)
    {
        var response = await admin.PostAsJsonAsync("/api/ota/packages/sample",
            new CreateSamplePackageRequest(ecuType, version, "1.0.0", behavior, SizeBytes: 4096), PlatformFixture.Json, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SoftwarePackageDto>(PlatformFixture.Json, Ct))!;
    }

    private static async Task<OtaDeploymentDto> DeployAsync(HttpClient admin, Guid packageId)
    {
        var response = await admin.PostAsJsonAsync("/api/ota/campaigns", new CreateCampaignRequest(null, packageId, ["AUTO-001"]), PlatformFixture.Json, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var campaign = await response.Content.ReadFromJsonAsync<OtaCampaignDto>(PlatformFixture.Json, Ct);
        return await WaitForCompletionAsync(admin, campaign!.Deployments.Single().Id);
    }

    private static async Task<OtaDeploymentDto> WaitForCompletionAsync(HttpClient client, Guid deploymentId)
    {
        OtaDeploymentDto? deployment = null;
        await PlatformFixture.WaitUntilAsync(async () =>
        {
            deployment = await client.GetFromJsonAsync<OtaDeploymentDto>($"/api/ota/deployments/{deploymentId}", PlatformFixture.Json, Ct);
            return deployment!.Status.IsTerminal();
        }, TimeSpan.FromSeconds(60), "OTA deployment did not complete");
        return deployment!;
    }
}
