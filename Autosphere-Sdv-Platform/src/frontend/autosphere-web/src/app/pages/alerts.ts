import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService, errorMessage } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { Alert } from '../core/models';
import { VehicleStore } from '../core/vehicle-store';
import { StatusBadge } from '../shared/status-badge';

@Component({
  selector: 'app-alerts',
  imports: [StatusBadge, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Alerts</h1>
          <p>Edge alerts are raised by the gateway on threshold crossings; backend alerts come from DTCs, connectivity and OTA results.</p>
        </div>
        <label class="check"><input type="checkbox" [checked]="activeOnly()" (change)="activeOnly.set($any($event.target).checked)" /> Active only</label>
      </div>
      @if (error()) { <div class="notice error">{{ error() }}</div> }
      <div class="panel table-wrap">
        <table>
          <thead><tr><th>Severity</th><th>Message</th><th>Category</th><th>Source</th><th>Raised</th><th>Cleared</th><th>Acknowledged</th><th></th></tr></thead>
          <tbody>
            @for (a of alerts(); track a.id) {
              <tr>
                <td><app-status [value]="a.severity" /></td>
                <td>{{ a.message }}@if (a.ecuId) { <div class="muted">{{ a.ecuId }}</div> }</td>
                <td>{{ a.category }}</td>
                <td>{{ a.source }}</td>
                <td>{{ a.raisedAt | date: 'medium' }}</td>
                <td>{{ a.clearedAt ? (a.clearedAt | date: 'mediumTime') : 'active' }}</td>
                <td>{{ a.acknowledgedBy ? a.acknowledgedBy + ' · ' + (a.acknowledgedAt | date: 'mediumTime') : '—' }}</td>
                <td>
                  @if (auth.canOperate() && !a.acknowledgedAt) {
                    <button class="small" (click)="acknowledge(a)">Acknowledge</button>
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="8" class="empty">No alerts.</td></tr>
            }
          </tbody>
        </table>
      </div>
    </div>
  `,
})
export class AlertsPage {
  protected readonly store = inject(VehicleStore);
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  protected readonly activeOnly = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly alerts = computed(() => (this.activeOnly() ? this.store.activeAlerts() : this.store.alerts()));

  protected async acknowledge(alert: Alert): Promise<void> {
    try {
      const updated = await this.api.acknowledgeAlert(alert.id);
      this.store.alerts.update((list) => list.map((a) => (a.id === updated.id ? updated : a)));
    } catch (error) {
      this.error.set(errorMessage(error));
    }
  }
}
