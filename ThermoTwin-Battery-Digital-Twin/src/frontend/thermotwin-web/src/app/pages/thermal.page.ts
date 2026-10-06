import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ExperimentStore } from '../core/experiment-store.service';
import { fmt } from '../core/format';
import { FieldDto, ForecastResult } from '../core/models';
import { TwinStore } from '../core/twin-store.service';
import { ChartComponent } from '../shared/chart.component';
import { INK, SERIES, SERIES_ORDER, Point, axis, limit, line, lineChart } from '../shared/chart-theme';
import { HeatmapComponent } from '../shared/heatmap.component';
import { CardComponent } from '../shared/ui';

@Component({
  selector: 'tt-thermal',
  imports: [CardComponent, ChartComponent, HeatmapComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Thermal Analysis</h1>
          <p>Temperature statistics, spatial profiles through the hotspot and the accuracy of the twin's forward prediction.</p>
        </div>
      </div>

      <div class="grid g-2">
        <tt-card heading="Temperature statistics over time" sub="True maximum, mean and minimum of the cell; twin-estimated maximum">
          <tt-chart [config]="statsChart()" [height]="260" label="Temperature statistics" />
        </tt-card>
        <tt-card heading="Prediction versus actual" [sub]="'Lead-time forecast: value predicted 300 s earlier (markers) against the measured truth (line). MAE ' + leadMae() + ' K'">
          <tt-chart [config]="leadChart()" [height]="260" label="Prediction versus actual" />
        </tt-card>
      </div>

      <div class="grid g-3">
        <tt-card heading="Current forecast" sub="600 s horizon from the latest estimate: optimised plan vs baseline cooling">
          <tt-chart [config]="forecastChart()" [height]="230" label="Current forecast" />
        </tt-card>
        <tt-card heading="Cross-section through the hotspot" [sub]="'Temperature along x at y = ' + profileY() + ' mm'">
          <tt-chart [config]="profileChart()" [height]="230" label="Cross-section" />
        </tt-card>
        <tt-card heading="Thermal spread & estimation RMSE" sub="T_max − T_min of the cell and RMSE of T̂ (K)">
          <tt-chart [config]="spreadChart()" [height]="230" label="Spread" />
        </tt-card>
      </div>

      <div class="grid g-2">
        <tt-card heading="Forecast accuracy experiment" sub="Forecasts issued at t = 300, 600, 900, 1200 s (baseline cooling, 600 s horizon) vs the plant">
          <tt-chart [config]="forecastExperimentChart()" [height]="260" label="Forecast accuracy" />
          @if (forecastExp(); as fx) {
            <table class="data">
              <tr><th>Issued at</th><th>MAE</th><th>Error at horizon</th><th>Predicted peak</th><th>Actual peak</th></tr>
              @for (t of fx.result.forecasts; track t.issuedAt) {
                <tr><td>{{ t.issuedAt }} s</td><td class="strong">{{ fmt(t.meanAbsoluteError, 3) }} K</td><td>{{ fmt(t.errorAtHorizon, 3) }} K</td><td>{{ fmt(t.predictedPeak, 2) }} °C</td><td>{{ fmt(t.actualPeak, 2) }} °C</td></tr>
              }
            </table>
          }
        </tt-card>
        <tt-card heading="Estimation error field" sub="T̂ − T on the model grid (diverging, centred at zero)">
          <tt-heatmap [field]="store.fields()['estimationError']" colormap="diverging" [symmetric]="true" [sensors]="store.frame()?.sensors ?? null" [showSensorLabels]="false" [decimals]="3" />
        </tt-card>
      </div>
    </div>
  `,
})
export class ThermalPage {
  readonly store = inject(TwinStore);
  private readonly experiments = inject(ExperimentStore);
  readonly forecastExp = this.experiments.latest<ForecastResult>('ForecastAccuracy');
  readonly fmt = fmt;

  private readonly end = computed(() => this.store.frame()?.duration ?? 1800);

  readonly statsChart = computed(() => {
    const h = this.store.history();
    return lineChart(
      [
        line('T_max', h.map((p) => ({ x: p.time, y: p.trueMax })), SERIES.blue),
        line('T_mean', h.map((p) => ({ x: p.time, y: p.trueMean })), SERIES.orange),
        line('T_min', h.map((p) => ({ x: p.time, y: p.trueMin })), SERIES.aqua),
        line('T̂_max (twin)', h.map((p) => ({ x: p.time, y: p.estimatedMax })), SERIES.yellow, { borderDash: [4, 3] }),
        limit('T_safe', this.store.frame()?.safeTemperature ?? 45, 0, this.end()),
      ],
      axis('time [s]', { min: 0, max: this.end() }),
      axis('°C'),
    );
  });

  readonly leadMae = computed(() => {
    const pts = this.store.history().filter((p) => p.leadPrediction !== null);
    if (!pts.length) {
      return '—';
    }
    return (pts.reduce((s, p) => s + Math.abs((p.leadPrediction ?? 0) - p.trueMax), 0) / pts.length).toFixed(2);
  });

  readonly leadChart = computed(() => {
    const h = this.store.history();
    return lineChart(
      [
        line('Actual T_max', h.map((p) => ({ x: p.time, y: p.trueMax })), SERIES.blue),
        line('Predicted 300 s ahead', h.filter((p) => p.leadPrediction !== null).map((p) => ({ x: p.time, y: p.leadPrediction })), SERIES.orange, {
          showLine: false,
          pointRadius: 4,
          pointBorderColor: '#151514',
          pointBorderWidth: 2,
        }),
        limit('T_safe', this.store.frame()?.safeTemperature ?? 45, 0, this.end()),
      ],
      axis('time [s]', { min: 0, max: this.end() }),
      axis('°C'),
    );
  });

  readonly forecastChart = computed(() => {
    const f = this.store.frame();
    const fc = f?.forecast;
    const h = this.store.history().filter((p) => fc && p.time >= fc.issuedAt - 400);
    const sets = [line('History', h.map((p) => ({ x: p.time, y: p.trueMax })), INK.secondary)];
    if (fc) {
      sets.push(line('Planned (optimised)', fc.times.map((t, i) => ({ x: t, y: fc.plannedMax[i] })), SERIES.blue));
      sets.push(line('Baseline cooling', fc.times.map((t, i) => ({ x: t, y: fc.unmitigatedMax[i] })), SERIES.orange, { borderDash: [5, 3] }));
      sets.push(limit('T_safe', f!.safeTemperature, fc.issuedAt - 400, fc.times.at(-1)!));
    }
    return lineChart(sets, axis('time [s]'), axis('°C'));
  });

  readonly profileY = computed(() => {
    const f = this.store.frame();
    const y = f?.estimatedHotspot?.y ?? f?.trueHotspots[0]?.y ?? 0.05;
    return (y * 1000).toFixed(0);
  });

  readonly profileChart = computed(() => {
    const fields = this.store.fields();
    const f = this.store.frame();
    const y = f?.estimatedHotspot?.y ?? f?.trueHotspots[0]?.y ?? 0.05;
    const row = (field: FieldDto | undefined): Point[] => {
      if (!field) {
        return [];
      }
      const j = Math.min(field.ny - 1, Math.max(0, Math.floor((y / field.lengthY) * field.ny)));
      return field.values[j].map((v, i) => ({ x: ((i + 0.5) * field.lengthX * 1000) / field.nx, y: v }));
    };
    return lineChart(
      [
        line('True', row(fields['trueTemperature']), SERIES.blue),
        line('Estimated', row(fields['estimatedTemperature']), SERIES.orange, { borderDash: [5, 3] }),
        line('Predicted (+600 s)', row(fields['predictedTemperature']), SERIES.aqua),
      ],
      axis('x [mm]', { min: 0, max: 200 }),
      axis('°C'),
    );
  });

  readonly spreadChart = computed(() => {
    const h = this.store.history();
    return lineChart(
      [
        line('Spread T_max − T_min', h.map((p) => ({ x: p.time, y: p.trueMax - p.trueMin })), SERIES.blue),
        line('RMSE of T̂', h.map((p) => ({ x: p.time, y: p.temperatureRmse })), SERIES.orange),
      ],
      axis('time [s]', { min: 0, max: this.end() }),
      axis('K', { min: 0 }),
      { tooltipDigits: 3 },
    );
  });

  readonly forecastExperimentChart = computed(() => {
    const fx = this.forecastExp()?.result;
    if (!fx) {
      return lineChart([], axis('time [s]'), axis('°C'));
    }
    const sets = [line('Actual T_max', fx.actualTimes.map((t, i) => ({ x: t, y: fx.actualMax[i] })), INK.secondary)];
    fx.forecasts.forEach((f, k) =>
      sets.push(line(`Forecast @ ${f.issuedAt} s`, f.times.map((t, i) => ({ x: t, y: f.predicted[i] })), SERIES_ORDER[k], { borderDash: [5, 3] })),
    );
    return lineChart(sets, axis('time [s]', { min: 0, max: 1800 }), axis('°C'));
  });
}
