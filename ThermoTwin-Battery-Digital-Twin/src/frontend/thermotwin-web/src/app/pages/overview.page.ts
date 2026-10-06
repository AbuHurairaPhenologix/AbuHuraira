import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ExperimentStore } from '../core/experiment-store.service';
import { clock, energy, fmt, mm, pct } from '../core/format';
import { pow10 } from '../core/math-format';
import { AdjointCheckResult, FemVerificationResult, ReducedOrderResult } from '../core/math-models';
import { TwinStore } from '../core/twin-store.service';
import { ChartComponent } from '../shared/chart.component';
import { INK, SERIES, axis, barChart, limit, line, lineChart } from '../shared/chart-theme';
import { HeatmapComponent } from '../shared/heatmap.component';
import { CardComponent, StatComponent } from '../shared/ui';

@Component({
  selector: 'tt-overview',
  imports: [StatComponent, CardComponent, HeatmapComponent, ChartComponent, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>PDE-Constrained Battery Thermal Digital Twin</h1>
          <p>
            Twelve noisy thermistors observe a 200 × 100 mm Li-ion cell during a fast charge. The twin couples a parabolic heat PDE (solved by finite
            volumes and verified against finite elements), a regularised inverse problem for the hidden heat source, a POD reduced-order model and an
            adjoint-based PDE-constrained optimiser that keeps T(x, t) ≤ T<sub>safe</sub> with the least pump energy.
          </p>
        </div>
      </div>

      @if (f(); as f) {
        <div class="grid g-6">
          <tt-stat label="Max temperature (true)" [value]="fmt(f.truth.max, 2)" unit="°C" [hint]="'at (' + mm(f.truth.maxX) + ', ' + mm(f.truth.maxY) + ') mm'" />
          <tt-stat label="Twin estimate · max" [value]="fmt(f.estimate?.max, 2)" unit="°C" [hint]="'mean ' + fmt(f.estimate?.mean, 2) + ' °C'" [accent]="true" />
          <tt-stat label="Average temperature" [value]="fmt(f.truth.mean, 2)" unit="°C" [hint]="'min ' + fmt(f.truth.min, 2) + ' °C'" />
          <tt-stat label="Thermal spread" [value]="fmt(f.truth.spread, 2)" unit="K" hint="T_max − T_min across the cell" />
          <tt-stat label="Hotspot (reconstructed)" [value]="hotspotText()" unit="mm" [hint]="'localisation error ' + fmt(f.hotspotErrorMm, 1) + ' mm'" [accent]="true" />
          <tt-stat label="Reconstruction RMSE" [value]="fmt(f.estimation?.temperatureRmse, 3)" unit="K" [hint]="'source error ' + pct(f.estimation?.sourceRelativeError, 1)" />
          <tt-stat label="Predicted peak · no action" [value]="fmt(f.forecast?.unmitigatedPeak, 2)" unit="°C" [hint]="'baseline cooling ' + pct(f.cooling.baselineLevel)" />
          <tt-stat label="Predicted peak · optimised" [value]="fmt(f.forecast?.plannedPeak, 2)" unit="°C" [hint]="'T_safe ' + f.safeTemperature + ' °C · margin 1 K'" [accent]="true" />
          <tt-stat label="Cooling level (applied)" [value]="pct(f.cooling.level, 1)" [hint]="fmt(f.cooling.powerWatts, 2) + ' W pump power'" />
          <tt-stat label="Recommended cooling" [value]="pct(f.cooling.recommendedLevel, 1)" [hint]="planText()" />
          <tt-stat label="Without twin (counterfactual)" [value]="fmt(f.counterfactualMax, 2)" unit="°C" hint="same cell, baseline cooling only" />
          <tt-stat label="Simulation time" [value]="clock(f.time)" unit="mm:ss" [hint]="'step ' + f.step + ' / ' + f.totalSteps + ' · energy ' + energy(f.cooling.energyJoules)" />
        </div>

        <div class="pipeline">
          @for (s of pipeline(); track s.title; let last = $last; let i = $index) {
            <a class="stage" [routerLink]="s.route">
              <div class="k"><span class="no">{{ i + 1 }}</span>{{ s.title }}</div>
              <div class="v">{{ s.value }}</div>
              <div class="d">{{ s.detail }}</div>
            </a>
            @if (!last) {
              <div class="arrow">→</div>
            }
          }
        </div>

        <div class="grid main">
          <tt-card heading="True temperature field" sub="Ground-truth plant (80×40 grid, restricted to 40×20). ○ dashed: hidden defect · ✛ blue: twin's reconstructed hotspot · ● thermistors">
            <tt-heatmap [field]="fields()['trueTemperature']" [sensors]="f.sensors" [trueHotspots]="f.trueHotspots" [estimatedHotspot]="f.estimatedHotspot" />
          </tt-card>
          <tt-card heading="Twin estimate of the temperature field" sub="T̂ = T₀ + Σ q̂ⱼ Tⱼ reconstructed from 12 sensors only">
            <tt-heatmap [field]="fields()['estimatedTemperature']" [range]="tempRange()" [sensors]="f.sensors" [estimatedHotspot]="f.estimatedHotspot" [showSensorLabels]="false" />
          </tt-card>
          <tt-card heading="Peak temperature" sub="Controlled cell vs counterfactual (baseline cooling) vs twin estimate">
            <tt-chart [config]="peakChart()" [height]="236" label="Peak temperature over time" />
          </tt-card>
        </div>

        <div class="grid g-3">
          <tt-card heading="Sensor readings" sub="Latest noisy measurements (σ = 0.1 K)">
            <tt-chart [config]="sensorChart()" [height]="210" label="Sensor readings" />
          </tt-card>
          <tt-card heading="Cooling intensity" sub="Applied control u(t) and charging heat load s(t)">
            <tt-chart [config]="coolingChart()" [height]="210" label="Cooling intensity" />
          </tt-card>
          <tt-card heading="Reconstructed heat source" sub="q̂(x, y) from Tikhonov inversion (GCV-selected λ)">
            <tt-heatmap [field]="fields()['estimatedSource']" colormap="source" [trueHotspots]="f.trueHotspots" [estimatedHotspot]="f.estimatedHotspot" [decimals]="0" />
          </tt-card>
        </div>
      }
    </div>
  `,
  styles: `
    .main { grid-template-columns: minmax(0, 1.05fr) minmax(0, 1.05fr) minmax(0, 1fr); }
    @media (max-width: 1300px) { .main { grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); } }
    @media (max-width: 760px) { .main { grid-template-columns: minmax(0, 1fr); } }
    .pipeline {
      display: flex; align-items: stretch; gap: 6px; padding: 10px; border-radius: 12px;
      background: var(--surface-1); border: 1px solid var(--line); overflow-x: auto;
    }
    .stage { flex: 1 1 0; min-width: 118px; padding: 8px 10px; border-radius: 8px; background: var(--surface-2); border: 1px solid var(--line); text-decoration: none; }
    .stage:hover { border-color: rgba(57, 135, 229, 0.6); }
    .no { display: inline-grid; place-items: center; width: 15px; height: 15px; border-radius: 50%; background: rgba(57,135,229,.18); color: var(--blue); font-size: 9.5px; font-weight: 700; margin-right: 5px; }
    .k { font-size: 10px; text-transform: uppercase; letter-spacing: .07em; color: var(--ink-3); white-space: nowrap; }
    .v { color: var(--ink-1); font-weight: 600; font-size: 14px; margin-top: 3px; white-space: nowrap; font-variant-numeric: tabular-nums; }
    .d { font-size: 10.5px; color: var(--ink-3); margin-top: 2px; white-space: nowrap; }
    .arrow { display: grid; place-items: center; color: var(--blue); font-size: 15px; }
  `,
})
export class OverviewPage {
  private readonly store = inject(TwinStore);
  private readonly experiments = inject(ExperimentStore);
  private readonly fem = this.experiments.latest<FemVerificationResult>('FemVerification');
  private readonly rom = this.experiments.latest<ReducedOrderResult>('ReducedOrderModel');
  private readonly adjoint = this.experiments.latest<AdjointCheckResult>('AdjointGradientCheck');
  readonly f = this.store.frame;
  readonly fields = this.store.fields;
  readonly fmt = fmt;
  readonly pct = pct;
  readonly mm = mm;
  readonly clock = clock;
  readonly energy = energy;

  readonly tempRange = computed<[number, number] | null>(() => {
    const t = this.fields()['trueTemperature'];
    return t ? [t.min, t.max] : null;
  });

  readonly hotspotText = computed(() => {
    const h = this.f()?.estimatedHotspot;
    return h ? `${mm(h.x)}, ${mm(h.y)}` : '—';
  });

  readonly planText = computed(() => {
    const plan = this.f()?.cooling.recommendedPlan;
    return plan ? `next ${plan.length}×100 s: ` + plan.map((v) => (v * 100).toFixed(0)).join(' · ') + ' %' : 'optimiser starts at t = 60 s';
  });

  readonly pipeline = computed(() => {
    const f = this.f();
    if (!f) {
      return [];
    }
    const e = f.estimation;
    const femRow = (this.fem()?.result.manufactured ?? []).filter((r) => r.method === 'FEM').at(-1);
    const battery = this.fem()?.result.battery.at(-1);
    const rom = this.rom()?.result;
    const romRow = rom?.comparison.find((c) => c.family === 'Source-basis family' && c.modes === rom.selectedModes);
    const optimizer = f.cooling.optimizer === 'AdjointReducedOrder' ? 'adjoint · POD ROM' : f.cooling.optimizer === 'PenaltyFiniteDifference' ? 'finite differences' : 'adjoint · full order';
    return [
      { title: 'Thermal model', route: '/model', value: 'Parabolic PDE', detail: 'ρcₚTₜ = ∇·k∇T + q − H(u)(T − T_c)' },
      { title: 'FVM / FEM solve', route: '/methods', value: `FEM L² p = ${fmt(femRow?.orderL2, 2)}`, detail: `FVM–FEM Δ ${pow10(battery?.fieldRmsDifference)} K` },
      { title: 'Sparse sensors', route: '/twin', value: `${f.sensors.length} thermistors`, detail: `${e?.measurementCount ?? 0} samples · σ = 0.1 K` },
      { title: 'Inverse source', route: '/inverse', value: `${fmt(f.hotspotErrorMm, 1)} mm`, detail: `λ = ${e ? e.relativeLambda.toExponential(1) : '—'} (GCV) · RMSE ${fmt(e?.temperatureRmse, 3)} K` },
      { title: 'POD reduced model', route: '/rom', value: rom ? `r = ${rom.selectedModes} of ${rom.fullDimension}` : '—', detail: `ROM error ${fmt(romRow?.rmse, 3)} K` },
      { title: 'Forecast', route: '/thermal', value: `${fmt(f.forecast?.unmitigatedPeak, 1)} °C`, detail: '600 s horizon, no action' },
      { title: 'Adjoint optimisation', route: '/optimization', value: `${fmt(f.forecast?.plannedPeak, 1)} °C`, detail: `${optimizer} · ∇ error ${pow10(this.adjoint()?.result.bestRelativeError)}` },
      { title: 'MPC cooling', route: '/cooling', value: pct(f.cooling.level, 1), detail: f.mode === 'Autonomous' ? `applied · ${fmt(f.cooling.optimizerMs, 0)} ms/solve` : f.mode.toLowerCase() },
    ];
  });

  readonly peakChart = computed(() => {
    const h = this.store.history();
    const f = this.f();
    const end = f?.duration ?? 1800;
    return lineChart(
      [
        line('True T_max', h.map((p) => ({ x: p.time, y: p.trueMax })), SERIES.blue),
        line('Without twin', h.map((p) => ({ x: p.time, y: p.counterfactualMax })), SERIES.orange),
        line('Twin estimate', h.map((p) => ({ x: p.time, y: p.estimatedMax })), SERIES.aqua, { borderDash: [4, 3] }),
        limit('T_safe', f?.safeTemperature ?? 45, 0, end),
      ],
      axis('time [s]', { min: 0, max: end }),
      axis('°C'),
    );
  });

  readonly sensorChart = computed(() => {
    const f = this.f();
    const sensors = f?.sensors ?? [];
    return barChart(
      sensors.map((s) => s.id),
      [{ label: 'Reading [°C]', data: sensors.map((s) => s.value), color: SERIES.blue }],
      axis('°C', { min: Math.floor(Math.min(...sensors.map((s) => s.value), 25) - 1) }),
    );
  });

  readonly coolingChart = computed(() => {
    const h = this.store.history();
    const end = this.f()?.duration ?? 1800;
    return lineChart(
      [
        line('Cooling u(t)', h.map((p) => ({ x: p.time, y: p.coolingLevel * 100 })), SERIES.blue, { stepped: true }),
        line('Heat load s(t)', h.map((p) => ({ x: p.time, y: p.loadFactor * 100 })), INK.muted, { borderDash: [4, 3], borderWidth: 1.5 }),
        limit('Baseline 15 %', (this.f()?.cooling.baselineLevel ?? 0.15) * 100, 0, end, INK.secondary),
      ],
      axis('time [s]', { min: 0, max: end }),
      axis('%', { min: 0, max: 105 }),
      { tooltipDigits: 1 },
    );
  });
}
