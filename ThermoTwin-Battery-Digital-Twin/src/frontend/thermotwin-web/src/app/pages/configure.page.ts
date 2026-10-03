import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { Subject, debounceTime, switchMap, catchError, of } from 'rxjs';
import { ApiService } from '../core/api.service';
import { fmt } from '../core/format';
import { ScenarioDefinition, StabilityAnalysis } from '../core/models';
import { TwinStore } from '../core/twin-store.service';
import { CardComponent } from '../shared/ui';

interface FieldSpec {
  label: string;
  path: string;
  unit?: string;
  step?: number;
  options?: string[];
  scale?: number;
}

const SECTIONS: { title: string; note: string; fields: FieldSpec[] }[] = [
  {
    title: 'Geometry & grid',
    note: 'Model grid of the twin; the synthetic plant runs on a refined grid.',
    fields: [
      { label: 'Length Lx', path: 'geometry.lengthX', unit: 'mm', scale: 1000, step: 5 },
      { label: 'Width Ly', path: 'geometry.lengthY', unit: 'mm', scale: 1000, step: 5 },
      { label: 'Cells Nx', path: 'geometry.nx', step: 2 },
      { label: 'Cells Ny', path: 'geometry.ny', step: 2 },
      { label: 'Plant refinement', path: 'geometry.plantRefinement', unit: '×', step: 1 },
    ],
  },
  {
    title: 'Material',
    note: 'Effective in-plane properties of the electrode stack.',
    fields: [
      { label: 'Density ρ', path: 'material.density', unit: 'kg/m³', step: 50 },
      { label: 'Specific heat cₚ', path: 'material.specificHeat', unit: 'J/(kg·K)', step: 50 },
      { label: 'Conductivity k', path: 'material.conductivity', unit: 'W/(m·K)', step: 1 },
      { label: 'Thickness δ', path: 'material.thickness', unit: 'mm', scale: 1000, step: 1 },
    ],
  },
  {
    title: 'Environment & boundaries',
    note: 'Initial state and edge condition (Robin = convective).',
    fields: [
      { label: 'Ambient T∞', path: 'environment.ambientTemperature', unit: '°C', step: 1 },
      { label: 'Initial T₀', path: 'environment.initialTemperature', unit: '°C', step: 1 },
      { label: 'Edge condition', path: 'environment.edgeCondition', options: ['Robin', 'Neumann', 'Dirichlet'] },
      { label: 'Edge h', path: 'environment.edgeHeatTransferCoefficient', unit: 'W/(m²·K)', step: 1 },
    ],
  },
  {
    title: 'Cooling system',
    note: 'Cold plate: h(u) = h_min + u(h_max − h_min), P(u) = P_rated·u³.',
    fields: [
      { label: 'Coolant T_c', path: 'cooling.coolantTemperature', unit: '°C', step: 1 },
      { label: 'h_min', path: 'cooling.minHeatTransferCoefficient', unit: 'W/(m²·K)', step: 1 },
      { label: 'h_max', path: 'cooling.maxHeatTransferCoefficient', unit: 'W/(m²·K)', step: 5 },
      { label: 'Rated power', path: 'cooling.ratedPowerWatts', unit: 'W', step: 10 },
      { label: 'Baseline level', path: 'cooling.baselineLevel', unit: '%', scale: 100, step: 5 },
      { label: 'Mode', path: 'cooling.mode', options: ['Autonomous', 'Advisory', 'Fixed'] },
    ],
  },
  {
    title: 'Heat generation',
    note: 'Bulk I²R heating plus hidden defects; both scale with the charging load s(t).',
    fields: [
      { label: 'Joule heating', path: 'heatSource.uniformJouleHeating', unit: 'kW/m³', scale: 0.001, step: 5 },
      { label: 'Load profile', path: 'heatSource.loadProfile', options: ['FastChargeCcCv', 'Constant'] },
      { label: 'CC phase end', path: 'heatSource.constantCurrentEnd', unit: 's', step: 60 },
      { label: 'Taper factor', path: 'heatSource.taperFactor', step: 0.05 },
    ],
  },
  {
    title: 'Sensors',
    note: 'Thermistor lattice and Gaussian noise.',
    fields: [
      { label: 'Sensor count', path: 'sensors.count', step: 1 },
      { label: 'Noise σ', path: 'sensors.noiseStd', unit: 'K', step: 0.05 },
      { label: 'Random seed', path: 'sensors.seed', step: 1 },
    ],
  },
  {
    title: 'Time integration',
    note: 'θ-method: explicit (θ=0), implicit (θ=1), Crank–Nicolson (θ=½).',
    fields: [
      { label: 'Scheme', path: 'solver.scheme', options: ['CrankNicolson', 'ImplicitEuler', 'ExplicitEuler'] },
      { label: 'Time step Δt', path: 'solver.timeStep', unit: 's', step: 0.5 },
      { label: 'Duration', path: 'solver.duration', unit: 's', step: 60 },
    ],
  },
  {
    title: 'Inverse estimator',
    note: 'Hat-function source basis and Tikhonov regularisation.',
    fields: [
      { label: 'Basis nodes x', path: 'estimator.basisNodesX', step: 1 },
      { label: 'Basis nodes y', path: 'estimator.basisNodesY', step: 1 },
      { label: 'Regulariser L', path: 'estimator.regularization', options: ['Gradient', 'Identity', 'Laplacian'] },
      { label: 'λ selection', path: 'estimator.lambdaSelection', options: ['Gcv', 'LCurve', 'Discrepancy', 'Fixed'] },
      { label: 'Fixed λ', path: 'estimator.fixedLambda', step: 0.0001 },
      { label: 'Estimation interval', path: 'estimator.estimationInterval', unit: 's', step: 5 },
    ],
  },
  {
    title: 'Prediction & control',
    note: 'Receding-horizon optimisation with a safety back-off margin.',
    fields: [
      { label: 'T_safe', path: 'control.safeTemperature', unit: '°C', step: 1 },
      { label: 'T_critical', path: 'control.criticalTemperature', unit: '°C', step: 1 },
      { label: 'Control margin', path: 'control.controlMargin', unit: 'K', step: 0.5 },
      { label: 'Horizon segments', path: 'control.horizonSegments', step: 1 },
      { label: 'Segment length', path: 'control.segmentDuration', unit: 's', step: 10 },
      { label: 'Control interval', path: 'control.controlInterval', unit: 's', step: 5 },
    ],
  },
  {
    title: 'Playback',
    note: 'Streaming speed of the live twin.',
    fields: [
      { label: 'Steps per frame', path: 'playback.stepsPerFrame', step: 1 },
      { label: 'Frame interval', path: 'playback.frameIntervalMs', unit: 'ms', step: 10 },
    ],
  },
];

