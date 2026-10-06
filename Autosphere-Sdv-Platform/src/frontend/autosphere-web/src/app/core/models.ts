// TypeScript mirrors of the AutoSphere API DTOs (enums are serialized as strings).

export type ConnectivityStatus = 'Unknown' | 'Online' | 'Offline';
export type HealthStatus = 'Unknown' | 'Healthy' | 'Warning' | 'Critical' | 'Offline';
export type EcuStatus = 'Unknown' | 'Online' | 'Warning' | 'Critical' | 'Updating' | 'Offline';
export type EcuType = 'VehicleControlUnit' | 'MotorControlUnit' | 'BatteryManagementSystem' | 'BodyControlModule' | 'CentralGateway';
export type Severity = 'Information' | 'Warning' | 'Critical';
export type SignalQuality = 'Valid' | 'OutOfRange' | 'Stale';
export type Role = 'Administrator' | 'Engineer' | 'Viewer';

export interface AuthResult {
  accessToken: string;
  expiresAt: string;
  userName: string;
  roles: Role[];
}

export interface Health {
  status: HealthStatus;
  score: number;
  summary: string | null;
  evaluatedAt: string | null;
}

export interface Ecu {
  ecuId: string;
  type: EcuType;
  name: string;
  status: EcuStatus;
  softwareVersion: string;
  hardwareVersion: string | null;
  lastHeartbeatAt: string | null;
  activeDtcCount: number;
  timeoutCount: number;
  e2EErrorCount: number;
}

export interface VehicleSummary {
  vehicleId: string;
  vin: string;
  model: string;
  modelYear: number;
  connectivity: ConnectivityStatus;
  lastSeenAt: string | null;
  health: Health;
  ecuCount: number;
}

export interface VehicleDetails extends Omit<VehicleSummary, 'ecuCount'> {
  registeredAt: string;
  connectivityChangedAt: string | null;
  gatewayId: string | null;
  gatewaySoftwareVersion: string | null;
  simulationMode: boolean;
  ecus: Ecu[];
}

export interface SignalValue {
  path: string;
  name: string;
  value: number;
  unit: string;
  sourceEcuId: string;
  timestamp: string;
  quality: SignalQuality;
  label?: string;
}

export interface VehicleSnapshot {
  speedKmh: number | null;
  motorRpm: number | null;
  motorTorqueNm: number | null;
  motorTemperatureC: number | null;
  batteryStateOfChargePercent: number | null;
  batteryVoltageV: number | null;
  batteryCurrentA: number | null;
  batteryTemperatureC: number | null;
  rangeKm: number | null;
  odometerKm: number | null;
  acceleratorPedalPercent: number | null;
  brakePressed: boolean | null;
  gear: 'Park' | 'Reverse' | 'Neutral' | 'Drive' | null;
  ignition: 'Off' | 'Accessory' | 'On' | 'Start' | null;
  charging: 'NotCharging' | 'Charging' | 'ChargeComplete' | 'Fault' | null;
  anyDoorOpen: boolean | null;
}

export interface GatewayStatistics {
  framesReceived: number;
  framesDecoded: number;
  framesRejected: number;
  framesPerSecond: number;
  averageDecodeMicroseconds: number;
  maxDecodeMicroseconds: number;
}

export interface VehicleTelemetry {
  vehicleId: string;
  sequence: number;
  gatewayTimestamp: string;
  backendReceivedAt: string;
  snapshot: VehicleSnapshot;
  signals: SignalValue[];
  statistics: GatewayStatistics | null;
  latency: { canToGatewayPublishMs: number; gatewayToBackendMs: number; canToBackendMs: number };
}

export interface TelemetrySeries {
  signalPath: string;
  points: { timestamp: string; value: number }[];
}

export interface DtcSnapshotValue {
  name: string;
  value: number;
  unit: string;
}

export type DtcRecordStatus = 'Active' | 'Resolved' | 'Cleared';

export interface DtcRecord {
  id: string;
  code: string;
  ecuId: string;
  description: string;
  severity: Severity;
  faultCategory: string;
  status: DtcRecordStatus;
  statusMask: number;
  confirmed: boolean;
  detectedAt: string;
  lastReportedAt: string;
  resolvedAt: string | null;
  clearedAt: string | null;
  occurrenceCount: number;
  snapshot: DtcSnapshotValue[];
  isClearable: boolean;
}

export interface UdsExchange {
  service: string;
  requestHex: string;
  responseHex: string | null;
  durationMs: number;
  negativeResponse: string | null;
}

export interface ReportedDtc {
  code: string;
  ecuId: string;
  statusMask: number;
  testFailed: boolean;
  confirmed: boolean;
  description: string;
  severity: Severity;
  faultCategory: string;
}

