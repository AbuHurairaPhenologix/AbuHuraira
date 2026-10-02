using System.Diagnostics.CodeAnalysis;

namespace AutoSphere.Contracts.Mqtt;

/// <summary>The kind of message carried by a vehicle topic.</summary>
public enum VehicleTopicKind
{
    Telemetry,
    Status,
    Dtcs,
    Alerts,
    DiagnosticRequest,
    DiagnosticResponse,
    OtaCommand,
    OtaStatus,
    FaultInjection,
    FaultInjectionAck,
}

/// <summary>
/// Single place where every MQTT topic of the platform is defined.
/// </summary>
public static class MqttTopics
{
    public const string Root = "autosphere";

    private static readonly (VehicleTopicKind Kind, string Prefix, string Suffix)[] Layout =
    [
        (VehicleTopicKind.Telemetry, "vehicles", "telemetry"),
        (VehicleTopicKind.Status, "vehicles", "status"),
        (VehicleTopicKind.Dtcs, "vehicles", "dtcs"),
        (VehicleTopicKind.Alerts, "vehicles", "alerts"),
        (VehicleTopicKind.DiagnosticRequest, "vehicles", "diagnostics/request"),
        (VehicleTopicKind.DiagnosticResponse, "vehicles", "diagnostics/response"),
        (VehicleTopicKind.OtaCommand, "vehicles", "ota/command"),
        (VehicleTopicKind.OtaStatus, "vehicles", "ota/status"),
        (VehicleTopicKind.FaultInjection, "simulation", "faults"),
        (VehicleTopicKind.FaultInjectionAck, "simulation", "faults/ack"),
    ];

    public static string For(VehicleTopicKind kind, string vehicleId)
    {
        ValidateVehicleId(vehicleId);
        var (_, prefix, suffix) = Layout.Single(l => l.Kind == kind);
        return $"{Root}/{prefix}/{vehicleId}/{suffix}";
    }

    /// <summary>Topic filter matching the given kind for all vehicles (single-level wildcard).</summary>
    public static string AllVehicles(VehicleTopicKind kind)
    {
        var (_, prefix, suffix) = Layout.Single(l => l.Kind == kind);
        return $"{Root}/{prefix}/+/{suffix}";
    }

    public static string Telemetry(string vehicleId) => For(VehicleTopicKind.Telemetry, vehicleId);

    public static string Status(string vehicleId) => For(VehicleTopicKind.Status, vehicleId);

    public static string Dtcs(string vehicleId) => For(VehicleTopicKind.Dtcs, vehicleId);

    public static string Alerts(string vehicleId) => For(VehicleTopicKind.Alerts, vehicleId);

    public static string DiagnosticRequest(string vehicleId) => For(VehicleTopicKind.DiagnosticRequest, vehicleId);

    public static string DiagnosticResponse(string vehicleId) => For(VehicleTopicKind.DiagnosticResponse, vehicleId);

    public static string OtaCommand(string vehicleId) => For(VehicleTopicKind.OtaCommand, vehicleId);

    public static string OtaStatus(string vehicleId) => For(VehicleTopicKind.OtaStatus, vehicleId);

    public static string FaultInjection(string vehicleId) => For(VehicleTopicKind.FaultInjection, vehicleId);

    public static string FaultInjectionAck(string vehicleId) => For(VehicleTopicKind.FaultInjectionAck, vehicleId);

    /// <summary>Parses a concrete topic into its vehicle id and kind.</summary>
    public static bool TryParse(string topic, [NotNullWhen(true)] out string? vehicleId, out VehicleTopicKind kind)
    {
        vehicleId = null;
        kind = default;
        var segments = topic.Split('/');
        if (segments.Length < 4 || segments[0] != Root)
        {
            return false;
        }

        var suffix = string.Join('/', segments[3..]);
        foreach (var (candidateKind, prefix, candidateSuffix) in Layout)
        {
            if (segments[1] == prefix && suffix == candidateSuffix && IsValidVehicleId(segments[2]))
            {
                vehicleId = segments[2];
                kind = candidateKind;
                return true;
            }
        }

        return false;
    }

    /// <summary>Vehicle ids are used in topics, so wildcard and separator characters are forbidden.</summary>
    public static bool IsValidVehicleId(string? vehicleId) =>
        !string.IsNullOrWhiteSpace(vehicleId) && vehicleId.Length <= 64
        && vehicleId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static void ValidateVehicleId(string vehicleId)
    {
        if (!IsValidVehicleId(vehicleId))
        {
            throw new ArgumentException($"'{vehicleId}' is not a valid vehicle id.", nameof(vehicleId));
        }
    }
}
