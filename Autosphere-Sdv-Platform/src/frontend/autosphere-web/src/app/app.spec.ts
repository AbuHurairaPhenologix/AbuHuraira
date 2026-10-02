import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { errorMessage } from './core/api.service';
import { AuthService } from './core/auth.service';
import { StatusBadge } from './shared/status-badge';

describe('App', () => {
  beforeEach(async () => {
    sessionStorage.clear();
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
  });

  it('creates the root component', () => {
    expect(TestBed.createComponent(App).componentInstance).toBeTruthy();
  });

  it('starts signed out and ignores expired sessions', () => {
    sessionStorage.setItem('autosphere.session', JSON.stringify({
      accessToken: 'x', expiresAt: new Date(Date.now() - 1000).toISOString(), userName: 'admin', roles: ['Administrator'],
    }));
    const auth = TestBed.inject(AuthService);
    expect(auth.isAuthenticated()).toBe(false);
    expect(auth.isAdministrator()).toBe(false);
  });
});

describe('StatusBadge', () => {
  it('pairs the status color with an icon and text', async () => {
    const fixture = TestBed.createComponent(StatusBadge);
    fixture.componentRef.setInput('value', 'Critical');
    await fixture.whenStable();
    const badge = fixture.nativeElement.querySelector('.badge') as HTMLElement;
    expect(badge.classList).toContain('tone-critical');
    expect(badge.textContent).toContain('Critical');
    expect(badge.querySelector('.icon')?.textContent).toBe('✕');
  });

  it('maps OTA states to tones', () => {
    const fixture = TestBed.createComponent(StatusBadge);
    fixture.componentRef.setInput('value', 'RolledBack');
    expect(fixture.componentInstance.tone()).toBe('serious');
  });
});

describe('errorMessage', () => {
  it('prefers validation errors, then ProblemDetails detail', () => {
    const validation = new HttpErrorResponse({ status: 400, error: { title: 'x', errors: { Vin: ['VIN must be 17 characters.'] } } });
    const conflict = new HttpErrorResponse({ status: 409, error: { title: 'Conflict', detail: 'P0A7E is still active.' } });
    const offline = new HttpErrorResponse({ status: 0 });
    expect(errorMessage(validation)).toBe('VIN must be 17 characters.');
    expect(errorMessage(conflict)).toBe('P0A7E is still active.');
    expect(errorMessage(offline)).toBe('The API is not reachable.');
  });
});
