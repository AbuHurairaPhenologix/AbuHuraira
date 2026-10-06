import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ExperimentStore } from '../core/experiment-store.service';
import { fmt, pct } from '../core/format';
import { correlationColor, pow10 } from '../core/math-format';
import { IdentifiabilityResult } from '../core/math-models';
import { ChartComponent } from '../shared/chart.component';
import { SERIES_ORDER, axis, barChart, line, lineChart } from '../shared/chart-theme';
import { CardComponent, StatComponent } from '../shared/ui';

@Component({
  selector: 'tt-identifiability',
  imports: [CardComponent, ChartComponent, StatComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Parameter Sensitivity &amp; Identifiability</h1>
          <p>
            <b>Question:</b> can twelve thermistors distinguish an anomalous heat source (amplitude Q, position x₀, y₀) from uncertainty in the
            thermal parameters (conductivity k, edge cooling h<sub>e</sub>)? The local sensitivity matrix
            <span class="math">S = ∂y/∂θ</span> of the sensor histories is computed by central differences of the PDE solver; its scaled column norms,
            collinearity and Fisher information <span class="math">F = SᵀS/σ²</span> quantify what the data can resolve.
          </p>
        </div>
      </div>

      @if (id(); as d) {
        <div class="grid g-6">
          <tt-stat label="Condition number" [value]="fmt(d.result.report.conditionNumber, 2)" hint="column-normalised S (σ_max/σ_min)" [accent]="true" />
          <tt-stat label="Max collinearity index" [value]="fmt(d.result.report.collinearity[0]?.index, 2)" [hint]="'{' + (d.result.report.collinearity[0]?.parameters ?? []).join(', ') + '} · threshold ≈ 10–15'" />
          <tt-stat label="Most confounded pair" [value]="worstPair().label" [hint]="'cos ∠(S_i, S_j) = ' + fmt(worstPair().value, 2)" [accent]="true" />
          <tt-stat label="Observations" [value]="'' + d.result.report.observationCount" [hint]="'σ = ' + d.result.report.noiseStd + ' K · ' + d.result.traceTimes.length + ' samples × sensors'" />
          <tt-stat label="Joint estimate · max error" [value]="pct(maxError('joint'), 2)" hint="all five parameters, fine-grid noisy data" />
          <tt-stat label="Defect bias if k, h_e wrong" [value]="pct(maxError('defect-misspecified'), 1)" hint="Q, x₀, y₀ with mis-specified k, h_e" />
        </div>

        <div class="grid g-3">
          <tt-card heading="Sensitivity magnitude" sub="RMS change of the sensor data for a one-δθ change of each parameter, relative to the noise σ (signal-to-noise)">
            <tt-chart [config]="normChart()" [height]="250" label="Sensitivity column norms" />
          </tt-card>
          <tt-card heading="Sensitivity collinearity" sub="cos∠(S_i, S_j): ±1 means one parameter's effect on the sensors can be mimicked by another">
            <div class="matrix">
              <table>
                <tr><th></th>@for (p of d.result.report.parameters; track p) { <th>{{ p }}</th> }</tr>
                @for (row of d.result.report.correlation; track $index; let i = $index) {
                  <tr>
                    <th>{{ d.result.report.parameters[i] }}</th>
                    @for (v of row; track $index) {
                      <td [style.background]="color(v)" [class.dim]="Math.abs(v) < 0.5">{{ v.toFixed(2) }}</td>
                    }
                  </tr>
                }
              </table>
            </div>
            <p class="muted note">Singular values of the normalised S: {{ singular() }}</p>
          </tt-card>
          <tt-card [heading]="'Sensitivity at ' + d.result.traceSensor" sub="δθ·∂y/∂θ over the charge at the thermistor nearest the defect">
            <tt-chart [config]="traceChart()" [height]="250" label="Sensitivity time series" />
          </tt-card>
        </div>

        <div class="grid g-2">
          <tt-card heading="Cramér–Rao lower bounds" sub="√diag(F⁻¹) — the smallest standard deviation any unbiased estimator can reach (local, model assumed exact)">
            <div class="table-scroll">
              <table class="data">
                <tr><th>θ</th><th>Nominal</th><th>Prior δθ</th><th>RMS sensitivity</th><th>SNR</th><th>CRB std</th><th>Relative</th></tr>
                @for (p of d.result.report.parameters; track p; let j = $index) {
                  <tr>
                    <td class="strong">{{ p }} [{{ d.result.report.units[j] }}]</td><td>{{ pow10(d.result.report.nominal[j], 3) }}</td><td>{{ pow10(d.result.report.uncertainty[j], 2) }}</td>
                    <td>{{ fmt(d.result.report.columnNorms[j], 3) }} K</td><td>{{ fmt(d.result.report.signalToNoise[j], 1) }}</td>
                    <td class="strong">{{ pow10(d.result.report.cramerRaoStd[j], 2) }}</td><td>{{ pct(d.result.report.cramerRaoRelative[j], 2) }}</td>
                  </tr>
                }
              </table>
            </div>
            <div class="table-scroll">
              <table class="data">
                <tr><th>Parameter subset (largest collinearity)</th><th>γ</th></tr>
                @for (c of d.result.report.collinearity.slice(0, 6); track $index) {
                  <tr><td>{{ '{' + c.parameters.join(', ') + '}' }}</td><td class="strong">{{ fmt(c.index, 2) }}</td></tr>
                }
              </table>
            </div>
          </tt-card>
          <tt-card heading="Bounded parameter estimation (Levenberg–Marquardt)" [sub]="'Synthetic data: ' + d.result.dataSource + ' — the estimator uses the coarser model grid (no inverse crime)'">
            @for (e of d.result.estimations; track e.key) {
              <div class="case">
                <div class="case-head"><b>{{ e.name }}</b><span class="muted">{{ e.result.iterations }} iterations · {{ e.result.modelEvaluations }} PDE solves · residual {{ fmt(e.result.initialResidual, 2) }} → {{ fmt(e.result.finalResidual, 2) }} K · σ̂ = {{ fmt(e.result.noiseEstimate, 3) }} K</span></div>
                <table class="data">
                  <tr><th>θ</th><th>Truth</th><th>Start</th><th>Estimate</th><th>Rel. error</th><th>Std. error</th></tr>
                  @for (p of e.result.parameters; track p; let j = $index) {
                    <tr>
                      <td>{{ p }}</td><td>{{ pow10(e.result.truth[j], 4) }}</td><td>{{ pow10(e.result.initial[j], 3) }}</td>
                      <td class="strong">{{ pow10(e.result.estimate[j], 4) }}</td><td [class.bad]="Math.abs(e.result.relativeErrors[j]) > 0.02">{{ pct(e.result.relativeErrors[j], 2) }}</td>
                      <td>{{ pow10(e.result.standardErrors[j], 2) }}</td>
                    </tr>
                  }
                </table>
              </div>
            }
            <p class="muted note">
              With k and h<sub>e</sub> estimated jointly the defect is recovered; holding them at wrong values biases Q and the location — and the residual
              σ̂ well above the 0.1 K noise flags the model mismatch. Identifiability here is local (linearised at the true parameters) and assumes the 2-D model form is correct.
            </p>
          </tt-card>
        </div>
      } @else {
        <tt-card heading="Identifiability experiment"><p class="muted">The identifiability experiment is running on the background worker…</p></tt-card>
      }
    </div>
  `,
  styles: `
    .matrix table { border-collapse: separate; border-spacing: 3px; width: 100%; font-size: 12px; }
    .matrix th { color: var(--ink-3); font-weight: 600; padding: 4px; }
    .matrix td { text-align: center; padding: 9px 4px; border-radius: 5px; color: #fff; font-variant-numeric: tabular-nums; font-weight: 600; }
    .matrix td.dim { color: var(--ink-2); font-weight: 400; }
    .note { font-size: 11.5px; line-height: 1.5; margin: 6px 0 0; }
    .case { margin-bottom: 12px; }
    .case-head { display: flex; flex-direction: column; gap: 2px; font-size: 12px; margin-bottom: 4px; color: var(--ink-1); }
    .case-head .muted { font-size: 11px; }
    td.bad { color: #ff8a8a !important; }
    .table-scroll + .table-scroll { margin-top: 12px; }
  `,
})
export class IdentifiabilityPage {
  private readonly experiments = inject(ExperimentStore);
  readonly id = this.experiments.latest<IdentifiabilityResult>('ParameterIdentifiability');
  readonly fmt = fmt;
  readonly pct = pct;
  readonly pow10 = pow10;
  readonly Math = Math;
  readonly color = correlationColor;

  maxError(key: string): number | null {
    const e = this.id()?.result.estimations.find((x) => x.key === key);
    return e ? Math.max(...e.result.relativeErrors.map(Math.abs)) : null;
  }

  readonly singular = computed(() => (this.id()?.result.report.singularValues ?? []).map((s) => s.toFixed(3)).join(', '));

  readonly worstPair = computed(() => {
    const r = this.id()?.result.report;
    let best = { label: '—', value: 0 };
    if (!r) {
      return best;
    }
    for (let i = 0; i < r.parameters.length; i++) {
      for (let j = i + 1; j < r.parameters.length; j++) {
        if (Math.abs(r.correlation[i][j]) > Math.abs(best.value)) {
          best = { label: `${r.parameters[i]} ↔ ${r.parameters[j]}`, value: r.correlation[i][j] };
        }
      }
    }
    return best;
  });

  readonly normChart = computed(() => {
    const r = this.id()?.result.report;
    return barChart(
      r?.parameters ?? [],
      [{ label: 'signal-to-noise (RMS sensitivity / σ)', data: r?.signalToNoise ?? [], color: SERIES_ORDER.slice(0, r?.parameters.length ?? 0) }],
      axis('SNR', { min: 0 }),
      { digits: 2 },
    );
  });

  readonly traceChart = computed(() => {
    const d = this.id()?.result;
    if (!d) {
      return lineChart([], axis('time [s]'), axis('K'));
    }
    return lineChart(
      d.traces.map((t, j) => line(`δ${t.parameter}`, d.traceTimes.map((x, i) => ({ x, y: t.values[i] })), SERIES_ORDER[j % SERIES_ORDER.length])),
      axis('time [s]', { min: 0 }),
      axis('δθ · ∂T_sensor/∂θ [K]'),
      { tooltipDigits: 3 },
    );
  });
}
