using AutoSphere.Domain.Common;
using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.Domain.Vehicles;

/// <summary>A registered vehicle (aggregate root owning its ECUs).</summary>
public sealed class Vehicle
{
    private readonly List<Ecu> _ecus = [];

    private Vehicle()
    {
        // EF Core
    }

    public Guid Id { get; private set; }

    /// <summary>Business identifier used in MQTT topics and URLs, e.g. <c>AUTO-001</c>.</summary>
    public string VehicleId { get; private set; } = string.Empty;

    public string Vin { get; private set; } = string.Empty;

    public string Model { get; private set; } = string.Empty;

    public int ModelYear { get; private set; }

    public DateTimeOffset RegisteredAt { get; private set; }

    public ConnectivityStatus Connectivity { get; private set; } = ConnectivityStatus.Unknown;

    public DateTimeOffset? LastSeenAt { get; private set; }

    public DateTimeOffset? ConnectivityChangedAt { get; private set; }

    public string? GatewayId { get; private set; }

    public string? GatewaySoftwareVersion { get; private set; }

    public bool SimulationMode { get; private set; }

    public HealthStatus Health { get; private set; } = HealthStatus.Unknown;

    public int HealthScore { get; private set; }

    public string? HealthSummary { get; private set; }

    public DateTimeOffset? HealthEvaluatedAt { get; private set; }

    public IReadOnlyCollection<Ecu> Ecus => _ecus;

    public static Vehicle Register(string vehicleId, string vin, string model, int modelYear, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(vehicleId))
        {
            throw new DomainException("Vehicle id is required.");
        }

        if (vin is null || vin.Length != 17)
        {
            throw new DomainException("A VIN must have exactly 17 characters.");
        }

        return new Vehicle
        {
            Id = Guid.NewGuid(),
            VehicleId = vehicleId.Trim().ToUpperInvariant(),
            Vin = vin.ToUpperInvariant(),
            Model = model.Trim(),
            ModelYear = modelYear,
            RegisteredAt = now,
        };
    }

    public void UpdateDetails(string model, int modelYear)
    {
        Model = model.Trim();
        ModelYear = modelYear;
    }

    /// <summary>Applies the connectivity reported by (or inferred for) the gateway. Returns true if it changed.</summary>
    public bool SetConnectivity(ConnectivityStatus connectivity, DateTimeOffset at)
    {
        if (connectivity == ConnectivityStatus.Online)
        {
            LastSeenAt = LastSeenAt is null || at > LastSeenAt ? at : LastSeenAt;
        }

        if (Connectivity == connectivity)
        {
            return false;
        }

        Connectivity = connectivity;
        ConnectivityChangedAt = at;
        return true;
    }

    public void MarkSeen(DateTimeOffset at)
    {
        if (LastSeenAt is null || at > LastSeenAt)
        {
            LastSeenAt = at;
        }
    }

    public void SetGateway(string? gatewayId, string? softwareVersion, bool simulationMode)
    {
        GatewayId = gatewayId ?? GatewayId;
        GatewaySoftwareVersion = softwareVersion ?? GatewaySoftwareVersion;
        SimulationMode = simulationMode;
    }

    /// <summary>Records the result of the health calculation. Returns true if the status changed.</summary>
    public bool ApplyHealth(HealthAssessment assessment, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        var changed = Health != assessment.Status;
        Health = assessment.Status;
        HealthScore = assessment.Score;
        HealthSummary = assessment.Summary;
        HealthEvaluatedAt = at;
        return changed;
    }

    public Ecu? FindEcu(string ecuId) => _ecus.FirstOrDefault(e => string.Equals(e.EcuId, ecuId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds an ECU or returns the existing one with the same id.</summary>
    public Ecu AddOrGetEcu(string ecuId, EcuType type, string name, string? softwareVersion)
    {
        var existing = FindEcu(ecuId);
        if (existing is not null)
        {
            return existing;
        }

        var ecu = Ecu.Create(Id, ecuId, type, name, softwareVersion);
        _ecus.Add(ecu);
        return ecu;
    }
}
