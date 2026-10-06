using AutoSphere.Application.Diagnostics;
using AutoSphere.Application.Telemetry;
using AutoSphere.Contracts.Messages;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.Domain.Alerts;
using AutoSphere.Domain.Diagnostics;
using AutoSphere.SharedKernel.Diagnostics;
using AutoSphere.SharedKernel.Signals;
using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.UnitTests.Backend;

public sealed class DtcLifecycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Dtc_moves_through_active_resolved_and_cleared()
    {
        var dtc = DiagnosticTroubleCode.Detect(Guid.NewGuid(), "BMS-001", "P0A7E", 0x2F, testFailed: true, confirmed: true, [], T0);
        Assert.Equal(DtcRecordStatus.Active, dtc.Status);
        Assert.Equal(DtcSeverity.Critical, dtc.Severity);
        Assert.Equal("Battery Thermal Fault", dtc.FaultCategory);
        Assert.False(dtc.IsClearable);

        Assert.True(dtc.Observe(0x2E, testFailed: false, confirmed: true, [], T0.AddSeconds(30)));
        Assert.Equal(DtcRecordStatus.Resolved, dtc.Status);
        Assert.Equal(T0.AddSeconds(30), dtc.ResolvedAt);
        Assert.True(dtc.IsClearable);

        dtc.MarkCleared(T0.AddSeconds(40));
        Assert.Equal(DtcRecordStatus.Cleared, dtc.Status);
        Assert.Equal(T0.AddSeconds(40), dtc.ClearedAt);
    }

    [Fact]
    public void Reactivation_counts_another_occurrence_and_keeps_first_snapshot()
    {
        var dtc = DiagnosticTroubleCode.Detect(Guid.NewGuid(), "BMS-001", "P0A7E", 0x2F, true, true, [new DtcSnapshotValue("BatteryTemperature", 61, "°C")], T0);
        dtc.Observe(0x2E, false, true, [], T0.AddSeconds(10));

        dtc.Observe(0x2F, true, true, [new DtcSnapshotValue("BatteryTemperature", 70, "°C")], T0.AddSeconds(20));

        Assert.Equal(2, dtc.OccurrenceCount);
        Assert.Null(dtc.ResolvedAt);
        Assert.Equal(61, Assert.Single(dtc.Snapshot).Value);
    }
}

public sealed class AlertLifecycleTests
{
    [Fact]
    public void Escalation_requires_new_acknowledgement()
    {
        var alert = Alert.Raise(Guid.NewGuid(), "battery-temperature", AlertSource.Vehicle, DtcSeverity.Warning, "Thermal", "52 °C", DateTimeOffset.UtcNow);
        alert.Acknowledge("engineer", DateTimeOffset.UtcNow);

        alert.Update(DtcSeverity.Critical, "61 °C", 61, 60);

        Assert.Null(alert.AcknowledgedAt);
        Assert.True(alert.IsActive);
        alert.Clear(DateTimeOffset.UtcNow);
        Assert.False(alert.IsActive);
    }
}

