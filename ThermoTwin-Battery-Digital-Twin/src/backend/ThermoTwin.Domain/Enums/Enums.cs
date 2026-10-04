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

    /// <summary>P1 finite-element convergence and FVM–FEM cross-validation.</summary>
    FemVerification,

    /// <summary>Discrete adjoint gradient against finite differences; gradient cost versus number of controls.</summary>
    AdjointGradientCheck,

    /// <summary>PDE-constrained optimisation with adjoint vs finite-difference gradients, KKT diagnostics.</summary>
    OptimizationBenchmark,

    /// <summary>POD spectrum, full-order vs reduced-order accuracy and speed, ROM-accelerated optimisation.</summary>
    ReducedOrderModel,

    /// <summary>Closed-loop MPC on the plant with the legacy, full-order adjoint and reduced-order adjoint optimisers.</summary>
    ReducedOrderControl,

    /// <summary>Local sensitivity, Fisher information, collinearity and bounded parameter estimation.</summary>
    ParameterIdentifiability,
}
