import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export type Tone = 'good' | 'warning' | 'serious' | 'critical' | 'neutral' | 'info';

const TONES: Record<string, Tone> = {
  Healthy: 'good', Online: 'good', Succeeded: 'good', Completed: 'good', Valid: 'good', Resolved: 'good', Connected: 'good',
  Warning: 'warning', Stale: 'warning', Updating: 'info', Pending: 'info', Downloading: 'info', Verifying: 'info',
  Installing: 'info', Restarting: 'info', HealthChecking: 'info', RollingBack: 'serious', RolledBack: 'serious',
  Critical: 'critical', Failed: 'critical', RollbackFailed: 'critical', TimedOut: 'critical', OutOfRange: 'critical', Active: 'critical',
  Offline: 'neutral', Unknown: 'neutral', Cleared: 'neutral', Information: 'info', Created: 'neutral', Cancelled: 'neutral',
};

const ICONS: Record<Tone, string> = {
  good: '✓', warning: '!', serious: '↺', critical: '✕', neutral: '○', info: '•',
};

/** Status shown as icon + text + reserved status color, so meaning never relies on color alone. */
@Component({
  selector: 'app-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="badge" [class]="'badge tone-' + tone()" [attr.title]="title() ?? value()">
    <span class="icon" aria-hidden="true">{{ icon() }}</span>{{ label() ?? value() }}
  </span>`,
  styles: `
    .badge {
      display: inline-flex; align-items: center; gap: 5px; padding: 1px 8px 1px 6px; border-radius: 999px;
      font-size: 12px; font-weight: 600; line-height: 20px; white-space: nowrap;
      border: 1px solid color-mix(in srgb, var(--tone) 55%, transparent);
      background: color-mix(in srgb, var(--tone) 14%, var(--surface-1));
      color: var(--text-primary);
    }
    .icon {
      display: inline-grid; place-items: center; width: 14px; height: 14px; border-radius: 50%;
      background: var(--tone); color: #fff; font-size: 10px; line-height: 1;
    }
    .tone-good { --tone: var(--status-good); }
    .tone-warning { --tone: var(--status-warning); }
    .tone-warning .icon { color: #111; }
    .tone-serious { --tone: var(--status-serious); }
    .tone-critical { --tone: var(--status-critical); }
    .tone-neutral { --tone: var(--status-neutral); }
    .tone-info { --tone: var(--status-info); }
  `,
})
export class StatusBadge {
  readonly value = input.required<string>();
  readonly label = input<string>();
  readonly title = input<string>();
  readonly tone = computed<Tone>(() => TONES[this.value()] ?? 'neutral');
  readonly icon = computed(() => ICONS[this.tone()]);
}
