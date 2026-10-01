import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { errorMessage } from '../../core/auth.interceptor';

@Component({
  selector: 'app-login',
  imports: [FormsModule],
  template: `
    <div class="login-wrap">
      <form class="panel login" (ngSubmit)="submit()">
        <div class="brand-row"><span class="logo">◆</span><strong>Anomaly Review</strong></div>
        <p class="muted small">ML-based anomaly detection for distributed web application logs. Development/demo sign-in.</p>
        @if (error()) {
          <div class="alert error">{{ error() }}</div>
        }
        <label class="field">Username
          <select name="username" [(ngModel)]="username">
            <option value="engineer">engineer (Engineer)</option>
            <option value="admin">admin (Administrator)</option>
          </select>
        </label>
        <label class="field">Password
          <input name="password" type="password" [(ngModel)]="password" autocomplete="current-password" required />
        </label>
        <button type="submit" [disabled]="busy() || !password">Sign in</button>
        <p class="muted small">Passwords are configured through <code>DEMO_ENGINEER_PASSWORD</code> / <code>DEMO_ADMIN_PASSWORD</code> in <code>.env</code>.</p>
      </form>
    </div>
  `,
  styles: `
    .login-wrap { min-height: 100vh; display: grid; place-items: center; background: var(--sidebar); }
    .login { width: 360px; display: flex; flex-direction: column; gap: 12px; }
    .brand-row { display: flex; gap: 8px; align-items: center; font-size: 18px; }
    .logo { color: var(--accent); }
  `,
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  username = 'engineer';
  password = '';
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  submit(): void {
    this.busy.set(true);
    this.error.set(null);
    this.auth.login(this.username, this.password).subscribe({
      next: () => void this.router.navigate(['/overview']),
      error: (e) => {
        this.error.set(errorMessage(e));
        this.busy.set(false);
      },
    });
  }
}
