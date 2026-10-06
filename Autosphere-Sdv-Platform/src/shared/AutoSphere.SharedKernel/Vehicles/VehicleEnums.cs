namespace AutoSphere.SharedKernel.Vehicles;

public enum EcuType
{
    VehicleControlUnit = 0,
    MotorControlUnit = 1,
    BatteryManagementSystem = 2,
    BodyControlModule = 3,
    CentralGateway = 4,
}

/// <summary>Operational status of a single ECU as observed on the network.</summary>
public enum EcuStatus
{
    Unknown = 0,
    Online = 1,
    Warning = 2,
    Critical = 3,
    Updating = 4,
    Offline = 5,
}

/// <summary>Overall vehicle health produced by the health calculation.</summary>
public enum HealthStatus
{
    Unknown = 0,
    Healthy = 1,
    Warning = 2,
    Critical = 3,
    Offline = 4,
}

public enum ConnectivityStatus
{
    Unknown = 0,
    Online = 1,
    Offline = 2,
}

public enum GearPosition
{
    Park = 0,
    Reverse = 1,
    Neutral = 2,
    Drive = 3,
}

public enum IgnitionStatus
{
    Off = 0,
    Accessory = 1,
    On = 2,
    Start = 3,
}

public enum ChargingStatus
{
    NotCharging = 0,
    Charging = 1,
    ChargeComplete = 2,
    Fault = 3,
}
