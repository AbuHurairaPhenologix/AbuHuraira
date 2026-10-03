namespace ThermoTwin.Domain.Enums;

/// <summary>Lifecycle of a simulation run.</summary>
public enum SimulationStatus
{
    Pending,
    Running,
    Paused,
    Completed,
    Stopped,
    Failed,
}

/// <summary>Thermal risk classification of the battery state.</summary>
public enum ThermalRisk
{
    /// <summary>Estimated and predicted temperatures are comfortably below the safety limit.</summary>
    Normal,

    /// <summary>Within the warning band below T_safe.</summary>
    Elevated,

    /// <summary>The forecast exceeds T_safe — action required.</summary>
    Warning,

    /// <summary>The current estimate already exceeds T_safe, or the forecast exceeds T_critical.</summary>
    Critical,
}

/// <summary>How the twin's cooling recommendation is used.</summary>
public enum CoolingMode
{
    /// <summary>Cooling stays at the configured baseline level; no optimisation.</summary>
    Fixed,

    /// <summary>The optimiser runs and recommends a plan, but the baseline level is applied.</summary>
    Advisory,

    /// <summary>Model-predictive control: the first segment of each optimal plan is applied.</summary>
    Autonomous,
}

public enum ExperimentKind
{
    NumericalConvergence,
    Regularization,
    SensorDensity,
    NoiseRobustness,
    ForecastAccuracy,
    CoolingComparison,
}
