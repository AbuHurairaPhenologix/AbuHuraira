import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { ApiService, errorMessage } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { DiagnosticSession, DtcRecord } from '../core/models';
import { VehicleStore } from '../core/vehicle-store';
import { StatusBadge } from '../shared/status-badge';

@Component({
  selector: 'app-diagnostics',
  imports: [StatusBadge, DatePipe, DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './diagnostics.html',
  styles: `
    .diagnosis { font-size: 15px; font-weight: 600; margin: 4px 0 8px; }
    .meta { display: flex; flex-wrap: wrap; gap: 14px; font-size: 12px; color: var(--text-secondary); }
    details { border-top: 1px solid var(--border); padding: 8px 0; }
    summary { cursor: pointer; display: flex; gap: 10px; align-items: center; }
    .hex { font-family: var(--mono); font-size: 12px; word-break: break-all; }
    .snapshot { font-size: 12px; color: var(--text-secondary); }
    .nrc { color: var(--status-critical); font-weight: 600; }
    .data { display: flex; flex-wrap: wrap; gap: 4px 14px; margin: 6px 0; font-size: 12px; }
  `,
})
export class DiagnosticsPage {
  protected readonly store = inject(VehicleStore);
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  protected readonly busy = signal<string | null>(null);
  protected readonly message = signal<{ text: string; error: boolean } | null>(null);
  protected readonly includeHistory = signal(false);
  protected readonly history = signal<DtcRecord[] | null>(null);

  protected readonly latest = computed(() => this.store.diagnostics().find((d) => d.operation === 'FullScan') ?? null);
  protected readonly dtcs = computed(() => (this.includeHistory() ? this.history() ?? this.store.dtcs() : this.store.dtcs()));
  protected readonly clearable = computed(() => this.store.dtcs().filter((d) => d.isClearable).length);

  protected async scan(): Promise<void> {
    await this.run('scan', async () => {
      const session = await this.api.scan(this.store.selectedId()!);
      this.store.upsertDiagnostic(session);
      return this.describe(session);
    });
  }

  protected async clear(dtc: DtcRecord | null): Promise<void> {
    await this.run(dtc?.id ?? 'clear-all', async () => {
      const session = await this.api.clearDtcs(this.store.selectedId()!, dtc?.code ?? null, dtc?.ecuId ?? null);
      this.store.upsertDiagnostic(session);
      return dtc ? `${dtc.code} cleared from ${dtc.ecuId} (ClearDiagnosticInformation 0x14).` : 'All eligible DTCs cleared.';
    });
  }

  protected async toggleHistory(include: boolean): Promise<void> {
    this.includeHistory.set(include);
    if (include) {
      this.history.set(await this.api.dtcs(this.store.selectedId()!, true));
    }
  }

  protected snapshotText(dtc: DtcRecord): string {
    return dtc.snapshot.map((s) => `${s.name} ${s.value} ${s.unit}`).join(' · ');
  }

  private describe(session: DiagnosticSession): string {
    return session.status === 'TimedOut' ? 'The vehicle did not answer in time.' : (session.diagnosis ?? session.status);
  }

  private async run(key: string, action: () => Promise<string>): Promise<void> {
    this.busy.set(key);
    this.message.set(null);
    try {
      this.message.set({ text: await action(), error: false });
    } catch (error) {
      this.message.set({ text: errorMessage(error), error: true });
    } finally {
      this.busy.set(null);
    }
  }
}
