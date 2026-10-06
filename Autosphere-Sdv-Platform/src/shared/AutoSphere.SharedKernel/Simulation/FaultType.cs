namespace AutoSphere.SharedKernel.Simulation;

/// <summary>
/// Faults that can be injected into the simulated vehicle for demonstration and testing.
/// These are test-harness features and are only accepted when fault injection is enabled.
/// </summary>
public enum FaultType
{
    /// <summary>The target ECU stops executing: no cyclic frames, no diagnostic responses.</summary>
    EcuCrash = 0,

    /// <summary>Battery cooling failure in the plant model; pack temperature rises.</summary>
    BatteryOverheat = 1,

    /// <summary>Motor cooling failure in the plant model; winding temperature rises.</summary>
    MotorOverheat = 2,

    /// <summary>The target ECU reports a physically implausible sensor value.</summary>
    InvalidSensorValue = 3,

    /// <summary>The target ECU stops transmitting its cyclic messages while staying alive.</summary>
    CanMessageLoss = 4,

    /// <summary>The target ECU transmits its cyclic messages with a large additional delay.</summary>
    CanMessageDelay = 5,

    /// <summary>The gateway loses its cloud connection abruptly (broker publishes the last will).</summary>
    GatewayDisconnect = 6,

    /// <summary>The MQTT session is interrupted; the gateway buffers data and reconnects.</summary>
    MqttDisconnect = 7,

    /// <summary>The next OTA package for the vehicle is corrupted in transit (handled by the backend).</summary>
    CorruptedOtaPackage = 8,
}
