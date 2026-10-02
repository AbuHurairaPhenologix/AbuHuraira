import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService, errorMessage } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { DiagnosticSession, ECU_TYPE_LABEL } from '../core/models';
import { VehicleStore } from '../core/vehicle-store';
import { StatusBadge } from '../shared/status-badge';

@Component({
  selector: 'app-ecus',
  imports: [StatusBadge, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Electronic control units</h1>
          <p>Status and software inventory as observed by the vehicle gateway. Live reads use UDS ReadDataByIdentifier (0x22).</p>
        </div>
      </div>
      @if (message(); as m) { <div class="notice" [class.error]="m.error" role="status">{{ m.text }}</div> }
      <div class="panel table-wrap">
        <table>
          <thead>
            <tr>
              <th>ECU</th><th>Type</th><th>Status</th><th>Software</th><th>Hardware</th><th>Last heartbeat</th>
              <th class="num">Active DTCs</th><th class="num">Timeouts</th><th class="num">E2E errors</th>
              @if (auth.canOperate()) { <th>Actions</th> }
            </tr>
          </thead>
          <tbody>
            @for (ecu of ecus(); track ecu.ecuId) {
              <tr>
                <td><strong>{{ ecu.ecuId }}</strong><div class="muted">{{ ecu.name }}</div></td>
                <td>{{ typeLabel[ecu.type] }}</td>
                <td><app-status [value]="ecu.status" /></td>
                <td class="mono">{{ ecu.softwareVersion }}</td>
                <td class="mono">{{ ecu.hardwareVersion ?? '—' }}</td>
                <td>{{ ecu.lastHeartbeatAt ? (ecu.lastHeartbeatAt | date: 'mediumTime') : '—' }}</td>
                <td class="num">{{ ecu.activeDtcCount }}</td>
                <td class="num">{{ ecu.timeoutCount }}</td>
                <td class="num">{{ ecu.e2EErrorCount }}</td>
                @if (auth.canOperate()) {
                  <td class="actions">
                    @if (ecu.type !== 'CentralGateway') {
                      <button class="small" [disabled]="busy() !== null" (click)="readVersion(ecu.ecuId)">Read version</button>
                      <button class="small danger" [disabled]="busy() !== null" (click)="reset(ecu.ecuId)">Reset</button>
                    }
                  </td>
                }
              </tr>
            }
          </tbody>
        </table>
      </div>
    </div>
  `,
  styles: `.actions { display: flex; gap: 6px; }`,
})
export class EcusPage {
  protected readonly store = inject(VehicleStore);
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  protected readonly typeLabel = ECU_TYPE_LABEL;
  protected readonly busy = signal<string | null>(null);
  protected readonly message = signal<{ text: string; error: boolean } | null>(null);
  protected readonly ecus = computed(() => this.store.vehicle()?.ecus ?? []);

  protected readVersion(ecuId: string): Promise<void> {
    return this.run(ecuId, () => this.api.readSoftwareVersion(this.store.selectedId()!, ecuId), (s) => {
      const data = s.results[0]?.data ?? [];
      return `${ecuId}: ${data.map((d) => `${d.name} = ${d.value}`).join(', ')} (round trip ${s.roundTripMs?.toFixed(0)} ms)`;
    });
  }

  protected reset(ecuId: string): Promise<void> {
    if (!confirm(`Reset ${ecuId}? The ECU will stop communicating while it reboots.`)) {
      return Promise.resolve();
    }
    return this.run(ecuId, () => this.api.resetEcu(this.store.selectedId()!, ecuId), () => `${ecuId} accepted ECUReset (0x11 01) and is rebooting.`);
  }

  private async run(ecuId: string, action: () => Promise<DiagnosticSession>, describe: (s: DiagnosticSession) => string): Promise<void> {
    this.busy.set(ecuId);
    this.message.set(null);
    try {
      const session = await action();
      const failure = session.results.find((r) => !r.success);
      this.message.set(failure ? { text: `${ecuId}: ${failure.error}`, error: true } : { text: describe(session), error: false });
    } catch (error) {
      this.message.set({ text: errorMessage(error), error: true });
    } finally {
      this.busy.set(null);
    }
  }
}
