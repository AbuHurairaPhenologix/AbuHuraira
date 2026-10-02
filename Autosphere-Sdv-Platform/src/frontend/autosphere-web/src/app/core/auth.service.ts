import { HttpClient, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, firstValueFrom, throwError } from 'rxjs';
import { AuthResult, Role } from './models';

const STORAGE_KEY = 'autosphere.session';

/**
 * Holds the JWT session. The token lives in sessionStorage (cleared when the tab closes) and is
 * never written to logs. Authorization is enforced by the API; the roles here only shape the UI.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly session = signal<AuthResult | null>(AuthService.restore());

  readonly user = computed(() => this.session()?.userName ?? null);
  readonly roles = computed(() => this.session()?.roles ?? []);
  readonly isAuthenticated = computed(() => this.session() !== null);
  readonly isAdministrator = computed(() => this.roles().includes('Administrator'));
  readonly canOperate = computed(() => this.roles().some((r) => r === 'Administrator' || r === 'Engineer'));

  get token(): string | null {
    return this.session()?.accessToken ?? null;
  }

  async login(userName: string, password: string): Promise<void> {
    const result = await firstValueFrom(this.http.post<AuthResult>('/api/auth/login', { userName, password }));
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(result));
    this.session.set(result);
  }

  logout(): void {
    sessionStorage.removeItem(STORAGE_KEY);
    this.session.set(null);
    void this.router.navigate(['/login']);
  }

  hasRole(...roles: Role[]): boolean {
    return this.roles().some((r) => roles.includes(r));
  }

  private static restore(): AuthResult | null {
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY);
      if (!raw) {
        return null;
      }
      const session = JSON.parse(raw) as AuthResult;
      return new Date(session.expiresAt).getTime() > Date.now() ? session : null;
    } catch {
      return null;
    }
  }
}

/** Adds the bearer token to API calls and signs out on 401. */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const token = auth.token;
  const authorized = token && request.url.startsWith('/api') ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;
  return next(authorized).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401 && !request.url.endsWith('/auth/login')) {
        auth.logout();
      }
      return throwError(() => error);
    }),
  );
};

export const authGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.isAuthenticated() ? true : inject(Router).createUrlTree(['/login']);
};

export const adminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.isAdministrator() ? true : inject(Router).createUrlTree(['/dashboard']);
};
