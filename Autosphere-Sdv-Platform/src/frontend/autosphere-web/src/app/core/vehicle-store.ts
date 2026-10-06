import { computed, inject, Injectable, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { ApiService } from './api.service';
import { AuthService } from './auth.service';
import {
  Alert, DiagnosticSession, DtcRecord, FaultInjection, OtaDeployment, VehicleDetails, VehicleSummary, VehicleTelemetry, VSS,
} from './models';

export type LinkState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';
export interface Point { t: number; v: number }

const BUFFER_SECONDS = 1800;
export const CHART_SIGNALS = [VSS.speed, VSS.motorSpeed, VSS.soc, VSS.batteryTemperature, VSS.motorTemperature] as const;

/**
 * Client-side state of the selected vehicle. REST provides the initial load, SignalR pushes every
 * change afterwards — the dashboard never polls for live values.
 */
@Injectable({ providedIn: 'root' })
export class VehicleStore {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private hub: HubConnection | null = null;
  private lastSample = 0;

  readonly link = signal<LinkState>('disconnected');
  readonly vehicles = signal<VehicleSummary[]>([]);
  readonly selectedId = signal<string | null>(null);
  readonly vehicle = signal<VehicleDetails | null>(null);
  readonly telemetry = signal<VehicleTelemetry | null>(null);
  readonly dtcs = signal<DtcRecord[]>([]);
  readonly alerts = signal<Alert[]>([]);
  readonly deployments = signal<OtaDeployment[]>([]);
  readonly diagnostics = signal<DiagnosticSession[]>([]);
  readonly faults = signal<FaultInjection[]>([]);
  readonly series = signal<Record<string, Point[]>>({});
  /** Backend receive → browser receive of the latest live update (ms, same-host clock assumption). */
  readonly dashboardLatencyMs = signal<number | null>(null);
  readonly lastUpdate = signal<number | null>(null);

  readonly activeAlerts = computed(() => this.alerts().filter((a) => a.isActive));
  readonly activeDtcs = computed(() => this.dtcs().filter((d) => d.status === 'Active'));

  async start(): Promise<void> {
    if (this.hub) {
      return;
    }

    this.hub = new HubConnectionBuilder()
      .withUrl('/hubs/vehicles', { accessTokenFactory: () => this.auth.token ?? '' })
      .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 15000])
      .configureLogging(LogLevel.Warning)
      .build();
    this.registerHandlers(this.hub);
    this.hub.onreconnecting(() => this.link.set('reconnecting'));
    this.hub.onreconnected(() => {
      this.link.set('connected');
      void this.resubscribe();
    });
    this.hub.onclose(() => this.link.set('disconnected'));

    const summaries = await this.api.vehicles();
    this.vehicles.set(summaries);
    await this.connect();
    const preferred = localStorage.getItem('autosphere.vehicle');
    const initial = summaries.find((v) => v.vehicleId === preferred)?.vehicleId ?? summaries[0]?.vehicleId;
    if (initial) {
      await this.select(initial);
    }
  }

  async stop(): Promise<void> {
    await this.hub?.stop();
    this.hub = null;
    this.selectedId.set(null);
  }

  async select(vehicleId: string): Promise<void> {
    const previous = this.selectedId();
    if (previous && this.hub?.state === HubConnectionState.Connected) {
      await this.hub.invoke('UnsubscribeVehicle', previous);
    }

    this.selectedId.set(vehicleId);
    try {
      localStorage.setItem('autosphere.vehicle', vehicleId);
    } catch {
      // storage unavailable (private mode): selection simply is not remembered
    }

    this.series.set({});
    this.lastSample = 0;
    await this.resubscribe();
  }

  /** (Re)loads everything for the selected vehicle and joins its SignalR group. */
  async reload(): Promise<void> {
    const id = this.selectedId();
    if (!id) {
      return;
    }

    const [vehicle, telemetry, dtcs, alerts, deployments, diagnostics] = await Promise.all([
      this.api.vehicle(id),
      this.api.latestTelemetry(id),
      this.api.dtcs(id),
      this.api.alerts(id, false),
      this.api.deployments(id),
      this.api.diagnosticHistory(id),
    ]);
    this.vehicle.set(vehicle);
    this.telemetry.set(telemetry);
    this.dtcs.set(dtcs);
    this.alerts.set(alerts);
    this.deployments.set(deployments);
    this.diagnostics.set(diagnostics);
    if (this.auth.isAdministrator()) {
      this.api.faultHistory(id).then((f) => this.faults.set(f)).catch(() => this.faults.set([]));
    }
    await this.loadHistory(id);
  }

  upsertDiagnostic(session: DiagnosticSession): void {
    this.diagnostics.update((list) => [session, ...list.filter((s) => s.correlationId !== session.correlationId)].slice(0, 30));
  }

  upsertFault(fault: FaultInjection): void {
    this.faults.update((list) => [fault, ...list.filter((f) => f.correlationId !== fault.correlationId)].slice(0, 50));
  }

  private async resubscribe(): Promise<void> {
    const id = this.selectedId();
    if (id && this.hub?.state === HubConnectionState.Connected) {
      await this.hub.invoke('SubscribeVehicle', id);
    }
    await this.reload();
  }

  private async connect(): Promise<void> {
    if (!this.hub) {
      return;
    }
    this.link.set('connecting');
    try {
      await this.hub.start();
      this.link.set('connected');
    } catch {
      this.link.set('disconnected');
      setTimeout(() => void this.connect().then(() => this.resubscribe()), 5000);
    }
  }

  private async loadHistory(id: string): Promise<void> {
    try {
      const history = await this.api.telemetryHistory(id, [...CHART_SIGNALS], 30);
      const series: Record<string, Point[]> = {};
      for (const s of history) {
        series[s.signalPath] = s.points.map((p) => ({ t: Date.parse(p.timestamp), v: p.value }));
      }
      this.series.set(series);
    } catch {
      this.series.set({});
    }
  }

  private registerHandlers(hub: HubConnection): void {
    hub.on('Telemetry', (t: VehicleTelemetry) => {
      if (t.vehicleId !== this.selectedId()) {
        return;
      }
      const now = Date.now();
      this.telemetry.set(t);
      this.lastUpdate.set(now);
      this.dashboardLatencyMs.set(Math.max(0, now - Date.parse(t.backendReceivedAt)));
      if (now - this.lastSample >= 1000) {
        this.lastSample = now;
        this.appendSamples(t, now);
      }
    });

    hub.on('VehicleUpdated', (v: VehicleDetails) => {
      if (v.vehicleId === this.selectedId()) {
        this.vehicle.set(v);
      }
      this.vehicles.update((list) => list.map((s) => (s.vehicleId === v.vehicleId
        ? { ...s, connectivity: v.connectivity, health: v.health, lastSeenAt: v.lastSeenAt }
        : s)));
    });

    hub.on('DtcsChanged', (vehicleId: string, dtcs: DtcRecord[]) => {
      if (vehicleId === this.selectedId()) {
        this.dtcs.set(dtcs);
      }
    });

    hub.on('Alert', (alert: Alert) => {
      if (alert.vehicleId === this.selectedId()) {
        this.alerts.update((list) => [alert, ...list.filter((a) => a.id !== alert.id)].slice(0, 100));
      }
    });

    hub.on('OtaDeployment', (deployment: OtaDeployment) => {
      if (deployment.vehicleId === this.selectedId()) {
        this.deployments.update((list) => [deployment, ...list.filter((d) => d.id !== deployment.id)]
          .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt)));
      }
    });

    hub.on('DiagnosticCompleted', (session: DiagnosticSession) => {
      if (session.vehicleId === this.selectedId()) {
        this.upsertDiagnostic(session);
      }
    });

    hub.on('FaultInjection', (fault: FaultInjection) => {
      if (fault.vehicleId === this.selectedId()) {
        this.upsertFault(fault);
      }
    });
  }

  private appendSamples(t: VehicleTelemetry, now: number): void {
    const cutoff = now - BUFFER_SECONDS * 1000;
    this.series.update((current) => {
      const next: Record<string, Point[]> = { ...current };
      for (const path of CHART_SIGNALS) {
        const signal = t.signals.find((s) => s.path === path);
        if (signal && signal.quality !== 'OutOfRange') {
          const points = (next[path] ?? []).filter((p) => p.t >= cutoff);
          points.push({ t: Date.parse(signal.timestamp), v: signal.value });
          next[path] = points;
        }
      }
      return next;
    });
  }
}
