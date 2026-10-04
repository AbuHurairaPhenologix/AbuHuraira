// TypeScript mirrors of the mathematical experiment payloads (FEM, adjoint, PDE-constrained optimisation, POD, identifiability).
import { FieldDto } from './models';

// ---------- FEM verification ----------

export interface MethodConvergenceRow {
  method: 'FVM' | 'FEM';
  problem: string;
  nx: number;
  ny: number;
  h: number;
  dofs: number;
  nonZeros: number;
  halfBandwidth: number;
  timeStep: number;
  steps: number;
  rmse: number;
  maxError: number;
  l2Error: number | null;
  h1Error: number | null;
  orderRmse: number | null;
  orderMax: number | null;
  orderL2: number | null;
  orderH1: number | null;
  runtimeMs: number;
}

export interface BatteryComparisonRow {
  nx: number;
  ny: number;
  h: number;
  fvmDofs: number;
  femDofs: number;
  fvmNonZeros: number;
  femNonZeros: number;
  fvmPeak: number;
  femPeak: number;
  fvmFinalMax: number;
  femFinalMax: number;
  fvmFinalMean: number;
  femFinalMean: number;
  fieldRmsDifference: number;
  fieldMaxDifference: number;
  fvmRuntimeMs: number;
  femRuntimeMs: number;
  fvmEnergyJoules: number;
  femEnergyJoules: number;
}

export interface FemMeshDto {
  nx: number;
  ny: number;
  lengthX: number;
  lengthY: number;
  nodes: number;
  elements: number;
  boundarySegments: number;
  nodeXy: number[][];
  triangles: number[][];
}

export interface DiscretisationSystem {
  method: string;
  unknowns: string;
  dofs: number;
  nonZeros: number;
  halfBandwidth: number;
  nonZerosPerRow: number;
}

export interface FemVerificationResult {
  eigenmode: MethodConvergenceRow[];
  manufactured: MethodConvergenceRow[];
  battery: BatteryComparisonRow[];
  coolingLevel: number;
  snapshotTime: number;
  fields: Record<string, FieldDto>;
  displayMesh: FemMeshDto;
  systems: DiscretisationSystem[];
}

// ---------- Adjoint gradient validation ----------

export interface GradientCheckRow {
  model: string;
  vector: number;
  epsilon: number;
  relativeError: number;
  maxAbsoluteError: number;
  adjointNorm: number;
  finiteDifferenceNorm: number;
}

export interface GradientCostRow {
  controls: number;
  segmentDuration: number;
  adjointMs: number;
  forwardDifferenceMs: number;
  centralDifferenceMs: number;
  adjointSolves: number;
  forwardDifferenceSolves: number;
  centralDifferenceSolves: number;
  relativeDifference: number;
}

export interface AdjointCheckResult {
  model: string;
  stateDimension: number;
  timeSteps: number;
  controls: number;
  mu: number;
  safeTemperature: number;
  rows: GradientCheckRow[];
  taylor: { epsilon: number; zeroOrderRemainder: number; firstOrderRemainder: number }[];
  components: { segment: number; control: number; adjoint: number; finiteDifference: number }[];
  cost: GradientCostRow[];
  bestRelativeError: number;
  bestEpsilon: number;
  romBestRelativeError: number;
}

// ---------- PDE-constrained optimisation ----------

export interface KktDiagnostics {
  mu: number;
  gradientNorm: number;
  projectedGradientNorm: number;
  activeLower: number;
  activeUpper: number;
  inactive: number;
  maxInactiveGradient: number;
  minLowerMultiplier: number;
  minUpperMultiplier: number;
  maxStateViolation: number;
  stateActivePoints: number;
  stateMultiplierSum: number;
  gradient: number[];
  controls: number[];
}

export interface PdeOptimizationIteration {
  iteration: number;
  mu: number;
  value: number;
  objective: number;
  penalty: number;
  peakTemperature: number;
  projectedGradientNorm: number | string;
  stepLength: number | string;
  forwardSolves: number;
  adjointSolves: number;
  elapsedMs: number | string;
}

export interface OptimizationMethodOutcome {
  key: string;
  name: string;
  gradient: string;
  model: string;
  objective: number;
  energyJoules: number;
  modelPeak: number;
  modelViolation: number;
  plantPeak: number;
  plantViolation: number;
  feasible: boolean;
  iterations: number;
  forwardSolves: number;
  adjointSolves: number;
  fullOrderSolveEquivalents: number;
  runtimeMs: number;
  plan: number[];
  kkt: KktDiagnostics;
  history: PdeOptimizationIteration[];
  times: number[];
  modelMaxTemperature: number[];
}

