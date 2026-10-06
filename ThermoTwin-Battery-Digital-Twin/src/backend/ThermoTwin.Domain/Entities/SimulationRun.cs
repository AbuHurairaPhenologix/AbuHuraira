using ThermoTwin.Domain.Enums;

namespace ThermoTwin.Domain.Entities;

/// <summary>A digital-twin simulation session and its headline outcomes.</summary>
public sealed class SimulationRun
{
    private SimulationRun()
    {
        Name = string.Empty;
        ScenarioKey = string.Empty;
        ConfigurationJson = "{}";
    }

    public SimulationRun(string name, string scenarioKey, string configurationJson, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("A simulation run needs a name.");
        }

        Id = Guid.NewGuid();
        Name = name;
        ScenarioKey = scenarioKey;
        ConfigurationJson = configurationJson;
        CreatedAt = createdAt;
        Status = SimulationStatus.Pending;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public string ScenarioKey { get; private set; }

    public string ConfigurationJson { get; private set; }

    public SimulationStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public double SimulatedSeconds { get; private set; }

    public double PeakTemperature { get; private set; }

    public double PeakCounterfactualTemperature { get; private set; }

    public double CoolingEnergyJoules { get; private set; }

    public double? FinalTemperatureRmse { get; private set; }

    public double? FinalSourceRelativeError { get; private set; }

    public double? HotspotLocalizationErrorMm { get; private set; }

    public string? FailureReason { get; private set; }

    public bool IsActive => Status is SimulationStatus.Running or SimulationStatus.Paused;

    public void Start(DateTimeOffset at)
    {
        if (Status is not SimulationStatus.Pending)
        {
            throw new DomainException($"Cannot start a run in state {Status}.");
        }

        Status = SimulationStatus.Running;
        StartedAt = at;
    }

    public void Pause()
    {
        if (Status != SimulationStatus.Running)
        {
            throw new DomainException($"Only a running simulation can be paused (state {Status}).");
        }

        Status = SimulationStatus.Paused;
    }

    public void Resume()
    {
        if (Status != SimulationStatus.Paused)
        {
            throw new DomainException($"Only a paused simulation can be resumed (state {Status}).");
        }

        Status = SimulationStatus.Running;
    }

    public void RecordProgress(double simulatedSeconds, double peakTemperature, double peakCounterfactual, double energy)
    {
        SimulatedSeconds = simulatedSeconds;
        PeakTemperature = Math.Max(PeakTemperature, peakTemperature);
        PeakCounterfactualTemperature = Math.Max(PeakCounterfactualTemperature, peakCounterfactual);
        CoolingEnergyJoules = energy;
    }

    public void Complete(DateTimeOffset at, double? temperatureRmse, double? sourceError, double? hotspotErrorMm, bool stoppedEarly)
    {
        if (!IsActive)
        {
            throw new DomainException($"Cannot complete a run in state {Status}.");
        }

        Status = stoppedEarly ? SimulationStatus.Stopped : SimulationStatus.Completed;
        CompletedAt = at;
        FinalTemperatureRmse = temperatureRmse;
        FinalSourceRelativeError = sourceError;
        HotspotLocalizationErrorMm = hotspotErrorMm;
    }

    public void Fail(DateTimeOffset at, string reason)
    {
        Status = SimulationStatus.Failed;
        CompletedAt = at;
        FailureReason = reason;
    }
}