@Component({
  selector: 'tt-configure',
  imports: [CardComponent, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head">
        <div>
          <h1>Simulation Configuration</h1>
          <p>Choose a built-in scenario or tune every physical and numerical parameter. The configuration is validated server-side (FluentValidation), including the explicit-scheme stability limit, before the twin starts.</p>
        </div>
        <div class="actions">
          <button class="btn primary" [disabled]="starting()" (click)="start()">▶ Start digital twin</button>
        </div>
      </div>

      <div class="presets">
        @for (s of scenarios(); track s.key) {
          <button class="preset" [class.active]="model()?.key === s.key" (click)="select(s)">
            <strong>{{ s.name }}</strong>
            <span>{{ s.description }}</span>
          </button>
        }
      </div>

      @if (errors().length) {
        <div class="errors">
          <strong>Validation failed</strong>
          @for (e of errors(); track e) {
            <div>• {{ e }}</div>
          }
        </div>
      }

      @if (model(); as m) {
        <div class="layout">
          <div class="sections">
            @for (section of sections; track section.title) {
              <tt-card [heading]="section.title" [sub]="section.note">
                <div class="fields">
                  @for (fld of section.fields; track fld.path) {
                    <label>
                      <span>{{ fld.label }} @if (fld.unit) {<em>[{{ fld.unit }}]</em>}</span>
                      @if (fld.options) {
                        <select [ngModel]="get(fld)" (ngModelChange)="set(fld, $event)">
                          @for (o of fld.options; track o) {
                            <option [value]="o">{{ o }}</option>
                          }
                        </select>
                      } @else {
                        <input type="number" [step]="fld.step ?? 1" [ngModel]="get(fld)" (ngModelChange)="set(fld, $event)" />
                      }
                    </label>
                  }
                </div>
              </tt-card>
            }
            <tt-card heading="Hidden hotspots" sub="Ground-truth defects (unknown to the estimator). Position in mm, peak in kW/m³, radius in mm.">
              <table class="data">
                <tr><th>#</th><th>x [mm]</th><th>y [mm]</th><th>Peak [kW/m³]</th><th>Radius [mm]</th><th></th></tr>
                @for (h of m.heatSource.hotspots; track $index; let i = $index) {
                  <tr>
                    <td>{{ i + 1 }}</td>
                    <td><input type="number" [ngModel]="h.x * 1000" (ngModelChange)="h.x = $event / 1000; changed()" /></td>
                    <td><input type="number" [ngModel]="h.y * 1000" (ngModelChange)="h.y = $event / 1000; changed()" /></td>
                    <td><input type="number" [ngModel]="h.peakPower / 1000" (ngModelChange)="h.peakPower = $event * 1000; changed()" /></td>
                    <td><input type="number" [ngModel]="h.radius * 1000" (ngModelChange)="h.radius = $event / 1000; changed()" /></td>
                    <td><button class="btn" (click)="removeHotspot(i)">✕</button></td>
                  </tr>
                }
              </table>
              <div><button class="btn" (click)="addHotspot()">+ Add hotspot</button></div>
            </tt-card>
          </div>

          <div class="side">
            <tt-card heading="Stability analysis" sub="Computed by the API for this configuration (u = 1)">
              @if (stability(); as st) {
                <div class="kv">
                  <span>Diffusivity α</span><b>{{ st.diffusivity.toExponential(2) }} m²/s</b>
                  <span>Diffusion time Ly²/α</span><b>{{ fmt(st.diffusionTimeScale, 0) }} s</b>
                  <span>Time step Δt</span><b>{{ st.timeStep }} s</b>
                </div>
                @for (g of st.grids; track g.grid) {
                  <div class="gridname">{{ g.grid }} · {{ g.nx }}×{{ g.ny }} · Δx = {{ (g.dx * 1000).toFixed(2) }} mm</div>
                  <table class="data">
                    <tr><th>Scheme</th><th>r</th><th>Δt_crit</th><th>|G|</th><th></th></tr>
                    @for (r of g.schemes; track r.scheme) {
                      <tr>
                        <td>{{ r.scheme }}</td><td>{{ fmt(r.fourierNumber, 2) }}</td><td>{{ fmt(r.explicitCriticalTimeStep, 3) }} s</td><td>{{ fmt(r.amplificationFactor, 3) }}</td>
                        <td><span class="pill" [class.ok]="r.isStable" [class.bad]="!r.isStable">{{ r.isStable ? '✓ stable' : '✖ unstable' }}</span></td>
                      </tr>
                    }
                  </table>
                }
                <div class="valid" [class.bad]="!st.valid">{{ st.valid ? '✓ Configuration valid' : '✖ ' + st.issues.length + ' validation issue(s)' }}</div>
                @for (i of st.issues; track i) {
                  <div class="issue">{{ i }}</div>
                }
              } @else {
                <p class="muted">Analysing…</p>
              }
            </tt-card>
          </div>
        </div>
      }
    </div>
  `,
  styles: `
    .actions { display: flex; gap: 10px; }
    .presets { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; }
    @media (max-width: 1100px) { .presets { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
    .preset { text-align: left; font: inherit; cursor: pointer; background: var(--surface-1); border: 1px solid var(--line); border-radius: 12px; padding: 12px 14px; color: var(--ink-2); display: flex; flex-direction: column; gap: 6px; }
    .preset strong { color: var(--ink-1); font-size: 13px; }
    .preset span { font-size: 11.5px; color: var(--ink-3); line-height: 1.45; }
    .preset.active { border-color: var(--blue); background: rgba(57,135,229,.08); }
    .layout { display: grid; grid-template-columns: minmax(0, 1fr) 420px; gap: 14px; align-items: start; }
    @media (max-width: 1200px) { .layout { grid-template-columns: minmax(0, 1fr); } }
    .sections { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; }
    @media (max-width: 900px) { .sections { grid-template-columns: minmax(0, 1fr); } }
    .side { position: sticky; top: 76px; }
    .fields { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px 12px; }
    label { display: flex; flex-direction: column; gap: 4px; font-size: 11.5px; color: var(--ink-3); }
    label em { font-style: normal; color: var(--ink-3); opacity: .8; }
    input, select { font: inherit; font-size: 12.5px; background: var(--surface-2); border: 1px solid var(--line-strong); color: var(--ink-1); border-radius: 7px; padding: 6px 8px; width: 100%; min-width: 0; }
    input:focus, select:focus { outline: none; border-color: var(--blue); }
    td input { width: 80px; }
    .kv { display: grid; grid-template-columns: 1fr auto; gap: 4px 12px; font-size: 12px; margin-bottom: 8px; }
    .kv b { color: var(--ink-1); font-weight: 600; font-variant-numeric: tabular-nums; }
    .gridname { margin: 10px 0 2px; font-size: 11.5px; color: var(--ink-2); font-weight: 600; }
    .valid { margin-top: 12px; color: var(--good); font-weight: 600; }
    .valid.bad { color: #ff6b6b; }
    .issue { font-size: 11.5px; color: #ff9a9a; margin-top: 4px; }
    .errors { border: 1px solid rgba(208,59,59,.5); background: rgba(208,59,59,.1); border-radius: 10px; padding: 10px 14px; color: #ffb3b3; font-size: 12px; }
  `,
})
export class ConfigurePage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly twin = inject(TwinStore);
  private readonly changes = new Subject<ScenarioDefinition>();

  readonly sections = SECTIONS;
  readonly scenarios = signal<ScenarioDefinition[]>([]);
  readonly model = signal<ScenarioDefinition | null>(null);
  readonly stability = signal<StabilityAnalysis | null>(null);
  readonly errors = signal<string[]>([]);
  readonly starting = signal(false);
  readonly fmt = fmt;

  constructor() {
    this.changes
      .pipe(
        debounceTime(250),
        switchMap((m) => this.api.stability(m).pipe(catchError(() => of(null)))),
      )
      .subscribe((s) => this.stability.set(s));
  }

  ngOnInit(): void {
    this.api.scenarios().subscribe((list) => {
      this.scenarios.set(list);
      if (list.length) {
        this.select(list[0]);
      }
    });
  }

  select(s: ScenarioDefinition): void {
    this.model.set(structuredClone(s));
    this.errors.set([]);
    this.changed();
  }

  get(f: FieldSpec): unknown {
    const value = f.path.split('.').reduce<unknown>((o, k) => (o as Record<string, unknown>)?.[k], this.model());
    return typeof value === 'number' && f.scale ? +(value * f.scale).toPrecision(10) : value;
  }

  set(f: FieldSpec, value: unknown): void {
    const m = this.model();
    if (!m) {
      return;
    }
    const keys = f.path.split('.');
    const target = keys.slice(0, -1).reduce<Record<string, unknown>>((o, k) => o[k] as Record<string, unknown>, m as unknown as Record<string, unknown>);
    target[keys.at(-1)!] = typeof value === 'number' && f.scale ? value / f.scale : value;
    this.changed();
  }

  changed(): void {
    const m = this.model();
    if (m) {
      this.model.set({ ...m });
      this.changes.next(m);
    }
  }

  addHotspot(): void {
    this.model()?.heatSource.hotspots.push({ x: 0.1, y: 0.05, peakPower: 300000, radius: 0.012 });
    this.changed();
  }

  removeHotspot(i: number): void {
    this.model()?.heatSource.hotspots.splice(i, 1);
    this.changed();
  }

  start(): void {
    const m = this.model();
    if (!m) {
      return;
    }
    this.starting.set(true);
    this.errors.set([]);
    this.api.startSimulation(m).subscribe({
      next: () => {
        this.starting.set(false);
        void this.twin.reload();
        void this.router.navigate(['/twin']);
      },
      error: (e: HttpErrorResponse) => {
        this.starting.set(false);
        const errs = e.error?.errors as Record<string, string[]> | undefined;
        this.errors.set(errs ? Object.values(errs).flat() : [e.error?.detail ?? e.message]);
      },
    });
  }
}
