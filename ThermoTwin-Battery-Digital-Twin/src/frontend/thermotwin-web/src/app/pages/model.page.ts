import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { ExperimentStore } from '../core/experiment-store.service';
import { fmt, pct } from '../core/format';
import { pow10, times } from '../core/math-format';
import {
  AdjointCheckResult,
  FemVerificationResult,
  IdentifiabilityResult,
  OptimizationBenchmarkResult,
  ReducedOrderControlResult,
  ReducedOrderResult,
} from '../core/math-models';
import { ConvergenceResult, RegularizationResult, ScenarioDefinition } from '../core/models';
import { CardComponent } from '../shared/ui';

interface Stage {
  step: string;
  title: string;
  route: string;
  equation: string;
  method: string;
  result: string;
}

@Component({
  selector: 'tt-model',
  imports: [CardComponent, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Mathematical Model · Scientific Summary</h1>
          <p>
            ThermoTwin.NET formulates battery thermal safety as a coupled mathematical problem: a <b>parabolic PDE</b>, <b>sparse-data inverse
            reconstruction</b>, <b>reduced-order simulation</b> and <b>PDE-constrained optimal control</b>. Every number below is read from the stored,
            reproducible experiment results.
          </p>
        </div>
      </div>

      <div class="chain">
        @for (s of stages(); track s.step; let last = $last) {
          <a class="stage" [routerLink]="s.route">
            <div class="n">{{ s.step }}</div>
            <div class="t">{{ s.title }}</div>
            <div class="eq">{{ s.equation }}</div>
            <div class="m">{{ s.method }}</div>
            <div class="r">{{ s.result }}</div>
          </a>
          @if (!last) {
            <div class="arrow">→</div>
          }
        }
      </div>

      <div class="grid g-3">
        <tt-card heading="Governing equation" sub="Depth-averaged energy balance of a thin pouch cell (δ ≪ L_x, L_y)">
          <div class="formula">ρc<sub>p</sub> ∂T/∂t = ∇·(k∇T) + q(x, t) − (h(u)/δ)(T − T<sub>c</sub>)</div>
          <div class="formula">−k ∂T/∂n = h<sub>e</sub>(T − T<sub>∞</sub>) on ∂Ω,&nbsp;&nbsp; T(x, 0) = T<sub>0</sub></div>
          <p class="muted note">
            Second-order, linear, <b>parabolic</b>: the principal part k∇² is uniformly elliptic and the equation is first order in time. The source
            q = s(t)·q̂(x) is unknown in space; the load s(t) = (I/I<sub>ref</sub>)² is measured. The control u ∈ [0, 1] enters bilinearly through h(u)T.
          </p>
        </tt-card>
        <tt-card heading="Dimensionless groups" [sub]="scenario()?.name ?? 'Demo scenario'">
          @if (groups(); as g) {
            <table class="data">
              <tr><th>Group</th><th>Definition</th><th>Value</th></tr>
              <tr><td>Diffusion time</td><td>τ = L<sub>y</sub>²/α</td><td class="strong">{{ fmt(g.tau, 0) }} s</td></tr>
              <tr><td>Fourier number of the charge</td><td>Fo = α t<sub>end</sub>/L<sub>y</sub>²</td><td class="strong">{{ fmt(g.fourier, 2) }}</td></tr>
              <tr><td>Edge Biot number</td><td>Bi<sub>e</sub> = h<sub>e</sub>L<sub>y</sub>/k</td><td class="strong">{{ fmt(g.biotEdge, 3) }}</td></tr>
              <tr><td>Cooling number (u = 0 … 1)</td><td>Γ(u) = h(u)L<sub>y</sub>²/(δk)</td><td class="strong">{{ fmt(g.coolingMin, 2) }} … {{ fmt(g.coolingMax, 1) }}</td></tr>
              <tr><td>Grid Fourier number</td><td>r = αΔt(Δx⁻² + Δy⁻²)</td><td class="strong">{{ fmt(g.gridFourier, 2) }}</td></tr>
            </table>
            <p class="muted note">
              Fo ≈ {{ fmt(g.fourier, 1) }}: heat spreads across the cell during one charge, so the defect becomes visible at the sensors, but only in smoothed form.
              Bi<sub>e</sub> ≪ 1: the edges barely cool. The cold plate dominates (Γ ≫ Bi<sub>e</sub>). r &gt; 2 rules out explicit Euler at Δt = 5 s.
            </p>
          }
        </tt-card>
        <tt-card heading="Verification, validation and benchmarking" sub="What the evidence does — and does not — show">
          <ul class="vv">
            <li><b>Verification</b> (are the equations solved correctly?): exact and manufactured solutions, observed orders, FVM ↔ FEM agreement, adjoint ↔ finite differences, conservation.</li>
            <li><b>Synthetic benchmarking</b> (does the algorithm work on realistic data?): an 80×40 ground-truth plant with an exact Gaussian defect and sensor noise, never the twin's own model.</li>
            <li><b>Validation</b> against measured battery data has <b>not</b> been performed. All "true" values come from simulation.</li>
          </ul>
        </tt-card>
      </div>
    </div>
  `,
  styles: `
    .chain { display: flex; align-items: stretch; gap: 6px; overflow-x: auto; padding: 2px; }
    .stage {
      flex: 1 1 0; min-width: 150px; text-decoration: none; display: flex; flex-direction: column; gap: 5px;
      padding: 12px; border-radius: 12px; background: var(--surface-1); border: 1px solid var(--line);
    }
    .stage:hover { border-color: rgba(57, 135, 229, 0.6); }
    .n { font-size: 10px; color: var(--blue); font-weight: 700; letter-spacing: .08em; }
    .t { font-size: 13px; font-weight: 650; color: var(--ink-1); }
    .eq { font-family: 'Cambria Math', 'STIX Two Math', 'Times New Roman', serif; font-size: 13px; color: var(--ink-1); background: var(--surface-2); border-radius: 6px; padding: 5px 7px; }
    .m { font-size: 11px; color: var(--ink-3); line-height: 1.35; }
    .r { font-size: 12px; color: var(--ink-1); font-weight: 600; margin-top: auto; font-variant-numeric: tabular-nums; }
    .arrow { display: grid; place-items: center; color: var(--blue); }
    .formula { font-size: 13.5px; margin-bottom: 6px; overflow-x: auto; white-space: nowrap; }
    .note { font-size: 11.5px; line-height: 1.5; margin: 6px 0 0; }
    .vv { margin: 0; padding-left: 18px; display: flex; flex-direction: column; gap: 8px; font-size: 12px; line-height: 1.45; }
  `,
})
export class ModelPage {
  private readonly api = inject(ApiService);
  private readonly experiments = inject(ExperimentStore);
  readonly fmt = fmt;

  readonly scenario = signal<ScenarioDefinition | null>(null);
  private readonly conv = this.experiments.latest<ConvergenceResult>('NumericalConvergence');
  private readonly fem = this.experiments.latest<FemVerificationResult>('FemVerification');
  private readonly reg = this.experiments.latest<RegularizationResult>('Regularization');
  private readonly ident = this.experiments.latest<IdentifiabilityResult>('ParameterIdentifiability');
  private readonly rom = this.experiments.latest<ReducedOrderResult>('ReducedOrderModel');
  private readonly check = this.experiments.latest<AdjointCheckResult>('AdjointGradientCheck');
  private readonly bench = this.experiments.latest<OptimizationBenchmarkResult>('OptimizationBenchmark');
  private readonly control = this.experiments.latest<ReducedOrderControlResult>('ReducedOrderControl');

  constructor() {
    this.api.scenarios().subscribe((s) => this.scenario.set(s[0] ?? null));
  }

  readonly groups = computed(() => {
    const s = this.scenario();
    if (!s) {
      return null;
    }
    const rhoC = s.material.density * s.material.specificHeat;
    const alpha = s.material.conductivity / rhoC;
    const ly = s.geometry.lengthY;
    const dx = s.geometry.lengthX / s.geometry.nx;
    const dy = s.geometry.lengthY / s.geometry.ny;
    return {
      tau: (ly * ly) / alpha,
      fourier: (alpha * s.solver.duration) / (ly * ly),
      biotEdge: (s.environment.edgeHeatTransferCoefficient * ly) / s.material.conductivity,
      coolingMin: (s.cooling.minHeatTransferCoefficient * ly * ly) / (s.material.thickness * s.material.conductivity),
      coolingMax: (s.cooling.maxHeatTransferCoefficient * ly * ly) / (s.material.thickness * s.material.conductivity),
      gridFourier: alpha * s.solver.timeStep * (1 / (dx * dx) + 1 / (dy * dy)),
    };
  });

  readonly stages = computed<Stage[]>(() => {
    const cn = (this.conv()?.result.spatial ?? []).filter((r) => r.scheme === 'CrankNicolson').at(-1);
    const fem = (this.fem()?.result.manufactured ?? []).filter((r) => r.method === 'FEM').at(-1);
    const gcv = this.reg()?.result.studies.find((s) => s.regularization === 'Gradient')?.choices.find((c) => c.method === 'GCV');
    const ident = this.ident()?.result.report;
    const rom = this.rom()?.result;
    const romRow = rom?.comparison.find((c) => c.family === 'Source-basis family' && c.modes === rom.selectedModes);
    const bench = this.bench()?.result;
    const romMpc = this.control()?.result.runs.find((r) => r.key === 'rom');
    const fullMpc = this.control()?.result.runs.find((r) => r.key === 'adjoint');
    return [
      { step: '01', title: 'PDE model', route: '/methods', equation: 'ρcₚTₜ = ∇·(k∇T) + q − H(u)(T − T_c)', method: 'Parabolic, Robin edges, bilinear control', result: 'well-posed IBVP' },
      { step: '02', title: 'Finite volumes', route: '/validation', equation: '(I − θΔtM)Tⁿ⁺¹ = (I + (1−θ)ΔtM)Tⁿ + Δt f', method: 'five-point stencil, Crank–Nicolson, banded Cholesky', result: `order ${fmt(cn?.observedOrderRmse, 2)}` },
      { step: '03', title: 'Finite elements', route: '/methods', equation: 'ρcₚMṪ + (K + R + HM)T = F', method: 'P1 Galerkin weak form, exact element matrices', result: `L² ${fmt(fem?.orderL2, 2)} · H¹ ${fmt(fem?.orderH1, 2)}` },
      { step: '04', title: 'Inverse problem', route: '/inverse', equation: 'min ‖Aq − y‖² + λ‖Lq‖²', method: '12 sensors → 91 unknowns, GCV-selected λ', result: `T RMSE ${fmt(gcv?.metrics.temperatureRmse, 3)} K · ${fmt(gcv?.metrics.hotspotErrorMm, 1)} mm` },
      { step: '05', title: 'Identifiability', route: '/identifiability', equation: 'F = SᵀS/σ²,  S = ∂y/∂θ', method: 'sensitivities, Cramér–Rao, collinearity', result: `cond ${fmt(ident?.conditionNumber, 1)} · γ ≤ ${fmt(ident?.collinearity[0]?.index, 1)}` },
      { step: '06', title: 'Reduced order', route: '/rom', equation: 'ȧ = Φᵀ(αA − σI)Φa + Φᵀg', method: 'POD by method of snapshots, Galerkin projection', result: `r = ${rom?.selectedModes ?? '—'} · ${fmt(romRow?.rmse, 3)} K` },
      { step: '07', title: 'Adjoint gradient', route: '/optimization', equation: 'Lᵀλⁿ = Rᵀλⁿ⁺¹ − μ∂P/∂xⁿ', method: 'one backward sweep for all K controls', result: `error ${pow10(this.check()?.result.bestRelativeError)}` },
      { step: '08', title: 'Optimal cooling', route: '/optimization', equation: 'min J(u)  s.t. PDE, T ≤ T_safe, u ∈ [0,1]ᴷ', method: 'Moreau–Yosida, projected BFGS, MPC', result: `${times(bench?.adjointSpeedup)} vs FD · ROM-MPC ${times(fullMpc && romMpc ? fullMpc.meanOptimizerMs / romMpc.meanOptimizerMs : null)}` },
    ];
  });

  readonly pct = pct;
}
