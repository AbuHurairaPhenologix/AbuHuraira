using AutoSphere.Diagnostics.Uds;
using AutoSphere.SharedKernel.Ota;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.SharedKernel.Versioning;
using AutoSphere.UnitTests.Simulation;

namespace AutoSphere.UnitTests.Diagnostics;

/// <summary>Exercises the simulated A/B bootloader through the real UDS programming sequence.</summary>
public sealed class EcuFlashingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Flashing_new_image_activates_it_after_reset_and_rollback_restores_previous_bank()
    {
        await using var vehicle = await SimulatedVehicle.StartAsync();
        var bms = vehicle.Simulation.FindEcu("BMS-001")!;
        await using var tester = await vehicle.CreateTesterAsync(EcuType.BatteryManagementSystem);
        var image = SimulatedFirmwareImage.Create(new FirmwareImageHeader(EcuType.BatteryManagementSystem, SoftwareVersion.Parse("1.1.0"), FirmwareBootBehavior.Normal, "test"), 3000);

        Assert.Equal(0x00, await FlashAsync(tester, image));
        await tester.EcuResetAsync(UdsResetType.HardReset, null, Ct);
        await vehicle.WaitUntilAsync(() => bms.IsApplicationRunning && bms.ActiveVersion == SoftwareVersion.Parse("1.1.0"));
        Assert.True(bms.IsTrialBoot);

        await tester.StartSessionAsync(UdsSession.Extended, null, Ct);
        await tester.RoutineControlAsync(RoutineIds.ActivatePreviousBank, null, null, Ct);
        await tester.EcuResetAsync(UdsResetType.HardReset, null, Ct);
        await vehicle.WaitUntilAsync(() => bms.IsApplicationRunning && bms.ActiveVersion == SoftwareVersion.Parse("1.0.0"));
    }

    [Fact]
    public async Task Image_for_another_ecu_type_fails_the_dependency_check_and_is_not_activated()
    {
        await using var vehicle = await SimulatedVehicle.StartAsync();
        var bms = vehicle.Simulation.FindEcu("BMS-001")!;
        await using var tester = await vehicle.CreateTesterAsync(EcuType.BatteryManagementSystem);
        var image = SimulatedFirmwareImage.Create(new FirmwareImageHeader(EcuType.MotorControlUnit, SoftwareVersion.Parse("2.0.0"), FirmwareBootBehavior.Normal, "wrong"), 500);

        Assert.Equal(0x01, await FlashAsync(tester, image));
        await tester.EcuResetAsync(UdsResetType.HardReset, null, Ct);
        await vehicle.WaitUntilAsync(() => bms.IsApplicationRunning);

        Assert.Equal(SoftwareVersion.Parse("1.0.0"), bms.ActiveVersion);
    }

    [Fact]
    public async Task Transfer_without_security_access_is_denied()
    {
        await using var vehicle = await SimulatedVehicle.StartAsync();
        await using var tester = await vehicle.CreateTesterAsync(EcuType.MotorControlUnit);
        await tester.StartSessionAsync(UdsSession.Extended, null, Ct);
        await tester.StartSessionAsync(UdsSession.Programming, null, Ct);

        var error = await Assert.ThrowsAsync<UdsNegativeResponseException>(() => tester.RoutineControlAsync(RoutineIds.EraseMemory, null, null, Ct));

        Assert.Equal(NegativeResponseCode.SecurityAccessDenied, error.Code);
    }

    private static async Task<byte> FlashAsync(UdsClient tester, byte[] image)
    {
        await tester.StartSessionAsync(UdsSession.Extended, null, Ct);
        await tester.StartSessionAsync(UdsSession.Programming, null, Ct);
        await tester.UnlockAsync(SimulatedVehicle.Secret, null, Ct);
        await tester.RoutineControlAsync(RoutineIds.EraseMemory, null, null, Ct);
        var maxBlock = await tester.RequestDownloadAsync(0, (uint)image.Length, null, Ct);
        var chunk = maxBlock - 2;
        byte counter = 1;
        for (var offset = 0; offset < image.Length; offset += chunk)
        {
            await tester.TransferDataAsync(counter++, image.AsMemory(offset, Math.Min(chunk, image.Length - offset)), null, Ct);
        }

        await tester.RequestTransferExitAsync(null, Ct);
        return (await tester.RoutineControlAsync(RoutineIds.CheckProgrammingDependencies, null, null, Ct))[0];
    }
}
