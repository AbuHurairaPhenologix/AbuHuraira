import { Component, computed, input } from '@angular/core';
import { TrendPoint } from '../core/models';

/** Daily anomaly trend (bars) with processed-window volume (line), rendered as SVG from API data. */
@Component({
  selector: 'app-trend-chart',
  template: `
    @if (points().length === 0) {
      <p class="muted">No windows processed yet.</p>
    } @else {
      <svg [attr.viewBox]="'0 0 ' + width + ' ' + height" class="trend" role="img" aria-label="Anomalies per day">
        @for (b of bars(); track b.label) {
          <g>
            <rect [attr.x]="b.x" [attr.y]="b.y" [attr.width]="b.w" [attr.height]="b.h" class="bar">
              <title>{{ b.label }}: {{ b.anomalies }} anomalies / {{ b.windows }} windows</title>
            </rect>
            <text [attr.x]="b.x + b.w / 2" [attr.y]="height - 4" class="axis" text-anchor="middle">{{ b.short }}</text>
            @if (b.anomalies > 0) {
              <text [attr.x]="b.x + b.w / 2" [attr.y]="b.y - 4" class="value" text-anchor="middle">{{ b.anomalies }}</text>
            }
          </g>
        }
        <polyline [attr.points]="line()" class="line" />
      </svg>
      <div class="legend"><span class="sw bar"></span> anomalies <span class="sw line"></span> processed windows (scaled)</div>
    }
  `,
  styles: `
    .trend { width: 100%; height: 200px; }
    .bar { fill: var(--accent); opacity: 0.85; }
    .line { fill: none; stroke: var(--muted); stroke-width: 1.5; stroke-dasharray: 4 3; }
    .axis { font-size: 10px; fill: var(--muted); }
    .value { font-size: 10px; fill: var(--text); font-weight: 600; }
    .legend { font-size: 12px; color: var(--muted); display: flex; gap: 6px; align-items: center; }
    .sw { display: inline-block; width: 12px; height: 8px; margin-left: 8px; }
    .sw.bar { background: var(--accent); }
    .sw.line { border-top: 2px dashed var(--muted); height: 0; }
  `,
})
export class TrendChartComponent {
  readonly points = input<TrendPoint[]>([]);
  readonly width = 640;
  readonly height = 200;
  private readonly top = 18;
  private readonly bottom = 18;

  readonly bars = computed(() => {
    const pts = this.points();
    const maxA = Math.max(1, ...pts.map((p) => p.anomalies));
    const slot = this.width / Math.max(1, pts.length);
    const usable = this.height - this.top - this.bottom;
    return pts.map((p, i) => {
      const h = (p.anomalies / maxA) * usable;
      return {
        x: i * slot + slot * 0.2,
        w: slot * 0.6,
        h,
        y: this.top + usable - h,
        label: p.day,
        short: p.day.slice(5),
        anomalies: p.anomalies,
        windows: p.windows,
      };
    });
  });

  readonly line = computed(() => {
    const pts = this.points();
    const maxW = Math.max(1, ...pts.map((p) => p.windows));
    const slot = this.width / Math.max(1, pts.length);
    const usable = this.height - this.top - this.bottom;
    return pts.map((p, i) => `${i * slot + slot / 2},${this.top + usable - (p.windows / maxW) * usable}`).join(' ');
  });
}
