using AutoSphere.CanBus;
using AutoSphere.SharedKernel.Diagnostics;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.VehicleGateway.Configuration;
using AutoSphere.VehicleGateway.Network;
using AutoSphere.VehicleSignals.Codec;
using AutoSphere.VehicleSignals.Database;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AutoSphere.UnitTests.Gateway;

public sealed class EcuNetworkMonitorTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
    private readonly CanSignalCodec _codec = new(CanDatabaseLoader.Default);
    private readonly EcuNetworkMonitor _monitor;
    private int _counter;

    public EcuNetworkMonitorTests() =>
        _monitor = new EcuNetworkMonitor(Options.Create(new GatewayOptions()), CanDatabaseLoader.Default, _time);

    [Fact]
    public void Ecu_is_online_while_all_messages_arrive()
    {
        SendBms(cycles: 10);
        _monitor.Evaluate();

        Assert.Equal(EcuStatus.Online, _monitor.Find(EcuType.BatteryManagementSystem)!.Status);
    }

    [Fact]
    public void Silent_ecu_goes_offline_and_gateway_records_lost_communication_dtc()
    {
        SendBms(cycles: 3);
        _time.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 6; i++)
        {
            _monitor.Evaluate(); // debounce: DTC is confirmed after several monitor cycles
        }

        var bms = _monitor.Find("BMS-001")!;
        Assert.Equal(EcuStatus.Offline, bms.Status);
        Assert.True(bms.TimeoutCount > 0);
        Assert.True(_monitor.GatewayDtcs.IsTestFailed(KnownDtcs.LostCommunicationBatteryManagement.Code));
    }

    [Fact]
    public void Planned_silence_during_update_is_not_a_fault()
    {
        SendBms(cycles: 3);
        _monitor.Find("BMS-001")!.IsUpdating = true;
        _time.Advance(TimeSpan.FromSeconds(2));
        _monitor.Evaluate();

        Assert.Equal(EcuStatus.Updating, _monitor.Find("BMS-001")!.Status);
        Assert.False(_monitor.GatewayDtcs.IsTestFailed(KnownDtcs.LostCommunicationBatteryManagement.Code));
    }

    [Fact]
    public void Corrupted_frame_is_rejected_and_counted_as_e2e_error()
    {
        var message = CanDatabaseLoader.Default.GetMessage("BMS_Status");
        var payload = _codec.Encode(message, new Dictionary<string, double> { ["BatteryTemperature"] = 30 }, 0);
        payload[4] ^= 0xFF;
        _codec.TryDecode(new CanFrame(message.Id, payload, _time.GetUtcNow()), out var decoded);

        Assert.False(_monitor.OnFrameReceived(decoded!));
        Assert.Equal(1, _monitor.Find("BMS-001")!.E2EErrorCount);
    }

    [Fact]
    public void Late_frames_mark_ecu_as_warning()
    {
        SendBms(cycles: 3);
        _time.Advance(TimeSpan.FromMilliseconds(400)); // 4x the 100 ms cycle but below the 500 ms timeout
        SendBms(cycles: 1);
        _monitor.Evaluate();

        Assert.Equal(EcuStatus.Warning, _monitor.Find("BMS-001")!.Status);
    }

    private void SendBms(int cycles)
    {
        foreach (var _ in Enumerable.Range(0, cycles))
        {
            foreach (var message in CanDatabaseLoader.Default.MessagesSentBy(EcuType.BatteryManagementSystem))
            {
                var payload = _codec.Encode(message, new Dictionary<string, double>(), _counter % 16);
                _codec.TryDecode(new CanFrame(message.Id, payload, _time.GetUtcNow()), out var decoded);
                _monitor.OnFrameReceived(decoded!);
            }

            _counter++;
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }
    }
}
