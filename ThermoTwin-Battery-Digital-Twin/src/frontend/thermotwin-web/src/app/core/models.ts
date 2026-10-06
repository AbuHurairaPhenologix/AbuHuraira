// TypeScript mirrors of the ThermoTwin API contracts (camelCase JSON, enums as strings).

export type SimulationStatus = 'Pending' | 'Running' | 'Paused' | 'Completed' | 'Stopped' | 'Failed';
export type ThermalRisk = 'Normal' | 'Elevated' | 'Warning' | 'Critical';
export type CoolingMode = 'Fixed' | 'Advisory' | 'Autonomous';
export type TimeScheme = 'ExplicitEuler' | 'ImplicitEuler' | 'CrankNicolson';
export type ExperimentKind =
  | 'NumericalConvergence'
  | 'Regularization'
  | 'SensorDensity'
  | 'NoiseRobustness'
  | 'ForecastAccuracy'
  | 'CoolingComparison'
  | 'FemVerification'
  | 'AdjointGradientCheck'
  | 'OptimizationBenchmark'
  | 'ReducedOrderModel'
  | 'ReducedOrderControl'
  | 'ParameterIdentifiability';
export type CoolingOptimizerKind = 'PenaltyFiniteDifference' | 'AdjointFullOrder' | 'AdjointReducedOrder';

export interface FieldDto {
  key: string;
  label: string;
  unit: string;
  nx: number;
  ny: number;
  lengthX: number;
  lengthY: number;
  min: number;
  max: number;
  values: number[][];
}

export interface TemperatureStats {
  max: number;
  min: number;
  mean: number;
  spread: number;
  maxX: number;
  maxY: number;
}

export interface HotspotDto {
  x: number;
  y: number;
  peakPower: number;
  areaMm2: number | null;
  prominence: number | null;
}

export interface SensorReadingDto {
  id: string;
  x: number;
  y: number;
  value: number;
  trueValue: number;
}

export interface CoolingDto {
  level: number;
  baselineLevel: number;
  recommendedLevel: number | null;
  recommendedPlan: number[] | null;
  segmentDuration: number;
  powerWatts: number;
  energyJoules: number;
  plannedEnergyJoules: number | null;
  optimizerEvaluations: number | null;
  optimizerMs: number | null;
  optimizer: CoolingOptimizerKind | null;
  adjointSolves: number | null;
  projectedGradientNorm: number | null;
  romValidationError: number | null;
  romFallback: boolean | null;
}

export interface ForecastDto {
  issuedAt: number;
  times: number[];
  plannedMax: number[];
  unmitigatedMax: number[];
  plannedPeak: number;
  unmitigatedPeak: number;
  plannedPeakTime: number;
}

export interface EstimationDto {
  lastUpdate: number;
  regularization: string;
  lambdaSelection: string;
  relativeLambda: number;
  residualNorm: number;
  solutionSeminorm: number;
  measurementCount: number;
  unknowns: number;
  temperatureRmse: number;
  sourceRelativeError: number;
  solveMs: number;
}

export interface TwinHistoryPoint {
  time: number;
  trueMax: number;
  trueMean: number;
  trueMin: number;
  estimatedMax: number | null;
  estimatedMean: number | null;
  counterfactualMax: number;
  coolingLevel: number;
  coolingPower: number;
  cumulativeEnergy: number;
  leadPrediction: number | null;
  temperatureRmse: number | null;
  sourceRelativeError: number | null;
  hotspotErrorMm: number | null;
  sensors: number[];
  risk: ThermalRisk;
  loadFactor: number;
}

