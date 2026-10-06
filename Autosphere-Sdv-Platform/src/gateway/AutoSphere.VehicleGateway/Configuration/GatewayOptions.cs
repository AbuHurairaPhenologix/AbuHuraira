using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.VehicleGateway.Configuration;

/// <summary>An ECU the gateway expects on the vehicle network.</summary>
public sealed class GatewayEcuOptions
{
    public string EcuId { get; set; } = string.Empty;

    public EcuType Type { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>Edge alert thresholds (°C).</summary>
public sealed class EdgeThresholdOptions
{
    public double BatteryTemperatureWarning { get; set; } = 50;

    public double BatteryTemperatureCritical { get; set; } = 60;

    public double MotorTemperatureWarning { get; set; } = 110;

    public double MotorTemperatureCritical { get; set; } = 130;

    /// <summary>Hysteresis applied before an alert is cleared.</summary>
    public double Hysteresis { get; set; } = 2;
}

/// <summary>Configuration section <c>Gateway</c>.</summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    public string VehicleId { get; set; } = "AUTO-001";

    /// <summary>The gateway's own ECU id (it is a node on the network and owns network DTCs).</summary>
    public string GatewayId { get; set; } = "CGW-001";

    public int TelemetryPublishIntervalMs { get; set; } = 250;

    public int StatusPublishIntervalMs { get; set; } = 5000;

    public int DtcPollIntervalMs { get; set; } = 2000;

    /// <summary>A message counts as lost after <c>max(cycle × multiplier, minimum)</c> without reception.</summary>
    public int MessageTimeoutMultiplier { get; set; } = 5;

    public int MinimumMessageTimeoutMs { get; set; } = 500;

    /// <summary>Must match the simulated ECUs' SecurityAccess secret.</summary>
    public string SecurityAccessSecret { get; set; } = string.Empty;

    /// <summary>Path to the PEM-encoded ECDSA public key trusted for OTA packages.</summary>
    public string? OtaTrustedPublicKeyPath { get; set; }

    /// <summary>Alternatively the PEM text itself (e.g. from an environment variable).</summary>
    public string? OtaTrustedPublicKeyPem { get; set; }

    /// <summary>
    /// Accept gateway-level test faults (GatewayDisconnect, MqttDisconnect). Must stay false on a real vehicle.
    /// </summary>
    public bool FaultInjectionEnabled { get; set; }

    /// <summary>Maximum number of messages buffered while the cloud connection is down.</summary>
    public int OfflineBufferCapacity { get; set; } = 2000;

    public EdgeThresholdOptions Thresholds { get; set; } = new();

    /// <summary>ECUs on the network; when empty, <see cref="DefaultEcus"/> is used.</summary>
    public List<GatewayEcuOptions> Ecus { get; set; } = [];

    public IReadOnlyList<GatewayEcuOptions> EffectiveEcus => Ecus.Count > 0 ? Ecus : DefaultEcus;

    public static IReadOnlyList<GatewayEcuOptions> DefaultEcus { get; } =
    [
        new() { EcuId = "VCU-001", Type = EcuType.VehicleControlUnit, Name = "Vehicle Control Unit" },
        new() { EcuId = "MCU-001", Type = EcuType.MotorControlUnit, Name = "Motor Control Unit" },
        new() { EcuId = "BMS-001", Type = EcuType.BatteryManagementSystem, Name = "Battery Management System" },
        new() { EcuId = "BCM-001", Type = EcuType.BodyControlModule, Name = "Body Control Module" },
    ];
}
