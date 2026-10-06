import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService, errorMessage } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { ECU_TYPE_LABEL, EcuType, OtaDeployment, SoftwarePackage, TERMINAL_OTA } from '../core/models';
import { VehicleStore } from '../core/vehicle-store';
import { StatusBadge } from '../shared/status-badge';

@Component({
  selector: 'app-ota',
  imports: [StatusBadge, DatePipe, DecimalPipe, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './ota.html',
  styles: `
    .active-deployment { display: flex; flex-direction: column; gap: 6px; padding: 10px 0; border-bottom: 1px solid var(--border); }
    .active-deployment:last-child { border-bottom: none; }
    .row { display: flex; flex-wrap: wrap; gap: 10px; align-items: center; justify-content: space-between; }
    .timeline { list-style: none; margin: 8px 0 0; padding: 0 0 0 4px; border-left: 2px solid var(--border); }
    .timeline li { padding: 3px 0 3px 12px; font-size: 12px; position: relative; }
    .timeline li::before { content: ''; position: absolute; left: -6px; top: 9px; width: 8px; height: 8px; border-radius: 50%; background: var(--border-strong); }
    .timeline time { color: var(--text-muted); margin-right: 8px; font-variant-numeric: tabular-nums; }
    .sha { font-family: var(--mono); font-size: 12px; }
    details summary { cursor: pointer; }
    .versions { display: flex; flex-wrap: wrap; gap: 8px; }
    .version-chip { display: inline-flex; gap: 6px; align-items: baseline; border: 1px solid var(--border); border-radius: 6px; padding: 6px 10px; background: var(--surface-0); font-size: 13px; }
  `,
})
export class OtaPage implements OnInit {
  protected readonly store = inject(VehicleStore);
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  protected readonly typeLabel = ECU_TYPE_LABEL;
  protected readonly packages = signal<SoftwarePackage[]>([]);
  protected readonly busy = signal<string | null>(null);
  protected readonly message = signal<{ text: string; error: boolean } | null>(null);
  protected readonly ecuTypes: EcuType[] = ['BatteryManagementSystem', 'MotorControlUnit', 'VehicleControlUnit', 'BodyControlModule'];
  protected draft = { targetEcuType: 'BatteryManagementSystem' as EcuType, version: '', minimumCompatibleVersion: '1.0.0', bootBehavior: 'Normal' };

  protected readonly active = computed(() => this.store.deployments().filter((d) => !TERMINAL_OTA.includes(d.status)));
  protected readonly finished = computed(() => this.store.deployments().filter((d) => TERMINAL_OTA.includes(d.status)));
  protected readonly installed = computed(() => (this.store.vehicle()?.ecus ?? []).filter((e) => e.type !== 'CentralGateway'));

  async ngOnInit(): Promise<void> {
    await this.loadPackages();
  }

  protected installedVersion(type: EcuType): string | null {
    return this.installed().find((e) => e.type === type)?.softwareVersion ?? null;
  }

  protected async deploy(pkg: SoftwarePackage): Promise<void> {
    const vehicleId = this.store.selectedId()!;
    if (!confirm(`Deploy ${pkg.name} to ${vehicleId}?`)) {
      return;
    }
    await this.run(pkg.id, async () => {
      await this.api.deploy(pkg.id, [vehicleId], `${pkg.targetEcuType} ${pkg.version}`);
      return `Deployment of ${pkg.version} to ${vehicleId} started; progress is reported live below.`;
    });
  }

  protected async createSample(): Promise<void> {
    await this.run('create', async () => {
      const pkg = await this.api.createSamplePackage(this.draft);
      await this.loadPackages();
      return `Signed simulated package ${pkg.name} created (key ${pkg.signingKeyId}).`;
    });
  }

  protected eventTime(deployment: OtaDeployment, index: number): string {
    const start = Date.parse(deployment.events[0]?.timestamp ?? deployment.createdAt);
    return `+${((Date.parse(deployment.events[index].timestamp) - start) / 1000).toFixed(1)} s`;
  }

  private async loadPackages(): Promise<void> {
    try {
      this.packages.set(await this.api.packages());
    } catch (error) {
      this.message.set({ text: errorMessage(error), error: true });
    }
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