export interface TwinFrame {
  runId: string;
  scenarioKey: string;
  scenarioName: string;
  status: SimulationStatus;
  mode: CoolingMode;
  step: number;
  totalSteps: number;
  time: number;
  duration: number;
  loadFactor: number;
  truth: TemperatureStats;
  estimate: TemperatureStats | null;
  estimatedHotspot: HotspotDto | null;
  trueHotspots: HotspotDto[];
  hotspotErrorMm: number | null;
  risk: ThermalRisk;
  mitigationActive: boolean;
  safeTemperature: number;
  criticalTemperature: number;
  counterfactualMax: number;
  cooling: CoolingDto;
  forecast: ForecastDto | null;
  estimation: EstimationDto | null;
  sensors: SensorReadingDto[];
  fields: Record<string, FieldDto> | null;
  newHistory: TwinHistoryPoint[];
}

// ---------- Scenario ----------

export interface HotspotSettings {
  x: number;
  y: number;
  peakPower: number;
  radius: number;
}

export interface ScenarioDefinition {
  key: string;
  name: string;
  description: string;
  geometry: { lengthX: number; lengthY: number; nx: number; ny: number; plantRefinement: number };
  material: { density: number; specificHeat: number; conductivity: number; thickness: number };
  environment: {
    ambientTemperature: number;
    initialTemperature: number;
    edgeCondition: 'Dirichlet' | 'Neumann' | 'Robin';
    edgeHeatTransferCoefficient: number;
  };
  cooling: {
    coolantTemperature: number;
    minHeatTransferCoefficient: number;
    maxHeatTransferCoefficient: number;
    ratedPowerWatts: number;
    baselineLevel: number;
    mode: CoolingMode;
  };
  heatSource: {
    uniformJouleHeating: number;
    hotspots: HotspotSettings[];
    loadProfile: 'FastChargeCcCv' | 'Constant';
    constantCurrentEnd: number;
    taperFactor: number;
  };
  sensors: { count: number; noiseStd: number; seed: number };
  solver: { scheme: TimeScheme; timeStep: number; duration: number };
  estimator: {
    basisNodesX: number;
    basisNodesY: number;
    regularization: 'Identity' | 'Gradient' | 'Laplacian';
    lambdaSelection: 'Fixed' | 'LCurve' | 'Discrepancy' | 'Gcv';
    fixedLambda: number;
    estimationInterval: number;
  };
  control: {
    safeTemperature: number;
    criticalTemperature: number;
    controlMargin: number;
    horizonSegments: number;
    segmentDuration: number;
    predictionTimeStep: number;
    controlInterval: number;
    forecastLead: number;
    optimizer: CoolingOptimizerKind;
    romModes: number;
    romValidationThreshold: number;
  };
  playback: { stepsPerFrame: number; frameIntervalMs: number };
}

export interface StabilityReport {
  scheme: TimeScheme;
  timeStep: number;
  fourierNumber: number;
  spectralRadiusBound: number;
  spectralRadiusEstimate: number;
  explicitCriticalTimeStep: number;
  classicalExplicitLimit: number;
  amplificationFactor: number;
  stiffModeAmplification: number;
  isStable: boolean;
}

export interface StabilityAnalysis {
  diffusivity: number;
  diffusionTimeScale: number;
  timeStep: number;
  grids: { grid: string; nx: number; ny: number; dx: number; dy: number; schemes: StabilityReport[] }[];
  valid: boolean;
  issues: string[];
}

export interface SimulationRunDto {
  id: string;
  name: string;
  scenarioKey: string;
  status: SimulationStatus;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  simulatedSeconds: number;
  peakTemperature: number;
  peakCounterfactualTemperature: number;
  coolingEnergyJoules: number;
  finalTemperatureRmse: number | null;
  finalSourceRelativeError: number | null;
  hotspotLocalizationErrorMm: number | null;
  failureReason: string | null;
}

// ---------- Experiments ----------

export interface ExperimentSummary {
  id: string;
  kind: ExperimentKind;
  title: string;
  summary: string;
  createdAt: string;
  durationMs: number;
}

