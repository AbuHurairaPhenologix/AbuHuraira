import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  effect,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { FieldDto, HotspotDto, SensorReadingDto } from '../core/models';
import { ColormapName, colorAt } from './colormaps';

interface Hover {
  x: number;
  y: number;
  left: number;
  top: number;
  value: number;
}

/**
 * Canvas heatmap of a scalar field on the cell-centred model grid. The field is rasterised at
 * native resolution and up-scaled with bilinear smoothing; sensors, true and estimated hotspots
 * are overlaid in physical coordinates (millimetres).
 */
@Component({
  selector: 'tt-heatmap',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <figure class="hm">
      @if (title()) {
        <figcaption>
          <span class="t">{{ title() }}</span>
          @if (field(); as f) {
            <span class="u">{{ f.unit }}</span>
          }
        </figcaption>
      }
      <div class="stage" #stage (mousemove)="onMove($event)" (mouseleave)="hover.set(null)">
        <canvas #canvas></canvas>
        @if (!field()) {
          <div class="empty">{{ emptyText() }}</div>
        }
        @if (hover(); as h) {
          <div class="tip" [style.left.px]="h.left" [style.top.px]="h.top">
            <b>{{ h.value.toFixed(decimals()) }} {{ field()?.unit }}</b>
            <span>x = {{ h.x.toFixed(0) }} mm · y = {{ h.y.toFixed(0) }} mm</span>
          </div>
        }
      </div>
    </figure>
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .hm { margin: 0; display: flex; flex-direction: column; gap: 6px; }
    figcaption { display: flex; justify-content: space-between; align-items: baseline; font-size: 12px; }
    .t { color: var(--ink-1); font-weight: 600; letter-spacing: .01em; }
    .u { color: var(--ink-3); font-size: 11px; }
    .stage { position: relative; width: 100%; }
    canvas { display: block; width: 100%; }
    .empty { position: absolute; inset: 0; display: grid; place-items: center; color: var(--ink-3); font-size: 12px; }
    .tip {
      position: absolute; pointer-events: none; transform: translate(10px, -110%);
      background: var(--surface-3); border: 1px solid var(--line-strong); border-radius: 6px;
      padding: 5px 8px; font-size: 11px; display: flex; flex-direction: column; gap: 1px; white-space: nowrap;
      color: var(--ink-2); box-shadow: 0 6px 18px rgba(0,0,0,.35);
    }
    .tip b { color: var(--ink-1); font-variant-numeric: tabular-nums; }
  `,
})
export class HeatmapComponent implements AfterViewInit, OnDestroy {
  readonly field = input<FieldDto | null | undefined>(null);
  readonly title = input<string>('');
  readonly colormap = input<ColormapName>('inferno');
  readonly range = input<[number, number] | null>(null);
  readonly symmetric = input(false);
  readonly sensors = input<SensorReadingDto[] | null>(null);
  readonly trueHotspots = input<HotspotDto[] | null>(null);
  readonly estimatedHotspot = input<HotspotDto | null | undefined>(null);
  readonly showSensorLabels = input(true);
  readonly decimals = input(2);
  readonly emptyText = input('Waiting for estimate…');

  readonly hover = signal<Hover | null>(null);

  private readonly canvasRef = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private readonly stageRef = viewChild.required<ElementRef<HTMLDivElement>>('stage');
  private observer?: ResizeObserver;
  private width = 0;
  private plot = { left: 0, top: 0, w: 0, h: 0 };
  private scale: [number, number] = [0, 1];

  constructor() {
    effect(() => {
      this.field();
      this.range();
      this.sensors();
      this.estimatedHotspot();
      this.trueHotspots();
      this.colormap();
      this.draw();
    });
  }

  ngAfterViewInit(): void {
    this.observer = new ResizeObserver((entries) => {
      const w = Math.floor(entries[0].contentRect.width);
      if (w !== this.width) {
        this.width = w;
        this.draw();
      }
    });
    this.observer.observe(this.stageRef().nativeElement);
  }

  ngOnDestroy(): void {
    this.observer?.disconnect();
  }

  onMove(event: MouseEvent): void {
    const f = this.field();
    if (!f) {
      return;
    }
    const rect = this.stageRef().nativeElement.getBoundingClientRect();
    const px = event.clientX - rect.left;
    const py = event.clientY - rect.top;
    const { left, top, w, h } = this.plot;
    if (px < left || px > left + w || py < top || py > top + h) {
      this.hover.set(null);
      return;
    }
    const i = Math.min(f.nx - 1, Math.floor(((px - left) / w) * f.nx));
    const j = Math.min(f.ny - 1, Math.floor(((top + h - py) / h) * f.ny));
    this.hover.set({
      x: ((px - left) / w) * f.lengthX * 1000,
      y: ((top + h - py) / h) * f.lengthY * 1000,
      left: px,
      top: py,
      value: f.values[j][i],
    });
  }

  private draw(): void {
    const canvas = this.canvasRef?.()?.nativeElement;
    if (!canvas || this.width === 0) {
      return;
    }
    const f = this.field();
    const lx = f?.lengthX ?? 0.2;
    const ly = f?.lengthY ?? 0.1;
    const dpr = window.devicePixelRatio || 1;
    const colorbar = 58;
    const margin = { left: 34, right: colorbar + 8, top: 6, bottom: 24 };
    const plotW = Math.max(40, this.width - margin.left - margin.right);
    const plotH = plotW * (ly / lx);
    const height = Math.round(plotH + margin.top + margin.bottom);
    this.plot = { left: margin.left, top: margin.top, w: plotW, h: plotH };

    canvas.width = Math.round(this.width * dpr);
    canvas.height = Math.round(height * dpr);
    canvas.style.height = `${height}px`;
    const ctx = canvas.getContext('2d')!;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, this.width, height);

    const style = getComputedStyle(document.documentElement);
    const ink2 = style.getPropertyValue('--ink-2').trim() || '#c3c2b7';
    const ink3 = style.getPropertyValue('--ink-3').trim() || '#898781';
    const line = style.getPropertyValue('--line-strong').trim() || '#383835';

    ctx.fillStyle = style.getPropertyValue('--surface-2').trim() || '#1a1a19';
    ctx.fillRect(margin.left, margin.top, plotW, plotH);

    if (f) {
      let [min, max] = this.range() ?? [f.min, f.max];
      if (this.symmetric()) {
        const m = Math.max(Math.abs(min), Math.abs(max), 1e-9);
        min = -m;
        max = m;
      }
      if (max - min < 1e-9) {
        max = min + 1;
      }
      this.scale = [min, max];

      const raster = document.createElement('canvas');
      raster.width = f.nx;
      raster.height = f.ny;
      const rctx = raster.getContext('2d')!;
      const image = rctx.createImageData(f.nx, f.ny);
      for (let j = 0; j < f.ny; j++) {
        for (let i = 0; i < f.nx; i++) {
          const [r, g, b] = colorAt(this.colormap(), (f.values[j][i] - min) / (max - min));
          const o = ((f.ny - 1 - j) * f.nx + i) * 4;
          image.data[o] = r;
          image.data[o + 1] = g;
          image.data[o + 2] = b;
          image.data[o + 3] = 255;
        }
      }
      rctx.putImageData(image, 0, 0);
      ctx.imageSmoothingEnabled = true;
      ctx.imageSmoothingQuality = 'high';
      ctx.drawImage(raster, margin.left, margin.top, plotW, plotH);
    }

    ctx.strokeStyle = line;
    ctx.lineWidth = 1;
    ctx.strokeRect(margin.left + 0.5, margin.top + 0.5, plotW - 1, plotH - 1);

    // Axes in millimetres.
    ctx.fillStyle = ink3;
    ctx.font = '10px system-ui, -apple-system, "Segoe UI", sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    const stepX = lx * 1000 > 150 ? 50 : 25;
    for (let v = 0; v <= lx * 1000 + 1e-6; v += stepX) {
      const x = margin.left + (v / (lx * 1000)) * plotW;
      ctx.fillText(`${v}`, x, margin.top + plotH + 5);
    }
    ctx.textAlign = 'right';
    ctx.textBaseline = 'middle';
    for (let v = 0; v <= ly * 1000 + 1e-6; v += stepX) {
      const y = margin.top + plotH - (v / (ly * 1000)) * plotH;
      ctx.fillText(`${v}`, margin.left - 5, y);
    }
    ctx.textAlign = 'left';
    ctx.fillText('mm', 2, margin.top + plotH + 11);

    const toPx = (x: number, y: number): [number, number] => [
      margin.left + (x / lx) * plotW,
      margin.top + plotH - (y / ly) * plotH,
    ];

    // True hidden hotspot(s): dashed ring.
    for (const hs of this.trueHotspots() ?? []) {
      const [x, y] = toPx(hs.x, hs.y);
      ctx.setLineDash([3, 3]);
      ctx.strokeStyle = 'rgba(255,255,255,0.85)';
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.arc(x, y, 11, 0, Math.PI * 2);
      ctx.stroke();
      ctx.setLineDash([]);
    }

    // Estimated hotspot: crosshair.
    const est = this.estimatedHotspot();
    if (est) {
      const [x, y] = toPx(est.x, est.y);
      ctx.strokeStyle = '#3987e5';
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.moveTo(x - 9, y);
      ctx.lineTo(x + 9, y);
      ctx.moveTo(x, y - 9);
      ctx.lineTo(x, y + 9);
      ctx.stroke();
      ctx.beginPath();
      ctx.arc(x, y, 5, 0, Math.PI * 2);
      ctx.stroke();
    }

    // Sensors.
    for (const s of this.sensors() ?? []) {
      const [x, y] = toPx(s.x, s.y);
      ctx.fillStyle = '#ffffff';
      ctx.strokeStyle = '#0d0d0d';
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.arc(x, y, 4, 0, Math.PI * 2);
      ctx.stroke();
      ctx.fill();
      if (this.showSensorLabels()) {
        ctx.font = '600 9px system-ui, -apple-system, "Segoe UI", sans-serif';
        ctx.textAlign = 'left';
        ctx.textBaseline = 'bottom';
        ctx.lineWidth = 3;
        ctx.strokeStyle = 'rgba(13,13,13,0.85)';
        ctx.strokeText(s.id, x + 5, y - 3);
        ctx.fillStyle = '#ffffff';
        ctx.fillText(s.id, x + 5, y - 3);
      }
    }

    // Colour bar.
    if (f) {
      const [min, max] = this.scale;
      const cbX = margin.left + plotW + 12;
      const cbW = 10;
      for (let k = 0; k < plotH; k++) {
        const t = 1 - k / plotH;
        const [r, g, b] = colorAt(this.colormap(), t);
        ctx.fillStyle = `rgb(${r},${g},${b})`;
        ctx.fillRect(cbX, margin.top + k, cbW, 1.5);
      }
      ctx.strokeStyle = line;
      ctx.strokeRect(cbX + 0.5, margin.top + 0.5, cbW - 1, plotH - 1);
      ctx.fillStyle = ink2;
      ctx.textAlign = 'left';
      ctx.textBaseline = 'middle';
      ctx.font = '10px system-ui, -apple-system, "Segoe UI", sans-serif';
      const ticks = 4;
      for (let k = 0; k <= ticks; k++) {
        const v = min + ((max - min) * k) / ticks;
        const y = margin.top + plotH - (k / ticks) * plotH;
        ctx.fillText(this.tick(v, max - min), cbX + cbW + 4, Math.min(Math.max(y, margin.top + 5), margin.top + plotH - 5));
      }
    }
  }

  private tick(v: number, span: number): string {
    if (span >= 100) {
      return v.toFixed(0);
    }
    if (span >= 5) {
      return v.toFixed(1);
    }
    return v.toFixed(2);
  }
}
