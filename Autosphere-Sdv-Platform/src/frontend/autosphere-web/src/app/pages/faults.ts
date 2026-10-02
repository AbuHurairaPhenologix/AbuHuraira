import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService, errorMessage } from '../core/api.service';
import { FaultType, SoftwarePackage } from '../core/models';
import { VehicleStore } from '../core/vehicle-store';
import { StatusBadge } from '../shared/status-badge';

interface FaultCard {
  fault: FaultType;
  title: string;
  description: string;
  needsEcu?: boolean;
  needsPackage?: boolean;
  clearable: boolean;
  defaultEcu?: string;
}

@Component({
  selector: 'app-faults',
  imports: [StatusBadge, DatePipe, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './faults.html',
  styles: `
    .banner { display: flex; gap: 12px; align-items: center; padding: 12px 16px; border-radius: var(--radius);
      border: 2px dashed var(--status-warning); background: color-mix(in srgb, var(--status-warning) 12%, var(--surface-1)); }
    .banner strong { font-size: 15px; letter-spacing: 0.02em; text-transform: uppercase; }
    .card { display: flex; flex-direction: column; gap: 10px; }
    .card p { margin: 0; color: var(--text-secondary); font-size: 13px; }
    .controls { display: flex; flex-wrap: wrap; gap: 8px; align-items: flex-end; margin-top: auto; }
  `,
})
export class FaultsPage implements OnInit {
  protected readonly store = inject(VehicleStore);
  private readonly api = inject(ApiService);
  protected readonly busy = signal<string | null>(null);
  protected readonly message = signal<{ text: string; error: boolean } | null>(null);
  protected readonly packages = signal<SoftwarePackage[]>([]);
  protected readonly ecus = computed(() => (this.store.vehicle()?.ecus ?? []).filter((e) => e.type !== 'CentralGateway'));
  protected readonly targets: Record<string, string> = {};
  protected readonly durations: Record<string, number | null> = {};
  protected selectedPackage = '';

  protected readonly cards: FaultCard[] = [
    { fault: 'BatteryOverheat', title: 'Battery overheat', description: 'Battery cooling failure in the plant model; pack temperature rises past 60 °C and the BMS sets P0A7E.', clearable: true },
    { fault: 'MotorOverheat', title: 'Motor overheat', description: 'Motor cooling failure; winding temperature rises past 130 °C and the MCU sets P0A2F.', clearable: true },
    { fault: 'EcuCrash', title: 'ECU crash', description: 'The ECU stops completely: no CAN frames, no diagnostic responses. Clearing reboots it.', needsEcu: true, clearable: true, defaultEcu: 'MCU-001' },
    { fault: 'InvalidSensorValue', title: 'Invalid sensor value', description: 'The ECU transmits a physically implausible value; the gateway flags it out of range.', needsEcu: true, clearable: true, defaultEcu: 'BMS-001' },
    { fault: 'CanMessageLoss', title: 'CAN message loss', description: 'The ECU stays alive but stops transmitting cyclic frames; the gateway detects the timeout (U-code).', needsEcu: true, clearable: true, defaultEcu: 'BCM-001' },
    { fault: 'CanMessageDelay', title: 'Delayed CAN messages', description: 'Frames arrive ~800 ms late; the gateway flags late frames and timeouts.', needsEcu: true, clearable: true, defaultEcu: 'VCU-001' },
    { fault: 'GatewayDisconnect', title: 'Gateway disconnect', description: 'The gateway drops its cloud link abruptly; the broker publishes the last will and the vehicle goes offline.', clearable: false },
    { fault: 'MqttDisconnect', title: 'MQTT session loss', description: 'Silent session loss; the gateway buffers data and flushes it after reconnecting. The backend detects stale telemetry.', clearable: false },
    { fault: 'CorruptedOtaPackage', title: 'Corrupted OTA package', description: 'Deploys the selected package with its payload corrupted in transit; verification must block installation.', needsPackage: true, clearable: false },
  ];

  async ngOnInit(): Promise<void> {
    for (const card of this.cards) {
      this.targets[card.fault] = card.defaultEcu ?? '';
      this.durations[card.fault] = card.fault.endsWith('Disconnect') ? 20 : null;
    }
    try {
      this.packages.set(await this.api.packages());
      this.selectedPackage = this.packages()[0]?.id ?? '';
    } catch {
      this.packages.set([]);
    }
  }

  protected async send(card: FaultCard, action: 'Inject' | 'Clear'): Promise<void> {
    this.busy.set(card.fault + action);
    this.message.set(null);
    try {
      const fault = await this.api.injectFault(this.store.selectedId()!, {
        fault: card.fault,
        action,
        targetEcuId: card.needsEcu ? this.targets[card.fault] : null,
        durationSeconds: action === 'Inject' ? this.durations[card.fault] : null,
        packageId: card.needsPackage ? this.selectedPackage : null,
      });
      this.store.upsertFault(fault);
      this.message.set({ text: `${card.title}: ${action === 'Inject' ? 'injected' : 'cleared'} (correlation ${fault.correlationId.slice(0, 8)}).`, error: false });
    } catch (error) {
      this.message.set({ text: errorMessage(error), error: true });
    } finally {
      this.busy.set(null);
    }
  }
}