export interface ExperimentJobStatus {
  kind: ExperimentKind;
  state: 'Queued' | 'Running' | 'Completed' | 'Failed';
  updatedAt: string;
  error: string | null;
}

export interface ExperimentOverview {
  experiments: ExperimentSummary[];
  queue: ExperimentJobStatus[];
}

export interface ExperimentDetail<T> extends ExperimentSummary {
  result: T;
}

export interface ConvergenceRow {
  scheme: TimeScheme;
  nx: number;
  ny: number;
  dx: number;
  timeStep: number;
  steps: number;
  rmse: number;
  maxError: number;
  runtimeMs: number;
  observedOrderRmse: number | null;
  observedOrderMax: number | null;
}

export interface ConvergenceResult {
  spatial: ConvergenceRow[];
  temporal: ConvergenceRow[];
  stabilityProbes: { scheme: TimeScheme; timeStepRatio: number; timeStep: number; steps: number; finalAmplitude: number; diverged: boolean }[];
  stabilityReports: StabilityReport[];
  energyConservation: { scheme: TimeScheme; injectedEnergy: number; storedEnergyChange: number; relativeImbalance: number }[];
  performance: { label: string; nx: number; ny: number; scheme: string; steps: number; totalMs: number; msPerStep: number }[];
}

export interface ReconstructionMetrics {
  temperatureRmse: number;
  temperatureMaxError: number;
  sourceRelativeError: number;
  hotspotErrorMm: number | null;
  estimatedHotspotX: number | null;
  estimatedHotspotY: number | null;
  peakSourceKw: number;
}

export interface LCurveSample {
  relativeLambda: number;
  residualNorm: number;
  solutionSeminorm: number;
  curvature: number;
  gcv: number;
  sourceRelativeError: number;
  temperatureRmse: number;
  hotspotErrorMm: number | null;
}

export interface RegularizationResult {
  measurementCount: number;
  unknowns: number;
  sensorCount: number;
  noiseStd: number;
  discrepancyTarget: number;
  studies: {
    regularization: string;
    curve: LCurveSample[];
    choices: { method: string; relativeLambda: number; residualNorm: number; solutionSeminorm: number; metrics: ReconstructionMetrics }[];
  }[];
  timeline: {
    time: number;
    relativeLambda: number;
    temperatureRmse: number;
    sourceRelativeError: number;
    hotspotErrorMm: number | null;
    trueMax: number;
    estimatedMax: number;
  }[];
  fields: Record<string, FieldDto>;
  sensors: SensorReadingDto[];
  trueHotspots: HotspotDto[];
}

export interface SensitivityResult {
  parameter: string;
  unit: string;
  rows: { parameter: number; relativeLambda: number; metrics: ReconstructionMetrics }[];
}

export interface ForecastResult {
  forecasts: {
    issuedAt: number;
    horizon: number;
    times: number[];
    predicted: number[];
    actual: number[];
    meanAbsoluteError: number;
    errorAtHorizon: number;
    predictedPeak: number;
    actualPeak: number;
  }[];
  actualTimes: number[];
  actualMax: number[];
}

export interface StrategyOutcome {
  key: string;
  name: string;
  description: string;
  peakTemperature: number;
  coolingEnergyJoules: number;
  maxViolation: number;
  timeAboveLimit: number;
  violationIntegral: number;
  objective: number;
  feasible: boolean;
  times: number[];
  maxTemperature: number[];
  coolingLevel: number[];
}

export interface CoolingComparisonResult {
  safeTemperature: number;
  controlTemperature: number;
  referenceEnergyJoules: number;
  minimumFeasibleConstantLevel: number;
  optimizedPlan: number[];
  segmentDuration: number;
  strategies: StrategyOutcome[];
  optimizationHistory: { iteration: number; penalty: number; objective: number; energyTerm: number; violation: number; peakTemperature: number }[];
  optimizationMs: number;
  optimizationEvaluations: number;
  optimizationAdjointSolves: number;
}
