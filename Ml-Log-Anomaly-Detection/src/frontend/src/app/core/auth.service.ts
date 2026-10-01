import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, tap } from 'rxjs';
import { TokenResponse } from './models';

interface Session {
  token: string;
  username: string;
  roles: string[];
  expiresAt: number;
}

const STORAGE_KEY = 'anomaly-dashboard.session';

/**
 * Development/demo sign-in against POST /api/v1/auth/token. The token is kept in sessionStorage (cleared when the
 * browser tab closes). Production deployments would replace this with an OIDC redirect flow.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly session = signal<Session | null>(this.restore());

  readonly isAuthenticated = computed(() => {
    const s = this.session();
    return !!s && s.expiresAt > Date.now();
  });
  readonly username = computed(() => this.session()?.username ?? '');
  readonly roles = computed(() => this.session()?.roles ?? []);
  readonly isAdmin = computed(() => this.roles().includes('Administrator'));

  token(): string | null {
    return this.isAuthenticated() ? this.session()!.token : null;
  }

  login(username: string, password: string): Observable<TokenResponse> {
    return this.http.post<TokenResponse>('/api/v1/auth/token', { username, password }).pipe(
      tap((r) => {
        const session: Session = { token: r.accessToken, username: r.username, roles: r.roles, expiresAt: Date.now() + r.expiresIn * 1000 };
        this.session.set(session);
        sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session));
      }),
    );
  }

  logout(): void {
    this.session.set(null);
    sessionStorage.removeItem(STORAGE_KEY);
    void this.router.navigate(['/login']);
  }

  private restore(): Session | null {
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY);
      const s = raw ? (JSON.parse(raw) as Session) : null;
      return s && s.expiresAt > Date.now() ? s : null;
    } catch {
      return null;
    }
  }
}
