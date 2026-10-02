using AutoSphere.Diagnostics.DataIdentifiers;
using AutoSphere.EcuSimulation.Plant;
using AutoSphere.SharedKernel.Diagnostics;

namespace AutoSphere.EcuSimulation.Ecus;

/// <summary>A threshold monitor with hysteresis, as used for ECU over-temperature detection.</summary>
internal sealed class HysteresisMonitor(double setThreshold, double clearThreshold)
{
    public bool Active { get; private set; }

    public bool Evaluate(double value)
    {
        Active = Active ? value > clearThreshold : value > setThreshold;
        return Active;
    }
}

/// <summary>Vehicle Control Unit: driver inputs, vehicle speed, odometer and range.</summary>
public sealed class VehicleControlEcuSimulator(EcuSimulatorOptions options, SimulationContext context) : EcuSimulator(options, context)
{
    /// <summary>Implausible speed transmitted while the "invalid sensor value" fault is active.</summary>
    public const double InvalidSpeedKmh = 600;

    protected override IReadOnlyList<DataIdentifierDefinition> SnapshotIdentifiers { get; } =
        [DataIdentifierCatalog.VehicleSpeed, DataIdentifierCatalog.Odometer];

    protected override IReadOnlyDictionary<string, double> ProduceSignals(PlantState state) => new Dictionary<string, double>
    {
        ["GearPosition"] = (int)state.Gear,
        ["BrakeStatus"] = state.BrakePressed ? 1 : 0,
        ["VehicleSpeed"] = Faults.InvalidSensorValue ? InvalidSpeedKmh : state.SpeedKmh,
        ["AcceleratorPedalPosition"] = state.AcceleratorPedalPercent,
        ["IgnitionStatus"] = (int)state.Ignition,
        ["Odometer"] = state.OdometerKm,
        ["VehicleRange"] = state.RangeKm,
    };

    protected override void MonitorFaults(PlantState state)
    {
        // The VCU has no own fault monitors in this prototype; implausible speed is detected at the gateway.
    }

    protected override double? ReadLiveData(DataIdentifierDefinition definition, PlantState state) => definition.Identifier switch
    {
        0x0301 => Faults.InvalidSensorValue ? InvalidSpeedKmh : state.SpeedKmh,
        0x0302 => state.OdometerKm,
        _ => null,
    };
}

/// <summary>Motor Control Unit (inverter): motor speed, torque and winding temperature.</summary>
public sealed class MotorEcuSimulator(EcuSimulatorOptions options, SimulationContext context) : EcuSimulator(options, context)
{
    public const double OverTemperatureSetC = 130;
    public const double OverTemperatureClearC = 120;
    public const double InvalidTemperatureC = 215; // largest value the 8-bit signal can carry

    private readonly HysteresisMonitor _overTemperature = new(OverTemperatureSetC, OverTemperatureClearC);

    protected override IReadOnlyList<DataIdentifierDefinition> SnapshotIdentifiers { get; } =
        [DataIdentifierCatalog.MotorTemperature, DataIdentifierCatalog.MotorSpeed];

    protected override IReadOnlyDictionary<string, double> ProduceSignals(PlantState state) => new Dictionary<string, double>
    {
        ["MotorRPM"] = state.MotorRpm,
        ["MotorTorque"] = state.MotorTorqueNm,
        ["MotorTemperature"] = MeasuredTemperature(state),
    };

    protected override void MonitorFaults(PlantState state)
    {
        Dtcs.Report(KnownDtcs.MotorTemperatureSensorInvalid.Code, Faults.InvalidSensorValue, CaptureSnapshot);
        if (!Faults.InvalidSensorValue)
        {
            Dtcs.Report(KnownDtcs.MotorOverTemperature.Code, _overTemperature.Evaluate(state.MotorTemperatureC), CaptureSnapshot);
        }
    }

    protected override double? ReadLiveData(DataIdentifierDefinition definition, PlantState state) => definition.Identifier switch
    {
        0x0201 => state.MotorRpm,
        0x0202 => MeasuredTemperature(state),
        _ => null,
    };

