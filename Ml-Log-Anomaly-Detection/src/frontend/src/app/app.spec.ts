import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ApiService } from './core/api.service';
import { AuthService } from './core/auth.service';
import { authInterceptor, errorMessage } from './core/auth.interceptor';
import { FeatureValuePipe, ReviewLabelPipe, UtcPipe } from './shared/format';
import { TrendChartComponent } from './shared/trend-chart.component';
import { HttpErrorResponse } from '@angular/common/http';

describe('formatting pipes', () => {
  it('formats timestamps in UTC', () => {
    expect(new UtcPipe().transform('2022-02-15T14:30:00Z')).toBe('2022-02-15 14:30 UTC');
    expect(new UtcPipe().transform(null)).toBe('—');
  });

  it('formats features by type', () => {
    const p = new FeatureValuePipe();
    expect(p.transform(0.031, 'error_rate')).toBe('3.10 %');
    expect(p.transform(681.7, 'p95_duration_ms')).toBe('681.7 ms');
    expect(p.transform(2.31, 'endpoint_entropy')).toBe('2.310 bits');
    expect(p.transform(142, 'request_count')).toBe('142');
  });

  it('labels review states', () => {
    expect(new ReviewLabelPipe().transform('InsufficientEvidence')).toBe('Insufficient Evidence');
  });
});

describe('API client and auth interceptor', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('signs in and attaches the bearer token to API calls', () => {
    const auth = TestBed.inject(AuthService);
    auth.login('engineer', 'pw').subscribe();
    const login = http.expectOne('/api/v1/auth/token');
    expect(login.request.body).toEqual({ username: 'engineer', password: 'pw' });
    login.flush({ accessToken: 'tok-123', tokenType: 'Bearer', expiresIn: 3600, username: 'engineer', roles: ['Engineer'] });
    expect(auth.isAuthenticated()).toBe(true);
    expect(auth.isAdmin()).toBe(false);

    TestBed.inject(ApiService).anomalies({ reviewState: 'Unreviewed', service: '', page: 1 }).subscribe();
    const req = http.expectOne((r) => r.url === '/api/v1/anomalies');
    expect(req.request.headers.get('Authorization')).toBe('Bearer tok-123');
    expect(req.request.params.get('reviewState')).toBe('Unreviewed');
    expect(req.request.params.has('service')).toBe(false);
    req.flush({ items: [], page: 1, pageSize: 25, total: 0 });
  });

  it('does not send tokens to non-API URLs', () => {
    TestBed.inject(HttpClient).get('/demo/products').subscribe();
    const req = http.expectOne('/demo/products');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush([]);
  });

  it('posts reviews with outcome and note', () => {
    TestBed.inject(ApiService).submitReview('a1', 'FalsePositive', 'deploy warm-up').subscribe();
    const req = http.expectOne('/api/v1/anomalies/a1/reviews');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ outcome: 'FalsePositive', note: 'deploy warm-up' });
    req.flush({});
  });

  it('extracts ProblemDetails titles', () => {
    expect(errorMessage(new HttpErrorResponse({ status: 422, error: { title: 'Activation rejected' } }))).toBe('Activation rejected');
    expect(errorMessage(new HttpErrorResponse({ status: 0 }))).toBe('The backend is unreachable.');
  });
});

describe('TrendChartComponent', () => {
  it('scales bars to the maximum anomaly count', () => {
    const fixture = TestBed.createComponent(TrendChartComponent);
    fixture.componentRef.setInput('points', [
      { day: '2022-02-14', windows: 100, anomalies: 2 },
      { day: '2022-02-15', windows: 120, anomalies: 4 },
    ]);
    const bars = fixture.componentInstance.bars();
    expect(bars).toHaveLength(2);
    expect(bars[1].h).toBeGreaterThan(bars[0].h);
    expect(bars[1].h).toBeCloseTo(2 * bars[0].h, 5);
  });
});