export interface OptimizationBenchmarkResult {
  safeTemperature: number;
  controlTemperature: number;
  segments: number;
  segmentDuration: number;
  predictionTimeStep: number;
  stateDimension: number;
  penaltySchedule: number[];
  smoothnessWeight: number;
  referenceEnergyJoules: number;
  methods: OptimizationMethodOutcome[];
  adjointSpeedup: number;
  solveReduction: number;
}

// ---------- Reduced-order model ----------

export interface PodSpectrumPoint {
  family: string;
  index: number;
  eigenvalue: number;
  cumulativeEnergy: number;
}

export interface RomComparisonRow {
  family: string;
  test: string;
  modes: number;
  capturedEnergy: number;
  rmse: number;
  maxError: number;
  projectionRmse: number;
  peakError: number;
  fullOrderMs: number;
  reducedOrderMs: number;
  speedup: number;
  fullDimension: number;
}

export interface RomOptimizationRow {
  model: string;
  modes: number;
  runtimeMs: number;
  speedup: number;
  objective: number;
  objectiveGap: number;
  energyJoules: number;
  fullOrderPeak: number;
  feasible: boolean;
  screenedCells: number;
  correctionRounds: number;
  constraintShift: number;
  repaired: boolean;
  fellBack: boolean;
  fallbackReason: string | null;
  romSolves: number;
  fullOrderSolves: number;
  validationError: number;
}

export interface ReducedOrderResult {
  fullDimension: number;
  timeStep: number;
  training: { family: string; trainingRuns: number; snapshots: number; rank: number; trainingMs: number; podMs: number; sampleSteps: number[] }[];
  spectrum: PodSpectrumPoint[];
  comparison: RomComparisonRow[];
  selectedModes: number;
  selectionRule: string;
  modes: FieldDto[];
  fields: Record<string, FieldDto>;
  timeSeries: { times: number[]; fullOrderMax: number[]; selectedMax: number[]; coarseMax: number[]; coarseModes: number };
  optimization: RomOptimizationRow[];
}

export interface MpcRunOutcome {
  key: string;
  name: string;
  optimizer: string;
  plantPeak: number;
  energyJoules: number;
  timeAboveLimit: number;
  maxViolation: number;
  feasible: boolean;
  optimizations: number;
  totalOptimizerMs: number;
  meanOptimizerMs: number;
  forwardSolves: number;
  adjointSolves: number;
  fullOrderSolves: number;
  fallbacks: number;
  correctionRounds: number;
  repairs: number;
  meanValidationError: number;
  maxValidationError: number;
  times: number[];
  maxTemperature: number[];
  coolingLevel: number[];
}

export interface ReducedOrderControlResult {
  safeTemperature: number;
  romModes: number;
  romValidationThreshold: number;
  runs: MpcRunOutcome[];
}

// ---------- Parameter identifiability ----------

export interface SensitivityReport {
  parameters: string[];
  units: string[];
  nominal: number[];
  uncertainty: number[];
  observationCount: number;
  noiseStd: number;
  columnNorms: number[];
  signalToNoise: number[];
  correlation: number[][];
  singularValues: number[];
  conditionNumber: number;
  cramerRaoStd: number[];
  cramerRaoRelative: number[];
  parameterCorrelation: number[][];
  collinearity: { parameters: string[]; index: number }[];
  finiteDifferenceConsistency: number;
}

export interface EstimationResult {
  parameters: string[];
  truth: number[];
  initial: number[];
  estimate: number[];
  standardErrors: number[];
  relativeErrors: number[];
  atBound: boolean[];
  initialResidual: number;
  finalResidual: number;
  noiseEstimate: number;
  iterations: number;
  modelEvaluations: number;
  history: { iteration: number; residualNorm: number; damping: number; parameters: number[] }[];
}

export interface IdentifiabilityResult {
  report: SensitivityReport;
  traceSensor: string;
  traceTimes: number[];
  traces: { parameter: string; values: number[] }[];
  estimations: { key: string; name: string; description: string; result: EstimationResult }[];
  dataNoiseStd: number;
  dataSource: string;
}
