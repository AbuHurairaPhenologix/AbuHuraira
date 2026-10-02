import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, afterNextRender, effect, input, viewChild } from '@angular/core';
import {
  CategoryScale, Chart, Filler, LineController, LineElement, LinearScale, Plugin, PointElement, Tooltip, TooltipItem,
} from 'chart.js';
import { Point } from '../core/vehicle-store';

Chart.register(LineController, LineElement, PointElement, LinearScale, CategoryScale, Tooltip, Filler);

export interface Threshold {
  value: number;
  label: string;
  tone: 'warning' | 'critical';
}

const css = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
const clock = (t: number) => new Date(t).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });

/** Dashed reference lines (e.g. warning/critical thresholds) labelled in text ink, not series color. */
const thresholdPlugin: Plugin<'line'> = {
  id: 'thresholds',
  afterDatasetsDraw(chart, _args, options: { lines?: Threshold[] }) {
    const { ctx, chartArea, scales } = chart;
    for (const line of options.lines ?? []) {
      const y = scales['y'].getPixelForValue(line.value);
      if (y < chartArea.top || y > chartArea.bottom) {
        continue;
      }
      ctx.save();
      ctx.strokeStyle = css(line.tone === 'critical' ? '--status-critical' : '--status-warning');
      ctx.setLineDash([4, 4]);
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(chartArea.left, y);
      ctx.lineTo(chartArea.right, y);
      ctx.stroke();
      ctx.fillStyle = css('--text-secondary');
      ctx.font = '11px system-ui, sans-serif';
      ctx.textAlign = 'right';
      ctx.fillText(line.label, chartArea.right - 4, y - 4);
      ctx.restore();
    }
  },
};

/** Vertical crosshair at the hovered x position. */
const crosshairPlugin: Plugin<'line'> = {
  id: 'crosshair',
  afterDraw(chart) {
    const active = chart.tooltip?.getActiveElements();
    if (!active?.length) {
      return;
    }
    const { ctx, chartArea } = chart;
    const x = active[0].element.x;
    ctx.save();
    ctx.strokeStyle = css('--border-strong');
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(x, chartArea.top);
    ctx.lineTo(x, chartArea.bottom);
    ctx.stroke();
    ctx.restore();
  },
};

/**
 * Single-series time chart (one measure per chart, one y-axis). The title names the series, so no
 * legend box is shown. Hover shows a crosshair and a tooltip with time and value.
 */
@Component({
  selector: 'app-line-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="chart-head">
      <h2>{{ title() }}</h2>
      <span class="current">{{ latest() }}</span>
    </div>
    <div class="canvas-wrap"><canvas #canvas role="img" [attr.aria-label]="title() + ' over time'"></canvas></div>
  `,
  styles: `
    :host { display: block; }
    .chart-head { display: flex; align-items: baseline; justify-content: space-between; margin-bottom: 8px; }
    .current { font-variant-numeric: tabular-nums; font-weight: 600; color: var(--text-secondary); }
    .canvas-wrap { position: relative; height: 200px; }
  `,
})
export class LineChart implements OnDestroy {
  readonly title = input.required<string>();
  readonly unit = input('');
  readonly points = input<Point[]>([]);
  readonly min = input<number | undefined>(undefined);
  readonly max = input<number | undefined>(undefined);
  readonly thresholds = input<Threshold[]>([]);
  readonly decimals = input(1);

  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private chart: Chart<'line', { x: number; y: number }[]> | null = null;
  private readonly media = window.matchMedia('(prefers-color-scheme: dark)');
  private readonly themeObserver = new MutationObserver(() => this.rebuild());
  private readonly onSchemeChange = () => this.rebuild();

  protected latest = () => {
    const p = this.points();
    return p.length ? `${p[p.length - 1].v.toFixed(this.decimals())} ${this.unit()}` : '—';
  };

  constructor() {
    afterNextRender(() => {
      this.rebuild();
      this.media.addEventListener('change', this.onSchemeChange);
      this.themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    });

    effect(() => {
      const data = this.points().map((p) => ({ x: p.t, y: p.v }));
      const thresholds = this.thresholds();
      if (!this.chart) {
        return;
      }
      this.chart.data.datasets[0].data = data;
      (this.chart.options.plugins as { thresholds: { lines: Threshold[] } }).thresholds.lines = thresholds;
      this.chart.update('none');
    });
  }

  ngOnDestroy(): void {
    this.media.removeEventListener('change', this.onSchemeChange);
    this.themeObserver.disconnect();
    this.chart?.destroy();
  }

  private rebuild(): void {
    this.chart?.destroy();
    const series = css('--series-1');
    const grid = css('--grid');
    const muted = css('--text-muted');
    const unit = this.unit();
    const decimals = this.decimals();
    this.chart = new Chart(this.canvas().nativeElement, {
      type: 'line',
      data: {
        datasets: [{
          label: this.title(),
          data: this.points().map((p) => ({ x: p.t, y: p.v })),
          borderColor: series,
          backgroundColor: series,
          borderWidth: 2,
          pointRadius: 0,
          pointHoverRadius: 5,
          pointHoverBorderWidth: 2,
          pointHoverBorderColor: css('--surface-1'),
          tension: 0.25,
        }],
      },
      plugins: [thresholdPlugin, crosshairPlugin],
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        parsing: false,
        normalized: true,
        interaction: { mode: 'index', intersect: false },
        scales: {
          x: {
            type: 'linear',
            grid: { display: false },
            border: { color: grid },
            ticks: { color: muted, maxTicksLimit: 6, callback: (v) => clock(Number(v)), font: { size: 11 } },
          },
          y: {
            min: this.min(),
            max: this.max(),
            // Keep reference lines in view: a threshold the reader cannot see carries no information.
            suggestedMax: this.thresholds().length ? Math.max(...this.thresholds().map((t) => t.value)) * 1.08 : undefined,
            suggestedMin: this.thresholds().length ? Math.min(...this.thresholds().map((t) => t.value)) * 0.5 : undefined,
            grid: { color: grid },
            border: { display: false },
            ticks: { color: muted, maxTicksLimit: 5, font: { size: 11 } },
          },
        },
        plugins: {
          legend: { display: false },
          tooltip: {
            displayColors: false,
            backgroundColor: css('--surface-1'),
            titleColor: css('--text-secondary'),
            bodyColor: css('--text-primary'),
            borderColor: css('--border-strong'),
            borderWidth: 1,
            callbacks: {
              title: (items: TooltipItem<'line'>[]) => clock(items[0].parsed.x ?? 0),
              label: (item: TooltipItem<'line'>) => `${(item.parsed.y ?? 0).toFixed(decimals)} ${unit}`,
            },
          },
          thresholds: { lines: this.thresholds() },
        } as never,
      },
    });
  }
}
