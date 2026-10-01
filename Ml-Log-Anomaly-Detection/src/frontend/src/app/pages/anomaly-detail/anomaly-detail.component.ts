import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { errorMessage } from '../../core/auth.interceptor';
import { AnomalyDetail, EventSearchResult, REVIEW_STATES, ReviewState } from '../../core/models';
import { FeatureValuePipe, ReviewLabelPipe, UtcPipe } from '../../shared/format';

@Component({
  selector: 'app-anomaly-detail',
  imports: [FormsModule, DecimalPipe, RouterLink, UtcPipe, FeatureValuePipe, ReviewLabelPipe],
  template: `
    <p><a routerLink="/anomalies">← Anomalies</a></p>
    @if (error()) { <div class="alert error">{{ error() }}</div> }
    @if (detail(); as d) {
      <div class="page-head">
        <div>
          <h1>{{ d.anomaly.service }} · {{ d.anomaly.windowStartUtc | utc }} – {{ d.anomaly.windowEndUtc | utc }}</h1>
          <span class="muted">{{ d.anomaly.environment }} · {{ d.window.windowSizeMinutes }}-minute window · {{ d.window.eventCount }} events
            @if (d.window.lateEventCount > 0) { · {{ d.window.lateEventCount }} late (excluded) }</span>
        </div>
        <span class="badge" [class]="d.anomaly.reviewState">{{ d.anomaly.reviewState | reviewLabel }}</span>
      </div>

      <div class="grid two">
        <div class="stack">
          <div class="panel">
            <h2>Why this window was flagged</h2>
            <p>{{ d.anomaly.reasonSummary }}</p>
            <p class="muted small">Decision support only: the summary lists the features that deviate most from the model's training baseline. It does not identify a root cause.</p>
            <div class="row">
              <div><div class="muted small">Score</div><div class="mono" style="font-size:20px">{{ d.anomaly.score | number: '1.4-4' }}</div></div>
              <div><div class="muted small">Threshold used</div><div class="mono" style="font-size:20px">{{ d.anomaly.threshold | number: '1.4-4' }}</div></div>
              <div><div class="muted small">Model</div><div class="mono">{{ d.anomaly.modelVersion }}</div><div class="small muted">{{ d.modelAlgorithm }} · {{ d.modelIsActive ? 'active' : 'no longer active' }}</div></div>
            </div>
          </div>

          <div class="panel">
            <h2>Feature vector (ops-v1)</h2>
            <table>
              <thead><tr><th>Feature</th><th class="num">Value</th><th class="num">Baseline mean</th><th class="num">z-score</th></tr></thead>
              <tbody>
                @for (f of d.featureDeviations; track f.feature) {
                  <tr>
                    <td>{{ f.label }} <div class="muted small mono">{{ f.feature }}</div></td>
                    <td class="num mono">{{ f.value | feature: f.feature }}</td>
                    <td class="num mono">{{ f.baselineMean === null ? '—' : (f.baselineMean | feature: f.feature) }}</td>
                    <td class="num mono" [class.z-high]="abs(f.zScore) >= 3" [class.z-mid]="abs(f.zScore) >= 2 && abs(f.zScore) < 3">{{ f.zScore === null ? '—' : (f.zScore | number: '1.2-2') }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <div class="panel">
            <div class="row"><h2 style="margin:0">Related events</h2><span class="spacer"></span>
              <select [(ngModel)]="eventType" (ngModelChange)="loadEvents(1)">
                <option value="">All event types</option>
                <option value="http_request">http_request</option>
                <option value="authentication">authentication</option>
                <option value="dependency_call">dependency_call</option>
                <option value="background_job">background_job</option>
              </select>
              <input placeholder="Correlation ID" [(ngModel)]="correlationId" (keyup.enter)="loadEvents(1)" />
              <button class="secondary" (click)="loadEvents(1)">Filter</button>
            </div>
            @if (events(); as ev) {
              <p class="muted small">{{ ev.total }} events · source: <span class="badge" [class.ok]="ev.source === 'opensearch'" [class.warn]="ev.source !== 'opensearch'">{{ ev.source }}</span></p>
              <div class="table-wrap">
                <table>
                  <thead><tr><th>Time (UTC)</th><th>Type</th><th>Endpoint</th><th class="num">Status</th><th class="num">Duration</th><th>Auth</th><th class="num">Retries</th><th>Correlation ID</th></tr></thead>
                  <tbody>
                    @for (e of ev.items; track e.id) {
                      <tr [class.err]="e.errorFlag">
                        <td class="small nowrap">{{ e.eventTimestampUtc | utc: true }}</td>
                        <td class="small">{{ e.eventType }}</td>
                        <td class="mono small">{{ e.endpointGroup }}@if (e.dependencyName) { <span class="muted"> ({{ e.dependencyName }})</span> }</td>
                        <td class="num">@if (e.statusCode) { <span [class.z-high]="e.errorFlag">{{ e.statusCode }}</span> }</td>
                        <td class="num mono small">{{ e.durationMs === null ? '—' : (e.durationMs | number: '1.0-1') + ' ms' }}</td>
                        <td class="small">{{ e.authenticationResult }}</td>
                        <td class="num">{{ e.retryCount }}</td>
                        <td class="mono small"><a (click)="filterCorrelation(e.correlationId)" style="cursor:pointer">{{ e.correlationId }}</a></td>
                      </tr>
                    } @empty { <tr><td colspan="8" class="muted">No events match.</td></tr> }
                  </tbody>
                </table>
              </div>
              <div class="pager">
                <span class="muted">page {{ ev.page }} of {{ eventPages() }}</span>
                <button class="secondary" (click)="loadEvents(ev.page - 1)" [disabled]="ev.page <= 1">Previous</button>
                <button class="secondary" (click)="loadEvents(ev.page + 1)" [disabled]="ev.page >= eventPages()">Next</button>
              </div>
            } @else { <p class="muted">Loading events…</p> }
          </div>
        </div>

        <div class="stack">
          <div class="panel">
            <h2>Record review</h2>
            @if (reviewMessage()) { <div class="alert success">{{ reviewMessage() }}</div> }
            <label class="field">Outcome
              <select [(ngModel)]="outcome">
                @for (s of states; track s) { <option [value]="s">{{ s | reviewLabel }}</option> }
              </select>
            </label>
            <label class="field" style="margin-top:8px">Note (optional)
              <textarea [(ngModel)]="note" maxlength="4000" placeholder="What did you find? e.g. deployment at 14:30, dependency timeout, expected campaign traffic…"></textarea>
            </label>
            <button style="margin-top:8px" (click)="submitReview()" [disabled]="saving()">Save review</button>
          </div>

          <div class="panel">
            <h2>Review history</h2>
            @for (r of d.reviews; track r.id) {
              <div class="history">
                <div class="row small"><strong>{{ r.reviewer }}</strong><span class="muted">{{ r.createdAtUtc | utc }}</span></div>
                <div class="small"><span class="badge" [class]="r.previousState">{{ r.previousState | reviewLabel }}</span> → <span class="badge" [class]="r.outcome">{{ r.outcome | reviewLabel }}</span></div>
                @if (r.note) { <p class="small">{{ r.note }}</p> }
              </div>
            } @empty { <p class="muted small">Not reviewed yet.</p> }
          </div>

          <div class="panel">
            <h2>Correlation IDs</h2>
            <p class="muted small">Error-related first. Click to filter related events.</p>
            @for (c of d.correlationIds; track c) {
              <div class="break"><a class="mono small" style="cursor:pointer" (click)="filterCorrelation(c)">{{ c }}</a></div>
            } @empty { <p class="muted small">None recorded.</p> }
          </div>

          <div class="panel">
            <h2>Window</h2>
            <dl class="meta small">
              <dt>Window ID</dt><dd class="mono">{{ d.window.windowId }}</dd>
              <dt>Schema</dt><dd>{{ d.window.featureSchemaVersion }}</dd>
              <dt>Scored</dt><dd>{{ d.window.scoringStatus }} ({{ d.window.scoringAttempts }} attempt(s))</dd>
              <dt>Event types</dt><dd>@for (t of typeCounts(); track t[0]) { {{ t[0] }}: {{ t[1] }}<br /> }</dd>
              @if (d.otherScores.length) {
                <dt>Other scores</dt><dd>@for (o of d.otherScores; track o.id) { <span class="mono">{{ o.modelVersion }}</span>: {{ o.score | number: '1.3-3' }}<br /> }</dd>
              }
            </dl>
          </div>
        </div>
      </div>
    } @else if (!error()) {
      <p class="muted">Loading…</p>
    }
  `,
  styles: `
    .history { border-bottom: 1px solid var(--border); padding: 8px 0; }
    .history p { margin: 6px 0 0; white-space: pre-wrap; }
    tr.err td { background: #fff7f7; }
  `,
})
export class AnomalyDetailComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly id = input.required<string>();
  readonly states = REVIEW_STATES.filter((s) => s !== 'Unreviewed');
  readonly detail = signal<AnomalyDetail | null>(null);
  readonly events = signal<EventSearchResult | null>(null);
  readonly error = signal<string | null>(null);
  readonly saving = signal(false);
  readonly reviewMessage = signal<string | null>(null);
  readonly eventPages = computed(() => {
    const e = this.events();
    return e ? Math.max(1, Math.ceil(e.total / e.pageSize)) : 1;
  });
  readonly typeCounts = computed(() => Object.entries(this.detail()?.eventTypeCounts ?? {}));
  outcome: ReviewState = 'ConfirmedIssue';
  note = '';
  eventType = '';
  correlationId = '';

  ngOnInit(): void {
    this.load();
    this.loadEvents(1);
  }

  abs(v: number | null): number {
    return v === null ? 0 : Math.abs(v);
  }

  load(): void {
    this.api.anomaly(this.id()).subscribe({
      next: (d) => this.detail.set(d),
      error: (e) => this.error.set(errorMessage(e)),
    });
  }

  loadEvents(page: number): void {
    this.api.anomalyEvents(this.id(), { eventType: this.eventType, correlationId: this.correlationId, page, pageSize: 25 }).subscribe({
      next: (r) => this.events.set(r),
      error: (e) => this.error.set(errorMessage(e)),
    });
  }

  filterCorrelation(id: string | null): void {
    this.correlationId = id ?? '';
    this.loadEvents(1);
  }

  submitReview(): void {
    this.saving.set(true);
    this.api.submitReview(this.id(), this.outcome, this.note.trim() || null).subscribe({
      next: (r) => {
        this.reviewMessage.set(`Saved: ${r.outcome} by ${r.reviewer}.`);
        this.note = '';
        this.saving.set(false);
        this.load();
      },
      error: (e) => {
        this.error.set(errorMessage(e));
        this.saving.set(false);
      },
    });
  }
}
