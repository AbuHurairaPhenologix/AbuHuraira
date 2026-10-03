import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ChartDataset } from 'chart.js';
import { ExperimentStore } from '../core/experiment-store.service';
import { fmt, pct, sci } from '../core/format';
import { RegularizationResult } from '../core/models';
import { TwinStore } from '../core/twin-store.service';
import { ChartComponent } from '../shared/chart.component';
import { Point, SERIES, axis, line, lineChart, logAxis } from '../shared/chart-theme';
import { HeatmapComponent } from '../shared/heatmap.component';
import { CardComponent, StatComponent } from '../shared/ui';

const KIND_COLORS: Record<string, string> = { Identity: SERIES.blue, Gradient: SERIES.orange, Laplacian: SERIES.aqua };

@Component({
  selector: 'tt-inverse',
  imports: [CardComponent, ChartComponent, HeatmapComponent, StatComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Inverse Problem Analysis</h1>
          <p>
            Twelve sensors cannot observe 800 cells. The source is written as q = Σ qⱼφⱼ (91 hat functions) and the stacked sensor history gives
            the linear model <span class="math">y = Aq + ε</span>. Because A is severely ill-conditioned, the twin solves
            <span class="math">q* = argmin ‖Aq − y‖² + λ‖Lq‖²</span> with λ chosen by generalised cross-validation.
          </p>
        </div>
      </div>

      @if (store.frame(); as f) {
        <div class="grid g-4">
          <tt-stat label="Hotspot localisation error" [value]="fmt(f.hotspotErrorMm, 1)" unit="mm" [hint]="'grid spacing 5 mm · basis spacing 16.7 mm'" [accent]="true" />
          <tt-stat label="Source relative error" [value]="pct(f.estimation?.sourceRelativeError, 1)" hint="‖q̂ − q‖₂ / ‖q‖₂" />
          <tt-stat label="Temperature RMSE" [value]="fmt(f.estimation?.temperatureRmse, 3)" unit="K" hint="over all 800 cells" />
          <tt-stat label="Selected λ (GCV)" [value]="sci(f.estimation?.relativeLambda, 2)" [hint]="(f.estimation?.measurementCount ?? 0) + ' equations · ' + (f.estimation?.unknowns ?? 0) + ' unknowns'" />
        </div>

        <div class="grid g-3">
          <tt-card heading="True heat source" sub="q(x, y) at full load — hidden 450 kW/m³ defect">
            <tt-heatmap [field]="store.fields()['trueSource']" colormap="source" [range]="sourceRange()" [trueHotspots]="f.trueHotspots" [sensors]="f.sensors" [showSensorLabels]="false" [decimals]="0" />
          </tt-card>
          <tt-card heading="Reconstructed heat source (live)" sub="q̂(x, y) — Tikhonov solution with gradient prior">
            <tt-heatmap [field]="store.fields()['estimatedSource']" colormap="source" [range]="sourceRange()" [trueHotspots]="f.trueHotspots" [estimatedHotspot]="f.estimatedHotspot" [sensors]="f.sensors" [showSensorLabels]="false" [decimals]="0" />
          </tt-card>
          <tt-card heading="Reconstruction error over time" sub="Live run: hotspot localisation error [mm] and source error [%]">
            <tt-chart [config]="liveErrorChart()" [height]="220" label="Reconstruction error over time" />
          </tt-card>
        </div>
      }

      @if (reg(); as r) {
        <div class="grid g-3">
          <tt-card heading="L-curve" sub="log ‖Aq − d‖ vs log ‖Lq‖ for three regularisers; ◆ corner of maximum curvature, ● GCV choice">
            <tt-chart [config]="lcurveChart()" [height]="270" label="L-curve" />
          </tt-card>
          <tt-card heading="Regularisation analysis" sub="Source reconstruction error versus λ (relative scale)">
            <tt-chart [config]="errorVsLambda()" [height]="270" label="Error versus lambda" />
          </tt-card>
          <tt-card heading="GCV function" sub="GCV(λ) = m‖Aq_λ − d‖² / (m − tr H_λ)² — its minimum selects λ">
            <tt-chart [config]="gcvChart()" [height]="270" label="GCV function" />
          </tt-card>
        </div>

        <div class="grid g-3">
          <tt-card heading="Under-regularised" sub="λ too small: noise is amplified into oscillations">
            <tt-heatmap [field]="r.result.fields['underRegularized']" colormap="source" [trueHotspots]="r.result.trueHotspots" [decimals]="0" />
          </tt-card>
          <tt-card heading="GCV-selected λ" sub="Balanced: hotspot recovered, oscillations suppressed">
            <tt-heatmap [field]="r.result.fields['gcvSource']" colormap="source" [trueHotspots]="r.result.trueHotspots" [decimals]="0" />
          </tt-card>
          <tt-card heading="Over-regularised" sub="λ too large: the hotspot is smoothed away">
            <tt-heatmap [field]="r.result.fields['overRegularized']" colormap="source" [trueHotspots]="r.result.trueHotspots" [decimals]="0" />
          </tt-card>
        </div>

        <div class="grid g-2">
          <tt-card heading="Parameter-choice rules" [sub]="'End of the 30-min charge: m = ' + r.result.measurementCount + ' samples, n = ' + r.result.unknowns + ' unknowns, σ = ' + r.result.noiseStd + ' K'">
            <div class="table-scroll">
              <table class="data">
                <tr><th>Prior L</th><th>Rule</th><th>λ (rel.)</th><th>‖Aq−d‖ [K]</th><th>T RMSE [K]</th><th>Source err.</th><th>Hotspot err.</th></tr>
                @for (s of r.result.studies; track s.regularization) {
                  @for (c of s.choices; track c.method) {
                    <tr [class.highlight]="s.regularization === 'Gradient' && c.method === 'GCV'">
                      <td>{{ s.regularization }}</td>
                      <td>{{ c.method }}</td>
                      <td>{{ sci(c.relativeLambda, 1) }}</td>
                      <td>{{ fmt(c.residualNorm, 2) }}</td>
                      <td class="strong">{{ fmt(c.metrics.temperatureRmse, 3) }}</td>
                      <td>{{ pct(c.metrics.sourceRelativeError, 1) }}</td>
                      <td class="strong">{{ fmt(c.metrics.hotspotErrorMm, 1) }} mm</td>
                    </tr>
                  }
                }
              </table>
            </div>
            <p class="muted note">
              The discrepancy principle targets ‖Aq − d‖ = σ√m = {{ fmt(r.result.discrepancyTarget, 2) }} K. Here the residual floor is set by
              model error (the plant runs on a 2× finer grid with an exact Gaussian defect), not by sensor noise, so the target is unattainable and
              the rule collapses to the smallest λ — an honest illustration of why GCV is used in the live twin.
            </p>
          </tt-card>
          <tt-card heading="Reconstruction accuracy during the charge" sub="Experiment run with baseline cooling, inverse re-solved every 60 s">
            <tt-chart [config]="timelineChart()" [height]="300" label="Reconstruction timeline" />
          </tt-card>
        </div>
      } @else {
        <tt-card heading="Regularisation experiment"><p class="muted">The regularisation experiment is running on the background worker…</p></tt-card>
      }
    </div>
  `,
  styles: `.note { font-size: 11.5px; line-height: 1.5; margin: 6px 0 0; }`,
})
export class InversePage {
  readonly store = inject(TwinStore);
  private readonly experiments = inject(ExperimentStore);
  readonly reg = this.experiments.latest<RegularizationResult>('Regularization');
  readonly fmt = fmt;
  readonly pct = pct;
  readonly sci = sci;

  readonly sourceRange = computed<[number, number] | null>(() => {
    const t = this.store.fields()['trueSource'];
    const e = this.store.fields()['estimatedSource'];
    return t ? [Math.min(0, e?.min ?? 0), Math.max(t.max, e?.max ?? 0)] : null;
  });

  readonly liveErrorChart = computed(() => {
    const h = this.store.history().filter((p) => p.hotspotErrorMm !== null);
    return lineChart(
      [
        line('Hotspot error [mm]', h.map((p) => ({ x: p.time, y: p.hotspotErrorMm })), SERIES.blue),
        line('Source error [%]', h.map((p) => ({ x: p.time, y: (p.sourceRelativeError ?? 0) * 100 })), SERIES.orange),
      ],
      axis('time [s]', { min: 0, max: this.store.frame()?.duration ?? 1800 }),
      axis('mm  ·  %', { min: 0 }),
      { tooltipDigits: 1 },
    );
  });

  readonly lcurveChart = computed(() => {
    const r = this.reg()?.result;
    const sets: ChartDataset<'line', Point[]>[] = [];
    for (const s of r?.studies ?? []) {
      const color = KIND_COLORS[s.regularization];
      sets.push(line(s.regularization, s.curve.map((c) => ({ x: c.residualNorm, y: c.solutionSeminorm })), color, { pointRadius: 1.5 }));
      const corner = s.choices.find((c) => c.method === 'L-curve corner');
      const gcv = s.choices.find((c) => c.method === 'GCV');
      if (corner) {
        sets.push(line(`${s.regularization} corner`, [{ x: corner.residualNorm, y: corner.solutionSeminorm }], color, { showLine: false, pointRadius: 7, pointStyle: 'rectRot', pointBorderColor: '#fff', pointBorderWidth: 1.5 }));
      }
      if (gcv) {
        sets.push(line(`${s.regularization} GCV`, [{ x: gcv.residualNorm, y: gcv.solutionSeminorm }], color, { showLine: false, pointRadius: 6, pointBorderColor: '#fff', pointBorderWidth: 1.5 }));
      }
    }
    const config = lineChart(sets, logAxis('residual ‖Aq − d‖ [K]'), logAxis('seminorm ‖Lq‖'), { tooltipDigits: 3 });
    config.options!.plugins!.legend!.labels!.filter = (item) => !item.text.includes(' ');
    return config;
  });

  readonly errorVsLambda = computed(() => {
    const r = this.reg()?.result;
    const sets = (r?.studies ?? []).map((s) =>
      line(s.regularization, s.curve.map((c) => ({ x: c.relativeLambda, y: c.sourceRelativeError * 100 })), KIND_COLORS[s.regularization]),
    );
    const g = r?.studies.find((s) => s.regularization === 'Gradient')?.choices.find((c) => c.method === 'GCV');
    if (g) {
      sets.push(line('GCV choice (gradient)', [{ x: g.relativeLambda, y: g.metrics.sourceRelativeError * 100 }], SERIES.orange, { showLine: false, pointRadius: 7, pointBorderColor: '#fff', pointBorderWidth: 2 }));
    }
    return lineChart(sets, logAxis('relative λ'), axis('source error [%]', { min: 0, max: 150 }), { tooltipDigits: 1 });
  });

  readonly gcvChart = computed(() => {
    const r = this.reg()?.result;
    const sets = (r?.studies ?? []).map((s) => line(s.regularization, s.curve.map((c) => ({ x: c.relativeLambda, y: c.gcv })), KIND_COLORS[s.regularization]));
    return lineChart(sets, logAxis('relative λ'), logAxis('GCV(λ)'), { tooltipDigits: 4 });
  });

  readonly timelineChart = computed(() => {
    const t = this.reg()?.result.timeline ?? [];
    return lineChart(
      [
        line('Hotspot error [mm]', t.map((p) => ({ x: p.time, y: p.hotspotErrorMm })), SERIES.blue, { pointRadius: 2 }),
        line('Source error [%]', t.map((p) => ({ x: p.time, y: p.sourceRelativeError * 100 })), SERIES.orange, { pointRadius: 2 }),
      ],
      axis('time [s]', { min: 0, max: 1800 }),
      axis('mm  ·  %', { min: 0 }),
      { tooltipDigits: 2 },
    );
  });
}
