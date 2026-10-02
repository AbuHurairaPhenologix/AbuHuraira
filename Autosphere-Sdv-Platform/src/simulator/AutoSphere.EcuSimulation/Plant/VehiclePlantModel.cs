using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.EcuSimulation.Plant;

/// <summary>Immutable snapshot of the simulated physical vehicle ("plant").</summary>
public sealed record PlantState
{
    public double SpeedKmh { get; init; }

    public double MotorRpm { get; init; }

    public double MotorTorqueNm { get; init; }

    public double MotorTemperatureC { get; init; }

    public double BatteryStateOfChargePercent { get; init; }

    public double BatteryVoltageV { get; init; }

    public double BatteryCurrentA { get; init; }

    public double BatteryTemperatureC { get; init; }

    public double OdometerKm { get; init; }

    public double RangeKm { get; init; }

    public double AcceleratorPedalPercent { get; init; }

    public bool BrakePressed { get; init; }

    public GearPosition Gear { get; init; } = GearPosition.Drive;

    public IgnitionStatus Ignition { get; init; } = IgnitionStatus.On;

    public ChargingStatus Charging { get; init; } = ChargingStatus.NotCharging;

    public bool DoorFrontLeftOpen { get; init; }

    public bool DoorFrontRightOpen { get; init; }

    public bool DoorRearLeftOpen { get; init; }

    public bool DoorRearRightOpen { get; init; }

    public bool TrunkOpen { get; init; }

    public double TractionPowerKw { get; init; }

    public double SimulationTimeSeconds { get; init; }
}

/// <summary>Configuration of the plant model.</summary>
public sealed class PlantOptions
{
    public double InitialStateOfChargePercent { get; set; } = 78.4;

    public double InitialOdometerKm { get; set; } = 12_480.0;

    public double AmbientTemperatureC { get; set; } = 25.0;

    public double BatteryCapacityKwh { get; set; } = 75.0;

    /// <summary>Average consumption used for the range estimate.</summary>
    public double RangeConsumptionKwhPer100Km { get; set; } = 16.5;
}

/// <summary>
/// A deliberately simple longitudinal EV model: road load (aerodynamic drag + rolling resistance +
/// inertia), single-speed reduction gear, a linear open-circuit-voltage battery with internal resistance
/// and first-order thermal models for pack and motor. It is good enough to keep all signals physically
/// coherent for telemetry, diagnostics and fault-injection experiments; it is not a validated vehicle model.
/// </summary>
public sealed class VehiclePlantModel
{
    // Vehicle parameters (typical mid-size EV).
    private const double MassKg = 1_900;
    private const double DragCoefficient = 0.28;
    private const double FrontalAreaM2 = 2.3;
    private const double AirDensity = 1.2;
    private const double RollingResistance = 0.011;
    private const double Gravity = 9.81;
    private const double WheelRadiusM = 0.33;
    private const double GearRatio = 6.05;
    private const double DrivetrainEfficiency = 0.9;
    private const double AuxiliaryLoadKw = 0.5;
    private const double MaxTractionForceN = 6_500;
    private const double MaxBrakeDecelerationMs2 = 6.0;

    // Electrical parameters.
    private const double OpenCircuitBaseV = 330.0;
    private const double OpenCircuitPerPercentV = 0.9;
    private const double InternalResistanceOhm = 0.08;

    // Thermal parameters: steady-state temperature rise per kW of traction power, time constants.
    private const double BatteryRisePerKw = 1.55;
    private const double MotorRisePerKw = 4.9;
    private const double BatteryTimeConstantS = 150.0;
    private const double MotorTimeConstantS = 120.0;
    private const double BatteryCoolingFailureRiseC = 45.0;
    private const double MotorCoolingFailureRiseC = 100.0;

    /// <summary>Time constant of a cooling failure and of maximum active cooling after overtemperature.</summary>
    private const double FaultTimeConstantS = 22.0;

    private readonly PlantOptions _options;
    private readonly DriveCycle _driveCycle;
    private readonly object _gate = new();
    private PlantState _state;

    public VehiclePlantModel(PlantOptions options, DriveCycle? driveCycle = null)
    {
        _options = options;
        _driveCycle = driveCycle ?? DriveCycle.Default;
        var initialSpeed = _driveCycle.TargetSpeedKmh(0);
        var power = RoadLoadPowerKw(initialSpeed / 3.6, 0);
        _state = new PlantState
        {
            SpeedKmh = initialSpeed,
            BatteryStateOfChargePercent = options.InitialStateOfChargePercent,
            OdometerKm = options.InitialOdometerKm,
            BatteryTemperatureC = options.AmbientTemperatureC + (BatteryRisePerKw * power),
            MotorTemperatureC = options.AmbientTemperatureC + (MotorRisePerKw * power),
        };
        _state = Derive(_state, initialSpeed / 3.6, power, 0);
    }

    /// <summary>Simulates a battery cooling-system failure (fault injection).</summary>
    public bool BatteryCoolingFailed { get; set; }

    /// <summary>Simulates a motor cooling-system failure (fault injection).</summary>
    public bool MotorCoolingFailed { get; set; }

