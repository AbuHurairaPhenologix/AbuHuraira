using AutoSphere.EcuSimulation.Plant;
using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.EcuSimulation;

/// <summary>Configuration of one simulated ECU.</summary>
public sealed class EcuSimulatorOptions
{
    public string EcuId { get; set; } = string.Empty;

    public EcuType Type { get; set; }

    public string Name { get; set; } = string.Empty;

    public string SoftwareVersion { get; set; } = "1.0.0";

    public string HardwareVersion { get; set; } = "HW-A1";

    public string SerialNumber { get; set; } = string.Empty;

    public string PartNumber { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

/// <summary>Configuration section <c>Simulation</c>.</summary>
public sealed class SimulationOptions
{
    public const string SectionName = "Simulation";

    /// <summary>Runs the simulated ECUs inside the host process.</summary>
    public bool Enabled { get; set; }

    public string VehicleId { get; set; } = "AUTO-001";

    /// <summary>17-character VIN reported by ReadDataByIdentifier 0xF190.</summary>
    public string Vin { get; set; } = "WASPH1EV2T0000001";

    /// <summary>
    /// Shared secret for the simulated SecurityAccess seed/key algorithm. Must match the gateway's
    /// <c>Gateway:SecurityAccessSecret</c>. It protects nothing outside the simulation, but is still
    /// supplied through configuration rather than hard-coded.
    /// </summary>
    public string SecurityAccessSecret { get; set; } = string.Empty;

    /// <summary>Simulated bootloader/application start-up time after a reset.</summary>
    public int BootTimeMs { get; set; } = 1200;

    /// <summary>Accept fault-injection commands (test harness).</summary>
    public bool FaultInjectionEnabled { get; set; } = true;

    public PlantOptions Plant { get; set; } = new();

    /// <summary>ECUs to simulate; when empty, <see cref="DefaultEcus"/> is used.</summary>
    public List<EcuSimulatorOptions> Ecus { get; set; } = [];

    public IReadOnlyList<EcuSimulatorOptions> EffectiveEcus => Ecus.Count > 0 ? Ecus : DefaultEcus;

    public static IReadOnlyList<EcuSimulatorOptions> DefaultEcus { get; } =
    [
        new() { EcuId = "VCU-001", Type = EcuType.VehicleControlUnit, Name = "Vehicle Control Unit", SerialNumber = "VCU2026A0001", PartNumber = "AS-VCU-1000" },
        new() { EcuId = "MCU-001", Type = EcuType.MotorControlUnit, Name = "Motor Control Unit", SerialNumber = "MCU2026A0001", PartNumber = "AS-MCU-2000" },
        new() { EcuId = "BMS-001", Type = EcuType.BatteryManagementSystem, Name = "Battery Management System", SerialNumber = "BMS2026A0001", PartNumber = "AS-BMS-3000" },
        new() { EcuId = "BCM-001", Type = EcuType.BodyControlModule, Name = "Body Control Module", SerialNumber = "BCM2026A0001", PartNumber = "AS-BCM-4000" },
    ];
}
