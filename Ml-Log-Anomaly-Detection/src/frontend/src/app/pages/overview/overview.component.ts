import { DecimalPipe, PercentPipe } from '@angular/common';
import { Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { errorMessage } from '../../core/auth.interceptor';
import { AnomalyListItem, DashboardStats, SystemStatus } from '../../core/models';
import { ReviewLabelPipe, UtcPipe } from '../../shared/format';
import { TrendChartComponent } from '../../shared/trend-chart.component';

@Component({
  selector: 'app-overview',
  imports: [DecimalPipe, PercentPipe, RouterLink, UtcPipe, ReviewLabelPipe, TrendChartComponent],
  template: `
    <div class="page-head">
      <div>
        <h1>Overview</h1>
        <span class="muted">Window-level anomaly detection across monitored services. Scores rank windows for review; they are not root-cause diagnoses.</span>
      </div>
      <div class="row">
        @if (auth.isAdmin()) {
          <button class="secondary" (click)="runPipeline()" [disabled]="running()">Run pipeline now</button>
        }
        <button class="secondary" (click)="load()">Refresh</button>
      </div>
    </div>
    @if (error()) { <div class="alert error">{{ error() }}</div> }
    @if (message()) { <div class="alert success">{{ message() }}</div> }

    @if (stats(); as s) {
      <div class="grid kpis">
        <div class="panel kpi"><div class="label">Processed windows</div><div class="value">{{ s.processedWindows | number }}</div><div class="sub">{{ s.scoredWindows | number }} scored · {{ s.pendingWindows + s.deferredWindows | number }} waiting</div></div>
        <div class="panel kpi"><div class="label">Anomalies</div><div class="value">{{ s.anomalies | number }}</div><div class="sub">across {{ s.services.length }} service/environment pairs</div></div>
        <div class="panel kpi"><div class="label">Anomaly rate</div><div class="value">{{ s.anomalyRate | percent: '1.1-2' }}</div><div class="sub">of scored windows</div></div>
        <div class="panel kpi"><div class="label">Unreviewed</div><div class="value">{{ s.unreviewed | number }}</div><div class="sub"><a routerLink="/anomalies" [queryParams]="{ reviewState: 'Unreviewed' }">open review queue →</a></div></div>
        <div class="panel kpi"><div class="label">Active model</div>
          @if (s.activeModel; as m) {
            <div class="value mono" style="font-size:15px">{{ m.modelVersion }}</div><div class="sub">{{ m.algorithm }} · threshold {{ m.validationThreshold | number: '1.3-4' }}</div>
          } @else {
            <div class="value" style="font-size:16px">None</div><div class="sub">scoring is deferred until a model is activated</div>
          }
        </div>
      </div>

      <div class="grid two">
        <div class="panel">
          <h2>Recent anomaly trend (last 7 days of data)</h2>
          <app-trend-chart [points]="s.trend" />
        </div>
        <div class="panel">
          <h2>Component health</h2>
          @if (status(); as st) {
            <table>
              @for (c of st.components; track c.name) {
                <tr><td>{{ c.name }}</td><td class="num"><span class="badge" [class]="c.status">{{ c.status }}</span></td></tr>
              }
            </table>
            <p class="muted small">Queue {{ st.eventQueueDepth }} · dropped {{ st.eventQueueDropped }} · quarantined {{ s.quarantinedEvents | number }} · late {{ s.lateEvents | number }}</p>
          } @else { <p class="muted">Loading…</p> }
        </div>
      </div>

      <div class="grid two" style="margin-top:14px">
        <div class="panel">
          <h2>Latest anomalies</h2>
          <div class="table-wrap">
            <table>
              <thead><tr><th>Window (UTC)</th><th>Service</th><th class="num">Score / threshold</th><th>Review</th></tr></thead>
              <tbody>
                @for (a of latest(); track a.anomalyId) {
                  <tr class="clickable" (click)="open(a)">
                    <td>{{ a.windowStartUtc | utc }}</td>
                    <td>{{ a.service }} <span class="muted small">{{ a.environment }}</span></td>
                    <td class="num mono">{{ a.score | number: '1.3-3' }} / {{ a.threshold | number: '1.3-3' }}</td>
                    <td><span class="badge" [class]="a.reviewState">{{ a.reviewState | reviewLabel }}</span></td>
                  </tr>
                } @empty {
                  <tr><td colspan="4" class="muted">No anomalies recorded.</td></tr>
                }
              </tbody>
            </table>
          </div>
        </div>
        <div class="panel">
          <h2>By service</h2>
          <table>
            <thead><tr><th>Service</th><th class="num">Windows</th><th class="num">Anomalies</th></tr></thead>
            <tbody>
              @for (svc of s.services; track svc.service + svc.environment) {
                <tr><td>{{ svc.service }} <span class="muted small">{{ svc.environment }}</span></td><td class="num">{{ svc.windows | number }}</td><td class="num">{{ svc.anomalies | number }}</td></tr>
              }
            </tbody>
          </table>
          <h3 style="margin-top:14px">Review outcomes</h3>
          @for (entry of reviewStates(s); track entry[0]) {
            <div class="row small"><span class="badge" [class]="entry[0]">{{ entry[0] | reviewLabel }}</span><span class="spacer"></span>{{ entry[1] }}</div>
          }
        </div>
      </div>
    } @else if (!error()) {
      <p class="muted">Loading…</p>
    }
  `,
})
export class OverviewComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  protected readonly auth = inject(AuthService);
  readonly stats = signal<DashboardStats | null>(null);
  readonly status = signal<SystemStatus | null>(null);
  readonly latest = signal<AnomalyListItem[]>([]);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);
  readonly running = signal(false);

  constructor() {
    const timer = setInterval(() => this.load(), 15000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    forkJoin({
      stats: this.api.stats(7),
      status: this.api.systemStatus(),
      latest: this.api.anomalies({ page: 1, pageSize: 8, sort: 'window' }),
    }).subscribe({
      next: (r) => {
        this.stats.set(r.stats);
        this.status.set(r.status);
        this.latest.set(r.latest.items);
        this.error.set(null);
      },
      error: (e) => this.error.set(errorMessage(e)),
    });
  }

  runPipeline(): void {
    this.running.set(true);
    this.api.runPipeline().subscribe({
      next: (r) => {
        const scored = r.scoring.reduce((n, s) => n + s.windowsScored, 0);
        const anomalies = r.scoring.reduce((n, s) => n + s.anomaliesCreated, 0);
        const last = r.scoring.at(-1);
        this.message.set(`Indexed ${r.eventsIndexed} events, created ${r.aggregation.windowsCreated} windows, scored ${scored} (${anomalies} anomalies). ${last?.message ?? ''}`);
        this.running.set(false);
        this.load();
      },
      error: (e) => {
        this.error.set(errorMessage(e));
        this.running.set(false);
      },
    });
  }

  reviewStates(s: DashboardStats): [string, number][] {
    return Object.entries(s.byReviewState);
  }

  open(a: AnomalyListItem): void {
    void this.router.navigate(['/anomalies', a.anomalyId]);
  }
}
