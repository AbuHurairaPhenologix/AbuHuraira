import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  Alert, DiagnosticSession, DtcRecord, EcuType, FaultInjection, FaultType, OtaCampaign, OtaDeployment, ProblemDetails,
  Role, SoftwarePackage, TelemetrySeries, UserAccount, VehicleDetails, VehicleSummary, VehicleTelemetry,
} from './models';

/** Typed client for the AutoSphere REST API (initial loads and commands; live data arrives via SignalR). */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  vehicles = () => this.get<VehicleSummary[]>('/api/vehicles');
  vehicle = (id: string) => this.get<VehicleDetails>(`/api/vehicles/${id}`);
  latestTelemetry = (id: string) => this.get<VehicleTelemetry | null>(`/api/vehicles/${id}/telemetry/latest`);

  telemetryHistory(id: string, signals: string[], minutes: number): Promise<TelemetrySeries[]> {
    let params = new HttpParams()
      .set('from', new Date(Date.now() - minutes * 60_000).toISOString())
      .set('maxPoints', 240);
    signals.forEach((s) => (params = params.append('signal', s)));
    return firstValueFrom(this.http.get<TelemetrySeries[]>(`/api/vehicles/${id}/telemetry/history`, { params }));
  }

  dtcs = (id: string, includeHistory = false) => this.get<DtcRecord[]>(`/api/vehicles/${id}/dtcs?includeHistory=${includeHistory}`);
  diagnosticHistory = (id: string) => this.get<DiagnosticSession[]>(`/api/vehicles/${id}/diagnostics?take=20`);
  scan = (id: string) => this.post<DiagnosticSession>(`/api/vehicles/${id}/diagnostics/scan`, {});
  clearDtcs = (id: string, code: string | null, ecuId: string | null) => this.post<DiagnosticSession>(`/api/vehicles/${id}/dtcs/clear`, { code, ecuId });
  resetEcu = (id: string, ecuId: string) => this.post<DiagnosticSession>(`/api/vehicles/${id}/ecus/${ecuId}/reset`, { resetKind: 'HardReset' });
  readSoftwareVersion = (id: string, ecuId: string) => this.get<DiagnosticSession>(`/api/vehicles/${id}/ecus/${ecuId}/software-version`);
  readData = (id: string, ecuId: string, dataIdentifiers: number[]) => this.post<DiagnosticSession>(`/api/vehicles/${id}/ecus/${ecuId}/data`, { dataIdentifiers });
  sessionControl = (id: string, ecuId: string, session: 'Default' | 'Extended') =>
    this.post<DiagnosticSession>(`/api/vehicles/${id}/diagnostics/session`, { ecuId, session });

  alerts = (id: string, activeOnly: boolean) => this.get<Alert[]>(`/api/vehicles/${id}/alerts?activeOnly=${activeOnly}&take=100`);
  acknowledgeAlert = (alertId: string) => this.post<Alert>(`/api/alerts/${alertId}/acknowledge`, {});

  packages = () => this.get<SoftwarePackage[]>('/api/ota/packages');
  createSamplePackage = (body: { targetEcuType: EcuType; version: string; minimumCompatibleVersion: string; bootBehavior: string; releaseNotes?: string }) =>
    this.post<SoftwarePackage>('/api/ota/packages/sample', body);
  campaigns = () => this.get<OtaCampaign[]>('/api/ota/campaigns');
  deploy = (packageId: string, vehicleIds: string[], name?: string) => this.post<OtaCampaign>('/api/ota/campaigns', { packageId, vehicleIds, name });
  deployments = (vehicleId: string) => this.get<OtaDeployment[]>(`/api/ota/deployments?vehicleId=${vehicleId}`);

  simulationStatus = () => this.get<{ faultInjectionEnabled: boolean }>('/api/simulation/status');
  faultHistory = (id: string) => this.get<FaultInjection[]>(`/api/vehicles/${id}/faults`);
  injectFault = (id: string, body: { fault: FaultType; action: 'Inject' | 'Clear'; targetEcuId?: string | null; durationSeconds?: number | null; delayMilliseconds?: number | null; packageId?: string | null }) =>
    this.post<FaultInjection>(`/api/vehicles/${id}/faults`, body);

  users = () => this.get<UserAccount[]>('/api/users');
  createUser = (body: { userName: string; email: string; password: string; role: Role }) => this.post<UserAccount>('/api/users', body);
  changeRole = (userId: string, role: Role) => firstValueFrom(this.http.put<UserAccount>(`/api/users/${userId}/role`, { role }));
  deleteUser = (userId: string) => firstValueFrom(this.http.delete<void>(`/api/users/${userId}`));

  private get<T>(url: string): Promise<T> {
    return firstValueFrom(this.http.get<T>(url));
  }

  private post<T>(url: string, body: unknown): Promise<T> {
    return firstValueFrom(this.http.post<T>(url, body));
  }
}

/** Extracts a user-facing message from an API error (ProblemDetails). */
export function errorMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const problem = error.error as ProblemDetails | null;
    if (problem?.errors) {
      return Object.values(problem.errors).flat().join(' ');
    }
    if (error.status === 504 && problem && 'diagnosis' in problem) {
      return 'The vehicle did not answer in time.';
    }
    return problem?.detail ?? problem?.title ?? (error.status === 0 ? 'The API is not reachable.' : `${error.status} ${error.statusText}`);
  }
  return error instanceof Error ? error.message : 'Unexpected error.';
}
