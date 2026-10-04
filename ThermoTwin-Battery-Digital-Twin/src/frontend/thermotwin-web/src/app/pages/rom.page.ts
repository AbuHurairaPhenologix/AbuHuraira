import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ChartDataset } from 'chart.js';
import { ExperimentStore } from '../core/experiment-store.service';
import { fmt, pct } from '../core/format';
import { pow10, times } from '../core/math-format';
import { ReducedOrderControlResult, ReducedOrderResult, RomComparisonRow } from '../core/math-models';
import { ChartComponent } from '../shared/chart.component';
import { INK, Point, SERIES, axis, limit, line, lineChart, logAxis } from '../shared/chart-theme';
import { HeatmapComponent } from '../shared/heatmap.component';
import { CardComponent, StatComponent } from '../shared/ui';

const FAMILY_COLOR: Record<string, string> = { 'Source-basis family': SERIES.blue, 'Defect-lattice family': SERIES.orange };
const RUN_COLOR: Record<string, string> = { legacy: SERIES.magenta, adjoint: SERIES.blue, rom: SERIES.aqua };

@Component({
  selector: 'tt-rom',
  imports: [CardComponent, ChartComponent, HeatmapComponent, StatComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Reduced-Order Model · Proper Orthogonal Decomposition</h1>
          <p>
            High-fidelity snapshots are compressed by POD (method of snapshots, symmetric eigensolver written in C#). The heat equation is
            Galerkin-projected onto the r leading modes, <span class="math">T ≈ T̄ + Φ<sub>r</sub>a</span>, giving
            <span class="math">ȧ = (αΦ<sub>r</sub>ᵀAΦ<sub>r</sub> − σ(u)I)a + g(t, u)</span>. Because the cold-plate sink acts as σ(u)·I it commutes with the
            diffusion operator, so the reduced operator is diagonalised once and every cooling level costs O(r) per step.
          </p>
        </div>
      </div>

      @if (rom(); as r) {
        <div class="grid g-6">
          <tt-stat label="Selected ROM dimension" [value]="'r = ' + r.result.selectedModes" [hint]="'from n = ' + r.result.fullDimension + ' (' + times(r.result.fullDimension / r.result.selectedModes, 0) + ' smaller)'" [accent]="true" />
          <tt-stat label="Captured snapshot energy" [value]="fmt(selected()?.capturedEnergy, 6)" [hint]="'1 − E(r) = ' + pow10(1 - (selected()?.capturedEnergy ?? 1))" />
          <tt-stat label="Field RMSE (held-out)" [value]="fmt(selected()?.rmse, 3)" unit="K" [hint]="'projection limit ' + fmt(selected()?.projectionRmse, 3) + ' K'" />
          <tt-stat label="Peak-temperature error" [value]="fmt(selected()?.peakError, 3)" unit="K" hint="true defect · baseline cooling" />
          <tt-stat label="ROM optimisation speed-up" [value]="times(selectedOpt()?.speedup)" [hint]="'objective gap ' + pct(selectedOpt()?.objectiveGap, 2) + ' · FOM-certified'" [accent]="true" />
          <tt-stat label="ROM-MPC time per decision" [value]="fmt(run('rom')?.meanOptimizerMs, 0)" unit="ms" [hint]="'vs ' + fmt(run('adjoint')?.meanOptimizerMs, 0) + ' ms full-order adjoint'" />
        </div>

        <div class="grid g-3">
          <tt-card heading="POD spectrum" sub="Eigenvalues λ_i of the snapshot correlation matrix (K²) for both training families">
            <tt-chart [config]="spectrumChart()" [height]="260" label="POD eigenvalue decay" />
          </tt-card>
          <tt-card heading="Unresolved energy 1 − E(r)" sub="E(r) = Σ_{i≤r} λ_i / Σ λ_i — fraction of snapshot variance outside the first r modes">
            <tt-chart [config]="energyChart()" [height]="260" label="Cumulative captured energy" />
          </tt-card>
          <tt-card heading="Accuracy versus dimension" sub="Held-out test (true defect, baseline cooling): ROM error tracks the best-approximation (projection) error">
            <tt-chart [config]="accuracyChart()" [height]="260" label="ROM error versus r" />
          </tt-card>
        </div>

        <tt-card heading="Leading POD modes" [sub]="'Source-basis training: ' + training()?.trainingRuns + ' full-order runs, ' + training()?.snapshots + ' snapshots, POD in ' + fmt(training()?.podMs, 0) + ' ms'">
          <div class="modes">
            @for (m of r.result.modes; track m.key) {
              <tt-heatmap [field]="m" [title]="m.label" colormap="diverging" [symmetric]="true" [decimals]="4" />
            }
          </div>
        </tt-card>

        <div class="grid g-3">
          <tt-card heading="Full-order model" [sub]="'n = ' + r.result.fullDimension + ' finite-volume unknowns'">
            <tt-heatmap [field]="r.result.fields['fom']" [range]="range()" />
          </tt-card>
          <tt-card [heading]="'POD–Galerkin ROM (r = ' + r.result.selectedModes + ')'" sub="Same initial state, source and cooling schedule; field reconstructed from r coefficients">
            <tt-heatmap [field]="r.result.fields['rom']" [range]="range()" />
          </tt-card>
          <tt-card heading="ROM error T_ROM − T_FOM" sub="Largest at the defect, whose sharp Gaussian is the hardest feature to compress">
            <tt-heatmap [field]="r.result.fields['error']" colormap="diverging" [symmetric]="true" [decimals]="3" />
          </tt-card>
        </div>

        <div class="grid g-2">
          <tt-card heading="Peak temperature: FOM vs ROM" [sub]="'Held-out test · r = ' + r.result.selectedModes + ' and r = ' + r.result.timeSeries.coarseModes">
            <tt-chart [config]="seriesChart()" [height]="250" label="FOM versus ROM peak temperature" />
          </tt-card>
          <tt-card heading="Accuracy–speed trade-off" sub="Full-field trajectory simulation (30 min); speed-up is limited by the O(nr) field reconstruction at every step">
            <tt-chart [config]="tradeoffChart()" [height]="250" label="ROM accuracy versus speed-up" />
          </tt-card>
        </div>

        <tt-card heading="ROM-accelerated optimisation (open loop, reconstructed source)" [sub]="'Optimise on the ROM with the state constraint screened to the hot region, certify with one full-order solve, defect-correct or fall back · ' + r.result.selectionRule">
          <div class="table-scroll">
            <table class="data">
              <tr><th>Model</th><th>Runtime</th><th>Speed-up</th><th>J</th><th>Gap to FOM optimum</th><th>FOM-certified peak</th><th>Feasible</th><th>Screened cells</th><th>Corrections</th><th>ROM error</th><th>Fallback</th></tr>
              @for (o of r.result.optimization; track o.model) {
                <tr [class.highlight]="o.modes === r.result.selectedModes">
                  <td class="strong">{{ o.model }}</td><td>{{ fmt(o.runtimeMs / 1000, 2) }} s</td><td class="strong">{{ times(o.speedup) }}</td>
                  <td>{{ fmt(o.objective, 5) }}</td><td>{{ pct(o.objectiveGap, 2) }}</td><td>{{ fmt(o.fullOrderPeak, 3) }} °C</td>
                  <td><span class="pill" [class.ok]="o.feasible" [class.bad]="!o.feasible">{{ o.feasible ? '✓' : '✖' }}</span></td>
                  <td>{{ o.screenedCells }}</td><td>{{ o.correctionRounds }}</td><td>{{ fmt(o.validationError, 3) }} K</td><td>{{ o.fellBack ? o.fallbackReason : 'no' }}</td>
                </tr>
              }
            </table>
          </div>
        </tt-card>
      }

      @if (control(); as c) {
        <div class="grid g-2">
          <tt-card heading="Closed-loop MPC on the plant" [sub]="'Full 30-min charge on the 80×40 ground truth; ROM rejected if |peak_ROM − peak_FOM| > ' + c.result.romValidationThreshold + ' K'">
            <tt-chart [config]="mpcChart()" [height]="260" label="Closed-loop MPC peak temperature" />
          </tt-card>
          <tt-card heading="High-fidelity MPC vs reduced-order MPC" sub="Same twin, same estimator; only the optimiser inside MPC differs">
            <div class="table-scroll">
              <table class="data">
                <tr><th>MPC optimiser</th><th>Plant peak</th><th>Energy</th><th>Per decision</th><th>Total</th><th>Fallbacks</th><th>Mean ROM error</th></tr>
                @for (x of c.result.runs; track x.key) {
                  <tr [class.highlight]="x.key === 'rom'">
                    <td class="strong">{{ x.name }}</td><td>{{ fmt(x.plantPeak, 2) }} °C</td><td>{{ fmt(x.energyJoules, 0) }} J</td>
                    <td class="strong">{{ fmt(x.meanOptimizerMs, 0) }} ms</td><td>{{ fmt(x.totalOptimizerMs / 1000, 1) }} s</td>
                    <td>{{ x.key === 'rom' ? x.fallbacks + ' / ' + x.optimizations : '—' }}</td><td>{{ x.key === 'rom' ? fmt(x.meanValidationError, 3) + ' K' : '—' }}</td>
                  </tr>
                }
              </table>
            </div>
          </tt-card>
        </div>
      }

      @if (!rom() && !control()) {
        <tt-card heading="Reduced-order experiments"><p class="muted">The POD experiments are running on the background worker…</p></tt-card>
      }
    </div>
  `,
  styles: `
    .modes { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px 16px; }
    @media (max-width: 1100px) { .modes { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
    @media (max-width: 700px) { .modes { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class RomPage {
  private readonly experiments = inject(ExperimentStore);
  readonly rom = this.experiments.latest<ReducedOrderResult>('ReducedOrderModel');
  readonly control = this.experiments.latest<ReducedOrderControlResult>('ReducedOrderControl');
  readonly fmt = fmt;
  readonly pct = pct;
  readonly pow10 = pow10;
  readonly times = times;

  private readonly baselineTest = computed(() => this.rom()?.result.comparison[0]?.test ?? '');

  readonly selected = computed<RomComparisonRow | undefined>(() => {
    const r = this.rom()?.result;
    return r?.comparison.find((c) => c.family === 'Source-basis family' && c.modes === r.selectedModes && c.test === this.baselineTest());
  });

  readonly selectedOpt = computed(() => {
    const r = this.rom()?.result;
    return r?.optimization.find((o) => o.modes === r.selectedModes);
  });

  readonly training = computed(() => this.rom()?.result.training.find((t) => t.family === 'Source-basis family'));

  run(key: string) {
    return this.control()?.result.runs.find((r) => r.key === key);
  }

  readonly range = computed<[number, number] | null>(() => {
    const f = this.rom()?.result.fields;
    return f?.['fom'] ? [f['fom'].min, f['fom'].max] : null;
  });

  private families() {
    return [...new Set((this.rom()?.result.spectrum ?? []).map((p) => p.family))];
  }

  readonly spectrumChart = computed(() => {
    const spectrum = this.rom()?.result.spectrum ?? [];
    const sets = this.families().map((f) =>
      line(f, spectrum.filter((p) => p.family === f && p.eigenvalue > 0).map((p) => ({ x: p.index, y: p.eigenvalue })), FAMILY_COLOR[f] ?? SERIES.yellow, { pointRadius: 1.5 }),
    );
    const r = this.rom()?.result.selectedModes;
    if (r) {
      sets.push(line(`r = ${r}`, [{ x: r, y: 1e-12 }, { x: r, y: 1e6 }], INK.muted, { borderDash: [5, 4], borderWidth: 1.5 }));
    }
    return lineChart(sets, axis('mode index i', { min: 1, max: 100 }), logAxis('λ_i [K²]', { min: 1e-12, max: 1e6 }), { tooltipDigits: 6 });
  });

  readonly energyChart = computed(() => {
    const spectrum = this.rom()?.result.spectrum ?? [];
    const sets = this.families().map((f) =>
      line(f, spectrum.filter((p) => p.family === f).map((p) => ({ x: p.index, y: Math.max(1 - p.cumulativeEnergy, 1e-14) })), FAMILY_COLOR[f] ?? SERIES.yellow, { pointRadius: 1.5 }),
    );
    return lineChart(sets, axis('number of modes r', { min: 1, max: 100 }), logAxis('1 − E(r)', { min: 1e-14 }), { tooltipDigits: 10 });
  });

  readonly accuracyChart = computed(() => {
    const rows = (this.rom()?.result.comparison ?? []).filter((c) => c.test === this.baselineTest());
    const sets: ChartDataset<'line', Point[]>[] = [];
    for (const f of this.families()) {
      const fr = rows.filter((c) => c.family === f);
      const color = FAMILY_COLOR[f] ?? SERIES.yellow;
      sets.push(line(`${f} · ROM RMSE`, fr.map((c) => ({ x: c.modes, y: c.rmse })), color, { pointRadius: 4 }));
      sets.push(line(`${f} · projection RMSE`, fr.map((c) => ({ x: c.modes, y: c.projectionRmse })), color, { borderDash: [4, 3], pointRadius: 0 }));
      sets.push(line(`${f} · peak error`, fr.map((c) => ({ x: c.modes, y: c.peakError })), color, { pointRadius: 3, pointStyle: 'triangle', borderWidth: 1 }));
    }
    return lineChart(sets, axis('ROM dimension r', { min: 0 }), logAxis('K'), { tooltipDigits: 4 });
  });

  readonly seriesChart = computed(() => {
    const s = this.rom()?.result.timeSeries;
    if (!s) {
      return lineChart([], axis('time [s]'), axis('°C'));
    }
    return lineChart(
      [
        line('Full-order model', s.times.map((t, i) => ({ x: t, y: s.fullOrderMax[i] })), SERIES.blue, { borderWidth: 3 }),
        line(`ROM r = ${this.rom()?.result.selectedModes}`, s.times.map((t, i) => ({ x: t, y: s.selectedMax[i] })), SERIES.aqua, { borderDash: [5, 3] }),
        line(`ROM r = ${s.coarseModes}`, s.times.map((t, i) => ({ x: t, y: s.coarseMax[i] })), SERIES.orange, { borderDash: [2, 3] }),
      ],
      axis('time [s]', { min: 0 }),
      axis('max T [°C]'),
      { tooltipDigits: 3 },
    );
  });

  readonly tradeoffChart = computed(() => {
    const rows = (this.rom()?.result.comparison ?? []).filter((c) => c.test === this.baselineTest());
    const sets = this.families().map((f) =>
      line(f, rows.filter((c) => c.family === f).map((c) => ({ x: c.speedup, y: c.rmse })), FAMILY_COLOR[f] ?? SERIES.yellow, { pointRadius: 4, showLine: true }),
    );
    return lineChart(sets, axis('speed-up over full-order simulation', { min: 0 }), logAxis('field RMSE [K]'), { tooltipDigits: 4 });
  });

  readonly mpcChart = computed(() => {
    const c = this.control()?.result;
    if (!c) {
      return lineChart([], axis('time [s]'), axis('°C'));
    }
    const end = Math.max(...c.runs.map((r) => r.times[r.times.length - 1] ?? 0));
    const sets: ChartDataset<'line', Point[]>[] = c.runs.map((r) =>
      line(r.name, r.times.map((t, i) => ({ x: t, y: r.maxTemperature[i] })), RUN_COLOR[r.key] ?? SERIES.yellow),
    );
    sets.push(limit('T_safe', c.safeTemperature, 0, end));
    return lineChart(sets, axis('time [s]', { min: 0, max: end }), axis('plant max T [°C]', { min: 25 }), { tooltipDigits: 2 });
  });
}
