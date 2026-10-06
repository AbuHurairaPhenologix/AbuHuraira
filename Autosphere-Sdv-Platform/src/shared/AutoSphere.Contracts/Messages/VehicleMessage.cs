namespace AutoSphere.Contracts.Messages;

/// <summary>Common header of every MQTT message.</summary>
public abstract record VehicleMessage
{
    /// <summary>Version of the message schema; incremented on breaking changes.</summary>
    public int SchemaVersion { get; init; } = 1;

    public required string VehicleId { get; init; }

    /// <summary>UTC time at which the sender created the message.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>A message that is part of a request/response or command/acknowledgement exchange.</summary>
public abstract record CorrelatedVehicleMessage : VehicleMessage
{
    public required Guid CorrelationId { get; init; }
}
