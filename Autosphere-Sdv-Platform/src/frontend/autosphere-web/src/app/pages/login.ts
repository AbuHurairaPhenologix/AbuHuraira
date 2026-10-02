import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { errorMessage } from '../core/api.service';
import { AuthService } from '../core/auth.service';

@Component({
  selector: 'app-login',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="login">
      <form class="panel" (ngSubmit)="submit()">
        <div class="brand"><span aria-hidden="true">◈</span> AutoSphere</div>
        <p class="secondary">Software-Defined Vehicle platform — telemetry, diagnostics and secure OTA.</p>
        <label>User name<input name="user" [(ngModel)]="userName" autocomplete="username" required /></label>
        <label>Password<input name="password" type="password" [(ngModel)]="password" autocomplete="current-password" required /></label>
        @if (error()) { <div class="notice error" role="alert">{{ error() }}</div> }
        <button class="primary" type="submit" [disabled]="busy() || !userName || !password">{{ busy() ? 'Signing in…' : 'Sign in' }}</button>
        <p class="muted hint">Demo accounts: <code>admin</code>, <code>engineer</code>, <code>viewer</code> — passwords from your <code>.env</code>.</p>
      </form>
    </div>
  `,
  styles: `
    .login { min-height: 100vh; display: grid; place-items: center; padding: 16px; }
    form { width: min(380px, 100%); display: flex; flex-direction: column; gap: 14px; padding: 28px; }
    .brand { font-size: 22px; font-weight: 700; display: flex; gap: 8px; align-items: center; }
    .brand span { color: var(--accent); }
    p { margin: 0; }
    button { justify-content: center; padding: 8px; }
    .hint { font-size: 12px; }
  `,
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected userName = '';
  protected password = '';
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.auth.login(this.userName, this.password);
      await this.router.navigate(['/dashboard']);
    } catch (error) {
      this.error.set(errorMessage(error));
    } finally {
      this.busy.set(false);
    }
  }
}
