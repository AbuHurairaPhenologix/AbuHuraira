import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ChartDataset } from 'chart.js';
import { ExperimentStore } from '../core/experiment-store.service';
import { fmt, sci } from '../core/format';
import { ConvergenceResult, ConvergenceRow, TimeScheme } from '../core/models';
import { ChartComponent } from '../shared/chart.component';
import { INK, Point, SERIES, line, lineChart, logAxis } from '../shared/chart-theme';
import { CardComponent, StatComponent } from '../shared/ui';

const SCHEME_COLOR: Record<TimeScheme, string> = {
  ExplicitEuler: SERIES.blue,
  ImplicitEuler: SERIES.orange,
  CrankNicolson: SERIES.aqua,
};

const SCHEME_NAME: Record<TimeScheme, string> = {
  ExplicitEuler: 'Explicit Euler',
  ImplicitEuler: 'Implicit Euler',
  CrankNicolson: 'Crank–Nicolson',
};

@Component({
  selector: 'tt-validation',
  imports: [CardComponent, ChartComponent, StatComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Numerical Validation</h1>
          <p>
            Verification against the exact solution T = T<sub>b</sub> + A·e<sup>−απ²(1/Lx² + 1/Ly²)t</sup> sin(πx/Lx) sin(πy/Ly) with Dirichlet edges.
            The sampled mode is an exact eigenvector of the discrete Laplacian, which also yields the exact semi-discrete solution
            used to isolate temporal error.
          </p>
        </div>
      </div>

      @if (conv(); as c) {
        <div class="grid g-4">
          <tt-stat label="Spatial order (Crank–Nicolson)" [value]="fmt(order('CrankNicolson', 'spatial'), 2)" hint="expected 2 — five-point Laplacian" [accent]="true" />
          <tt-stat label="Temporal order (Crank–Nicolson)" [value]="fmt(order('CrankNicolson', 'temporal'), 2)" hint="expected 2 — θ = ½" />
          <tt-stat label="Temporal order (Implicit Euler)" [value]="fmt(order('ImplicitEuler', 'temporal'), 2)" hint="expected 1 — θ = 1" />
          <tt-stat label="Energy balance error" [value]="sci(maxImbalance(), 1)" hint="finite-volume conservation (relative)" />
        </div>

        <div class="grid g-2">
          <tt-card heading="Spatial convergence" sub="RMSE at t = 100 s versus grid spacing (Δt ∝ h²); dashed: O(h²) reference">
            <tt-chart [config]="spatialChart()" [height]="280" label="Spatial convergence" />
          </tt-card>
          <tt-card heading="Temporal convergence" sub="RMSE against the exact semi-discrete solution (40×20 grid); dashed: O(Δt) and O(Δt²)">
            <tt-chart [config]="temporalChart()" [height]="280" label="Temporal convergence" />
          </tt-card>
        </div>

        <tt-card heading="Grid refinement study" sub="Observed order p = log(e_coarse / e_fine) / log 2">
          <div class="table-scroll">
            <table class="data">
              <tr><th>Scheme</th><th>Grid</th><th>Δx [mm]</th><th>Δt [s]</th><th>Steps</th><th>RMSE [K]</th><th>Max error [K]</th><th>p (RMSE)</th><th>p (max)</th><th>Runtime</th></tr>
              @for (r of c.result.spatial; track $index) {
                <tr>
                  <td>{{ name(r.scheme) }}</td>
                  <td>{{ r.nx }}×{{ r.ny }}</td>
                  <td>{{ (r.dx * 1000).toFixed(2) }}</td>
                  <td>{{ r.timeStep.toPrecision(3) }}</td>
                  <td>{{ r.steps }}</td>
                  <td class="strong">{{ sci(r.rmse, 3) }}</td>
                  <td>{{ sci(r.maxError, 3) }}</td>
                  <td class="strong">{{ fmt(r.observedOrderRmse, 3) }}</td>
                  <td>{{ fmt(r.observedOrderMax, 3) }}</td>
                  <td>{{ fmt(r.runtimeMs, 1) }} ms</td>
                </tr>
              }
            </table>
          </div>
        </tt-card>

        <div class="grid g-3">
          <tt-card heading="Explicit Euler stability limit" sub="Rough initial field, 400 steps at Δt = ratio × Δt_crit (Δt_crit = 2/ρ(M) from power iteration)">
            <tt-chart [config]="stabilityChart()" [height]="220" label="Stability probe" />
            <table class="data">
              <tr><th>Δt / Δt_crit</th><th>Δt [s]</th><th>Final ‖T‖∞</th><th>Result</th></tr>
              @for (p of c.result.stabilityProbes; track p.timeStepRatio) {
                <tr><td>{{ p.timeStepRatio }}</td><td>{{ p.timeStep.toFixed(4) }}</td><td>{{ sci(p.finalAmplitude, 2) }}</td>
                  <td><span class="pill" [class.ok]="!p.diverged" [class.bad]="p.diverged">{{ p.diverged ? '✖ diverges' : '✓ stable' }}</span></td></tr>
              }
            </table>
          </tt-card>
          <tt-card heading="Stability of the demo configuration" sub="40×20 model grid, full cooling (u = 1)">
            <table class="data">
              <tr><th>Scheme</th><th>Δt [s]</th><th>Fourier r</th><th>|G| max</th><th>G(stiff)</th><th>Stable</th></tr>
              @for (r of c.result.stabilityReports; track $index) {
                <tr>
                  <td>{{ name(r.scheme) }}</td><td>{{ r.timeStep }}</td><td>{{ fmt(r.fourierNumber, 3) }}</td>
                  <td>{{ fmt(r.amplificationFactor, 3) }}</td><td>{{ fmt(r.stiffModeAmplification, 3) }}</td>
                  <td><span class="pill" [class.ok]="r.isStable" [class.bad]="!r.isStable">{{ r.isStable ? '✓' : '✖' }}</span></td>
                </tr>
              }
            </table>
            @if (c.result.stabilityReports[0]; as r0) {
              <p class="muted note">
                ρ(M) ≈ {{ fmt(r0.spectralRadiusEstimate, 4) }} s⁻¹ (Gershgorin bound {{ fmt(r0.spectralRadiusBound, 4) }}) ⇒ explicit
                Δt<sub>crit</sub> = {{ fmt(r0.explicitCriticalTimeStep, 3) }} s; the textbook bound 1/(2α(Δx⁻² + Δy⁻²)) gives
                {{ fmt(r0.classicalExplicitLimit, 3) }} s. Crank–Nicolson's stiff-mode factor near −1 explains why it damps
                high-frequency errors slowly — acceptable here because the heat sources are smooth.
              </p>
            }
          </tt-card>
          <tt-card heading="Conservation & performance" sub="Insulated cell with non-uniform heating; Release build timings">
            <table class="data">
              <tr><th>Scheme</th><th>Injected [J]</th><th>Stored [J]</th><th>Rel. error</th></tr>
              @for (e of c.result.energyConservation; track e.scheme) {
                <tr><td>{{ name(e.scheme) }}</td><td>{{ fmt(e.injectedEnergy, 2) }}</td><td>{{ fmt(e.storedEnergyChange, 2) }}</td><td class="strong">{{ sci(e.relativeImbalance, 1) }}</td></tr>
              }
            </table>
            <table class="data perf">
              <tr><th>Workload</th><th>Steps</th><th>Total</th><th>Per step</th></tr>
              @for (p of c.result.performance; track p.label) {
                <tr><td>{{ p.label }}</td><td>{{ p.steps }}</td><td>{{ fmt(p.totalMs, 0) }} ms</td><td class="strong">{{ fmt(p.msPerStep, 3) }} ms</td></tr>
              }
            </table>
          </tt-card>
        </div>
      } @else {
        <tt-card heading="Convergence experiment"><p class="muted">The convergence experiment is running on the background worker…</p></tt-card>
      }
    </div>
  `,
  styles: `
    .note { font-size: 11.5px; line-height: 1.5; margin: 8px 0 0; }
    .perf { margin-top: 12px; }
  `,
})
export class ValidationPage {
  private readonly experiments = inject(ExperimentStore);
  readonly conv = this.experiments.latest<ConvergenceResult>('NumericalConvergence');
  readonly fmt = fmt;
  readonly sci = sci;

  name(s: TimeScheme): string {
    return SCHEME_NAME[s];
  }

  order(scheme: TimeScheme, kind: 'spatial' | 'temporal'): number | null {
    const rows = (this.conv()?.result[kind] ?? []).filter((r) => r.scheme === scheme);
    return rows.at(-1)?.observedOrderRmse ?? null;
  }

  readonly maxImbalance = computed(() => Math.max(...(this.conv()?.result.energyConservation ?? []).map((e) => e.relativeImbalance), 0));

  private series(rows: ConvergenceRow[], x: (r: ConvergenceRow) => number): ChartDataset<'line', Point[]>[] {
    return (['ExplicitEuler', 'ImplicitEuler', 'CrankNicolson'] as TimeScheme[])
      .map((s) => ({ s, pts: rows.filter((r) => r.scheme === s) }))
      .filter((g) => g.pts.length)
      .map((g) => line(SCHEME_NAME[g.s], g.pts.map((r) => ({ x: x(r), y: r.rmse })), SCHEME_COLOR[g.s], { pointRadius: 4 }));
  }

  private reference(label: string, xs: number[], y0: number, order: number): ChartDataset<'line', Point[]> {
    const x0 = xs[0];
    return line(label, xs.map((x) => ({ x, y: y0 * Math.pow(x / x0, order) })), INK.muted, { borderDash: [5, 4], borderWidth: 1.5 });
  }

  readonly spatialChart = computed(() => {
    const rows = this.conv()?.result.spatial ?? [];
    const sets = this.series(rows, (r) => r.dx * 1000);
    const cn = rows.filter((r) => r.scheme === 'CrankNicolson');
    if (cn.length) {
      sets.push(this.reference('O(h²)', cn.map((r) => r.dx * 1000), cn[0].rmse * 1.6, 2));
    }
    return lineChart(sets, logAxis('Δx [mm]'), logAxis('RMSE [K]'), { tooltipDigits: 6 });
  });

  readonly temporalChart = computed(() => {
    const rows = (this.conv()?.result.temporal ?? []).filter((r) => r.scheme !== 'ExplicitEuler');
    const sets = this.series(rows, (r) => r.timeStep);
    const ie = rows.filter((r) => r.scheme === 'ImplicitEuler');
    const cn = rows.filter((r) => r.scheme === 'CrankNicolson');
    if (ie.length) {
      sets.push(this.reference('O(Δt)', ie.map((r) => r.timeStep), ie[0].rmse * 1.6, 1));
    }
    if (cn.length) {
      sets.push(this.reference('O(Δt²)', cn.map((r) => r.timeStep), cn[0].rmse * 1.6, 2));
    }
    return lineChart(sets, logAxis('Δt [s]'), logAxis('RMSE [K]'), { tooltipDigits: 6 });
  });

  readonly stabilityChart = computed(() => {
    const probes = this.conv()?.result.stabilityProbes ?? [];
    const config = lineChart(
      [line('Final amplitude ‖T‖∞', probes.map((p) => ({ x: p.timeStepRatio, y: Math.max(p.finalAmplitude, 1e-12) })), SERIES.blue, { pointRadius: 4 })],
      { type: 'linear', title: { display: true, text: 'Δt / Δt_crit', color: INK.muted }, grid: { color: INK.grid }, ticks: { color: INK.muted } },
      logAxis('‖T‖∞ after 400 steps'),
      { tooltipDigits: 3 },
    );
    return config;
  });
}
