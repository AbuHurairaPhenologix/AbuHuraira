using System.Diagnostics;
using AutoSphere.CanBus;
using AutoSphere.CanBus.IsoTp;
using AutoSphere.Contracts.Messages;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.Diagnostics.DataIdentifiers;
using AutoSphere.Diagnostics.Uds;
using AutoSphere.SharedKernel.Diagnostics;
using AutoSphere.SharedKernel.Ota;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.SharedKernel.Versioning;
using AutoSphere.VehicleGateway.Configuration;
using AutoSphere.VehicleGateway.Messaging;
using AutoSphere.VehicleGateway.Network;
using AutoSphere.VehicleGateway.Publishing;
using AutoSphere.VehicleGateway.RemoteDiagnostics;
using Microsoft.Extensions.Options;

namespace AutoSphere.VehicleGateway.Ota;

/// <summary>
/// Vehicle-side OTA update agent.
/// </summary>
/// <remarks>
/// Sequence: Downloading → Verifying (SHA-256, ECDSA signature, target ECU, version rules) → Installing
/// (UDS programming sequence into the inactive bank) → Restarting (ECU reset) → HealthChecking
/// (new version reported, stable communication, no active critical DTC) → Succeeded (image confirmed) or
/// RollingBack → RolledBack (previous bank re-activated). Nothing is installed unless every
/// verification check passes.
/// </remarks>
public sealed class OtaUpdateAgent(
    DiagnosticClientPool clients,
    EcuNetworkMonitor network,
    OtaTrustAnchor trustAnchor,
    GatewayMqttClient mqtt,
    StatusSignal statusSignal,
    IOptions<GatewayOptions> options,
    TimeProvider timeProvider,
    ILogger<OtaUpdateAgent> logger) : IDisposable
{
    private static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _single = new(1, 1);

    public async Task HandleAsync(OtaUpdateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = command.CorrelationId,
            ["OtaDeploymentId"] = command.DeploymentId,
            ["EcuId"] = command.TargetEcuId,
        });

        var update = new UpdateContext(command, this);
        if (!await _single.WaitAsync(0, cancellationToken))
        {
            update.Report(OtaUpdateStatus.Failed, 0, "Another update is already in progress on this vehicle.", failure: "Busy");
            return;
        }

        var node = network.Find(command.TargetEcuId);
        try
        {
            if (node is null)
            {
                update.Report(OtaUpdateStatus.Failed, 0, $"ECU {command.TargetEcuId} is not part of this vehicle.", failure: "UnknownEcu");
                return;
            }

            await RunAsync(update, node, cancellationToken);
        }
        catch (Exception ex) when (ex is UdsException or IsoTpException or CanBusException or TimeoutException)
        {
            logger.LogError(ex, "OTA deployment {DeploymentId} aborted", command.DeploymentId);
            update.Report(update.RestartIssued ? OtaUpdateStatus.RollbackFailed : OtaUpdateStatus.Failed, update.Progress,
                $"Update aborted: {ex.Message}", failure: ex.Message);
        }
        finally
        {
            if (node is not null)
            {
                node.IsUpdating = false;
                node.SoftwareVersion = null; // force re-read by the DTC monitor
            }

            statusSignal.Signal();
            _single.Release();
        }
    }

    private async Task RunAsync(UpdateContext update, EcuNode node, CancellationToken cancellationToken)
    {
        var command = update.Command;
        var client = await clients.GetAsync(node.Type);
        var total = Stopwatch.StartNew();

        // 1. Downloading: the payload arrives inside the command (see OtaUpdateCommand remarks).
        update.Report(OtaUpdateStatus.Downloading, 5, $"Receiving package {command.Version} ({command.PayloadSize} bytes)");
        byte[] payload;
        byte[] signature;
        try
        {
            payload = Convert.FromBase64String(command.PayloadBase64);
            signature = Convert.FromBase64String(command.SignatureBase64);
        }
        catch (FormatException)
        {
            update.Report(OtaUpdateStatus.Failed, 5, "Package encoding is invalid.", failure: "InvalidEncoding");
            return;
        }

        update.Report(OtaUpdateStatus.Downloading, 15, "Package received");

        // 2. Verifying: integrity, authenticity and applicability against the ECU's *live* software version.
        update.Report(OtaUpdateStatus.Verifying, 20, "Verifying checksum, signature, target ECU and version compatibility");
        var installed = await client.ReadDataByIdentifierAsync(DataIdentifierCatalog.SoftwareVersion, null, cancellationToken);
        if (!SoftwareVersion.TryParse(installed.Text, out var installedVersion)
            || !SoftwareVersion.TryParse(command.Version, out var targetVersion)
            || !SoftwareVersion.TryParse(command.MinimumCompatibleVersion, out var minimumVersion))
        {
            update.Report(OtaUpdateStatus.Failed, 20, "Version information is invalid.", failure: "InvalidVersion");
            return;
        }

        update.PreviousVersion = installedVersion.ToString();
        if (!trustAnchor.TryGetKey(out var key, out var keyError))
        {
            update.Report(OtaUpdateStatus.Failed, 20, keyError!, failure: "NoTrustAnchor");
            return;
        }

        var manifest = new PackageManifest(command.PackageId, command.TargetEcuType, targetVersion, minimumVersion,
            command.PayloadSha256, command.PayloadSize, command.PackageCreatedAt);
        var verification = OtaPackageVerifier.Verify(manifest, payload, signature, new OtaVerificationContext(node.Type, installedVersion, key!));
        update.ChecksPassed = verification.Passed.Select(c => c.ToString()).ToList();
        if (!verification.IsValid)
        {
            logger.LogWarning("OTA package {PackageId} rejected: {Reason}", command.PackageId, verification.Summary);
            update.Report(OtaUpdateStatus.Failed, 25, $"Verification failed — installation prevented. {verification.Summary}", failure: verification.Summary);
            return;
        }

        update.Report(OtaUpdateStatus.Verifying, 30, verification.Summary);

        // 3. Installing: UDS programming sequence into the inactive bank.
        node.IsUpdating = true;
        statusSignal.Signal();
        using (await clients.LockAsync(node.Type, cancellationToken))
        {
            update.Report(OtaUpdateStatus.Installing, 32, "Entering programming session");
            await client.StartSessionAsync(UdsSession.Extended, null, cancellationToken);
            await client.StartSessionAsync(UdsSession.Programming, null, cancellationToken);
            await client.UnlockAsync(options.Value.SecurityAccessSecret, null, cancellationToken);
            await client.RoutineControlAsync(RoutineIds.EraseMemory, null, null, cancellationToken);

            var maxBlock = await client.RequestDownloadAsync(0x0000_0000, (uint)payload.Length, null, cancellationToken);
            var chunk = Math.Max(1, maxBlock - 2);
            byte counter = 1;
            var lastReported = 0;
            for (var offset = 0; offset < payload.Length; offset += chunk)
            {
                await client.TransferDataAsync(counter, payload.AsMemory(offset, Math.Min(chunk, payload.Length - offset)), null, cancellationToken);
                counter = unchecked((byte)(counter + 1));
                var progress = 35 + (int)(40.0 * Math.Min(payload.Length, offset + chunk) / payload.Length);
                if (progress - lastReported >= 10)
                {
                    lastReported = progress;
                    update.Report(OtaUpdateStatus.Installing, progress, $"Transferring image: {Math.Min(payload.Length, offset + chunk)}/{payload.Length} bytes");
                }
            }

            await client.RequestTransferExitAsync(null, cancellationToken);
            var check = await client.RoutineControlAsync(RoutineIds.CheckProgrammingDependencies, null, null, cancellationToken);
            if (check.Length == 0 || check[0] != 0x00)
            {
                await client.StartSessionAsync(UdsSession.Default, null, cancellationToken);
                update.Report(OtaUpdateStatus.Failed, 75, "ECU rejected the image (check programming dependencies failed); active software unchanged.",
                    failure: "EcuImageCheckFailed");
                return;
            }

            // 4. Restarting into the new bank.
            update.Report(OtaUpdateStatus.Restarting, 78, "Resetting ECU to activate the new image");
            update.RestartIssued = true;
            await client.EcuResetAsync(UdsResetType.HardReset, null, cancellationToken);
        }

        // The ECU stays flagged as "updating" until it has rebooted, so the planned silence does not raise U-codes.
        var restarted = await WaitForRestartAsync(node, cancellationToken);
        node.IsUpdating = false;
        var healthy = restarted && await HealthCheckAsync(update, node, client, targetVersion, cancellationToken);
        if (healthy)
        {
            await client.StartSessionAsync(UdsSession.Extended, null, cancellationToken);
            await client.RoutineControlAsync(RoutineIds.ConfirmActiveImage, null, null, cancellationToken);
            await client.StartSessionAsync(UdsSession.Default, null, cancellationToken);
            update.InstalledVersion = targetVersion.ToString();
            update.Report(OtaUpdateStatus.Succeeded, 100, $"Update to {targetVersion} confirmed after {total.Elapsed.TotalSeconds:0.0} s");
            return;
        }

        await RollbackAsync(update, node, client, installedVersion, cancellationToken);
    }

    private async Task<bool> WaitForRestartAsync(EcuNode node, CancellationToken cancellationToken)
    {
        // Wait until the ECU is silent (reset taken) and then transmits again.
        var deadline = timeProvider.GetUtcNow() + RestartTimeout;
        var resetAt = timeProvider.GetUtcNow();
        while (timeProvider.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, cancellationToken);
            if (node.LastSeen is { } seen && seen > resetAt.AddMilliseconds(300))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> HealthCheckAsync(UpdateContext update, EcuNode node, UdsClient client, SoftwareVersion expected, CancellationToken cancellationToken)
    {
        var seconds = Math.Clamp(update.Command.HealthCheckSeconds, 2, 120);
        update.Report(OtaUpdateStatus.HealthChecking, 82, $"Observing ECU for {seconds} s after restart");

        var reported = await client.ReadDataByIdentifierAsync(DataIdentifierCatalog.SoftwareVersion, null, cancellationToken);
        if (reported.Text != expected.ToString())
        {
            update.FailureReason = $"ECU reports version {reported.Text} instead of {expected}";
            return false;
        }

        var timeoutsBefore = node.TimeoutCount;
        var end = timeProvider.GetUtcNow().AddSeconds(seconds);
        while (timeProvider.GetUtcNow() < end)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), timeProvider, cancellationToken);
            if (node.TimeoutCount > timeoutsBefore || node.Status == EcuStatus.Offline)
            {
                update.FailureReason = "ECU communication was lost after the restart (application not stable)";
                return false;
            }

            IReadOnlyList<UdsDtcRecord> dtcs;
            try
            {
                dtcs = await client.ReadDtcsAsync(DtcStatusBits.TestFailed, null, cancellationToken);
            }
            catch (UdsTimeoutException)
            {
                update.FailureReason = "ECU stopped responding to diagnostics after the restart";
                return false;
            }

            var critical = dtcs.FirstOrDefault(d => KnownDtcs.Describe(d.Code).Severity == DtcSeverity.Critical);
            if (critical is not null)
            {
                update.FailureReason = $"Critical DTC {critical.Code} ({KnownDtcs.Describe(critical.Code).Description}) is active after the restart";
                return false;
            }
        }

        return true;
    }

    private async Task RollbackAsync(UpdateContext update, EcuNode node, UdsClient client, SoftwareVersion previous, CancellationToken cancellationToken)
    {
        logger.LogWarning("Health check failed: {Reason}. Rolling back to {PreviousVersion}", update.FailureReason, previous);
        update.Report(OtaUpdateStatus.RollingBack, 88, $"Health check failed ({update.FailureReason}); restoring {previous}");
        var rollbackTimer = Stopwatch.StartNew();
        var restarted = false;
        node.IsUpdating = true;
        try
        {
            await client.StartSessionAsync(UdsSession.Extended, null, cancellationToken);
            await client.RoutineControlAsync(RoutineIds.ActivatePreviousBank, null, null, cancellationToken);
            await client.EcuResetAsync(UdsResetType.HardReset, null, cancellationToken);
            restarted = await WaitForRestartAsync(node, cancellationToken);
        }
        finally
        {
            node.IsUpdating = false;
        }

        if (restarted)
        {
            var restored = await client.ReadDataByIdentifierAsync(DataIdentifierCatalog.SoftwareVersion, null, cancellationToken);
            if (restored.Text == previous.ToString())
            {
                update.InstalledVersion = restored.Text;
                update.Report(OtaUpdateStatus.RolledBack, 100,
                    $"Rollback completed in {rollbackTimer.Elapsed.TotalSeconds:0.0} s; restored {previous}", failure: update.FailureReason);
                return;
            }
        }

        update.Report(OtaUpdateStatus.RollbackFailed, 100, "Rollback could not be confirmed; manual intervention required.", failure: update.FailureReason);
    }

    public void Dispose() => _single.Dispose();

    private void Publish(OtaStatusMessage message)
    {
        logger.LogInformation("OTA {DeploymentId} {Status} ({Progress}%): {Message}", message.DeploymentId, message.Status, message.ProgressPercent, message.Message);
        mqtt.Publish(MqttTopics.OtaStatus(options.Value.VehicleId), message with { Timestamp = timeProvider.GetUtcNow() });
    }

    /// <summary>Per-deployment state and status reporting.</summary>
    private sealed class UpdateContext(OtaUpdateCommand command, OtaUpdateAgent agent)
    {
        public OtaUpdateCommand Command { get; } = command;

        public int Progress { get; private set; }

        public bool RestartIssued { get; set; }

        public string? PreviousVersion { get; set; }

        public string? InstalledVersion { get; set; }

        public string? FailureReason { get; set; }

        public IReadOnlyList<string>? ChecksPassed { get; set; }

        public void Report(OtaUpdateStatus status, int progress, string message, string? failure = null)
        {
            Progress = progress;
            agent.Publish(new OtaStatusMessage
            {
                VehicleId = Command.VehicleId,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Command.CorrelationId,
                DeploymentId = Command.DeploymentId,
                EcuId = Command.TargetEcuId,
                Status = status,
                ProgressPercent = progress,
                Message = message,
                InstalledVersion = InstalledVersion,
                PreviousVersion = PreviousVersion,
                FailureReason = failure,
                VerificationChecksPassed = ChecksPassed,
            });
        }
    }
}
