import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService } from '../core/api.service';
import { ExperimentStore } from '../core/experiment-store.service';
import { energy, fmt, pct } from '../core/format';
import { ExperimentKind, ExperimentOverview, SensitivityResult, SimulationRunDto } from '../core/models';
import { EXPERIMENT_TITLES, TwinStore } from '../core/twin-store.service';
import { ChartComponent } from '../shared/chart.component';
import { SERIES, axis, line, lineChart, logAxis } from '../shared/chart-theme';
import { CardComponent } from '../shared/ui';

@Component({
  selector: 'tt-experiments',
  imports: [CardComponent, ChartComponent, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Experiment Results</h1>
          <p>
            Every number in this dashboard and in the README comes from these stored experiment runs (SQLite, JSON payloads). Experiments run on a
            background worker and can be re-executed at any time; results are pushed to the browser over SignalR when they finish.
          </p>
        </div>
      </div>

      <tt-card heading="Experiment registry">
        <div class="table-scroll">
          <table class="data">
            <tr><th>Experiment</th><th>Latest result</th><th>Run at</th><th>Duration</th><th>Status</th><th></th></tr>
            @for (k of kinds; track k) {
              <tr>
                <td class="strong">{{ titles[k] }}</td>
                <td class="summary">{{ latest(k)?.summary ?? '—' }}</td>
                <td>{{ latest(k)?.createdAt | date: 'yyyy-MM-dd HH:mm:ss' }}</td>
                <td>{{ latest(k) ? (latest(k)!.durationMs / 1000).toFixed(1) + ' s' : '—' }}</td>
                <td><span class="pill" [class.info]="state(k) === 'Running'" [class.ok]="!state(k) && !!latest(k)" [class.bad]="state(k) === 'Failed'">{{ state(k) ?? (latest(k) ? 'Stored' : 'Missing') }}</span></td>
                <td><button class="btn" [disabled]="state(k) === 'Running' || state(k) === 'Queued'" (click)="run(k)">↻ Re-run</button></td>
              </tr>
            }
          </table>
        </div>
      </tt-card>

      <div class="grid g-2">
        <tt-card heading="Sensor density study" sub="Reconstruction accuracy at the end of the charge versus number of thermistors (GCV, σ = 0.1 K)">
          <tt-chart [config]="sensorChart()" [height]="250" label="Sensor density" />
          @if (sensors(); as s) {
            <table class="data">
              <tr><th>Sensors</th><th>λ (rel.)</th><th>T RMSE</th><th>Source err.</th><th>Hotspot err.</th></tr>
              @for (r of s.result.rows; track r.parameter) {
                <tr [class.highlight]="r.parameter === 12"><td>{{ r.parameter }}</td><td>{{ r.relativeLambda.toExponential(1) }}</td><td class="strong">{{ fmt(r.metrics.temperatureRmse, 3) }} K</td><td>{{ pct(r.metrics.sourceRelativeError, 1) }}</td><td class="strong">{{ fmt(r.metrics.hotspotErrorMm, 1) }} mm</td></tr>
              }
            </table>
          }
        </tt-card>
        <tt-card heading="Noise robustness study" sub="Reconstruction accuracy versus sensor noise standard deviation σ (12 sensors)">
          <tt-chart [config]="noiseChart()" [height]="250" label="Noise robustness" />
          @if (noise(); as s) {
            <table class="data">
              <tr><th>σ [K]</th><th>λ (rel.)</th><th>T RMSE</th><th>Source err.</th><th>Hotspot err.</th></tr>
              @for (r of s.result.rows; track r.parameter) {
                <tr [class.highlight]="r.parameter === 0.1"><td>{{ r.parameter }}</td><td>{{ r.relativeLambda.toExponential(1) }}</td><td class="strong">{{ fmt(r.metrics.temperatureRmse, 3) }} K</td><td>{{ pct(r.metrics.sourceRelativeError, 1) }}</td><td class="strong">{{ fmt(r.metrics.hotspotErrorMm, 1) }} mm</td></tr>
              }
            </table>
          }
        </tt-card>
      </div>

      <tt-card heading="Simulation runs" sub="Persisted digital-twin sessions (EF Core / SQLite)">
        <div class="table-scroll">
          <table class="data">
            <tr><th>Run</th><th>Status</th><th>Started</th><th>Simulated</th><th>Peak T</th><th>Counterfactual peak</th><th>Energy</th><th>T RMSE</th><th>Hotspot err.</th></tr>
            @for (r of runs(); track r.id) {
              <tr>
                <td>{{ r.name }}</td><td>{{ r.status }}</td><td>{{ r.startedAt | date: 'HH:mm:ss' }}</td><td>{{ r.simulatedSeconds }} s</td>
                <td class="strong">{{ fmt(r.peakTemperature, 2) }} °C</td><td>{{ fmt(r.peakCounterfactualTemperature, 2) }} °C</td>
                <td>{{ energy(r.coolingEnergyJoules) }}</td><td>{{ fmt(r.finalTemperatureRmse, 3) }} K</td><td>{{ fmt(r.hotspotLocalizationErrorMm, 1) }} mm</td>
              </tr>
            }
          </table>
        </div>
      </tt-card>
    </div>
  `,
  styles: `.summary { white-space: normal !important; text-align: left !important; max-width: 560px; color: var(--ink-2); }`,
})
export class ExperimentsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly store = inject(ExperimentStore);
  private readonly twin = inject(TwinStore);

  readonly kinds: ExperimentKind[] = ['NumericalConvergence', 'Regularization', 'SensorDensity', 'NoiseRobustness', 'ForecastAccuracy', 'CoolingComparison'];
  readonly titles = EXPERIMENT_TITLES;
  readonly overview = signal<ExperimentOverview | null>(null);
  readonly runs = signal<SimulationRunDto[]>([]);
  readonly sensors = this.store.latest<SensitivityResult>('SensorDensity');
  readonly noise = this.store.latest<SensitivityResult>('NoiseRobustness');
  readonly fmt = fmt;
  readonly pct = pct;
  readonly energy = energy;

  constructor() {
    effect(() => {
      if (this.twin.experimentCompleted()) {
        this.refresh();
      }
    });
  }

  ngOnInit(): void {
    this.refresh();
  }

  latest(kind: ExperimentKind) {
    return this.overview()?.experiments.find((e) => e.kind === kind) ?? null;
  }

  state(kind: ExperimentKind) {
    const s = this.overview()?.queue.find((q) => q.kind === kind)?.state;
    return s === 'Completed' ? null : s;
  }

  run(kind: ExperimentKind): void {
    this.api.runExperiment(kind).subscribe(() => this.refresh());
  }

  private refresh(): void {
    this.api.experiments().subscribe((o) => this.overview.set(o));
    this.api.runs().subscribe((r) => this.runs.set(r));
  }

  readonly sensorChart = computed(() => {
    const rows = this.sensors()?.result.rows ?? [];
    return lineChart(
      [
        line('Hotspot error [mm]', rows.map((r) => ({ x: r.parameter, y: r.metrics.hotspotErrorMm })), SERIES.blue, { pointRadius: 4 }),
        line('Source error [%]', rows.map((r) => ({ x: r.parameter, y: r.metrics.sourceRelativeError * 100 })), SERIES.orange, { pointRadius: 4 }),
      ],
      axis('number of sensors'),
      axis('mm  ·  %', { min: 0 }),
      { tooltipDigits: 1 },
    );
  });

  readonly noiseChart = computed(() => {
    const rows = (this.noise()?.result.rows ?? []).filter((r) => r.parameter > 0);
    return lineChart(
      [line('Temperature RMSE [K]', rows.map((r) => ({ x: r.parameter, y: r.metrics.temperatureRmse })), SERIES.blue, { pointRadius: 4 })],
      logAxis('noise σ [K]'),
      axis('RMSE [K]', { min: 0 }),
      { tooltipDigits: 3 },
    );
  });
}
