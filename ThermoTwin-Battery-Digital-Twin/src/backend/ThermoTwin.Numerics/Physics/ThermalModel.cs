using ThermoTwin.Numerics.Grid;

namespace ThermoTwin.Numerics.Physics;

/// <summary>
/// Thermophysical properties of the battery cell (effective, in-plane, homogenised).
/// </summary>
/// <param name="Density">ρ [kg/m³]</param>
/// <param name="SpecificHeat">cₚ [J/(kg·K)]</param>
/// <param name="Conductivity">k [W/(m·K)] — effective in-plane conductivity of the electrode stack.</param>
/// <param name="Thickness">δ [m] — cell thickness, used to convert face cooling into a volumetric sink.</param>
public sealed record MaterialProperties(double Density, double SpecificHeat, double Conductivity, double Thickness)
{
    /// <summary>ρ·cₚ [J/(m³·K)]</summary>
    public double VolumetricHeatCapacity => Density * SpecificHeat;

    /// <summary>α = k / (ρ cₚ) [m²/s]</summary>
    public double Diffusivity => Conductivity / VolumetricHeatCapacity;

    /// <summary>Representative large-format Li-ion pouch cell (in-plane properties).</summary>
    public static MaterialProperties LithiumIonPouchCell { get; } = new(2500, 1000, 20, 0.010);
}

public enum BoundaryKind
{
    /// <summary>Prescribed temperature T = T_b.</summary>
    Dirichlet,

    /// <summary>Prescribed outward heat flux −k ∂T/∂n = g (g = 0 is an insulated edge).</summary>
    Neumann,

    /// <summary>Convective edge −k ∂T/∂n = h (T − T∞).</summary>
    Robin,
}

/// <summary>Boundary condition on one edge of the rectangle.</summary>
/// <param name="Kind">Condition type.</param>
/// <param name="Value">Dirichlet: T_b [°C]; Neumann: outward flux g [W/m²]; Robin: T∞ [°C].</param>
/// <param name="HeatTransferCoefficient">Robin only: h [W/(m²·K)].</param>
public sealed record BoundaryCondition(BoundaryKind Kind, double Value, double HeatTransferCoefficient = 0)
{
    public static BoundaryCondition Insulated { get; } = new(BoundaryKind.Neumann, 0);

    public static BoundaryCondition FixedTemperature(double t) => new(BoundaryKind.Dirichlet, t);

    public static BoundaryCondition Convective(double ambient, double h) => new(BoundaryKind.Robin, ambient, h);

    /// <summary>Same condition with all inhomogeneous data set to zero (used for linear superposition).</summary>
    public BoundaryCondition Homogeneous() => this with { Value = 0 };
}

public sealed record BoundaryConditions(
    BoundaryCondition West,
    BoundaryCondition East,
    BoundaryCondition South,
    BoundaryCondition North)
{
    public static BoundaryConditions All(BoundaryCondition bc) => new(bc, bc, bc, bc);
}

/// <summary>
/// Active cooling through a cold plate on the cell face.
/// The face heat-transfer coefficient is h(u) = h_min + u (h_max − h_min) for a control level u ∈ [0, 1];
/// in the 2-D (depth-averaged) model it acts as the volumetric sink −(h(u)/δ)(T − T_c).
/// Pump/fan power follows the affinity law P(u) = P_rated · u³.
/// </summary>
public sealed record CoolingModel(
    double CoolantTemperature,
    double MinHeatTransferCoefficient,
    double MaxHeatTransferCoefficient,
    double RatedPower)
{
    public double HeatTransferCoefficient(double u) =>
        MinHeatTransferCoefficient + Math.Clamp(u, 0, 1) * (MaxHeatTransferCoefficient - MinHeatTransferCoefficient);

    /// <summary>Volumetric sink coefficient H(u) = h(u)/δ [W/(m³·K)].</summary>
    public double VolumetricCoefficient(double u, MaterialProperties material) =>
        HeatTransferCoefficient(u) / material.Thickness;

    /// <summary>Electrical power drawn by the cooling system [W].</summary>
    public double Power(double u)
    {
        var c = Math.Clamp(u, 0, 1);
        return RatedPower * c * c * c;
    }
}

/// <summary>Everything needed to assemble the semi-discrete heat equation on a grid.</summary>
public sealed record ThermalModel(
    Grid2D Grid,
    MaterialProperties Material,
    BoundaryConditions Boundaries,
    CoolingModel Cooling)
{
    public ThermalModel WithGrid(Grid2D grid) => this with { Grid = grid };
}
