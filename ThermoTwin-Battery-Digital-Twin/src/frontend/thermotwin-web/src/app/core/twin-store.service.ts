import { Injectable, computed, inject, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { firstValueFrom } from 'rxjs';
import { ApiService } from './api.service';
import { ExperimentKind, ExperimentSummary, TwinFrame, TwinHistoryPoint } from './models';

export type ConnectionState = 'connecting' | 'live' | 'reconnecting' | 'offline';

/**
 * Single source of truth for the live digital twin. Loads the current frame and history over
 * REST, then applies incremental frames pushed by SignalR (`twinFrame`).
 */
@Injectable({ providedIn: 'root' })
export class TwinStore {
  private readonly api = inject(ApiService);
  private hub?: HubConnection;
  private started = false;

  readonly frame = signal<TwinFrame | null>(null);
  readonly history = signal<TwinHistoryPoint[]>([]);
  readonly connection = signal<ConnectionState>('connecting');
  readonly experimentCompleted = signal<ExperimentSummary | null>(null);

  readonly fields = computed(() => this.frame()?.fields ?? {});
  readonly progress = computed(() => {
    const f = this.frame();
    return f ? f.step / Math.max(1, f.totalSteps) : 0;
  });

  start(): void {
    if (this.started) {
      return;
    }
    this.started = true;
    void this.reload();

    this.hub = new HubConnectionBuilder()
      .withUrl('/hubs/twin')
      .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
      .configureLogging(LogLevel.Warning)
      .build();

    this.hub.on('twinFrame', (frame: TwinFrame) => this.apply(frame));
    this.hub.on('experimentCompleted', (summary: ExperimentSummary) => this.experimentCompleted.set(summary));
    this.hub.onreconnecting(() => this.connection.set('reconnecting'));
    this.hub.onreconnected(() => {
      this.connection.set('live');
      void this.reload();
    });
    this.hub.onclose(() => this.connection.set('offline'));
    void this.connect();
  }

  /** Reloads the frame and full history (used on start, reconnect and after starting a new run). */
  async reload(): Promise<void> {
    try {
      const [frame, history] = await Promise.all([firstValueFrom(this.api.state()), firstValueFrom(this.api.history())]);
      this.frame.set(frame);
      this.history.set(history);
    } catch {
      // No session yet — the first SignalR frame will populate the store.
    }
  }

  private async connect(): Promise<void> {
    try {
      await this.hub!.start();
      this.connection.set('live');
    } catch {
      this.connection.set('offline');
      setTimeout(() => {
        if (this.hub?.state === HubConnectionState.Disconnected) {
          void this.connect();
        }
      }, 3000);
    }
  }

  private apply(frame: TwinFrame): void {
    const current = this.frame();
    if (current && current.runId !== frame.runId) {
      // A new run started elsewhere: refetch the complete history of the new run.
      this.frame.set(frame);
      void this.reload();
      return;
    }

    if (frame.newHistory.length > 0) {
      const last = this.history().at(-1)?.time ?? -1;
      const fresh = frame.newHistory.filter((p) => p.time > last);
      if (fresh.length > 0) {
        this.history.update((h) => [...h, ...fresh]);
      }
    }
    this.frame.set({ ...frame, newHistory: [] });
  }
}

export const EXPERIMENT_TITLES: Record<ExperimentKind, string> = {
  NumericalConvergence: 'Numerical convergence & stability',
  Regularization: 'Inverse problem regularisation',
  SensorDensity: 'Sensor density study',
  NoiseRobustness: 'Noise robustness study',
  ForecastAccuracy: 'Forecast accuracy',
  CoolingComparison: 'Cooling strategy comparison',
  FemVerification: 'FEM convergence & FVM–FEM comparison',
  AdjointGradientCheck: 'Adjoint gradient validation',
  OptimizationBenchmark: 'PDE-constrained optimisation benchmark',
  ReducedOrderModel: 'POD reduced-order model',
  ReducedOrderControl: 'Reduced-order vs full-order MPC',
  ParameterIdentifiability: 'Parameter identifiability',
};
