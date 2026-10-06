import { DecimalPipe } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { errorMessage } from '../../core/auth.interceptor';
import { AnomalyListItem, REVIEW_STATES } from '../../core/models';
import { ReviewLabelPipe, UtcPipe } from '../../shared/format';

@Component({
  selector: 'app-anomalies',
  imports: [FormsModule, DecimalPipe, UtcPipe, ReviewLabelPipe],
  template: `
    <div class="page-head">
      <div><h1>Anomalies</h1><span class="muted">Ranked windows whose score met the model's validated threshold.</span></div>
    </div>
    <div class="panel" style="margin-bottom:14px">
      <form class="row" (ngSubmit)="apply()">
        <label class="field">Service<input name="service" [(ngModel)]="filter.service" placeholder="e.g. demo-shop-api" /></label>
        <label class="field">Environment<input name="environment" [(ngModel)]="filter.environment" placeholder="production" /></label>
        <label class="field">Review state
          <select name="reviewState" [(ngModel)]="filter.reviewState">
            <option value="">Any</option>
            @for (s of states; track s) { <option [value]="s">{{ s | reviewLabel }}</option> }
          </select>
        </label>
        <label class="field">From (UTC)<input name="from" type="datetime-local" [(ngModel)]="filter.from" /></label>
        <label class="field">To (UTC)<input name="to" type="datetime-local" [(ngModel)]="filter.to" /></label>
        <label class="field">Sort
          <select name="sort" [(ngModel)]="filter.sort">
            <option value="created">Newest</option>
            <option value="window">Window time</option>
            <option value="score">Score</option>
            <option value="severity">Score − threshold</option>
          </select>
        </label>
        <button type="submit">Apply</button>
        <button type="button" class="secondary" (click)="reset()">Reset</button>
      </form>
    </div>
    @if (error()) { <div class="alert error">{{ error() }}</div> }
    <div class="panel table-wrap">
      <table>
        <thead>
          <tr><th>Window (UTC)</th><th>Service</th><th>Environment</th><th class="num">Score</th><th class="num">Threshold</th><th>Score vs threshold</th><th>Model</th><th>Review state</th></tr>
        </thead>
        <tbody>
          @for (a of items(); track a.anomalyId) {
            <tr class="clickable" (click)="open(a)" [title]="a.reasonSummary">
              <td>{{ a.windowStartUtc | utc }}</td>
              <td>{{ a.service }}</td>
              <td>{{ a.environment }}</td>
              <td class="num mono">{{ a.score | number: '1.4-4' }}</td>
              <td class="num mono">{{ a.threshold | number: '1.4-4' }}</td>
              <td><div class="scorebar"><div class="fill" [style.width.%]="bar(a)"></div><div class="thr" [style.left.%]="thresholdPos(a)"></div></div></td>
              <td class="mono small">{{ a.modelVersion }}</td>
              <td><span class="badge" [class]="a.reviewState">{{ a.reviewState | reviewLabel }}</span></td>
            </tr>
          } @empty {
            <tr><td colspan="8" class="muted">{{ loading() ? 'Loading…' : 'No anomalies match the filters.' }}</td></tr>
          }
        </tbody>
      </table>
      <div class="pager">
        <span class="muted">{{ total() }} anomalies · page {{ page() }} of {{ pages() }}</span>
        <button class="secondary" (click)="go(page() - 1)" [disabled]="page() <= 1">Previous</button>
        <button class="secondary" (click)="go(page() + 1)" [disabled]="page() >= pages()">Next</button>
      </div>
    </div>
  `,
})
export class AnomaliesComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly states = REVIEW_STATES;
  readonly items = signal<AnomalyListItem[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly pages = signal(1);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly pageSize = 25;
  filter = { service: '', environment: '', reviewState: '', from: '', to: '', sort: 'created' };

  ngOnInit(): void {
    this.route.queryParamMap.subscribe((q) => {
      this.filter = {
        service: q.get('service') ?? '',
        environment: q.get('environment') ?? '',
        reviewState: q.get('reviewState') ?? '',
        from: q.get('from') ?? '',
        to: q.get('to') ?? '',
        sort: q.get('sort') ?? 'created',
      };
      this.page.set(Number(q.get('page') ?? 1));
      this.load();
    });
  }

  apply(): void {
    void this.router.navigate([], { queryParams: { ...this.clean(), page: 1 } });
  }

  reset(): void {
    void this.router.navigate([], { queryParams: {} });
  }

  go(page: number): void {
    void this.router.navigate([], { queryParams: { ...this.clean(), page } });
  }

  open(a: AnomalyListItem): void {
    void this.router.navigate(['/anomalies', a.anomalyId]);
  }

  bar(a: AnomalyListItem): number {
    const max = Math.max(Math.abs(a.score), Math.abs(a.threshold)) * 1.25 || 1;
    return Math.min(100, (Math.abs(a.score) / max) * 100);
  }

  thresholdPos(a: AnomalyListItem): number {
    const max = Math.max(Math.abs(a.score), Math.abs(a.threshold)) * 1.25 || 1;
    return Math.min(100, (Math.abs(a.threshold) / max) * 100);
  }

  private clean(): Record<string, string> {
    return Object.fromEntries(Object.entries(this.filter).filter(([, v]) => v !== ''));
  }

  private load(): void {
    this.loading.set(true);
    const f = this.filter;
    this.api
      .anomalies({
        service: f.service,
        environment: f.environment,
        reviewState: f.reviewState,
        from: f.from ? `${f.from}:00Z` : '',
        to: f.to ? `${f.to}:00Z` : '',
        sort: f.sort,
        page: this.page(),
        pageSize: this.pageSize,
      })
      .subscribe({
        next: (r) => {
          this.items.set(r.items);
          this.total.set(r.total);
          this.pages.set(Math.max(1, Math.ceil(r.total / r.pageSize)));
          this.loading.set(false);
          this.error.set(null);
        },
        error: (e) => {
          this.error.set(errorMessage(e));
          this.loading.set(false);
        },
      });
  }
}
