import { DecimalPipe, JsonPipe } from '@angular/common';
import { Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { errorMessage } from '../../core/auth.interceptor';
import { ModelVersion, RegistryEntry, TrainingJob } from '../../core/models';
import { UtcPipe } from '../../shared/format';

@Component({
  selector: 'app-models',
  imports: [FormsModule, DecimalPipe, JsonPipe, UtcPipe],
  template: `
    <div class="page-head">
      <div><h1>Models</h1><span class="muted">Registered model versions, validated thresholds and activation state. Changes require the Administrator role and are audited.</span></div>
      <button class="secondary" (click)="load()">Refresh</button>
    </div>
    @if (error()) { <div class="alert error">{{ error() }}</div> }
    @if (message()) { <div class="alert success">{{ message() }}</div> }

    <div class="panel table-wrap" style="margin-bottom:14px">
      <h2>Registered in the backend</h2>
      <table>
        <thead><tr><th>Version</th><th>Algorithm</th><th class="num">Threshold</th><th>Validation (P / R / F1 / FPR)</th><th>Training period</th><th class="num">Scored</th><th class="num">Anomalies</th><th>State</th><th></th></tr></thead>
        <tbody>
          @for (m of models(); track m.modelId) {
            <tr>
              <td class="mono small">{{ m.modelVersion }}<div class="muted">seed {{ m.randomSeed }} · {{ m.featureSchemaVersion }}</div></td>
              <td>{{ m.algorithm }} @if (!m.productionEligible) { <span class="badge warn">reference only</span> }</td>
              <td class="num mono">{{ m.validationThreshold | number: '1.4-4' }}<div class="muted small">{{ m.thresholdObjective }}</div></td>
              <td class="mono small">{{ metric(m, 'precision') }} / {{ metric(m, 'recall') }} / {{ metric(m, 'f1') }} / {{ metric(m, 'fpr') }}</td>
              <td class="small">{{ m.trainingPeriodStartUtc | utc }}<br />{{ m.trainingPeriodEndUtc | utc }}</td>
              <td class="num">{{ m.scoredWindows | number }}</td>
              <td class="num">{{ m.anomalies | number }}</td>
              <td>
                @if (m.isActive) { <span class="badge active">active</span><div class="muted small">by {{ m.activatedBy }}</div> } @else { <span class="badge">inactive</span> }
              </td>
              <td>
                @if (auth.isAdmin()) {
                  @if (m.isActive) {
                    <button class="danger" (click)="deactivate(m)" [disabled]="busy()">Deactivate</button>
                  } @else {
                    <button (click)="activate(m)" [disabled]="busy() || !m.productionEligible">Activate</button>
                  }
                }
              </td>
            </tr>
          } @empty { <tr><td colspan="9" class="muted">No models registered yet.</td></tr> }
        </tbody>
      </table>
    </div>

    @if (auth.isAdmin()) {
      <div class="grid half">
        <div class="panel table-wrap">
          <h2>ML artifact registry</h2>
          <p class="muted small">Versions available in controlled storage. Registering copies verified metadata; models are never loaded from arbitrary paths.</p>
          <table>
            <thead><tr><th>Version</th><th class="num">Threshold</th><th>Artifact</th><th></th></tr></thead>
            <tbody>
              @for (r of registry(); track r.metadata.modelVersion) {
                <tr>
                  <td class="mono small">{{ r.metadata.modelVersion }} @if (r.metadata.recommended) { <span class="badge ok">recommended</span> }</td>
                  <td class="num mono">{{ r.metadata.validationThreshold | number: '1.4-4' }}</td>
                  <td>@if (r.metadata.artifactExists) { <span class="badge ok">present</span> } @else { <span class="badge bad">missing</span> }</td>
                  <td>@if (r.registeredInBackend) { <span class="muted small">registered</span> } @else { <button class="secondary" (click)="register(r)" [disabled]="busy()">Register</button> }</td>
                </tr>
              } @empty { <tr><td colspan="4" class="muted">{{ registryError() ?? 'Registry is empty.' }}</td></tr> }
            </tbody>
          </table>
        </div>
        <div class="panel">
          <h2>Retrain (explicit action)</h2>
          <p class="muted small">Retraining never happens automatically. New versions are registered <strong>inactive</strong> and must be reviewed and activated.</p>
          <div class="row">
            <label class="field">Algorithm
              <select [(ngModel)]="retrain.algorithm">
                <option value="ocsvm">One-Class SVM</option>
                <option value="lof">Local Outlier Factor</option>
                <option value="isolation_forest">Isolation Forest</option>
                <option value="random_forest">Random Forest (reference)</option>
                <option value="all">All four (benchmark)</option>
              </select>
            </label>
            <label class="field">Source
              <select [(ngModel)]="retrain.source">
                <option value="benchmark">Synthetic benchmark (labelled)</option>
                <option value="feature-windows">Stored feature windows</option>
              </select>
            </label>
          </div>
          @if (retrain.source === 'feature-windows') {
            <div class="row" style="margin-top:8px">
              <label class="field">Training start (UTC)<input type="datetime-local" [(ngModel)]="retrain.start" /></label>
              <label class="field">Training end (UTC)<input type="datetime-local" [(ngModel)]="retrain.end" /></label>
            </div>
          }
          <button style="margin-top:10px" (click)="startRetrain()" [disabled]="busy()">Start training job</button>
          @if (job(); as j) {
            <div class="alert info" style="margin-top:10px">Job <span class="mono">{{ j.jobId }}</span>: <strong>{{ j.status }}</strong>
              @if (j.modelVersions.length) { → {{ j.modelVersions | json }} }
              @if (j.error) { <div>{{ j.error }}</div> }
            </div>
          }
        </div>
      </div>
    }
  `,
})
export class ModelsComponent implements OnInit {
  private readonly api = inject(ApiService);
  protected readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);
  readonly models = signal<ModelVersion[]>([]);
  readonly registry = signal<RegistryEntry[]>([]);
  readonly registryError = signal<string | null>(null);
  readonly job = signal<TrainingJob | null>(null);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);
  readonly busy = signal(false);
  retrain = { algorithm: 'ocsvm', source: 'benchmark', start: '', end: '' };
  private poll: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.load();
    this.destroyRef.onDestroy(() => this.poll && clearInterval(this.poll));
  }

  metric(m: ModelVersion, key: string): string {
    const v = m.validationMetrics?.[key];
    return typeof v === 'number' ? v.toFixed(3) : '—';
  }

  load(): void {
    this.api.models().subscribe({ next: (m) => this.models.set(m), error: (e) => this.error.set(errorMessage(e)) });
    if (this.auth.isAdmin()) {
      this.api.registry().subscribe({
        next: (r) => {
          this.registry.set(r);
          this.registryError.set(null);
        },
        error: (e) => this.registryError.set(errorMessage(e)),
      });
    }
  }

  register(r: RegistryEntry): void {
    this.act(this.api.registerModel(r.metadata.modelVersion), `Registered ${r.metadata.modelVersion}.`);
  }

  activate(m: ModelVersion): void {
    if (confirm(`Activate ${m.modelVersion}? The current active model will be deactivated. New windows will be scored with this model; existing anomalies keep their original model.`)) {
      this.act(this.api.activateModel(m.modelId), `Activated ${m.modelVersion}.`);
    }
  }

  deactivate(m: ModelVersion): void {
    if (confirm(`Deactivate ${m.modelVersion}? Scoring will be deferred until another model is activated.`)) {
      this.act(this.api.deactivateModel(m.modelId), `Deactivated ${m.modelVersion}.`);
    }
  }

  startRetrain(): void {
    const body = {
      algorithm: this.retrain.algorithm,
      source: this.retrain.source,
      trainingPeriodStartUtc: this.retrain.start ? `${this.retrain.start}:00Z` : undefined,
      trainingPeriodEndUtc: this.retrain.end ? `${this.retrain.end}:00Z` : undefined,
    };
    this.busy.set(true);
    this.api.retrain(body).subscribe({
      next: (j) => {
        this.job.set(j);
        this.busy.set(false);
        this.poll = setInterval(() => this.refreshJob(j.jobId), 3000);
      },
      error: (e) => {
        this.error.set(errorMessage(e));
        this.busy.set(false);
      },
    });
  }

  private refreshJob(id: string): void {
    this.api.trainingJob(id).subscribe((j) => {
      this.job.set(j);
      if (j.status === 'succeeded' || j.status === 'failed') {
        if (this.poll) {
          clearInterval(this.poll);
        }
        this.load();
      }
    });
  }

  private act(obs: ReturnType<ApiService['activateModel']>, success: string): void {
    this.busy.set(true);
    this.error.set(null);
    obs.subscribe({
      next: () => {
        this.message.set(success);
        this.busy.set(false);
        this.load();
      },
      error: (e) => {
        this.error.set(errorMessage(e));
        this.busy.set(false);
      },
    });
  }
}
