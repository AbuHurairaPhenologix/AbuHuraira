import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DecimalPipe } from '@angular/common';

/**
 * A 240° arc gauge for a single headline value (speed, motor speed). The arc is one hue; the number
 * carries the precise value, the arc only gives an at-a-glance magnitude.
 */
@Component({
  selector: 'app-gauge',
  imports: [DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <figure class="gauge" [attr.aria-label]="label() + ': ' + (value() ?? 'no data') + ' ' + unit()">
      <svg viewBox="0 -12 200 162" role="img" aria-hidden="true">
        <path [attr.d]="track" class="track" />
        <path [attr.d]="arc()" class="value" />
        @for (tick of ticks(); track tick.label) {
          <text [attr.x]="tick.x" [attr.y]="tick.y" class="tick">{{ tick.label }}</text>
        }
      </svg>
      <div class="readout">
        <span class="number">{{ value() === null ? '—' : (value()! | number: '1.0-0') }}</span>
        <span class="unit">{{ unit() }}</span>
      </div>
      <figcaption>{{ label() }}</figcaption>
    </figure>
  `,
  styles: `
    .gauge { position: relative; margin: 0; display: flex; flex-direction: column; align-items: center; }
    svg { width: 100%; max-width: 240px; }
    .track { fill: none; stroke: var(--surface-2); stroke-width: 14; stroke-linecap: round; }
    .value { fill: none; stroke: var(--series-1); stroke-width: 14; stroke-linecap: round; transition: d 0.25s ease; }
    .tick { font-size: 10px; fill: var(--text-muted); text-anchor: middle; }
    .readout { position: absolute; top: 38%; display: flex; flex-direction: column; align-items: center; }
    .number { font-size: 34px; font-weight: 650; font-variant-numeric: tabular-nums; letter-spacing: -0.02em; }
    .unit { font-size: 12px; color: var(--text-secondary); }
    figcaption { margin-top: -18px; font-size: 13px; font-weight: 600; color: var(--text-secondary); }
  `,
})
export class Gauge {
  readonly value = input<number | null>(null);
  readonly max = input(200);
  readonly unit = input('');
  readonly label = input('');

  private static readonly start = 150;
  private static readonly sweep = 240;
  protected readonly track = Gauge.describe(Gauge.start, Gauge.start + Gauge.sweep);

  protected readonly arc = computed(() => {
    const ratio = Math.min(Math.max((this.value() ?? 0) / this.max(), 0), 1);
    return Gauge.describe(Gauge.start, Gauge.start + Math.max(ratio * Gauge.sweep, 0.5));
  });

  protected readonly ticks = computed(() => [0, 0.5, 1].map((r) => {
    const p = Gauge.point(Gauge.start + r * Gauge.sweep, 92);
    return { x: p.x, y: p.y + 4, label: `${Math.round(this.max() * r)}` };
  }));

  private static point(angle: number, radius = 72): { x: number; y: number } {
    const rad = (angle * Math.PI) / 180;
    return { x: 100 + radius * Math.cos(rad), y: 90 + radius * Math.sin(rad) };
  }

  private static describe(from: number, to: number): string {
    const a = Gauge.point(from);
    const b = Gauge.point(to);
    const large = to - from > 180 ? 1 : 0;
    return `M ${a.x.toFixed(2)} ${a.y.toFixed(2)} A 72 72 0 ${large} 1 ${b.x.toFixed(2)} ${b.y.toFixed(2)}`;
  }
}

/** A KPI tile: label, value with unit, optional context line and level bar. */
@Component({
  selector: 'app-kpi',
  imports: [DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="kpi" [class.alarm]="alarm()">
      <span class="label">{{ label() }}</span>
      <span class="value">
        @if (text()) { <span class="text">{{ text() }}</span> } @else if (value() === null) { — } @else { {{ value() | number: format() }} }
        <small>{{ unit() }}</small>
      </span>
      @if (level() !== null) {
        <div class="progress" aria-hidden="true"><span [style.width.%]="level()"></span></div>
      }
      @if (hint()) { <span class="hint">{{ hint() }}</span> }
    </div>
  `,
  styles: `
    .kpi { display: flex; flex-direction: column; gap: 4px; padding: 12px 14px; border: 1px solid var(--border); border-radius: var(--radius);
      background: var(--surface-1); box-shadow: var(--shadow); height: 100%; }
    .kpi.alarm { border-color: var(--status-critical); box-shadow: inset 3px 0 0 var(--status-critical); }
    .label { font-size: 12px; color: var(--text-secondary); font-weight: 600; }
    .value { font-size: 24px; font-weight: 650; font-variant-numeric: tabular-nums; letter-spacing: -0.01em; }
    .text { font-size: 18px; font-weight: 600; }
    small { font-size: 13px; font-weight: 500; color: var(--text-secondary); margin-left: 2px; }
    .hint { font-size: 12px; color: var(--text-muted); }
  `,
})
export class Kpi {
  readonly label = input.required<string>();
  readonly value = input<number | null>(null);
  readonly text = input<string | null>(null);
  readonly unit = input('');
  readonly format = input('1.0-1');
  readonly hint = input<string | null>(null);
  readonly level = input<number | null>(null);
  readonly alarm = input(false);
}
