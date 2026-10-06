// Typed contracts of the ASP.NET Core API (/api/v1). Field names match the backend DTOs (camelCase JSON).

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

export const REVIEW_STATES = [
  'Unreviewed',
  'ConfirmedIssue',
  'BenignChange',
  'FalsePositive',
  'DuplicateAlert',
  'InsufficientEvidence',
] as const;
export type ReviewState = (typeof REVIEW_STATES)[number];

export const FEATURE_NAMES = [
  'request_count',
  'error_rate',
  'avg_duration_ms',
  'p95_duration_ms',
  'auth_failure_rate',
  'dependency_failure_count',
  'retry_count',
  'endpoint_entropy',
] as const;

export interface AnomalyListItem {
  anomalyId: string;
  windowId: string;
  service: string;
  environment: string;
  windowStartUtc: string;
  windowEndUtc: string;
  score: number;
  threshold: number;
  modelId: string;
  modelVersion: string;
  reviewState: ReviewState;
  reasonSummary: string;
  createdAtUtc: string;
}

export interface FeatureWindow {
  windowId: string;
  service: string;
  environment: string;
  windowStartUtc: string;
  windowEndUtc: string;
  windowSizeMinutes: number;
  featureSchemaVersion: string;
  features: Record<string, number>;
  eventCount: number;
  lateEventCount: number;
  createdAtUtc: string;
  scoringStatus: string;
  scoringAttempts: number;
  nextScoringAttemptUtc: string | null;
  lastScoringError: string | null;
}

export interface FeatureDeviation {
  feature: string;
  label: string;
  value: number;
  baselineMean: number | null;
  baselineStd: number | null;
  zScore: number | null;
  direction: string | null;
}

export interface Review {
  id: string;
  anomalyId: string;
  previousState: ReviewState;
  outcome: ReviewState;
  note: string | null;
  reviewer: string;
  createdAtUtc: string;
}

export interface ScoringRecord {
  id: string;
  windowId: string;
  modelId: string;
  modelVersion: string;
  score: number;
  threshold: number;
  isAnomaly: boolean;
  reasonSummary: string;
  scoredAtUtc: string;
}

export interface AnomalyDetail {
  anomaly: AnomalyListItem;
  window: FeatureWindow;
  modelAlgorithm: string;
  modelValidationThreshold: number;
  modelIsActive: boolean;
  featureDeviations: FeatureDeviation[];
  correlationIds: string[];
  eventTypeCounts: Record<string, number>;
  reviews: Review[];
  otherScores: ScoringRecord[];
}

export interface EventDocument {
  id: string;
  eventId: string;
  eventTimestampUtc: string;
  serviceName: string;
  environment: string;
  eventType: string;
  endpointGroup: string;
  statusCode: number | null;
  durationMs: number | null;
  errorFlag: boolean;
  authenticationResult: string;
  dependencyName: string | null;
  retryCount: number;
  correlationId: string | null;
  isLate: boolean;
  featureWindowId: string | null;
}

export interface EventSearchResult {
  items: EventDocument[];
  total: number;
  page: number;
  pageSize: number;
  source: 'opensearch' | 'postgresql-fallback';
}

export interface TrendPoint {
  day: string;
  windows: number;
  anomalies: number;
}

export interface ActiveModel {
  modelId: string;
  modelVersion: string;
  algorithm: string;
  validationThreshold: number;
  activatedAtUtc: string | null;
}

export interface DashboardStats {
  processedWindows: number;
  scoredWindows: number;
  pendingWindows: number;
  deferredWindows: number;
  rejectedWindows: number;
  anomalies: number;
  anomalyRate: number;
  unreviewed: number;
  byReviewState: Record<string, number>;
  events: number;
  quarantinedEvents: number;
  lateEvents: number;
  unindexedEvents: number;
  activeModel: ActiveModel | null;
  trend: TrendPoint[];
  services: { service: string; environment: string; windows: number; anomalies: number }[];
}

export interface ComponentHealth {
  name: string;
  status: 'Healthy' | 'Degraded' | 'Unhealthy';
  description: string | null;
  durationMs: number;
  data: Record<string, unknown> | null;
}

export interface WorkerStatus {
  name: string;
  lastRunUtc: string | null;
  lastSuccessUtc: string | null;
  lastResult: string | null;
  lastError: string | null;
  runs: number;
  failures: number;
}

export interface SystemStatus {
  status: string;
  checkedAtUtc: string;
  components: ComponentHealth[];
  workers: WorkerStatus[];
  pipeline: { windowSizeMinutes: number; allowedLatenessSeconds: number; scoringBatchSize: number; backgroundWorkersEnabled: boolean };
  eventQueueDepth: number;
  eventQueueDropped: number;
  stats: DashboardStats;
}

export interface ModelVersion {
  modelId: string;
  modelVersion: string;
  algorithm: string;
  featureSchemaVersion: string;
  trainingPeriodStartUtc: string | null;
  trainingPeriodEndUtc: string | null;
  randomSeed: number;
  libraryVersions: Record<string, string>;
  parameters: Record<string, unknown>;
  validationThreshold: number;
  thresholdObjective: string;
  artifactPath: string;
  artifactSha256: string;
  productionEligible: boolean;
  validationMetrics: Record<string, unknown> | null;
  trainedAtUtc: string;
  createdAtUtc: string;
  registeredBy: string;
  isActive: boolean;
  activatedAtUtc: string | null;
  activatedBy: string | null;
  deactivatedAtUtc: string | null;
  deactivatedBy: string | null;
  scoredWindows: number;
  anomalies: number;
}

export interface RegistryEntry {
  metadata: {
    modelId: string;
    modelVersion: string;
    algorithm: string;
    featureSchemaVersion: string;
    validationThreshold: number;
    productionEligible: boolean;
    validationMetrics: Record<string, unknown> | null;
    createdAtUtc: string;
    artifactExists: boolean;
    activeInService: boolean;
    recommended: boolean;
  };
  registeredInBackend: boolean;
  backendModelId: string | null;
}

export interface TrainingJob {
  jobId: string;
  status: string;
  algorithm: string | null;
  modelVersions: string[];
  error: string | null;
  createdAtUtc: string;
  completedAtUtc: string | null;
}

export interface TokenResponse {
  accessToken: string;
  tokenType: string;
  expiresIn: number;
  username: string;
  roles: string[];
}

export interface PipelineRunSummary {
  eventsIndexed: number;
  indexingError: string | null;
  aggregation: { windowsCreated: number; eventsAggregated: number; lateEvents: number };
  scoring: { outcome: string; windowsScored: number; anomaliesCreated: number; windowsDeferred: number; message: string | null }[];
}
