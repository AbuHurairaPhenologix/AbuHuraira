import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ChartDataset } from 'chart.js';
import { ExperimentStore } from '../core/experiment-store.service';
import { fmt } from '../core/format';
import { pow10, referenceLine, times } from '../core/math-format';
import { FemVerificationResult, MethodConvergenceRow } from '../core/math-models';
import { ChartComponent } from '../shared/chart.component';
import { INK, Point, SERIES, line, lineChart, logAxis } from '../shared/chart-theme';
import { HeatmapComponent } from '../shared/heatmap.component';
import { MeshComponent } from '../shared/mesh.component';
import { CardComponent, StatComponent } from '../shared/ui';

type ProblemKey = 'manufactured' | 'eigenmode';

@Component({
  selector: 'tt-methods',
  imports: [CardComponent, ChartComponent, HeatmapComponent, MeshComponent, StatComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Numerical Methods · Finite Volumes and Finite Elements</h1>
          <p>
            The same parabolic PDE <span class="math">ρc<sub>p</sub>∂<sub>t</sub>T − ∇·(k∇T) + H(u)(T − T<sub>c</sub>) = q</span> is discretised twice,
            independently: a cell-centred <b>finite-volume</b> scheme (flux balance, ghost-cell boundary closure) and a Galerkin <b>P1 finite-element</b>
            method on the triangulated cell (weak form, element assembly). Both use Crank–Nicolson in time and banded Cholesky in space. Agreement
            between two different discretisations, and convergence of both to the exact solutions, is the verification evidence.
          </p>
        </div>
      </div>

      @if (fem(); as f) {
        <div class="grid g-6">
          <tt-stat label="FEM L² order" [value]="fmt(finest('FEM')?.orderL2, 2)" hint="expected 2 (P1, smooth solution)" [accent]="true" />
          <tt-stat label="FEM H¹ order" [value]="fmt(finest('FEM')?.orderH1, 2)" hint="expected 1 (gradient error)" />
          <tt-stat label="FVM order (RMSE)" [value]="fmt(finest('FVM')?.orderRmse, 2)" hint="expected 2 (five-point stencil)" />
          <tt-stat label="FVM–FEM difference" [value]="pow10(battery()?.fieldRmsDifference, 1)" unit="K" [hint]="'RMS at cell centres, ' + battery()?.nx + '×' + battery()?.ny" [accent]="true" />
          <tt-stat label="Difference shrinks" [value]="times(shrink(), 1)" hint="per grid halving (≈ 4 ⇒ O(h²))" />
          <tt-stat label="Energy agreement" [value]="pow10(energyGap(), 1)" hint="|E_FEM − E_FVM| / E_FVM at t_end" />
        </div>

        <div class="grid g-3">
          <tt-card [heading]="'Finite volumes · ' + f.result.systems[0].dofs + ' cell unknowns'" [sub]="'Battery model at t = ' + f.result.snapshotTime + ' s, u = ' + (f.result.coolingLevel * 100).toFixed(0) + ' % (Robin edges, cold-plate sink, hidden defect)'">
            <tt-heatmap [field]="f.result.fields['fvm']" [range]="range()" />
          </tt-card>
          <tt-card [heading]="'Finite elements · ' + f.result.systems[1].dofs + ' nodal unknowns'" sub="P1 Galerkin solution on the vertices of the same rectangles (each split into two triangles)">
            <tt-heatmap [field]="f.result.fields['fem']" [range]="range()" />
          </tt-card>
          <tt-card heading="Difference T_FEM − T_FVM" sub="FEM evaluated at the FVM cell centres — largest where the defect curvature is largest">
            <tt-heatmap [field]="f.result.fields['difference']" colormap="diverging" [symmetric]="true" [decimals]="4" />
          </tt-card>
        </div>

        <div class="grid g-2">
          <tt-card heading="Mesh-refinement convergence against an exact solution" [sub]="problemLabel()">
            <div card-actions class="seg">
              <button [class.on]="problem() === 'manufactured'" (click)="problem.set('manufactured')">Manufactured</button>
              <button [class.on]="problem() === 'eigenmode'" (click)="problem.set('eigenmode')">Eigenmode</button>
            </div>
            <tt-chart [config]="convergenceChart()" [height]="300" label="FVM and FEM convergence" />
          </tt-card>
          <tt-card heading="Cross-validation on the battery model" sub="No exact solution: RMS and max difference between the two methods, and the cost of each solve">
            <tt-chart [config]="agreementChart()" [height]="300" label="FVM–FEM agreement and runtime" />
          </tt-card>
        </div>

        <div class="grid g-2">
          <tt-card heading="P1 triangulation (coarse illustration)" sub="Each FVM cell is split along its diagonal; the boundary (orange) carries the Robin terms ∫ h_e T v ds">
            <tt-mesh [mesh]="f.result.displayMesh" />
          </tt-card>
          <tt-card heading="From the weak form to a linear system" sub="Find T_h ∈ V_h such that for every test function v ∈ V_h">
            <div class="formula">∫ ρc<sub>p</sub> ∂<sub>t</sub>T v + ∫ k∇T·∇v + ∫ H(u) T v + ∮<sub>Γ<sub>R</sub></sub> h<sub>e</sub> T v = ∫ q v + ∫ H(u)T<sub>c</sub> v + ∮<sub>Γ<sub>R</sub></sub> h<sub>e</sub>T<sub>∞</sub> v</div>
            <div class="formula">ρc<sub>p</sub> M Ṫ + (K + R + H(u) M) T = M q + H(u) T<sub>c</sub> M𝟙 + r</div>
            <p class="muted note">
              Element matrices are exact: M<sup>e</sup><sub>ij</sub> = |T|(1 + δ<sub>ij</sub>)/12, K<sup>e</sup><sub>ij</sub> = k|T|∇φ<sub>i</sub>·∇φ<sub>j</sub>
              with constant barycentric gradients. The θ-step matrix ρc<sub>p</sub>M + θΔt(K + R + H M) is symmetric positive definite.
            </p>
            <div class="table-scroll">
              <table class="data">
                <tr><th>Method</th><th>Unknowns</th><th>DOFs</th><th>Non-zeros</th><th>Per row</th><th>Half-bandwidth</th></tr>
                @for (s of f.result.systems; track s.method) {
                  <tr><td class="strong">{{ s.method }}</td><td>{{ s.unknowns }}</td><td>{{ s.dofs }}</td><td>{{ s.nonZeros }}</td><td>{{ s.nonZerosPerRow }}</td><td>{{ s.halfBandwidth }}</td></tr>
                }
              </table>
            </div>
          </tt-card>
        </div>

        <tt-card heading="Refinement tables" sub="Crank–Nicolson with Δt ∝ h; observed order p = log(e_coarse/e_fine)/log 2. FEM L² and H¹ errors are integrated with a degree-4 quadrature and divided by √|Ω|.">
          <div class="table-scroll">
            <table class="data">
              <tr><th>Problem</th><th>Method</th><th>Grid</th><th>DOFs</th><th>RMSE [K]</th><th>p</th><th>L² error [K]</th><th>p (L²)</th><th>H¹ error [K/m]</th><th>p (H¹)</th><th>Runtime</th></tr>
              @for (r of allRows(); track $index) {
                <tr>
                  <td>{{ r.problem }}</td><td class="strong">{{ r.method }}</td><td>{{ r.nx }}×{{ r.ny }}</td><td>{{ r.dofs }}</td>
                  <td>{{ pow10(r.rmse, 2) }}</td><td class="strong">{{ fmt(r.orderRmse, 2) }}</td>
                  <td>{{ pow10(r.l2Error, 2) }}</td><td class="strong">{{ fmt(r.orderL2, 2) }}</td>
                  <td>{{ pow10(r.h1Error, 2) }}</td><td class="strong">{{ fmt(r.orderH1, 2) }}</td>
                  <td>{{ fmt(r.runtimeMs, 0) }} ms</td>
                </tr>
              }
            </table>
          </div>
          <div class="table-scroll">
            <table class="data">
              <tr><th>Battery grid</th><th>FVM peak</th><th>FEM peak</th><th>RMS diff.</th><th>Max diff.</th><th>FVM energy</th><th>FEM energy</th><th>FVM time</th><th>FEM time</th></tr>
              @for (b of f.result.battery; track b.nx) {
                <tr>
                  <td>{{ b.nx }}×{{ b.ny }}</td><td>{{ fmt(b.fvmPeak, 3) }} °C</td><td>{{ fmt(b.femPeak, 3) }} °C</td>
                  <td class="strong">{{ pow10(b.fieldRmsDifference, 2) }} K</td><td>{{ pow10(b.fieldMaxDifference, 2) }} K</td>
                  <td>{{ fmt(b.fvmEnergyJoules, 1) }} J</td><td>{{ fmt(b.femEnergyJoules, 1) }} J</td>
                  <td>{{ fmt(b.fvmRuntimeMs, 0) }} ms</td><td>{{ fmt(b.femRuntimeMs, 0) }} ms</td>
                </tr>
              }
            </table>
          </div>
        </tt-card>
      } @else {
        <tt-card heading="FEM verification experiment"><p class="muted">The FEM verification experiment is running on the background worker…</p></tt-card>
      }
    </div>
  `,
  styles: `
    .note { font-size: 11.5px; line-height: 1.5; margin: 4px 0 8px; }
    .formula { font-size: 13px; overflow-x: auto; white-space: nowrap; }
    .seg { display: inline-flex; border: 1px solid var(--line-strong); border-radius: 8px; overflow: hidden; }
    .seg button { font: inherit; font-size: 11px; padding: 4px 10px; background: var(--surface-2); color: var(--ink-3); border: 0; cursor: pointer; }
    .seg button.on { background: var(--surface-3); color: var(--ink-1); }
    table.data + .table-scroll, .table-scroll + .table-scroll { margin-top: 12px; }
  `,
})
export class MethodsPage {
  private readonly experiments = inject(ExperimentStore);
  readonly fem = this.experiments.latest<FemVerificationResult>('FemVerification');
  readonly problem = signal<ProblemKey>('manufactured');
  readonly fmt = fmt;
  readonly pow10 = pow10;
  readonly times = times;

  readonly rows = computed<MethodConvergenceRow[]>(() => this.fem()?.result[this.problem()] ?? []);
  readonly allRows = computed(() => [...(this.fem()?.result.manufactured ?? []), ...(this.fem()?.result.eigenmode ?? [])]);
  readonly battery = computed(() => {
    const rows = this.fem()?.result.battery ?? [];
    return rows.length ? rows[rows.length - 1] : null;
  });

  readonly problemLabel = computed(() =>
    this.problem() === 'manufactured'
      ? 'T = T_c + β + A(1 − e^{−t/τ}) cos(πx/Lx) cos(2πy/Ly) with sink and source, insulated edges (every model term active)'
      : 'T = T_b + A e^{−λt} sin(πx/Lx) sin(πy/Ly), Dirichlet edges, no source',
  );

  finest(method: 'FVM' | 'FEM'): MethodConvergenceRow | undefined {
    return this.rows().filter((r) => r.method === method).at(-1);
  }

  readonly range = computed<[number, number] | null>(() => {
    const f = this.fem()?.result.fields;
    if (!f?.['fvm'] || !f?.['fem']) {
      return null;
    }
    return [Math.min(f['fvm'].min, f['fem'].min), Math.max(f['fvm'].max, f['fem'].max)];
  });

  readonly shrink = computed(() => {
    const rows = this.fem()?.result.battery ?? [];
    return rows.length >= 2 ? rows[rows.length - 2].fieldRmsDifference / rows[rows.length - 1].fieldRmsDifference : null;
  });

  readonly energyGap = computed(() => {
    const b = this.battery();
    return b ? Math.abs(b.femEnergyJoules - b.fvmEnergyJoules) / b.fvmEnergyJoules : null;
  });

  readonly convergenceChart = computed(() => {
    const rows = this.rows();
    const fvm = rows.filter((r) => r.method === 'FVM');
    const fem = rows.filter((r) => r.method === 'FEM');
    const h = (r: MethodConvergenceRow) => r.h * 1000;
    const sets: ChartDataset<'line', Point[]>[] = [
      line('FVM · RMSE at cell centres', fvm.map((r) => ({ x: h(r), y: r.rmse })), SERIES.blue, { pointRadius: 4 }),
      line('FEM · RMSE at nodes', fem.map((r) => ({ x: h(r), y: r.rmse })), SERIES.orange, { pointRadius: 4 }),
      line('FEM · L² error', fem.map((r) => ({ x: h(r), y: r.l2Error })), SERIES.aqua, { pointRadius: 4 }),
      line('FEM · H¹ error / 100', fem.map((r) => ({ x: h(r), y: (r.h1Error ?? 0) / 100 })), SERIES.violet, { pointRadius: 4 }),
    ];
    if (fem.length) {
      const xs = fem.map(h);
      sets.push(line('O(h²)', referenceLine(xs, (fem[0].l2Error ?? fem[0].rmse) * 1.8, 2), INK.muted, { borderDash: [5, 4], borderWidth: 1.5 }));
      sets.push(line('O(h)', referenceLine(xs, ((fem[0].h1Error ?? 0) / 100) * 1.8, 1), INK.muted, { borderDash: [2, 3], borderWidth: 1.5 }));
    }
    return lineChart(sets, logAxis('Δx [mm]'), logAxis('error'), { tooltipDigits: 6 });
  });

  readonly agreementChart = computed(() => {
    const rows = this.fem()?.result.battery ?? [];
    const h = (r: { h: number }) => r.h * 1000;
    const sets: ChartDataset<'line', Point[]>[] = [
      line('RMS(T_FEM − T_FVM) [K]', rows.map((r) => ({ x: h(r), y: r.fieldRmsDifference })), SERIES.blue, { pointRadius: 4 }),
      line('max |T_FEM − T_FVM| [K]', rows.map((r) => ({ x: h(r), y: r.fieldMaxDifference })), SERIES.orange, { pointRadius: 4 }),
      line('FVM runtime [s]', rows.map((r) => ({ x: h(r), y: r.fvmRuntimeMs / 1000 })), SERIES.aqua, { pointRadius: 3, borderDash: [4, 3] }),
      line('FEM runtime [s]', rows.map((r) => ({ x: h(r), y: r.femRuntimeMs / 1000 })), SERIES.yellow, { pointRadius: 3, borderDash: [4, 3] }),
    ];
    if (rows.length) {
      sets.push(line('O(h²)', referenceLine(rows.map(h), rows[0].fieldRmsDifference * 1.8, 2), INK.muted, { borderDash: [5, 4], borderWidth: 1.5 }));
    }
    return lineChart(sets, logAxis('Δx [mm]'), logAxis('K  ·  s'), { tooltipDigits: 5 });
  });
}