    private double MeasuredTemperature(PlantState state) => Faults.InvalidSensorValue ? InvalidTemperatureC : state.MotorTemperatureC;
}

/// <summary>Battery Management System: state of charge, pack voltage/current and temperature.</summary>
public sealed class BatteryEcuSimulator(EcuSimulatorOptions options, SimulationContext context) : EcuSimulator(options, context)
{
    public const double OverTemperatureSetC = 60;
    public const double OverTemperatureClearC = 55;
    public const double InvalidTemperatureC = 3000; // physically impossible pack temperature

    private readonly HysteresisMonitor _overTemperature = new(OverTemperatureSetC, OverTemperatureClearC);

    protected override IReadOnlyList<DataIdentifierDefinition> SnapshotIdentifiers { get; } =
    [
        DataIdentifierCatalog.BatteryTemperature, DataIdentifierCatalog.BatteryStateOfCharge,
        DataIdentifierCatalog.BatteryVoltage, DataIdentifierCatalog.BatteryCurrent,
    ];

    protected override IReadOnlyDictionary<string, double> ProduceSignals(PlantState state) => new Dictionary<string, double>
    {
        ["ChargingStatus"] = (int)state.Charging,
        ["BatteryStateOfCharge"] = state.BatteryStateOfChargePercent,
        ["BatteryTemperature"] = MeasuredTemperature(state),
        ["BatteryVoltage"] = state.BatteryVoltageV,
        ["BatteryCurrent"] = state.BatteryCurrentA,
    };

    protected override void MonitorFaults(PlantState state)
    {
        Dtcs.Report(KnownDtcs.BatteryTemperatureSensorInvalid.Code, Faults.InvalidSensorValue, CaptureSnapshot);
        if (!Faults.InvalidSensorValue)
        {
            Dtcs.Report(KnownDtcs.BatteryOverTemperature.Code, _overTemperature.Evaluate(state.BatteryTemperatureC), CaptureSnapshot);
        }
    }

    protected override double? ReadLiveData(DataIdentifierDefinition definition, PlantState state) => definition.Identifier switch
    {
        0x0101 => MeasuredTemperature(state),
        0x0102 => state.BatteryStateOfChargePercent,
        0x0103 => state.BatteryVoltageV,
        0x0104 => state.BatteryCurrentA,
        _ => null,
    };

    private double MeasuredTemperature(PlantState state) => Faults.InvalidSensorValue ? InvalidTemperatureC : state.BatteryTemperatureC;
}

/// <summary>Body Control Module: doors and trunk.</summary>
public sealed class BodyControlEcuSimulator(EcuSimulatorOptions options, SimulationContext context) : EcuSimulator(options, context)
{
    protected override IReadOnlyList<DataIdentifierDefinition> SnapshotIdentifiers { get; } = [DataIdentifierCatalog.DoorStatus];

    protected override IReadOnlyDictionary<string, double> ProduceSignals(PlantState state) => new Dictionary<string, double>
    {
        ["DoorFrontLeft"] = state.DoorFrontLeftOpen ? 1 : 0,
        ["DoorFrontRight"] = state.DoorFrontRightOpen ? 1 : 0,
        ["DoorRearLeft"] = state.DoorRearLeftOpen ? 1 : 0,
        ["DoorRearRight"] = state.DoorRearRightOpen ? 1 : 0,
        ["TrunkStatus"] = state.TrunkOpen ? 1 : 0,
    };

    protected override void MonitorFaults(PlantState state)
    {
    }

    protected override double? ReadLiveData(DataIdentifierDefinition definition, PlantState state) => definition.Identifier switch
    {
        0x0401 => (state.DoorFrontLeftOpen ? 1 : 0) | (state.DoorFrontRightOpen ? 2 : 0) | (state.DoorRearLeftOpen ? 4 : 0)
                  | (state.DoorRearRightOpen ? 8 : 0) | (state.TrunkOpen ? 16 : 0),
        _ => null,
    };
}
