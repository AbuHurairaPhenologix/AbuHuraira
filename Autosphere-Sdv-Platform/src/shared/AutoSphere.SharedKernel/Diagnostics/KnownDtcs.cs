namespace AutoSphere.SharedKernel.Diagnostics;

public enum DtcSeverity
{
    Information = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>Static description of a DTC known to the AutoSphere platform.</summary>
/// <param name="Code">The trouble code.</param>
/// <param name="Description">Human readable description.</param>
/// <param name="Severity">Severity used by health monitoring.</param>
/// <param name="FaultCategory">The diagnosis shown to an engineer, e.g. "Battery Thermal Fault".</param>
public sealed record DtcDefinition(DtcCode Code, string Description, DtcSeverity Severity, string FaultCategory);

/// <summary>
/// DTC catalogue shared by the edge and the cloud.
/// </summary>
/// <remarks>
/// Codes are taken from the SAE J2012 ranges where an appropriate public code exists
/// (e.g. P0A7E "Hybrid/EV Battery Pack Over Temperature", U0140 "Lost Communication With Body Control Module").
/// Descriptions are shortened and in some cases adapted for the AutoSphere prototype; this is a
/// project catalogue, not an authoritative reproduction of SAE J2012.
/// </remarks>
public static class KnownDtcs
{
    public static readonly DtcDefinition BatteryOverTemperature =
        Define("P0A7E", "Hybrid/EV Battery Pack Over Temperature", DtcSeverity.Critical, "Battery Thermal Fault");

    public static readonly DtcDefinition BatteryPackDeterioration =
        Define("P0A7F", "Hybrid/EV Battery Pack Deterioration", DtcSeverity.Warning, "Battery Degradation");

    public static readonly DtcDefinition BatteryTemperatureSensorInvalid =
        Define("P0A9C", "Battery Temperature Sensor Signal Implausible", DtcSeverity.Warning, "Battery Sensor Fault");

    public static readonly DtcDefinition MotorOverTemperature =
        Define("P0A2F", "Drive Motor Temperature Too High", DtcSeverity.Critical, "Motor Thermal Fault");

    public static readonly DtcDefinition MotorTemperatureSensorInvalid =
        Define("P0A2C", "Drive Motor Temperature Sensor Signal Implausible", DtcSeverity.Warning, "Motor Sensor Fault");

    public static readonly DtcDefinition ControlModuleInternalFault =
        Define("P0606", "Control Module Processor / Self-Test Fault", DtcSeverity.Critical, "ECU Internal Fault");

    public static readonly DtcDefinition LostCommunicationMotorController =
        Define("U0100", "Lost Communication With Motor Controller", DtcSeverity.Critical, "Network Communication Fault");

    public static readonly DtcDefinition LostCommunicationBatteryManagement =
        Define("U0111", "Lost Communication With Battery Management System", DtcSeverity.Critical, "Network Communication Fault");

    public static readonly DtcDefinition LostCommunicationBodyControl =
        Define("U0140", "Lost Communication With Body Control Module", DtcSeverity.Warning, "Network Communication Fault");

    public static readonly DtcDefinition LostCommunicationVehicleControl =
        Define("U0293", "Lost Communication With Vehicle Control Unit", DtcSeverity.Critical, "Network Communication Fault");

    public static readonly DtcDefinition CanMessageTimingFault =
        Define("U0001", "CAN Bus Message Timing Out Of Range", DtcSeverity.Warning, "Network Timing Fault");

    public static readonly DtcDefinition E2EProtectionFault =
        Define("U0401", "Invalid Data Received (E2E Check Failed)", DtcSeverity.Warning, "Network Data Integrity Fault");

    private static readonly Dictionary<DtcCode, DtcDefinition> Definitions = new[]
    {
        BatteryOverTemperature, BatteryPackDeterioration, BatteryTemperatureSensorInvalid, MotorOverTemperature,
        MotorTemperatureSensorInvalid, ControlModuleInternalFault, LostCommunicationMotorController,
        LostCommunicationBatteryManagement, LostCommunicationBodyControl, LostCommunicationVehicleControl,
        CanMessageTimingFault, E2EProtectionFault,
    }.ToDictionary(d => d.Code);

    public static IReadOnlyCollection<DtcDefinition> All => Definitions.Values;

    public static DtcDefinition Describe(DtcCode code) =>
        Definitions.TryGetValue(code, out var definition)
            ? definition
            : new DtcDefinition(code, $"Unknown trouble code {code}", DtcSeverity.Warning, "Unclassified Fault");

    private static DtcDefinition Define(string code, string description, DtcSeverity severity, string category) =>
        new(DtcCode.Parse(code), description, severity, category);
}
