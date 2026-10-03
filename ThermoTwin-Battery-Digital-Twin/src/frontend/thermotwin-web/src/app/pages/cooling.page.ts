import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ExperimentStore } from '../core/experiment-store.service';
import { energy, fmt, pct } from '../core/format';
import { CoolingComparisonResult } from '../core/models';
import { TwinStore } from '../core/twin-store.service';
import { ChartComponent } from '../shared/chart.component';
import { INK, SERIES, SERIES_ORDER, axis, barChart, limit, line, lineChart, logAxis } from '../shared/chart-theme';
import { CardComponent, StatComponent } from '../shared/ui';

@Component({
  selector: 'tt-cooling',
  imports: [CardComponent, ChartComponent, StatComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Cooling Optimisation</h1>
          <p>
            <span class="math">min J(u) = Σ P(uₖ)Δt / E<sub>ref</sub> + w Σ(Δuₖ)²</span> subject to
            <span class="math">T<sub>max</sub>(t; u) ≤ T<sub>safe</sub></span> and <span class="math">0 ≤ uₖ ≤ 1</span>,
            with pump power P(u) = P<sub>rated</sub>u³. Solved by an exterior quadratic penalty method with projected-gradient inner iterations;
            each gradient component costs one forward PDE solve.
          </p>
        </div>
      </div>

      @if (store.frame(); as f) {
        <div class="grid g-6">
          <tt-stat label="Applied cooling" [value]="pct(f.cooling.level, 1)" [hint]="fmt(f.cooling.powerWatts, 2) + ' W'" [accent]="true" />
          <tt-stat label="Recommended next" [value]="pct(f.cooling.recommendedLevel, 1)" [hint]="(f.cooling.optimizerEvaluations ?? 0) + ' PDE solves · ' + fmt(f.cooling.optimizerMs, 0) + ' ms'" />
          <tt-stat label="Forecast peak · baseline" [value]="fmt(f.forecast?.unmitigatedPeak, 2)" unit="°C" hint="no action (15 % cooling)" />
          <tt-stat label="Forecast peak · optimised" [value]="fmt(f.forecast?.plannedPeak, 2)" unit="°C" [hint]="'control target ' + (f.safeTemperature - 1) + ' °C'" [accent]="true" />
          <tt-stat label="Peak so far · controlled" [value]="fmt(peak().controlled, 2)" unit="°C" [hint]="'counterfactual ' + fmt(peak().counterfactual, 2) + ' °C'" />
          <tt-stat label="Cooling energy used" [value]="energy(f.cooling.energyJoules)" [hint]="'≈ ' + fmt(f.cooling.energyJoules / 3600, 2) + ' Wh'" />
        </div>

        <div class="grid g-3">
          <tt-card heading="Reduction of peak temperature" sub="Controlled cell (MPC) vs identical cell with baseline cooling">
            <tt-chart [config]="peakChart()" [height]="240" label="Peak temperature" />
          </tt-card>
          <tt-card heading="Cooling intensity u(t)" sub="Receding-horizon decisions applied every 60 s">
            <tt-chart [config]="levelChart()" [height]="240" label="Cooling intensity" />
          </tt-card>
          <tt-card heading="Current recommendation" [sub]="'Optimal plan for the next ' + ((f.cooling.recommendedPlan?.length ?? 0) * f.cooling.segmentDuration) + ' s'">
            <tt-chart [config]="planChart()" [height]="240" label="Recommended plan" />
          </tt-card>
        </div>
      }

      @if (cmp(); as c) {
        <tt-card heading="Strategy comparison on the ground-truth plant"
          [sub]="'Each strategy runs on the 80×40 plant for the full 30-minute charge. T_safe = ' + c.result.safeTemperature + ' °C; optimiser target ' + c.result.controlTemperature + ' °C. Objective Φ = E/E_ref + wΣ(Δu)² + μ·mean(max(0, T−T_safe)²), μ = 10⁴.'">
          <div class="table-scroll">
            <table class="data">
              <tr><th>Strategy</th><th>Peak T</th><th>Energy</th><th>Max violation</th><th>Time above T_safe</th><th>Objective Φ</th><th>Feasible</th></tr>
              @for (s of c.result.strategies; track s.key) {
                <tr [class.highlight]="s.key === 'optimized' || s.key === 'mpc'">
                  <td>{{ s.name }}<div class="desc">{{ s.description }}</div></td>
                  <td class="strong">{{ fmt(s.peakTemperature, 2) }} °C</td>
                  <td>{{ energy(s.coolingEnergyJoules) }}</td>
                  <td>{{ fmt(s.maxViolation, 2) }} K</td>
                  <td>{{ s.timeAboveLimit.toFixed(0) }} s</td>
                  <td>{{ objective(s.objective) }}</td>
                  <td><span class="pill" [class.ok]="s.feasible" [class.bad]="!s.feasible">{{ s.feasible ? '✓ yes' : '✖ no' }}</span></td>
                </tr>
              }
            </table>
          </div>
        </tt-card>

        <div class="grid g-3">
          <tt-card heading="Maximum temperature by strategy" sub="Plant T_max(t) under each cooling policy">
            <tt-chart [config]="strategyTemperature()" [height]="270" label="Maximum temperature by strategy" />
          </tt-card>
          <tt-card heading="Cooling energy by strategy" sub="Total pump energy over the charge (log scale)">
            <tt-chart [config]="energyChart()" [height]="270" label="Cooling energy" />
          </tt-card>
          <tt-card heading="Optimisation objective" [sub]="'Penalty continuation μ = 10 → 300 → 10⁴; ' + c.result.optimizationEvaluations + ' PDE solves in ' + (c.result.optimizationMs / 1000).toFixed(1) + ' s'">
            <tt-chart [config]="objectiveChart()" [height]="270" label="Optimisation objective" />
          </tt-card>
        </div>
        <tt-card heading="Cooling schedules" sub="Constant minimal-feasible level vs time-varying optimised plan vs closed-loop MPC">
          <tt-chart [config]="scheduleChart()" [height]="230" label="Cooling schedules" />
        </tt-card>
      } @else {
        <tt-card heading="Strategy comparison"><p class="muted">The cooling-comparison experiment is running on the background worker (≈ 1 minute)…</p></tt-card>
      }
    </div>
  `,
  styles: `.desc { font-size: 10.5px; color: var(--ink-3); white-space: normal; max-width: 420px; }`,
})
export class CoolingPage {
  readonly store = inject(TwinStore);
  private readonly experiments = inject(ExperimentStore);
  readonly cmp = this.experiments.latest<CoolingComparisonResult>('CoolingComparison');
  readonly fmt = fmt;
  readonly pct = pct;
  readonly energy = energy;

  private readonly end = computed(() => this.store.frame()?.duration ?? 1800);

  readonly peak = computed(() => {
    const h = this.store.history();
    return {
      controlled: h.length ? Math.max(...h.map((p) => p.trueMax)) : null,
      counterfactual: h.length ? Math.max(...h.map((p) => p.counterfactualMax)) : null,
    };
  });

  objective(v: number): string {
    return v >= 100 ? v.toExponential(2) : v.toFixed(4);
  }

  readonly peakChart = computed(() => {
    const h = this.store.history();
    return lineChart(
      [
        line('With ThermoTwin (MPC)', h.map((p) => ({ x: p.time, y: p.trueMax })), SERIES.blue),
        line('Baseline cooling only', h.map((p) => ({ x: p.time, y: p.counterfactualMax })), SERIES.orange),
        limit('T_safe', this.store.frame()?.safeTemperature ?? 45, 0, this.end()),
      ],
      axis('time [s]', { min: 0, max: this.end() }),
      axis('°C'),
    );
  });

  readonly levelChart = computed(() => {
    const h = this.store.history();
    return lineChart(
      [
        line('Applied u(t)', h.map((p) => ({ x: p.time, y: p.coolingLevel * 100 })), SERIES.blue, { stepped: true }),
        line('Baseline', [{ x: 0, y: 15 }, { x: this.end(), y: 15 }], INK.secondary, { borderDash: [5, 4], borderWidth: 1.5 }),
      ],
      axis('time [s]', { min: 0, max: this.end() }),
      axis('%', { min: 0 }),
      { tooltipDigits: 1 },
    );
  });

  readonly planChart = computed(() => {
    const f = this.store.frame();
    const plan = f?.cooling.recommendedPlan ?? [];
    const t0 = f?.forecast?.issuedAt ?? 0;
    return barChart(
      plan.map((_, k) => `${(t0 + k * (f?.cooling.segmentDuration ?? 100)).toFixed(0)} s`),
      [{ label: 'Cooling level [%]', data: plan.map((v) => v * 100), color: SERIES.blue }],
      axis('%', { min: 0 }),
      { digits: 1 },
    );
  });

  readonly strategyTemperature = computed(() => {
    const c = this.cmp()?.result;
    const sets = (c?.strategies ?? []).map((s, k) => line(s.name, s.times.map((t, i) => ({ x: t, y: s.maxTemperature[i] })), SERIES_ORDER[k]));
    if (c) {
      sets.push(limit('T_safe', c.safeTemperature, 0, 1800));
    }
    const config = lineChart(sets, axis('time [s]', { min: 0, max: 1800 }), axis('°C'));
    config.options!.plugins!.legend!.position = 'bottom';
    return config;
  });

  readonly energyChart = computed(() => {
    const s = (this.cmp()?.result.strategies ?? []).filter((x) => x.coolingEnergyJoules > 0);
    return barChart(
      s.map((x) => x.name.replace(' (open loop)', '').replace(' (live twin)', '')),
      [{ label: 'Energy [J]', data: s.map((x) => x.coolingEnergyJoules), color: s.map((x) => (x.key === 'optimized' || x.key === 'mpc' ? SERIES.blue : '#5c5b56')) }],
      logAxis('energy [J]') as never,
      { horizontal: true, digits: 0 },
    );
  });

  readonly objectiveChart = computed(() => {
    const hist = this.cmp()?.result.optimizationHistory ?? [];
    return lineChart(
      [
        line('Penalised objective Φ', hist.map((h) => ({ x: h.iteration, y: h.objective })), SERIES.blue, { pointRadius: 2 }),
        line('Energy term E/E_ref', hist.map((h) => ({ x: h.iteration, y: h.energyTerm })), SERIES.orange, { pointRadius: 2 }),
      ],
      axis('iteration'),
      logAxis('value'),
      { tooltipDigits: 5 },
    );
  });

  readonly scheduleChart = computed(() => {
    const c = this.cmp()?.result;
    const pick = (key: string) => c?.strategies.find((s) => s.key === key);
    const sets = [];
    for (const [key, color] of [
      ['min-constant', INK.secondary],
      ['optimized', SERIES.blue],
      ['mpc', SERIES.orange],
    ] as const) {
      const s = pick(key);
      if (s) {
        sets.push(line(s.name, s.times.map((t, i) => ({ x: t, y: s.coolingLevel[i] * 100 })), color, { stepped: true }));
      }
    }
    return lineChart(sets, axis('time [s]', { min: 0, max: 1800 }), axis('cooling level [%]', { min: 0 }), { tooltipDigits: 1 });
  });
}