export interface EcuDiagnosticResult {
  ecuId: string;
  success: boolean;
  error: string | null;
  durationMs: number;
  exchanges: UdsExchange[];
  dtcs?: ReportedDtc[] | null;
  data?: { identifier: string; name: string; value: string; unit: string | null }[] | null;
}

export type DiagnosticOperation = 'FullScan' | 'ReadDtcs' | 'ClearDtcs' | 'EcuReset' | 'ReadDataByIdentifier' | 'ReadSoftwareVersion' | 'SessionControl' | 'TesterPresent';

export interface DiagnosticSession {
  correlationId: string;
  vehicleId: string;
  operation: DiagnosticOperation;
  ecuId: string | null;
  status: 'Pending' | 'Completed' | 'Failed' | 'TimedOut';
  requestedBy: string;
  requestedAt: string;
  completedAt: string | null;
  vehicleDurationMs: number | null;
  roundTripMs: number | null;
  error: string | null;
  diagnosis: string | null;
  results: EcuDiagnosticResult[];
}

export interface Alert {
  id: string;
  vehicleId: string;
  alertKey: string;
  source: 'Vehicle' | 'Backend';
  severity: Severity;
  category: string;
  message: string;
  ecuId: string | null;
  signalPath: string | null;
  value: number | null;
  threshold: number | null;
  raisedAt: string;
  clearedAt: string | null;
  acknowledgedAt: string | null;
  acknowledgedBy: string | null;
  isActive: boolean;
}

export type OtaStatus =
  | 'Created' | 'Pending' | 'Downloading' | 'Verifying' | 'Installing' | 'Restarting' | 'HealthChecking'
  | 'Succeeded' | 'Failed' | 'RollingBack' | 'RolledBack' | 'RollbackFailed' | 'Cancelled';

export const TERMINAL_OTA: OtaStatus[] = ['Succeeded', 'Failed', 'RolledBack', 'RollbackFailed', 'Cancelled'];

export interface SoftwarePackage {
  id: string;
  name: string;
  targetEcuType: EcuType;
  version: string;
  minimumCompatibleVersion: string;
  payloadSha256: string;
  payloadSize: number;
  signatureBase64: string;
  signingKeyId: string;
  createdAt: string;
  createdBy: string;
  releaseNotes: string | null;
  firmwareDescription: string | null;
}

export interface OtaDeployment {
  id: string;
  campaignId: string;
  vehicleId: string;
  ecuId: string;
  packageId: string;
  fromVersion: string;
  toVersion: string;
  installedVersion: string | null;
  status: OtaStatus;
  progressPercent: number;
  lastMessage: string | null;
  failureReason: string | null;
  simulateTransportCorruption: boolean;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  durationSeconds: number | null;
  events: { status: OtaStatus; progressPercent: number; message: string; timestamp: string }[];
}

export interface OtaCampaign {
  id: string;
  name: string;
  packageId: string;
  packageVersion: string;
  targetEcuType: EcuType;
  createdAt: string;
  createdBy: string;
  isCompleted: boolean;
  deployments: OtaDeployment[];
}

export type FaultType =
  | 'EcuCrash' | 'BatteryOverheat' | 'MotorOverheat' | 'InvalidSensorValue' | 'CanMessageLoss'
  | 'CanMessageDelay' | 'GatewayDisconnect' | 'MqttDisconnect' | 'CorruptedOtaPackage';

export interface FaultInjection {
  correlationId: string;
  vehicleId: string;
  fault: FaultType;
  action: 'Inject' | 'Clear';
  targetEcuId: string | null;
  durationSeconds: number | null;
  requestedBy: string;
  requestedAt: string;
  accepted: boolean | null;
  result: string | null;
  handledBy: string | null;
  acknowledgedAt: string | null;
}

export interface UserAccount {
  id: string;
  userName: string;
  email: string | null;
  roles: Role[];
  isLockedOut: boolean;
}

export interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
  errors?: Record<string, string[]>;
}

export const ECU_TYPE_LABEL: Record<EcuType, string> = {
  VehicleControlUnit: 'Vehicle Control',
  MotorControlUnit: 'Motor Control',
  BatteryManagementSystem: 'Battery Management',
  BodyControlModule: 'Body Control',
  CentralGateway: 'Central Gateway',
};

/** VSS-inspired signal paths used by the dashboard. */
export const VSS = {
  speed: 'Vehicle.Speed',
  motorSpeed: 'Vehicle.Powertrain.ElectricMotor.Speed',
  motorTemperature: 'Vehicle.Powertrain.ElectricMotor.Temperature',
  soc: 'Vehicle.Powertrain.TractionBattery.StateOfCharge.Current',
  batteryTemperature: 'Vehicle.Powertrain.TractionBattery.Temperature.Average',
} as const;
