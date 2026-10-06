using AutoSphere.CanBus;
using AutoSphere.SharedKernel.Signals;
using AutoSphere.VehicleSignals.Codec;
using AutoSphere.VehicleSignals.Database;

namespace AutoSphere.UnitTests.VehicleSignals;

public sealed class CanSignalCodecTests
{
    private static readonly CanSignalCodec Codec = new(CanDatabaseLoader.Default);

    [Fact]
    public void Default_database_is_valid_and_maps_every_signal_to_a_vss_path()
    {
        var database = CanDatabaseLoader.Default;

        Assert.Equal(6, database.Messages.Count);
        Assert.All(database.Signals, s => Assert.StartsWith("Vehicle.", s.VssPath, StringComparison.Ordinal));
        Assert.Contains(database.Signals, s => s.VssPath == VssPaths.BatteryStateOfCharge);
    }

    [Fact]
    public void Battery_status_round_trips_through_encoder_and_decoder()
    {
        var message = CanDatabaseLoader.Default.GetMessage("BMS_Status");
        var payload = Codec.Encode(message, new Dictionary<string, double>
        {
            ["BatteryStateOfCharge"] = 78.4,
            ["BatteryTemperature"] = -12.3,
            ["ChargingStatus"] = 1,
        }, aliveCounter: 7);

        Assert.True(Codec.TryDecode(new CanFrame(0x300, payload, DateTimeOffset.UtcNow), out var decoded));

        Assert.Equal(E2EStatus.Ok, decoded!.E2EStatus);
        Assert.Equal(7, decoded.AliveCounter);
        var soc = decoded.Signals.Single(s => s.Definition.Name == "BatteryStateOfCharge");
        Assert.Equal(78.4, soc.PhysicalValue, precision: 6);
        Assert.Equal("%", soc.Definition.Unit);
        Assert.Equal(-12.3, decoded.Signals.Single(s => s.Definition.Name == "BatteryTemperature").PhysicalValue, precision: 6);
        Assert.Equal("Charging", decoded.Signals.Single(s => s.Definition.Name == "ChargingStatus").Label);
    }

    [Fact]
    public void Motorola_pack_message_round_trips_signed_current()
    {
        var message = CanDatabaseLoader.Default.GetMessage("BMS_Pack");
        var payload = Codec.Encode(message, new Dictionary<string, double> { ["BatteryVoltage"] = 400.2, ["BatteryCurrent"] = -45.6 }, 0);

        Assert.True(Codec.TryDecode(new CanFrame(0x301, payload, DateTimeOffset.UtcNow), out var decoded));

        Assert.Equal(0x0F, payload[2]); // 4002 = 0x0FA2, big-endian: MSB first
        Assert.Equal(0xA2, payload[3]);
        Assert.Equal(400.2, decoded!.Signals[0].PhysicalValue, precision: 6);
        Assert.Equal(-45.6, decoded.Signals[1].PhysicalValue, precision: 6);
    }

    [Fact]
    public void Corrupted_payload_fails_the_e2e_crc_check()
    {
        var message = CanDatabaseLoader.Default.GetMessage("VCU_Status");
        var payload = Codec.Encode(message, new Dictionary<string, double> { ["VehicleSpeed"] = 72 }, 3);
        payload[3] ^= 0x01;

        Assert.True(Codec.TryDecode(new CanFrame(0x100, payload, DateTimeOffset.UtcNow), out var decoded));
        Assert.Equal(E2EStatus.CrcError, decoded!.E2EStatus);
    }

    [Fact]
    public void Out_of_range_physical_value_is_flagged()
    {
        var message = CanDatabaseLoader.Default.GetMessage("BMS_Status");
        var payload = Codec.Encode(message, new Dictionary<string, double> { ["BatteryTemperature"] = 3000 }, 0);

        Codec.TryDecode(new CanFrame(0x300, payload, DateTimeOffset.UtcNow), out var decoded);

        Assert.False(decoded!.Signals.Single(s => s.Definition.Name == "BatteryTemperature").IsInRange);
    }

    [Fact]
    public void Unknown_ids_and_wrong_lengths_are_not_decoded()
    {
        Assert.False(Codec.TryDecode(new CanFrame(0x555, new byte[8], DateTimeOffset.UtcNow), out _));
        Assert.False(Codec.TryDecode(new CanFrame(0x300, new byte[4], DateTimeOffset.UtcNow), out _));
    }

    [Fact]
    public void Database_validation_detects_overlapping_signals()
    {
        const string json = """
        { "name": "bad", "version": "1", "messages": [ { "name": "M", "id": "0x10", "length": 8, "cycleTimeMs": 10, "sender": "VehicleControlUnit",
          "signals": [ { "name": "A", "vssPath": "Vehicle.A", "startBit": 0, "length": 8, "minimum": 0, "maximum": 255 },
                       { "name": "B", "vssPath": "Vehicle.B", "startBit": 4, "length": 8, "minimum": 0, "maximum": 255 } ] } ] }
        """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var error = Assert.Throws<InvalidDataException>(() => CanDatabaseLoader.Load(stream));
        Assert.Contains("overlaps", error.Message, StringComparison.Ordinal);
    }
}
