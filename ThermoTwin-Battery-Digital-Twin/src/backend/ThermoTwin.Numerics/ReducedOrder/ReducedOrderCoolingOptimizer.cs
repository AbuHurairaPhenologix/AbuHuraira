using System.Diagnostics;
using ThermoTwin.Numerics.Optimization;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Physics;
using ThermoTwin.Numerics.Prediction;

namespace ThermoTwin.Numerics.ReducedOrder;

/// <summary>Diagnostics of one ROM-accelerated optimisation.</summary>
/// <param name="ScreenedCells">|C| — cells at which the state constraint was enforced on the ROM.</param>
/// <param name="CorrectionRounds">Defect-correction rounds (constraint tightened by the certified FOM–ROM peak discrepancy).</param>
/// <param name="ConstraintShift">Total tightening of T_safe applied on the ROM [K].</param>
/// <param name="Repaired">True if a final uniform-shift repair on the full-order model was needed.</param>
/// <param name="RomPeak">Peak temperature of the final ROM plan predicted by the ROM (screened cells) [°C].</param>
/// <param name="FullOrderPeak">Peak temperature of the same plan on the full-order model [°C].</param>
/// <param name="ValidationError">|ROM peak − FOM peak| [K] — compared with the acceptance threshold.</param>
/// <param name="FellBack">True if the ROM was rejected and the full-order optimiser was used.</param>
public sealed record RomOptimizationReport(
    int Modes,
    int ScreenedCells,
    int CorrectionRounds,
    double ConstraintShift,
    bool Repaired,
    int RomForwardSolves,
    int RomAdjointSolves,
    int FullOrderForwardSolves,
    int FullOrderAdjointSolves,
    double RomPeak,
    double FullOrderPeak,
    double ValidationError,
    bool FellBack,
    string? FallbackReason,
    double RomMs,
    double CertificationMs,
    double FallbackMs);

/// <summary>
/// ROM-accelerated, full-order-certified cooling optimisation ("high-fidelity PDE → POD surrogate → fast
/// optimisation"):
/// <list type="number">
/// <item><b>Screening.</b> The ROM simulates the initial guess; the state constraint is kept only on cells that come
///   within δ of the instantaneous maximum temperature at some time. Cooling acts uniformly on the cell
///   (σ(u)I), so it shifts temperatures without reordering the hot region much; the screened set is a
///   heuristic that the certification step checks.</item>
/// <item><b>Reduced optimisation.</b> <see cref="AdjointCoolingOptimizer"/> on the POD–Galerkin model (adjoint in
///   reduced coordinates: O(r + |C|r) per step instead of O(np)).</item>
/// <item><b>Certification.</b> One full-order forward solve of the ROM plan.</item>
/// <item><b>Defect correction.</b> If the full model violates T_safe, the ROM constraint is tightened by the certified
///   discrepancy, T_safe^ROM ← T_safe^ROM − (peak_FOM − T_safe), and the ROM problem is re-solved from the current plan
///   (at most <see cref="MaxCorrections"/> rounds). A residual violation is removed by a uniform-shift bisection on
///   the full model, so the returned plan is always feasible <i>on the high-fidelity model</i>.</item>
/// <item><b>Fallback.</b> If the ROM peak error |peak_ROM − peak_FOM| exceeds the validation threshold, the ROM is
///   rejected and the full-order adjoint optimiser runs, warm-started from the ROM plan. The ROM is never used
///   silently when its error is not acceptable.</item>
/// </list>
/// </summary>
public sealed class ReducedOrderCoolingOptimizer : ICoolingOptimizer
{
    private readonly ReducedThermalModel _rom;
    private readonly FullOrderThermalDynamics _fullOrder;
    private readonly LoadProfile _load;
    private readonly CoolingModel _cooling;
    private readonly PdeOptimizerSettings _settings;

