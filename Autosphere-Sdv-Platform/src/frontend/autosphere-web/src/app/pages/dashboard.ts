import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ECU_TYPE_LABEL } from '../core/models';
import { VehicleStore } from '../core/vehicle-store';
import { Gauge, Kpi } from '../shared/gauge';
import { StatusBadge } from '../shared/status-badge';

@Component({
  selector: 'app-dashboard',
  imports: [Gauge, Kpi, StatusBadge, DatePipe, DecimalPipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss',
})
export class DashboardPage {
  protected readonly store = inject(VehicleStore);
  protected readonly typeLabel = ECU_TYPE_LABEL;
  protected readonly snapshot = computed(() => this.store.telemetry()?.snapshot ?? null);
  protected readonly reasons = computed(() => {
    const summary = this.store.vehicle()?.health.summary;
    return summary ? summary.split(/(?<=\.)\s+/).filter((r) => r.length > 0) : [];
  });
  protected readonly ecus = computed(() => [...(this.store.vehicle()?.ecus ?? [])]);
  protected readonly topAlerts = computed(() => this.store.activeAlerts().slice(0, 5));

  protected chargingLabel(value: string | null | undefined): string {
    return value === 'NotCharging' ? 'Not charging' : value === 'ChargeComplete' ? 'Complete' : (value ?? '—');
  }
}
