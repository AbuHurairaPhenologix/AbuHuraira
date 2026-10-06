import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { errorMessage } from '../../core/auth.interceptor';
import { EventSearchResult } from '../../core/models';
import { UtcPipe } from '../../shared/format';

/** Investigation search with constrained filters only; the backend builds the OpenSearch query. */
@Component({
  selector: 'app-events',
  imports: [FormsModule, DecimalPipe, UtcPipe],
  template: `
    <div class="page-head">
      <div><h1>Investigate events</h1><span class="muted">Search normalized events by service, environment, time range, correlation ID and event type.</span></div>
    </div>
    <div class="panel" style="margin-bottom:14px">
      <form class="row" (ngSubmit)="search(1)">
        <label class="field">Service<input name="service" [(ngModel)]="f.service" placeholder="demo-shop-api" /></label>
        <label class="field">Environment<input name="environment" [(ngModel)]="f.environment" placeholder="development" /></label>
        <label class="field">From (UTC)<input name="from" type="datetime-local" [(ngModel)]="f.from" /></label>
        <label class="field">To (UTC)<input name="to" type="datetime-local" [(ngModel)]="f.to" /></label>
        <label class="field">Correlation ID<input name="correlationId" [(ngModel)]="f.correlationId" /></label>
        <label class="field">Event type
          <select name="eventType" [(ngModel)]="f.eventType">
            <option value="">Any</option>
            <option value="http_request">http_request</option>
            <option value="authentication">authentication</option>
            <option value="dependency_call">dependency_call</option>
            <option value="background_job">background_job</option>
          </select>
        </label>
        <button type="submit">Search</button>
      </form>
    </div>
    @if (error()) { <div class="alert error">{{ error() }}</div> }
    @if (result(); as r) {
      <div class="panel table-wrap">
        <p class="muted small">{{ r.total | number }} events · source <span class="badge" [class.ok]="r.source === 'opensearch'" [class.warn]="r.source !== 'opensearch'">{{ r.source }}</span></p>
        <table>
          <thead><tr><th>Time (UTC)</th><th>Service</th><th>Type</th><th>Endpoint</th><th class="num">Status</th><th class="num">Duration</th><th>Auth</th><th>Dependency</th><th class="num">Retries</th><th>Correlation ID</th><th>Late</th></tr></thead>
          <tbody>
            @for (e of r.items; track e.id) {
              <tr>
                <td class="small nowrap">{{ e.eventTimestampUtc | utc: true }}</td>
                <td class="small">{{ e.serviceName }} <span class="muted">{{ e.environment }}</span></td>
                <td class="small">{{ e.eventType }}</td>
                <td class="mono small">{{ e.endpointGroup }}</td>
                <td class="num" [class.z-high]="e.errorFlag">{{ e.statusCode ?? '—' }}</td>
                <td class="num mono small">{{ e.durationMs === null ? '—' : (e.durationMs | number: '1.0-1') }}</td>
                <td class="small">{{ e.authenticationResult }}</td>
                <td class="small">{{ e.dependencyName ?? '' }}</td>
                <td class="num">{{ e.retryCount }}</td>
                <td class="mono small"><a style="cursor:pointer" (click)="byCorrelation(e.correlationId)">{{ e.correlationId }}</a></td>
                <td>@if (e.isLate) { <span class="badge warn">late</span> }</td>
              </tr>
            } @empty { <tr><td colspan="11" class="muted">No events match.</td></tr> }
          </tbody>
        </table>
        <div class="pager">
          <span class="muted">page {{ r.page }} of {{ pages() }}</span>
          <button class="secondary" (click)="search(r.page - 1)" [disabled]="r.page <= 1">Previous</button>
          <button class="secondary" (click)="search(r.page + 1)" [disabled]="r.page >= pages()">Next</button>
        </div>
      </div>
    }
  `,
})
export class EventsComponent {
  private readonly api = inject(ApiService);
  readonly result = signal<EventSearchResult | null>(null);
  readonly error = signal<string | null>(null);
  readonly pages = computed(() => {
    const r = this.result();
    return r ? Math.max(1, Math.ceil(r.total / r.pageSize)) : 1;
  });
  f = { service: '', environment: '', from: '', to: '', correlationId: '', eventType: '' };

  constructor() {
    this.search(1);
  }

  byCorrelation(id: string | null): void {
    this.f = { service: '', environment: '', from: '', to: '', correlationId: id ?? '', eventType: '' };
    this.search(1);
  }

  search(page: number): void {
    this.api
      .searchEvents({
        service: this.f.service,
        environment: this.f.environment,
        from: this.f.from ? `${this.f.from}:00Z` : '',
        to: this.f.to ? `${this.f.to}:00Z` : '',
        correlationId: this.f.correlationId,
        eventType: this.f.eventType,
        page,
        pageSize: 50,
      })
      .subscribe({
        next: (r) => {
          this.result.set(r);
          this.error.set(null);
        },
        error: (e) => this.error.set(errorMessage(e)),
      });
  }
}