    public ReducedOrderCoolingOptimizer(
        ReducedThermalModel rom,
        HeatEquationSolver fullOrderSolver,
        LoadProfile load,
        CoolingModel cooling,
        PdeOptimizerSettings? settings = null,
        double validationThreshold = 0.25,
        double screeningBand = 2.0)
    {
        if (Math.Abs(rom.TimeStep - fullOrderSolver.TimeStep) > 1e-12)
        {
            throw new ArgumentException("ROM and full-order model must use the same time step.", nameof(rom));
        }

        _rom = rom;
        _fullOrder = new FullOrderThermalDynamics(fullOrderSolver);
        _load = load;
        _cooling = cooling;
        _settings = settings ?? new PdeOptimizerSettings();
        ValidationThreshold = validationThreshold;
        ScreeningBand = screeningBand;
    }

    /// <summary>Largest accepted |peak_ROM − peak_FOM| [K].</summary>
    public double ValidationThreshold { get; }

    /// <summary>δ of the constraint screening [K].</summary>
    public double ScreeningBand { get; }

    /// <summary>Maximum number of defect-correction rounds.</summary>
    public int MaxCorrections { get; init; } = 3;

    public ReducedThermalModel Rom => _rom;

    public string Name => $"ROM-accelerated PDE-constrained optimiser ({_rom.Name}, FOM-certified)";

    public RomOptimizationReport? LastReport { get; private set; }

    public CoolingOptimizationResult Optimize(
        ReadOnlySpan<double> initialState,
        ReadOnlySpan<double> sourceShape,
        double startTime,
        CoolingOptimizationOptions options,
        double[]? initialGuess = null)
    {
        var initial = initialState.ToArray();
        var shape = sourceShape.ToArray();
        var plan = initialGuess is { Length: > 0 } g && g.Length == options.Segments
            ? g.Select(v => Math.Clamp(v, 0, 1)).ToArray()
            : Enumerable.Repeat(0.5, options.Segments).ToArray();

        var sw = Stopwatch.StartNew();
        var cells = ScreenConstraintCells(initial, shape, startTime, new CoolingPlan(options.SegmentDuration, plan), options.SafeTemperature);
        var restricted = _rom.WithOutputCells(cells);
        var romOptimizer = new AdjointCoolingOptimizer(restricted, _load, _cooling, _settings);
        var fullObjective = new PdeConstrainedObjective(_fullOrder, _load, _cooling, initial, shape, startTime,
            new ControlProblemSettings(options.SafeTemperature, options.Segments, options.SegmentDuration, options.SmoothnessWeight));
        var romMs = sw.Elapsed.TotalMilliseconds;

        var romOptions = options;
        CoolingOptimizationResult? romResult = null;
        ObjectiveEvaluation? certified = null;
        int romForward = 0, romAdjoint = 0, fullForward = 0, rounds = 0;
        double certificationMs = 0, validationError = 0;
        while (true)
        {
            sw.Restart();
            romResult = romOptimizer.Optimize(initial, shape, startTime, romOptions, plan);
            romMs += sw.Elapsed.TotalMilliseconds;
            romForward += romResult.Evaluations;
            romAdjoint += romResult.AdjointSolves;
            plan = romResult.Plan.Levels;

            sw.Restart();
            certified = fullObjective.Evaluate(plan, 0);
            fullForward++;
            certificationMs += sw.Elapsed.TotalMilliseconds;
            validationError = Math.Abs(romResult.PeakTemperature - certified.PeakTemperature);
            if (validationError > ValidationThreshold || certified.MaxViolation <= options.FeasibilityTolerance || rounds >= MaxCorrections)
            {
                break;
            }

            // Defect correction: tighten the ROM constraint by the certified violation (plus half the tolerance).
            rounds++;
            romOptions = romOptions with { SafeTemperature = romOptions.SafeTemperature - certified.MaxViolation - 0.5 * options.FeasibilityTolerance };
        }

        var shift = options.SafeTemperature - romOptions.SafeTemperature;
        if (validationError > ValidationThreshold)
        {
            var reason = $"ROM peak error {validationError:0.000} K exceeds {ValidationThreshold:0.00} K";
            sw.Restart();
            var fallback = new AdjointCoolingOptimizer(_fullOrder, _load, _cooling, _settings).Optimize(initial, shape, startTime, options, plan);
            LastReport = new RomOptimizationReport(_rom.Modes, cells.Length, rounds, shift, false, romForward, romAdjoint,
                fullForward + fallback.Evaluations, fallback.AdjointSolves, romResult.PeakTemperature, certified.PeakTemperature,
                validationError, true, reason, romMs, certificationMs, sw.Elapsed.TotalMilliseconds);
            return fallback with { Model = $"Full-order fallback ({reason})" };
        }

        var repaired = false;
        if (certified.MaxViolation > options.FeasibilityTolerance)
        {
            sw.Restart();
            (plan, var evaluations) = RepairOnFullOrder(fullObjective, plan, options.FeasibilityTolerance);
            fullForward += evaluations;
            certified = fullObjective.Evaluate(plan, 0);
            fullForward++;
            certificationMs += sw.Elapsed.TotalMilliseconds;
            repaired = true;
        }

        LastReport = new RomOptimizationReport(_rom.Modes, cells.Length, rounds, shift, repaired, romForward, romAdjoint, fullForward, 0,
            romResult.PeakTemperature, certified.PeakTemperature, validationError, false, null, romMs, certificationMs, 0);
        return romResult with
        {
            Plan = new CoolingPlan(options.SegmentDuration, plan),
            Objective = certified.Objective,
            CoolingEnergy = certified.EnergyJoules,
            PeakTemperature = certified.PeakTemperature,
            MaxViolation = certified.MaxViolation,
            Feasible = certified.MaxViolation <= options.FeasibilityTolerance,
            Evaluations = romForward,
            AdjointSolves = romAdjoint,
            Forecast = certified.Forecast,
            Model = restricted.Name + ", certified on the full-order model",
        };
    }

