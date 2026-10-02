import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { VehicleStore } from '../core/vehicle-store';
import { StatusBadge } from '../shared/status-badge';

interface NavItem { path: string; label: string; icon: string; admin?: boolean; simulation?: boolean }

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, StatusBadge, DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell implements OnInit, OnDestroy {
  protected readonly store = inject(VehicleStore);
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  protected readonly faultInjection = signal(false);
  protected readonly menuOpen = signal(false);
  protected readonly theme = signal<'light' | 'dark' | 'system'>((localStorage.getItem('autosphere.theme') as 'light' | 'dark' | null) ?? 'system');
  protected readonly startError = signal<string | null>(null);

  private readonly items: NavItem[] = [
    { path: '/dashboard', label: 'Dashboard', icon: '◎' },
    { path: '/ecus', label: 'ECUs', icon: '▦' },
    { path: '/telemetry', label: 'Telemetry', icon: '∿' },
    { path: '/diagnostics', label: 'Diagnostics', icon: '⚕' },
    { path: '/ota', label: 'OTA Updates', icon: '⇪' },
    { path: '/alerts', label: 'Alerts', icon: '⚠' },
    { path: '/faults', label: 'Fault Injection', icon: '⚡', admin: true, simulation: true },
    { path: '/users', label: 'Users', icon: '☺', admin: true },
  ];

  protected readonly nav = computed(() => this.items.filter((i) =>
    (!i.admin || this.auth.isAdministrator()) && (!i.simulation || this.faultInjection())));

  async ngOnInit(): Promise<void> {
    this.applyTheme();
    try {
      await this.store.start();
    } catch {
      this.startError.set('The AutoSphere API is not reachable. Check that the backend is running.');
      return;
    }

    try {
      this.faultInjection.set((await this.api.simulationStatus()).faultInjectionEnabled);
    } catch {
      this.faultInjection.set(false); // optional feature: never block the dashboard
    }
  }

  async ngOnDestroy(): Promise<void> {
    await this.store.stop();
  }

  protected selectVehicle(event: Event): void {
    void this.store.select((event.target as HTMLSelectElement).value);
  }

  protected cycleTheme(): void {
    const next = this.theme() === 'system' ? 'dark' : this.theme() === 'dark' ? 'light' : 'system';
    this.theme.set(next);
    try {
      if (next === 'system') {
        localStorage.removeItem('autosphere.theme');
      } else {
        localStorage.setItem('autosphere.theme', next);
      }
    } catch {
      // storage unavailable
    }
    this.applyTheme();
  }

  private applyTheme(): void {
    const theme = this.theme();
    if (theme === 'system') {
      document.documentElement.removeAttribute('data-theme');
    } else {
      document.documentElement.setAttribute('data-theme', theme);
    }
  }
}
