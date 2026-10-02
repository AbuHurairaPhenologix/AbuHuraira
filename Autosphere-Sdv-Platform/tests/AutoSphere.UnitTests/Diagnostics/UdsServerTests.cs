using AutoSphere.Contracts.Messages;
using AutoSphere.Diagnostics.DataIdentifiers;
using AutoSphere.Diagnostics.FaultMemory;
using AutoSphere.Diagnostics.Uds;
using AutoSphere.SharedKernel.Diagnostics;
using AutoSphere.SharedKernel.Simulation;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.UnitTests.Simulation;

namespace AutoSphere.UnitTests.Diagnostics;

public sealed class UdsServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reads_identification_data_by_identifier()
    {
        await using var vehicle = await SimulatedVehicle.StartAsync();
        await using var tester = await vehicle.CreateTesterAsync(EcuType.BatteryManagementSystem);
        var trace = new UdsTrace();

        var version = await tester.ReadDataByIdentifierAsync(DataIdentifierCatalog.SoftwareVersion, trace, Ct);
        var vin = await tester.ReadDataByIdentifierAsync(DataIdentifierCatalog.Vin, trace, Ct);

        Assert.Equal("1.0.0", version.Text);
        Assert.Equal("WASPH1EV2T0000001", vin.Text);
        Assert.Equal(2, trace.Exchanges.Count);
        Assert.Equal(new byte[] { 0x22, 0xF1, 0x89 }, trace.Exchanges[0].Request);
        Assert.Equal(0x62, trace.Exchanges[0].Response![0]);
    }

    [Fact]
    public async Task Reads_live_data_consistent_with_plant_model()
    {
        await using var vehicle = await SimulatedVehicle.StartAsync();
        await using var tester = await vehicle.CreateTesterAsync(EcuType.BatteryManagementSystem);

        var soc = await tester.ReadDataByIdentifierAsync(DataIdentifierCatalog.BatteryStateOfCharge, null, Ct);

        Assert.InRange(soc.Numeric!.Value, vehicle.Simulation.Plant.Current.BatteryStateOfChargePercent - 0.5, vehicle.Simulation.Plant.Current.BatteryStateOfChargePercent + 0.5);
    }

    [Fact]
    public async Task Unsupported_identifier_returns_request_out_of_range()
    {
        await using var vehicle = await SimulatedVehicle.StartAsync();
        await using var tester = await vehicle.CreateTesterAsync(EcuType.BatteryManagementSystem);

        var error = await Assert.ThrowsAsync<UdsNegativeResponseException>(
            () => tester.ReadDataByIdentifierAsync(DataIdentifierCatalog.MotorTemperature, null, Ct));

        Assert.Equal(NegativeResponseCode.RequestOutOfRange, error.Code);
    }

    [Fact]
    public async Task Programming_session_requires_extended_session_first()
    {
        await using var vehicle = await SimulatedVehicle.StartAsync();
        await using var tester = await vehicle.CreateTesterAsync(EcuType.MotorControlUnit);

        var error = await Assert.ThrowsAsync<UdsNegativeResponseException>(() => tester.StartSessionAsync(UdsSession.Programming, null, Ct));
        Assert.Equal(NegativeResponseCode.ConditionsNotCorrect, error.Code);

        Assert.Equal(UdsSession.Extended, await tester.StartSessionAsync(UdsSession.Extended, null, Ct));
        Assert.Equal(UdsSession.Programming, await tester.StartSessionAsync(UdsSession.Programming, null, Ct));
    }

    [Fact]
    public async Task Security_access_rejects_wrong_key_and_accepts_correct_key()
    {
        await using var vehicle = await SimulatedVehicle.StartAsync();
        await using var tester = await vehicle.CreateTesterAsync(EcuType.BatteryManagementSystem);
        await tester.StartSessionAsync(UdsSession.Extended, null, Ct);

        var error = await Assert.ThrowsAsync<UdsNegativeResponseException>(() => tester.UnlockAsync("wrong-secret", null, Ct));
        Assert.Equal(NegativeResponseCode.InvalidKey, error.Code);

        await tester.UnlockAsync(SimulatedVehicle.Secret, null, Ct);
        Assert.True(vehicle.Simulation.FindEcu("BMS-001")!.SecurityUnlocked);
    }

    [Fact]
    public async Task Crashed_ecu_does_not_answer()
    {
        await using var vehicle = await SimulatedVehicle.StartAsync();
        await using var tester = await vehicle.CreateTesterAsync(EcuType.BodyControlModule);

        vehicle.Simulation.FaultInjector.Apply(Fault(FaultType.EcuCrash, "BCM-001"));

        await Assert.ThrowsAsync<UdsTimeoutException>(() => tester.TesterPresentAsync(null, Ct));
    }

    [Fact]
    public async Task Injected_sensor_fault_creates_confirmed_dtc_with_snapshot_which_cannot_be_cleared_while_active()
    {
        await using var vehicle = await SimulatedVehicle.StartAsync();
        await using var tester = await vehicle.CreateTesterAsync(EcuType.BatteryManagementSystem);
        var code = KnownDtcs.BatteryTemperatureSensorInvalid.Code;

        vehicle.Simulation.FaultInjector.Apply(Fault(FaultType.InvalidSensorValue, "BMS-001"));
        await vehicle.WaitUntilAsync(() => vehicle.Simulation.FindEcu("BMS-001")!.Dtcs.Query(DtcStatusBits.ConfirmedDtc).Count > 0);

        var dtcs = await tester.ReadDtcsAsync(DtcStatusBits.ConfirmedDtc, null, Ct);
        var dtc = Assert.Single(dtcs);
        Assert.Equal(code, dtc.Code);
        Assert.True(dtc.TestFailed);

        var snapshot = await tester.ReadDtcSnapshotAsync(code, null, Ct);
        Assert.Contains(snapshot, v => v.Definition == DataIdentifierCatalog.BatteryTemperature && v.Numeric > 1000);

        var refused = await Assert.ThrowsAsync<UdsNegativeResponseException>(() => tester.ClearDtcsAsync(code, null, Ct));
        Assert.Equal(NegativeResponseCode.ConditionsNotCorrect, refused.Code);

        vehicle.Simulation.FaultInjector.Apply(Fault(FaultType.InvalidSensorValue, "BMS-001", FaultAction.Clear));
        await vehicle.WaitUntilAsync(() => !vehicle.Simulation.FindEcu("BMS-001")!.Dtcs.IsTestFailed(code));
        await tester.ClearDtcsAsync(code, null, Ct);

        Assert.Empty(await tester.ReadDtcsAsync((DtcStatusBits)0xFF, null, Ct));
    }

    internal static FaultInjectionCommand Fault(FaultType fault, string? ecuId, FaultAction action = FaultAction.Inject) => new()
    {
        VehicleId = "AUTO-001",
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = Guid.NewGuid(),
        Fault = fault,
        Action = action,
        TargetEcuId = ecuId,
    };
}
