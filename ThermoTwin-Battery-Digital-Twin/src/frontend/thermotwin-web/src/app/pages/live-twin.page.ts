import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ApiService } from '../core/api.service';
import { fmt, pct, sci } from '../core/format';
import { TwinStore } from '../core/twin-store.service';
import { ChartComponent } from '../shared/chart.component';
import { SERIES, axis, limit, line, lineChart } from '../shared/chart-theme';
import { HeatmapComponent } from '../shared/heatmap.component';
import { CardComponent, RiskBadgeComponent } from '../shared/ui';

@Component({
  selector: 'tt-live-twin',
  imports: [CardComponent, HeatmapComponent, ChartComponent, RiskBadgeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Live Digital Twin</h1>
          <p>
            Left column: what physically happens inside the cell (normally invisible). Middle: what the twin infers from sparse sensors.
            Right: the discrepancy and the forward prediction. All fields update in real time over SignalR.
          </p>
        </div>
        @if (f(); as f) {
          <div class="actions">
            <tt-risk [level]="f.risk" />
            @if (f.mitigationActive) {
              <span class="pill info">❄ mitigation active</span>
            }
            @if (f.status === 'Running' || f.status === 'Paused') {
              <button class="btn" (click)="toggle()">{{ f.status === 'Running' ? '❚❚ Pause' : '▶ Resume' }}</button>
              <button class="btn" (click)="stop()">■ Stop</button>
            }
          </div>
        }
      </div>

      @if (f(); as f) {
        <div class="grid g-3">
          <tt-card heading="True temperature T(x, y, t)" sub="Synthetic ground truth (hidden from the twin)">
            <tt-heatmap [field]="fields()['trueTemperature']" [range]="tempRange()" [sensors]="f.sensors" [trueHotspots]="f.trueHotspots" />
          </tt-card>
          <tt-card heading="Estimated temperature T̂(x, y, t)" sub="Reconstructed from the sensor readings only">
            <tt-heatmap [field]="fields()['estimatedTemperature']" [range]="tempRange()" [sensors]="f.sensors" [estimatedHotspot]="f.estimatedHotspot" [showSensorLabels]="false" />
          </tt-card>
          <tt-card heading="Estimation error T̂ − T" sub="Diverging scale centred at 0 K">
            <tt-heatmap [field]="fields()['estimationError']" colormap="diverging" [symmetric]="true" [sensors]="f.sensors" [showSensorLabels]="false" [decimals]="3" />
          </tt-card>
          <tt-card heading="True heat source q(x, y)" sub="Uniform Joule heating + hidden defect (full load)">
            <tt-heatmap [field]="fields()['trueSource']" colormap="source" [range]="sourceRange()" [trueHotspots]="f.trueHotspots" [decimals]="0" />
          </tt-card>
          <tt-card heading="Reconstructed heat source q̂(x, y)" sub="Tikhonov-regularised inverse solution on the 13×7 hat basis">
            <tt-heatmap [field]="fields()['estimatedSource']" colormap="source" [range]="sourceRange()" [trueHotspots]="f.trueHotspots" [estimatedHotspot]="f.estimatedHotspot" [decimals]="0" />
          </tt-card>
          <tt-card [heading]="fields()['predictedTemperature']?.label ?? 'Predicted temperature'" sub="Forward prediction from T̂ and q̂ under the planned cooling">
            <tt-heatmap [field]="fields()['predictedTemperature']" [range]="tempRange()" [estimatedHotspot]="f.estimatedHotspot" emptyText="Forecast starts at t = 60 s" />
          </tt-card>
        </div>

        <div class="grid sensors">
          <tt-card heading="Sensor measurements" [sub]="'All 12 thermistors (gray); ' + nearest().id + ' — the sensor nearest the reconstructed hotspot — highlighted'">
            <tt-chart [config]="sensorChart()" [height]="250" label="Sensor measurements" />
          </tt-card>
          <tt-card heading="Inverse solver state">
            @if (f.estimation; as e) {
              <table class="data">
                <tr><td>Regularisation</td><td class="strong">{{ e.regularization }} (L = ∇ₕ)</td></tr>
                <tr><td>λ selection</td><td class="strong">{{ e.lambdaSelection }}</td></tr>
                <tr><td>Relative λ</td><td>{{ sci(e.relativeLambda) }}</td></tr>
                <tr><td>Residual ‖Aq − d‖</td><td>{{ fmt(e.residualNorm, 3) }} K</td></tr>
                <tr><td>Seminorm ‖Lq‖</td><td>{{ fmt(e.solutionSeminorm, 1) }}</td></tr>
                <tr><td>Measurements m</td><td>{{ e.measurementCount }}</td></tr>
                <tr><td>Unknowns n</td><td>{{ e.unknowns }}</td></tr>
                <tr><td>Temperature RMSE</td><td class="strong">{{ fmt(e.temperatureRmse, 3) }} K</td></tr>
                <tr><td>Source rel. error</td><td>{{ pct(e.sourceRelativeError, 1) }}</td></tr>
                <tr><td>Hotspot error</td><td class="strong">{{ fmt(f.hotspotErrorMm, 1) }} mm</td></tr>
                <tr><td>Solve time</td><td>{{ fmt(e.solveMs, 0) }} ms</td></tr>
              </table>
            } @else {
              <p class="muted">The first inverse solve happens at t = 30 s.</p>
            }
          </tt-card>
          <tt-card heading="Current readings" sub="measured (noisy) vs true">
            <div class="table-scroll">
              <table class="data">
                <tr><th>Sensor</th><th>x, y [mm]</th><th>Measured</th><th>True</th></tr>
                @for (s of f.sensors; track s.id) {
                  <tr [class.highlight]="s.id === nearest().id">
                    <td>{{ s.id }}</td>
                    <td>{{ (s.x * 1000).toFixed(0) }}, {{ (s.y * 1000).toFixed(0) }}</td>
                    <td class="strong">{{ s.value.toFixed(2) }}</td>
                    <td>{{ s.trueValue.toFixed(2) }}</td>
                  </tr>
                }
              </table>
            </div>
          </tt-card>
        </div>
      }
    </div>
  `,
  styles: `
    .actions { display: flex; gap: 10px; align-items: center; }
    .sensors { grid-template-columns: minmax(0, 2fr) minmax(0, 1fr) minmax(0, 1fr); }
    @media (max-width: 1200px) { .sensors { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class LiveTwinPage {
  private readonly store = inject(TwinStore);
  private readonly api = inject(ApiService);
  readonly f = this.store.frame;
  readonly fields = this.store.fields;
  readonly fmt = fmt;
  readonly pct = pct;
  readonly sci = sci;

  readonly tempRange = computed<[number, number] | null>(() => {
    const t = this.fields()['trueTemperature'];
    const e = this.fields()['estimatedTemperature'];
    const p = this.fields()['predictedTemperature'];
    if (!t) {
      return null;
    }
    return [Math.min(t.min, e?.min ?? t.min, p?.min ?? t.min), Math.max(t.max, e?.max ?? t.max, p?.max ?? t.max)];
  });

  readonly sourceRange = computed<[number, number] | null>(() => {
    const t = this.fields()['trueSource'];
    const e = this.fields()['estimatedSource'];
    return t ? [Math.min(0, e?.min ?? 0), Math.max(t.max, e?.max ?? 0)] : null;
  });

  readonly nearest = computed(() => {
    const f = this.f();
    const sensors = f?.sensors ?? [];
    const target = f?.estimatedHotspot ?? f?.trueHotspots[0];
    let best = 0;
    if (target) {
      let d = Infinity;
      sensors.forEach((s, i) => {
        const dist = Math.hypot(s.x - target.x, s.y - target.y);
        if (dist < d) {
          d = dist;
          best = i;
        }
      });
    }
    return { index: best, id: sensors[best]?.id ?? 'S01' };
  });

  readonly sensorChart = computed(() => {
    const h = this.store.history();
    const n = h[0]?.sensors.length ?? 0;
    const highlight = this.nearest().index;
    const end = this.f()?.duration ?? 1800;
    const sets = Array.from({ length: n }, (_, k) =>
      line(
        k === highlight ? `S${String(k + 1).padStart(2, '0')} (near hotspot)` : `S${String(k + 1).padStart(2, '0')}`,
        h.filter((_, i) => i % 2 === 0).map((p) => ({ x: p.time, y: p.sensors[k] })),
        k === highlight ? SERIES.orange : 'rgba(195,194,183,0.35)',
        { borderWidth: k === highlight ? 2 : 1, order: k === highlight ? 0 : 1 },
      ),
    );
    sets.push(line('True T_max (not measured)', h.map((p) => ({ x: p.time, y: p.trueMax })), SERIES.blue, { borderDash: [4, 3] }));
    sets.push(limit('T_safe', this.f()?.safeTemperature ?? 45, 0, end));
    const config = lineChart(sets, axis('time [s]', { min: 0, max: end }), axis('°C'));
    config.options!.plugins!.legend!.labels!.filter = (item) =>
      item.text.includes('near') || item.text.startsWith('True') || item.text === 'T_safe';
    return config;
  });

  toggle(): void {
    (this.f()?.status === 'Running' ? this.api.pause() : this.api.resume()).subscribe();
  }

  stop(): void {
    this.api.stop().subscribe();
  }
}
