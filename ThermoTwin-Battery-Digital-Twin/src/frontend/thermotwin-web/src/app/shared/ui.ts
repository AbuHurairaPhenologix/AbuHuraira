import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ThermalRisk } from '../core/models';

/** KPI tile: label, value, unit and an optional secondary line. */
@Component({
  selector: 'tt-stat',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="stat" [class.accent]="accent()">
      <div class="label">{{ label() }}</div>
      <div class="value">
        <span>{{ value() }}</span>
        @if (unit()) {
          <small>{{ unit() }}</small>
        }
      </div>
      @if (hint()) {
        <div class="hint">{{ hint() }}</div>
      }
    </div>
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .stat {
      height: 100%; box-sizing: border-box; padding: 12px 14px; border-radius: 10px;
      background: var(--surface-2); border: 1px solid var(--line);
      display: flex; flex-direction: column; gap: 4px;
    }
    .stat.accent { border-color: rgba(57, 135, 229, 0.45); background: linear-gradient(180deg, rgba(57,135,229,.09), var(--surface-2) 70%); }
    .label { font-size: 11px; color: var(--ink-3); text-transform: uppercase; letter-spacing: .06em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .value { display: flex; align-items: baseline; gap: 5px; color: var(--ink-1); }
    .value span { font-size: 24px; font-weight: 600; letter-spacing: -.01em; }
    .value small { font-size: 12px; color: var(--ink-2); }
    .hint { font-size: 11px; color: var(--ink-2); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  `,
})
export class StatComponent {
  readonly label = input.required<string>();
  readonly value = input.required<string>();
  readonly unit = input('');
  readonly hint = input('');
  readonly accent = input(false);
}

/** Risk badge: status colour always paired with an icon and a text label. */
@Component({
  selector: 'tt-risk',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="risk" [attr.data-level]="level()"><i>{{ icon() }}</i>{{ level() }}</span>`,
  styles: `
    .risk { display: inline-flex; align-items: center; gap: 6px; padding: 3px 10px 3px 7px; border-radius: 999px; font-size: 12px; font-weight: 600; border: 1px solid; }
    i { font-style: normal; font-size: 11px; }
    [data-level='Normal'] { color: #0ca30c; border-color: rgba(12,163,12,.5); background: rgba(12,163,12,.10); }
    [data-level='Elevated'] { color: #fab219; border-color: rgba(250,178,25,.5); background: rgba(250,178,25,.10); }
    [data-level='Warning'] { color: #ec835a; border-color: rgba(236,131,90,.55); background: rgba(236,131,90,.12); }
    [data-level='Critical'] { color: #ff6b6b; border-color: rgba(208,59,59,.6); background: rgba(208,59,59,.15); }
  `,
})
export class RiskBadgeComponent {
  readonly level = input.required<ThermalRisk>();
  readonly icon = computed(() => ({ Normal: '●', Elevated: '▲', Warning: '⚠', Critical: '✖' })[this.level()]);
}

/** Section card with title, optional subtitle and projected content. */
@Component({
  selector: 'tt-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card">
      @if (heading()) {
        <header>
          <div>
            <h3>{{ heading() }}</h3>
            @if (sub()) {
              <p>{{ sub() }}</p>
            }
          </div>
          <ng-content select="[card-actions]" />
        </header>
      }
      <ng-content />
    </section>
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .card { height: 100%; box-sizing: border-box; background: var(--surface-1); border: 1px solid var(--line); border-radius: 12px; padding: 14px 16px; display: flex; flex-direction: column; gap: 10px; }
    header { display: flex; justify-content: space-between; align-items: flex-start; gap: 12px; }
    h3 { margin: 0; font-size: 13px; font-weight: 600; color: var(--ink-1); letter-spacing: .01em; }
    p { margin: 3px 0 0; font-size: 11.5px; color: var(--ink-3); line-height: 1.4; }
  `,
})
export class CardComponent {
  readonly heading = input('');
  readonly sub = input('');
}
