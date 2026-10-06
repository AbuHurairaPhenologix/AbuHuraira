import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ChartDataset } from 'chart.js';
import { ExperimentStore } from '../core/experiment-store.service';
import { fmt, pct } from '../core/format';
import { groupBy, num, pow10, referenceLine, times } from '../core/math-format';
import { AdjointCheckResult, OptimizationBenchmarkResult } from '../core/math-models';
import { ChartComponent } from '../shared/chart.component';
import { INK, Point, SERIES, SERIES_ORDER, axis, barChart, limit, line, lineChart, logAxis } from '../shared/chart-theme';
import { CardComponent, StatComponent } from '../shared/ui';

const METHOD_COLOR: Record<string, string> = {
  adjoint: SERIES.blue,
  'finite-difference': SERIES.orange,
  'rom-adjoint': SERIES.aqua,
  legacy: SERIES.magenta,
};

@Component({
  selector: 'tt-optimization',
  imports: [CardComponent, ChartComponent, StatComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>PDE-Constrained Optimisation · Adjoint Method</h1>
          <p>
            Cooling is planned by solving an optimal-control problem whose constraint is the discretised heat equation itself. The pointwise state
            constraint T ≤ T<sub>safe</sub> is relaxed by a Moreau–Yosida penalty, and the gradient with respect to all K control segments is obtained
            from <b>one forward and one backward (adjoint) sweep</b> of the same Crank–Nicolson operators — independent of K. Finite differences are kept
            as the validation reference.
          </p>
        </div>
      </div>

      @if (bench(); as b) {
        <div class="grid g-6">
          <tt-stat label="Adjoint vs FD optimisation" [value]="times(b.result.adjointSpeedup)" [hint]="'wall time · ' + times(b.result.solveReduction) + ' fewer PDE solves'" [accent]="true" />
          <tt-stat label="Gradient check" [value]="pow10(check()?.result?.bestRelativeError)" [hint]="'‖g_adj − g_FD‖/‖g_FD‖ at ε = ' + pow10(check()?.result?.bestEpsilon, 0)" [accent]="true" />
          <tt-stat label="Adjoint gradient cost" [value]="fmt(adjointCost(), 0)" unit="ms" [hint]="'for K = ' + costRange() + ' controls (constant)'" />
          <tt-stat label="KKT residual (adjoint)" [value]="pow10(method('adjoint')?.kkt?.projectedGradientNorm)" hint="‖P(u − ∇Φ) − u‖∞ at the solution" />
          <tt-stat label="Energy vs original optimiser" [value]="pct(energySaving(), 1)" [hint]="fmt(method('adjoint')?.energyJoules, 0) + ' J vs ' + fmt(method('legacy')?.energyJoules, 0) + ' J'" />
          <tt-stat label="Plant peak (adjoint plan)" [value]="fmt(method('adjoint')?.plantPeak, 2)" unit="°C" [hint]="'T_safe ' + b.result.safeTemperature + ' °C · model target ' + b.result.controlTemperature + ' °C'" />
        </div>

        <div class="grid g-2">
          <tt-card heading="Optimal-control problem" [sub]="b.result.segments + ' piecewise-constant controls × ' + b.result.segmentDuration + ' s · state dimension n = ' + b.result.stateDimension + ' · Δt = ' + b.result.predictionTimeStep + ' s'">
            <div class="formula">min<sub>u ∈ U<sub>ad</sub></sub> J(T, u) = (1/E<sub>ref</sub>) Σ<sub>k</sub> P<sub>rated</sub> u<sub>k</sub><sup>3</sup> Δt<sub>k</sub> + w Σ<sub>k</sub> (u<sub>k+1</sub> − u<sub>k</sub>)<sup>2</sup></div>
            <div class="formula">s.t. ρc<sub>p</sub>∂<sub>t</sub>T − k∇²T + H(u)(T − T<sub>c</sub>) = q,&nbsp;&nbsp; T(x, t) ≤ T<sub>safe</sub>,&nbsp;&nbsp; U<sub>ad</sub> = [0, 1]<sup>K</sup></div>
            <div class="formula">Φ<sub>μ</sub>(u) = J + μ · mean<sub>x,t</sub> max(0, T − T<sub>safe</sub>)<sup>2</sup>,&nbsp;&nbsp; μ = {{ penalties() }}</div>
            <div class="formula">L(u<sup>n−1</sup>)λ<sup>n</sup> = R(u<sup>n</sup>)λ<sup>n+1</sup> − μ∂P/∂x<sup>n</sup>,&nbsp;&nbsp; ∂Φ/∂u<sub>k</sub> = ∂J/∂u<sub>k</sub> + Σ<sub>n∈k</sub> σ′Δt λ<sup>n+1</sup>·(θx<sup>n+1</sup> + (1−θ)x<sup>n</sup> − T<sub>c</sub>)</div>
            <p class="muted note">L = I − θΔtM and R = I + (1−θ)ΔtM are symmetric (Lᵀ = L), so the backward sweep re-uses the forward Cholesky factors. Stage solver: projected BFGS with Armijo arc search; a final uniform-shift bisection removes any O(1/μ) violation.</p>
          </tt-card>
          <tt-card heading="Convergence per PDE solve" sub="Penalised objective Φ_μ versus cumulative solves (forward + adjoint). Each μ stage restarts at a higher penalty.">
            <tt-chart [config]="historyChart()" [height]="300" label="Objective versus PDE solves" />
          </tt-card>
        </div>

        <div class="grid g-2">
          <tt-card heading="Optimal control schedules" sub="u(t) of each method — all within the box constraints 0 ≤ u ≤ 1">
            <tt-chart [config]="planChart()" [height]="250" label="Control schedules" />
          </tt-card>
          <tt-card heading="State constraint along the horizon" sub="Maximum model temperature under each plan versus the model target T_safe − margin">
            <tt-chart [config]="temperatureChart()" [height]="250" label="Maximum temperature" />
          </tt-card>
        </div>

        <tt-card heading="Benchmark: adjoint gradient vs finite differences" [sub]="'Same objective, same projected quasi-Newton algorithm, same start; PDE solves of ' + b.result.stateDimension + ' unknowns × ' + (b.result.segments * b.result.segmentDuration / b.result.predictionTimeStep) + ' steps'">
          <div class="table-scroll">
            <table class="data">
              <tr><th>Method</th><th>Gradient</th><th>J</th><th>Energy</th><th>Model peak</th><th>Plant peak</th><th>Iter.</th><th>Forward</th><th>Adjoint</th><th>Runtime</th><th>‖P(u−∇Φ)−u‖∞</th><th>Feasible</th></tr>
              @for (m of b.result.methods; track m.key) {
                <tr [class.highlight]="m.key === 'adjoint'">
                  <td class="strong">{{ m.name }}</td><td>{{ m.gradient }}</td><td>{{ fmt(m.objective, 5) }}</td><td>{{ fmt(m.energyJoules, 0) }} J</td>
                  <td>{{ fmt(m.modelPeak, 3) }} °C</td><td>{{ fmt(m.plantPeak, 3) }} °C</td><td>{{ m.iterations }}</td><td>{{ m.forwardSolves }}</td><td>{{ m.adjointSolves }}</td>
                  <td class="strong">{{ fmt(m.runtimeMs / 1000, 1) }} s</td><td>{{ pow10(m.kkt.projectedGradientNorm) }}</td>
                  <td><span class="pill" [class.ok]="m.feasible" [class.bad]="!m.feasible">{{ m.feasible ? '✓' : '✖' }}</span></td>
                </tr>
              }
            </table>
          </div>
          <p class="muted note">
            The finite-difference run uses forward differences (ε = 10⁻⁴, K + 1 solves per gradient, sequential); its O(ε) gradient error stalls the line search before the
            KKT residual is small. The ROM run optimises the POD model on screened hot cells and certifies the plan with one full-order solve. The original optimiser
            penalises max<sub>x</sub>T (non-smooth) and is parallelised over finite-difference components.
          </p>
        </tt-card>

        <tt-card heading="First-order optimality (KKT) at the computed plans" [sub]="'Box constraints: g_k ≥ 0 if u_k = 0, g_k ≤ 0 if u_k = 1, g_k = 0 otherwise · evaluated with the adjoint gradient at μ = ' + pow10(b.result.methods[0].kkt.mu, 0)">
          <div class="table-scroll">
            <table class="data">
              <tr><th>Method</th><th>‖∇Φ‖∞</th><th>‖P(u−∇Φ)−u‖∞</th><th>Lower active</th><th>Upper active</th><th>Inactive</th><th>max |g| inactive</th><th>min multiplier (L / U)</th><th>State violation</th><th>Active state points</th></tr>
              @for (m of b.result.methods; track m.key) {
                <tr>
                  <td class="strong">{{ m.name }}</td><td>{{ pow10(m.kkt.gradientNorm) }}</td><td class="strong">{{ pow10(m.kkt.projectedGradientNorm) }}</td>
                  <td>{{ m.kkt.activeLower }}</td><td>{{ m.kkt.activeUpper }}</td><td>{{ m.kkt.inactive }}</td><td>{{ pow10(m.kkt.maxInactiveGradient) }}</td>
                  <td>{{ pow10(m.kkt.minLowerMultiplier) }} / {{ pow10(m.kkt.minUpperMultiplier) }}</td><td>{{ fmt(m.kkt.maxStateViolation, 4) }} K</td><td>{{ m.kkt.stateActivePoints }}</td>
                </tr>
              }
            </table>
          </div>
        </tt-card>
      }

      @if (check(); as c) {
        <div class="adjoint-section">
          <div class="grid g-3">
            <tt-card heading="Adjoint gradient check" [sub]="'Relative error ‖g_adj − g_FD‖/‖g_FD‖ against central differences for 4 random control vectors (K = ' + c.result.controls + ')'">
              <tt-chart [config]="checkChart()" [height]="260" label="Gradient check" />
            </tt-card>
            <tt-card heading="Taylor test" sub="|Φ(u+εd) − Φ(u)| = O(ε) while |Φ(u+εd) − Φ(u) − ε∇Φᵀd| = O(ε²) — only a correct gradient gives slope 2">
              <tt-chart [config]="taylorChart()" [height]="260" label="Taylor test" />
            </tt-card>
            <tt-card heading="Cost of one gradient" sub="Fixed 30-min horizon split into K controls: the adjoint cost is independent of K, finite differences grow linearly">
              <tt-chart [config]="costChart()" [height]="260" label="Gradient cost versus number of controls" />
            </tt-card>
          </div>
          <tt-card heading="Adjoint and finite-difference gradient components" [sub]="'Control vector 1, central FD with ε = 10⁻⁶ · ' + c.result.model + ', N = ' + c.result.timeSteps + ' steps, μ = ' + pow10(c.result.mu, 0)">
            <tt-chart [config]="componentChart()" [height]="220" label="Gradient components" />
          </tt-card>
        </div>
      }

      @if (!bench() && !check()) {
        <tt-card heading="Optimisation experiments"><p class="muted">The adjoint and optimisation experiments are running on the background worker…</p></tt-card>
      }
    </div>
  `,
  styles: `
    .note { font-size: 11.5px; line-height: 1.5; margin: 6px 0 0; }
    .formula { font-size: 13px; overflow-x: auto; white-space: nowrap; margin-bottom: 6px; }
    .adjoint-section { display: flex; flex-direction: column; gap: 14px; }
  `,
})
export class OptimizationPage {
  private readonly experiments = inject(ExperimentStore);
  readonly bench = this.experiments.latest<OptimizationBenchmarkResult>('OptimizationBenchmark');
  readonly check = this.experiments.latest<AdjointCheckResult>('AdjointGradientCheck');
  readonly fmt = fmt;
  readonly pct = pct;
  readonly pow10 = pow10;
  readonly times = times;

  method(key: string) {
    return this.bench()?.result.methods.find((m) => m.key === key);
  }

  readonly penalties = computed(() => (this.bench()?.result.penaltySchedule ?? []).map((p) => pow10(p, 0)).join(' → '));

  readonly energySaving = computed(() => {
    const a = this.method('adjoint');
    const l = this.method('legacy');
    return a && l ? 1 - a.energyJoules / l.energyJoules : null;
  });

  readonly adjointCost = computed(() => {
    const cost = this.check()?.result.cost ?? [];
    return cost.length ? cost.reduce((s, c) => s + c.adjointMs, 0) / cost.length : null;
  });

  readonly costRange = computed(() => {
    const cost = this.check()?.result.cost ?? [];
    return cost.length ? `${cost[0].controls}…${cost[cost.length - 1].controls}` : '—';
  });

  readonly historyChart = computed(() => {
    const methods = (this.bench()?.result.methods ?? []).filter((m) => m.key === 'adjoint' || m.key === 'finite-difference');
    const sets = methods.map((m) =>
      line(
        m.name,
        m.history.map((h) => ({ x: h.forwardSolves + h.adjointSolves, y: num(h.value) })),
        METHOD_COLOR[m.key],
        { pointRadius: 2 },
      ),
    );
    return lineChart(sets, logAxis('cumulative PDE solves (forward + adjoint)'), logAxis('Φ_μ(u)'), { tooltipDigits: 6 });
  });

  readonly planChart = computed(() => {
    const b = this.bench()?.result;
    if (!b) {
      return lineChart([], axis('time [s]'), axis('u'));
    }
    const sets = b.methods.map((m) => {
      const pts: Point[] = m.plan.map((u, k) => ({ x: k * b.segmentDuration, y: u * 100 }));
      pts.push({ x: m.plan.length * b.segmentDuration, y: m.plan[m.plan.length - 1] * 100 });
      return line(m.name, pts, METHOD_COLOR[m.key] ?? SERIES.yellow, { stepped: true });
    });
    return lineChart(sets, axis('time [s]', { min: 0 }), axis('cooling u [%]', { min: 0 }), { tooltipDigits: 1 });
  });

  readonly temperatureChart = computed(() => {
    const b = this.bench()?.result;
    if (!b) {
      return lineChart([], axis('time [s]'), axis('°C'));
    }
    const end = b.segments * b.segmentDuration;
    const sets: ChartDataset<'line', Point[]>[] = b.methods.map((m) =>
      line(m.name, m.times.map((t, i) => ({ x: t, y: m.modelMaxTemperature[i] })), METHOD_COLOR[m.key] ?? SERIES.yellow),
    );
    sets.push(limit('model target T_safe − margin', b.controlTemperature, 0, end));
    return lineChart(sets, axis('time [s]', { min: 0, max: end }), axis('max T [°C]'), { tooltipDigits: 3 });
  });

  readonly checkChart = computed(() => {
    const rows = this.check()?.result.rows ?? [];
    const groups = groupBy(rows, (r) => `${r.model}#${r.vector}`);
    const sets: ChartDataset<'line', Point[]>[] = [];
    let i = 0;
    for (const [key, list] of groups) {
      const [model, vector] = key.split('#');
      const rom = !model.startsWith('Full');
      sets.push(
        line(rom ? `${model}` : `FOM · vector ${Number(vector) + 1}`, list.map((r) => ({ x: r.epsilon, y: Math.max(r.relativeError, 1e-14) })),
          rom ? SERIES.aqua : SERIES_ORDER[[0, 1, 3, 4][i++ % 4]], { pointRadius: 3, borderDash: rom ? [4, 3] : undefined }),
      );
    }
    return lineChart(sets, logAxis('finite-difference step ε'), logAxis('relative gradient error'), { tooltipDigits: 10 });
  });

  readonly taylorChart = computed(() => {
    const rows = this.check()?.result.taylor ?? [];
    const xs = rows.map((r) => r.epsilon);
    return lineChart(
      [
        line('zero-order remainder R₀', rows.map((r) => ({ x: r.epsilon, y: r.zeroOrderRemainder })), SERIES.orange, { pointRadius: 3 }),
        line('first-order remainder R₁', rows.map((r) => ({ x: r.epsilon, y: r.firstOrderRemainder })), SERIES.blue, { pointRadius: 3 }),
        line('O(ε)', referenceLine(xs, (rows[0]?.zeroOrderRemainder ?? 1) * 2, 1), INK.muted, { borderDash: [2, 3], borderWidth: 1.5 }),
        line('O(ε²)', referenceLine(xs, (rows[0]?.firstOrderRemainder ?? 1) * 2, 2), INK.muted, { borderDash: [5, 4], borderWidth: 1.5 }),
      ],
      logAxis('step ε along direction d'),
      logAxis('remainder'),
      { tooltipDigits: 12 },
    );
  });

  readonly costChart = computed(() => {
    const cost = this.check()?.result.cost ?? [];
    return lineChart(
      [
        line('discrete adjoint (2 sweeps)', cost.map((c) => ({ x: c.controls, y: c.adjointMs / 1000 })), SERIES.blue, { pointRadius: 4 }),
        line('forward differences (K + 1 solves)', cost.map((c) => ({ x: c.controls, y: c.forwardDifferenceMs / 1000 })), SERIES.orange, { pointRadius: 4 }),
        line('central differences (2K solves)', cost.map((c) => ({ x: c.controls, y: c.centralDifferenceMs / 1000 })), SERIES.yellow, { pointRadius: 4 }),
      ],
      logAxis('number of controls K'),
      logAxis('time per gradient [s]'),
      { tooltipDigits: 3 },
    );
  });

  readonly componentChart = computed(() => {
    const comps = this.check()?.result.components ?? [];
    return barChart(
      comps.map((c) => `u${c.segment + 1}`),
      [
        { label: 'adjoint ∂Φ/∂u_k', data: comps.map((c) => c.adjoint), color: SERIES.blue },
        { label: 'central FD', data: comps.map((c) => c.finiteDifference), color: SERIES.orange },
      ],
      axis('∂Φ/∂u_k'),
      { digits: 6 },
    );
  });
}
