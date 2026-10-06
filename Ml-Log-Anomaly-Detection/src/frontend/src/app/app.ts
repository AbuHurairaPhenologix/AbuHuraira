import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from './core/auth.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    @if (auth.isAuthenticated()) {
      <div class="shell">
        <aside class="sidebar">
          <div class="brand">
            <span class="logo">◆</span>
            <div>
              <strong>Anomaly Review</strong>
              <small>ops-v1 · decision support</small>
            </div>
          </div>
          <nav>
            <a routerLink="/overview" routerLinkActive="active">Overview</a>
            <a routerLink="/anomalies" routerLinkActive="active">Anomalies</a>
            <a routerLink="/events" routerLinkActive="active">Investigate events</a>
            <a routerLink="/models" routerLinkActive="active">Models</a>
            <a routerLink="/health" routerLinkActive="active">System health</a>
          </nav>
          <div class="who">
            <div>{{ auth.username() }}</div>
            <small>{{ auth.roles().join(' · ') }}</small>
            <button class="link" (click)="auth.logout()">Sign out</button>
          </div>
        </aside>
        <main class="content"><router-outlet /></main>
      </div>
    } @else {
      <router-outlet />
    }
  `,
})
export class App {
  protected readonly auth = inject(AuthService);
}
