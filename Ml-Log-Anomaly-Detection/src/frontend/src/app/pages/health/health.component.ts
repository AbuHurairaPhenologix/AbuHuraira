import { DecimalPipe, JsonPipe } from '@angular/common';
import { Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { ApiService } from '../../core/api.service';
import { errorMessage } from '../../core/auth.interceptor';
import { SystemStatus } from '../../core/models';
import { UtcPipe } from '../../shared/format';

@Component({
  selector: 'app-health',
  imports: [DecimalPipe, JsonPipe, UtcPipe],
  template: `
    <div class="page-head">
      <div><h1>System health</h1><span class="muted">Backend, PostgreSQL, OpenSearch, ML scoring service and background workers. Refreshes every 10 s.</span></div>
      @if (status(); as s) { <span class="badge" [class]="s.status">{{ s.status }}</span> }
    </div>
    @if (error()) { <div class="alert error">{{ error() }}</div> }
    @if (status(); as s) {
      <div class="grid kpis">
        @for (c of s.components; track c.name) {
          <div class="panel kpi">
            <div class="label">{{ c.name }}</div>
            <div class="value" style="font-size:18px"><span class="badge" [class]="c.status">{{ c.status }}</span></div>
            <div class="sub">{{ c.description }}</div>
            @if (c.data) { <div class="sub mono">{{ c.data | json }}</div> }
          </div>
        }
      </div>
      <div class="grid half">
        <div class="panel">
          <h2>Pipeline</h2>
          <dl class="meta">
            <dt>Window size</dt><dd>{{ s.pipeline.windowSizeMinutes }} minutes</dd>
            <dt>Allowed lateness</dt><dd>{{ s.pipeline.allowedLatenessSeconds }} s</dd>
            <dt>Scoring batch</dt><dd>{{ s.pipeline.scoringBatchSize }} windows</dd>
            <dt>Background workers</dt><dd>{{ s.pipeline.backgroundWorkersEnabled ? 'enabled' : 'disabled' }}</dd>
            <dt>Event queue</dt><dd>{{ s.eventQueueDepth }} queued · {{ s.eventQueueDropped }} dropped</dd>
            <dt>Windows</dt><dd>{{ s.stats.scoredWindows | number }} scored · {{ s.stats.pendingWindows | number }} pending · {{ s.stats.deferredWindows | number }} deferred · {{ s.stats.rejectedWindows | number }} rejected</dd>
            <dt>Events</dt><dd>{{ s.stats.events | number }} stored · {{ s.stats.unindexedEvents | number }} awaiting search indexing · {{ s.stats.quarantinedEvents | number }} quarantined</dd>
          </dl>
          @if (s.stats.deferredWindows > 0) {
            <div class="alert info" style="margin-top:10px">Scoring for {{ s.stats.deferredWindows }} window(s) is deferred (ML service unavailable or no active model). It is retried automatically; ordinary application traffic is unaffected.</div>
          }
        </div>
        <div class="panel table-wrap">
          <h2>Background workers</h2>
          <table>
            <thead><tr><th>Worker</th><th>Last run</th><th>Result</th><th class="num">Runs / failures</th></tr></thead>
            <tbody>
              @for (w of s.workers; track w.name) {
                <tr>
                  <td>{{ w.name }}</td>
                  <td class="small">{{ w.lastRunUtc | utc: true }}</td>
                  <td class="small mono">{{ w.lastError ?? w.lastResult }}</td>
                  <td class="num">{{ w.runs }} / <span [class.z-high]="w.failures > 0">{{ w.failures }}</span></td>
                </tr>
              } @empty { <tr><td colspan="4" class="muted">No worker activity yet.</td></tr> }
            </tbody>
          </table>
        </div>
      </div>
      <p class="muted small">Checked {{ s.checkedAtUtc | utc: true }}</p>
    }
  `,
})
export class HealthComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly status = signal<SystemStatus | null>(null);
  readonly error = signal<string | null>(null);

  constructor() {
    const timer = setInterval(() => this.load(), 10000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.api.systemStatus().subscribe({
      next: (s) => {
        this.status.set(s);
        this.error.set(null);
      },
      error: (e) => this.error.set(errorMessage(e)),
    });
  }
}