public sealed class DiagnosisTests
{
    [Fact]
    public void Full_scan_diagnosis_names_active_fault_category_and_clearable_dtcs()
    {
        var message = Response(DiagnosticOperation.FullScan,
            Result("BMS-001", Dtc("P0A7E", "BMS-001", testFailed: true), Dtc("P0A9C", "BMS-001", testFailed: false)),
            Result("MCU-001"));

        var diagnosis = DiagnosticService.Diagnose(message);

        Assert.Contains("Battery Thermal Fault (P0A7E on BMS-001)", diagnosis, StringComparison.Ordinal);
        Assert.Contains("1 stored (healed) DTC(s) can be cleared", diagnosis, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreachable_ecus_are_reported()
    {
        var message = Response(DiagnosticOperation.FullScan, Result("BCM-001") with { Success = false, Error = "P2 timeout" });

        Assert.Contains("Not responding: BCM-001", DiagnosticService.Diagnose(message), StringComparison.Ordinal);
    }

    [Fact]
    public void Healthy_scan_reports_no_faults()
    {
        Assert.StartsWith("No active faults", DiagnosticService.Diagnose(Response(DiagnosticOperation.FullScan, Result("VCU-001"))), StringComparison.Ordinal);
    }

    private static DiagnosticResponseMessage Response(DiagnosticOperation operation, params EcuDiagnosticResultDto[] results) => new()
    {
        VehicleId = "AUTO-001",
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = Guid.NewGuid(),
        Operation = operation,
        Success = results.All(r => r.Success),
        Results = results,
    };

    private static EcuDiagnosticResultDto Result(string ecuId, params DtcDto[] dtcs) => new(ecuId, true, null, 5, [], dtcs, []);

    private static DtcDto Dtc(string code, string ecuId, bool testFailed)
    {
        var definition = KnownDtcs.Describe(DtcCode.Parse(code));
        return new DtcDto(code, ecuId, (byte)(testFailed ? 0x09 : 0x08), testFailed, true, definition.Description, definition.Severity, definition.FaultCategory, []);
    }
}

public sealed class TelemetryMappingTests
{
    [Fact]
    public void Snapshot_maps_vss_signals_to_typed_model()
    {
        var now = DateTimeOffset.UtcNow;
        SignalValueDto Signal(string path, double value) => new(path, path, value, "", "ECU", now, SignalQuality.Valid);

        var snapshot = VehicleSnapshotDto.From(
        [
            Signal(VssPaths.VehicleSpeed, 72), Signal(VssPaths.MotorSpeed, 3500), Signal(VssPaths.BatteryStateOfCharge, 78),
            Signal(VssPaths.GearPosition, 3), Signal(VssPaths.IgnitionStatus, 2), Signal(VssPaths.BrakePedalPressed, 1),
            Signal(VssPaths.DoorFrontLeftOpen, 0), Signal(VssPaths.TrunkOpen, 1),
        ]);

        Assert.Equal(72, snapshot.SpeedKmh);
        Assert.Equal(3500, snapshot.MotorRpm);
        Assert.Equal(GearPosition.Drive, snapshot.Gear);
        Assert.Equal(IgnitionStatus.On, snapshot.Ignition);
        Assert.True(snapshot.BrakePressed);
        Assert.True(snapshot.AnyDoorOpen);
        Assert.Null(snapshot.BatteryTemperatureC);
    }

    [Fact]
    public void Downsampling_bounds_the_number_of_points_and_averages_buckets()
    {
        var start = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, 600).Select(i => new TelemetryPointDto(start.AddSeconds(i), i % 2 == 0 ? 10 : 20)).ToList();

        var result = TelemetryQueryService.Downsample(samples, start, start.AddSeconds(600), 60);

        Assert.InRange(result.Count, 55, 61);
        Assert.All(result, p => Assert.Equal(15, p.Value, precision: 6));
    }

    [Fact]
    public void Latency_breakdown_is_derived_from_timestamps()
    {
        var can = DateTimeOffset.UtcNow;
        var state = new VehicleLiveState("AUTO-001", 1, can.AddMilliseconds(20), can.AddMilliseconds(25),
            [new SignalValueDto(VssPaths.VehicleSpeed, "VehicleSpeed", 72, "km/h", "VCU-001", can, SignalQuality.Valid)], null);

        var latency = VehicleTelemetryDto.From(state).Latency;

        Assert.Equal(20, latency.CanToGatewayPublishMs, precision: 1);
        Assert.Equal(5, latency.GatewayToBackendMs, precision: 1);
        Assert.Equal(25, latency.CanToBackendMs, precision: 1);
    }
}

public sealed class MqttTopicTests
{
    [Fact]
    public void Topics_round_trip_and_reject_foreign_patterns()
    {
        var topic = MqttTopics.DiagnosticResponse("AUTO-001");

        Assert.Equal("autosphere/vehicles/AUTO-001/diagnostics/response", topic);
        Assert.True(MqttTopics.TryParse(topic, out var vehicleId, out var kind));
        Assert.Equal(("AUTO-001", VehicleTopicKind.DiagnosticResponse), (vehicleId, kind));
        Assert.True(MqttTopics.TryParse("autosphere/simulation/AUTO-001/faults/ack", out _, out var ackKind));
        Assert.Equal(VehicleTopicKind.FaultInjectionAck, ackKind);
        Assert.False(MqttTopics.TryParse("autosphere/vehicles/AUTO-001/unknown", out _, out _));
        Assert.False(MqttTopics.TryParse("other/vehicles/AUTO-001/telemetry", out _, out _));
        Assert.Equal("autosphere/vehicles/+/telemetry", MqttTopics.AllVehicles(VehicleTopicKind.Telemetry));
    }

    [Theory]
    [InlineData("AUTO/001")]
    [InlineData("AUTO+")]
    [InlineData("#")]
    [InlineData("")]
    public void Vehicle_ids_with_topic_wildcards_are_rejected(string vehicleId)
    {
        Assert.Throws<ArgumentException>(() => MqttTopics.Telemetry(vehicleId));
    }

    [Fact]
    public void Message_json_uses_camel_case_and_string_enums()
    {
        var json = System.Text.Encoding.UTF8.GetString(AutoSphereJson.Serialize(new AlertMessage
        {
            VehicleId = "AUTO-001",
            Timestamp = DateTimeOffset.UnixEpoch,
            AlertKey = "battery-temperature",
            State = AlertState.Raised,
            Severity = DtcSeverity.Critical,
            Category = AlertCategory.Thermal,
            Message = "hot",
        }));

        Assert.Contains("\"severity\":\"Critical\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\":1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ecuId", json, StringComparison.Ordinal); // nulls omitted
    }
}