    /// <summary>Cells within <see cref="ScreeningBand"/> of the instantaneous maximum at some time of a ROM run of the plan.</summary>
    public int[] ScreenConstraintCells(double[] initial, double[] shape, double startTime, CoolingPlan plan, double safeTemperature)
    {
        var trajectory = ThermalTrajectorySimulator.Simulate(_rom, _load, initial, shape, startTime, plan);
        var cells = new HashSet<int>();
        foreach (var field in trajectory.Fields.Skip(1))
        {
            var max = field.Max();
            if (max < safeTemperature - 2 * ScreeningBand)
            {
                continue; // far from the limit: no candidate active points at this time
            }

            for (var i = 0; i < field.Length; i++)
            {
                if (field[i] >= max - ScreeningBand)
                {
                    cells.Add(i);
                }
            }
        }

        if (cells.Count == 0)
        {
            // Nothing comes near the limit: keep the hottest cell of the final state so the problem stays well posed.
            var final = trajectory.Fields[^1];
            cells.Add(Array.IndexOf(final, final.Max()));
        }

        return [.. cells.Order()];
    }

    private static (double[] Plan, int Evaluations) RepairOnFullOrder(PdeConstrainedObjective objective, double[] levels, double tolerance)
    {
        var evaluations = 0;
        double[] Shift(double delta) => levels.Select(v => Math.Clamp(v + delta, 0, 1)).ToArray();
        bool Feasible(double delta)
        {
            evaluations++;
            return objective.Evaluate(Shift(delta), 0).MaxViolation <= tolerance;
        }

        if (!Feasible(1))
        {
            return (Shift(1), evaluations);
        }

        double lo = 0, hi = 1;
        for (var it = 0; it < 20; it++)
        {
            var mid = 0.5 * (lo + hi);
            if (Feasible(mid))
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return (Shift(hi), evaluations);
    }
}
