import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { VSS } from '../core/models';
import { Point, VehicleStore } from '../core/vehicle-store';
import { LineChart, Threshold } from '../shared/line-chart';
import { StatusBadge } from '../shared/status-badge';

interface ChartSpec { path: string; title: string; unit: string; min?: number; max?: number; decimals: number; thresholds: Threshold[] }

@Component({
  selector: 'app-telemetry',
  imports: [LineChart, StatusBadge, DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Telemetry</h1>
          <p>History from the database (1 sample/s) continued live via SignalR. One measure per chart.</p>
        </div>
        <div class="form-row" role="group" aria-label="Time window">
          @for (w of windows; track w) {
            <button class="small" [class.primary]="minutes() === w" (click)="minutes.set(w)">{{ w }} min</button>
          }
        </div>
      </div>

      <div class="grid grid-2">
        @for (chart of charts; track chart.path) {
          <div class="panel">
            <app-line-chart [title]="chart.title" [unit]="chart.unit" [points]="pointsFor(chart.path)"
                            [min]="chart.min" [max]="chart.max" [decimals]="chart.decimals" [thresholds]="chart.thresholds" />
          </div>
        }
      </div>

      <div class="panel">
        <div class="panel-header">
          <h2>All signals (VSS-inspired paths)</h2>
          <span class="muted">{{ signals().length }} signals · updated live</span>
        </div>
        <div class="table-wrap">
          <table>
            <thead><tr><th>Path</th><th>Signal</th><th class="num">Value</th><th>Unit</th><th>Source ECU</th><th>Quality</th></tr></thead>
            <tbody>
              @for (s of signals(); track s.path) {
                <tr>
                  <td class="mono">{{ s.path }}</td>
                  <td>{{ s.name }}</td>
                  <td class="num">{{ s.label ?? (s.value | number: '1.0-2') }}</td>
                  <td>{{ s.unit }}</td>
                  <td>{{ s.sourceEcuId }}</td>
                  <td><app-status [value]="s.quality" /></td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </div>
    </div>
  `,
})
export class TelemetryPage {
  protected readonly store = inject(VehicleStore);
  protected readonly windows = [5, 10, 30];
  protected readonly minutes = signal(5);

  protected readonly charts: ChartSpec[] = [
    { path: VSS.speed, title: 'Vehicle speed', unit: 'km/h', min: 0, decimals: 1, thresholds: [] },
    { path: VSS.motorSpeed, title: 'Motor speed', unit: 'rpm', min: 0, decimals: 0, thresholds: [] },
    { path: VSS.soc, title: 'Battery state of charge', unit: '%', decimals: 1, thresholds: [] },
    {
      path: VSS.batteryTemperature, title: 'Battery temperature', unit: '°C', decimals: 1,
      thresholds: [{ value: 50, label: 'warning 50 °C', tone: 'warning' }, { value: 60, label: 'critical 60 °C', tone: 'critical' }],
    },
    {
      path: VSS.motorTemperature, title: 'Motor temperature', unit: '°C', decimals: 0,
      thresholds: [{ value: 110, label: 'warning 110 °C', tone: 'warning' }, { value: 130, label: 'critical 130 °C', tone: 'critical' }],
    },
  ];

  protected readonly window = computed(() => {
    const cutoff = Date.now() - this.minutes() * 60_000;
    const series = this.store.series();
    const result: Partial<Record<string, Point[]>> = {};
    for (const [path, points] of Object.entries(series)) {
      result[path] = points.filter((p) => p.t >= cutoff);
    }
    return result;
  });

  protected pointsFor(path: string): Point[] {
    return this.window()[path] ?? [];
  }

  protected readonly signals = computed(() => this.store.telemetry()?.signals ?? []);
}
