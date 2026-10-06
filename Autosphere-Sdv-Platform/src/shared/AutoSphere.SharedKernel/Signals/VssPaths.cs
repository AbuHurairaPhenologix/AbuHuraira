namespace AutoSphere.SharedKernel.Signals;

/// <summary>
/// Normalized signal identifiers inspired by the COVESA Vehicle Signal Specification (VSS 4.x).
/// </summary>
/// <remarks>
/// Paths marked as <b>extension</b> are not part of the official VSS catalogue and were added for this
/// prototype (VSS explicitly allows overlays for OEM-specific signals). Units also deviate where noted:
/// VSS models <c>Vehicle.Powertrain.Range</c> in metres whereas AutoSphere uses kilometres.
/// </remarks>
public static class VssPaths
{
    public const string VehicleSpeed = "Vehicle.Speed";
    public const string Odometer = "Vehicle.TraveledDistance";
    public const string IgnitionStatus = "Vehicle.LowVoltageSystemState";
    public const string VehicleRange = "Vehicle.Powertrain.Range";
    public const string GearPosition = "Vehicle.Powertrain.Transmission.SelectedGear";

    public const string MotorSpeed = "Vehicle.Powertrain.ElectricMotor.Speed";
    public const string MotorTorque = "Vehicle.Powertrain.ElectricMotor.Torque";
    public const string MotorTemperature = "Vehicle.Powertrain.ElectricMotor.Temperature";

    public const string BatteryStateOfCharge = "Vehicle.Powertrain.TractionBattery.StateOfCharge.Current";
    public const string BatteryVoltage = "Vehicle.Powertrain.TractionBattery.CurrentVoltage";
    public const string BatteryCurrent = "Vehicle.Powertrain.TractionBattery.CurrentCurrent";
    public const string BatteryTemperature = "Vehicle.Powertrain.TractionBattery.Temperature.Average";

    /// <summary>Extension: VSS only defines the boolean <c>Charging.IsCharging</c>.</summary>
    public const string ChargingStatus = "Vehicle.Powertrain.TractionBattery.Charging.Status";

    public const string AcceleratorPedalPosition = "Vehicle.Chassis.Accelerator.PedalPosition";

    /// <summary>Extension: VSS defines <c>Brake.PedalPosition</c> (%), the prototype only has a switch.</summary>
    public const string BrakePedalPressed = "Vehicle.Chassis.Brake.IsPressed";

    public const string DoorFrontLeftOpen = "Vehicle.Cabin.Door.Row1.DriverSide.IsOpen";
    public const string DoorFrontRightOpen = "Vehicle.Cabin.Door.Row1.PassengerSide.IsOpen";
    public const string DoorRearLeftOpen = "Vehicle.Cabin.Door.Row2.DriverSide.IsOpen";
    public const string DoorRearRightOpen = "Vehicle.Cabin.Door.Row2.PassengerSide.IsOpen";
    public const string TrunkOpen = "Vehicle.Body.Trunk.Rear.IsOpen";
}