    public PlantState Current
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Advances the model by <paramref name="dt"/>.</summary>
    public PlantState Step(TimeSpan dt)
    {
        var seconds = Math.Clamp(dt.TotalSeconds, 0, 1.0);
        lock (_gate)
        {
            var s = _state;
            var time = s.SimulationTimeSeconds + seconds;
            var speed = s.SpeedKmh / 3.6;

            // Driver model: proportional speed controller on the drive cycle target.
            var target = _driveCycle.TargetSpeedKmh(time) / 3.6;
            var desiredAcceleration = Math.Clamp(0.6 * (target - speed), -MaxBrakeDecelerationMs2, 2.0);
            var resistance = RoadLoadForceN(speed);
            var requiredForce = (MassKg * desiredAcceleration) + resistance;

            double tractionForce;
            double pedal;
            bool brake;
            if (requiredForce >= 0)
            {
                tractionForce = Math.Min(requiredForce, MaxTractionForceN);
                pedal = Math.Clamp(tractionForce / MaxTractionForceN * 100.0, 0, 100);
                brake = false;
            }
            else
            {
                // Regenerative braking limited to 30 % of max force, friction brake for the rest.
                tractionForce = Math.Max(requiredForce, -0.3 * MaxTractionForceN);
                pedal = 0;
                brake = desiredAcceleration < -0.4;
            }

            // When braking, friction brakes supply whatever regeneration cannot, so the requested force is achieved.
            var wheelForce = requiredForce >= 0 ? tractionForce : requiredForce;
            var acceleration = (wheelForce - resistance) / MassKg;
            speed = Math.Max(0, speed + (acceleration * seconds));
            var power = TractionPowerKw(tractionForce, speed);

            // Battery energy and thermal behaviour.
            var socDelta = power * seconds / 3600.0 / _options.BatteryCapacityKwh * 100.0;
            var soc = Math.Clamp(s.BatteryStateOfChargePercent - socDelta, 0, 100);
            var batteryTarget = _options.AmbientTemperatureC + (BatteryRisePerKw * Math.Abs(power)) + (BatteryCoolingFailed ? BatteryCoolingFailureRiseC : 0);
            var motorTarget = _options.AmbientTemperatureC + (MotorRisePerKw * Math.Abs(power)) + (MotorCoolingFailed ? MotorCoolingFailureRiseC : 0);
            var batteryTemperature = FirstOrder(s.BatteryTemperatureC, batteryTarget, ThermalTimeConstant(BatteryCoolingFailed, s.BatteryTemperatureC, batteryTarget, BatteryTimeConstantS), seconds);
            var motorTemperature = FirstOrder(s.MotorTemperatureC, motorTarget, ThermalTimeConstant(MotorCoolingFailed, s.MotorTemperatureC, motorTarget, MotorTimeConstantS), seconds);

            var next = s with
            {
                SimulationTimeSeconds = time,
                SpeedKmh = speed * 3.6,
                OdometerKm = s.OdometerKm + (speed * seconds / 1000.0),
                BatteryStateOfChargePercent = soc,
                BatteryTemperatureC = batteryTemperature,
                MotorTemperatureC = motorTemperature,
                AcceleratorPedalPercent = pedal,
                BrakePressed = brake,
            };

            _state = Derive(next, speed, power, tractionForce);
            return _state;
        }
    }

    private PlantState Derive(PlantState state, double speedMs, double powerKw, double tractionForceN)
    {
        var openCircuit = OpenCircuitBaseV + (OpenCircuitPerPercentV * state.BatteryStateOfChargePercent);
        var current = powerKw * 1000.0 / openCircuit;
        var energyKwh = state.BatteryStateOfChargePercent / 100.0 * _options.BatteryCapacityKwh;
        return state with
        {
            MotorRpm = speedMs / WheelRadiusM * 60.0 / (2 * Math.PI) * GearRatio,
            MotorTorqueNm = tractionForceN * WheelRadiusM / GearRatio,
            BatteryCurrentA = current,
            BatteryVoltageV = openCircuit - (current * InternalResistanceOhm),
            RangeKm = energyKwh / _options.RangeConsumptionKwhPer100Km * 100.0,
            TractionPowerKw = powerKw,
        };
    }

    private static double RoadLoadForceN(double speedMs) =>
        (0.5 * AirDensity * DragCoefficient * FrontalAreaM2 * speedMs * speedMs) + (speedMs > 0.1 ? RollingResistance * MassKg * Gravity : 0);

    private static double RoadLoadPowerKw(double speedMs, double accelerationMs2) =>
        TractionPowerKw(RoadLoadForceN(speedMs) + (MassKg * accelerationMs2), speedMs);

    private static double TractionPowerKw(double forceN, double speedMs)
    {
        var mechanicalKw = forceN * speedMs / 1000.0;
        var electricalKw = mechanicalKw >= 0 ? mechanicalKw / DrivetrainEfficiency : mechanicalKw * DrivetrainEfficiency;
        return electricalKw + AuxiliaryLoadKw;
    }

    /// <summary>
    /// Large thermal mass smooths normal load changes; a failed cooling circuit heats quickly, and a healthy
    /// thermal management system cools at full power when the component is well above its operating point.
    /// </summary>
    private static double ThermalTimeConstant(bool coolingFailed, double temperature, double target, double normal) =>
        coolingFailed || temperature > target + 5 ? FaultTimeConstantS : normal;

    private static double FirstOrder(double current, double target, double timeConstant, double dt) =>
        current + ((target - current) * (1 - Math.Exp(-dt / timeConstant)));
}
