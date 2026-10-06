import { AfterViewInit, ChangeDetectionStrategy, Component, ElementRef, OnDestroy, effect, input, viewChild } from '@angular/core';
import { Chart, ChartConfiguration, registerables } from 'chart.js';

Chart.register(...registerables);
Chart.defaults.font.family = 'system-ui, -apple-system, "Segoe UI", sans-serif';
Chart.defaults.font.size = 11;
Chart.defaults.color = '#898781';
Chart.defaults.borderColor = '#2c2c2a';
Chart.defaults.animation = false;

/** Thin Chart.js host: creates the chart once and updates data/options in place on every config change. */
@Component({
  selector: 'tt-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="wrap" [style.height.px]="height()">
      <canvas #canvas [attr.aria-label]="label()" role="img"></canvas>
    </div>
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .wrap { position: relative; width: 100%; }
  `,
})
export class ChartComponent implements AfterViewInit, OnDestroy {
  readonly config = input.required<ChartConfiguration>();
  readonly height = input(240);
  readonly label = input('chart');

  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private chart?: Chart;
  private ready = false;

  constructor() {
    effect(() => {
      const config = this.config();
      if (this.ready) {
        this.render(config);
      }
    });
  }

  ngAfterViewInit(): void {
    this.ready = true;
    this.render(this.config());
  }

  ngOnDestroy(): void {
    this.chart?.destroy();
  }

  private render(config: ChartConfiguration): void {
    if (this.chart && (this.chart.config as ChartConfiguration).type === config.type) {
      this.chart.data = config.data;
      this.chart.options = config.options ?? {};
      this.chart.update('none');
      return;
    }
    this.chart?.destroy();
    this.chart = new Chart(this.canvas().nativeElement, config);
  }
}
