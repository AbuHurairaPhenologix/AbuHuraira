import { ChartConfiguration, ChartDataset, ScaleOptions } from 'chart.js';

/** Categorical slots (dark-surface steps of the validated reference palette), assigned in fixed order. */
export const SERIES = {
  blue: '#3987e5',
  orange: '#d95926',
  aqua: '#199e70',
  yellow: '#c98500',
  magenta: '#d55181',
  green: '#008300',
  violet: '#9085e9',
  red: '#e66767',
} as const;

export const SERIES_ORDER = [SERIES.blue, SERIES.orange, SERIES.aqua, SERIES.yellow, SERIES.magenta, SERIES.green, SERIES.violet, SERIES.red];

/** Status colours are reserved for state (never for series). */
export const STATUS = { good: '#0ca30c', warning: '#fab219', serious: '#ec835a', critical: '#d03b3b' } as const;

export const INK = { primary: '#ffffff', secondary: '#c3c2b7', muted: '#898781', grid: '#2c2c2a', axis: '#383835' } as const;

export type Point = { x: number; y: number | null };

export function line(label: string, data: Point[], color: string, extra: Partial<ChartDataset<'line'>> = {}): ChartDataset<'line', Point[]> {
  return {
    label,
    data,
    borderColor: color,
    backgroundColor: color,
    borderWidth: 2,
    pointRadius: 0,
    pointHoverRadius: 4,
    tension: 0,
    spanGaps: true,
    ...extra,
  } as ChartDataset<'line', Point[]>;
}

/** Horizontal reference line (e.g. T_safe) drawn as a dashed status-coloured series. */
export function limit(label: string, y: number, x0: number, x1: number, color: string = STATUS.critical): ChartDataset<'line', Point[]> {
  return line(label, [{ x: x0, y }, { x: x1, y }], color, { borderDash: [6, 4], borderWidth: 1.5, pointHoverRadius: 0 });
}

export function axis(title: string, extra: Partial<ScaleOptions<'linear'>> = {}): ScaleOptions<'linear'> {
  return {
    type: 'linear',
    title: { display: true, text: title, color: INK.muted, font: { size: 11 } },
    grid: { color: INK.grid },
    border: { color: INK.axis },
    ticks: { color: INK.muted, maxTicksLimit: 8 },
    ...extra,
  } as ScaleOptions<'linear'>;
}

export function logAxis(title: string, extra: Record<string, unknown> = {}): ScaleOptions<'logarithmic'> {
  return {
    type: 'logarithmic',
    title: { display: true, text: title, color: INK.muted, font: { size: 11 } },
    grid: { color: (ctx: { tick?: { label?: unknown } }) => (ctx.tick?.label ? INK.grid : 'transparent') },
    border: { color: INK.axis },
    ticks: {
      color: INK.muted,
      autoSkip: false,
      callback: function (this: { min: number; max: number }, value: number | string) {
        const v = Number(value);
        const wide = Math.log10(Math.max(this.max, 1e-300) / Math.max(this.min, 1e-300)) > 3.5;
        const exp = Math.floor(Math.log10(v) + 1e-9);
        const mantissa = Math.round(v / Math.pow(10, exp));
        if (Math.abs(v / Math.pow(10, exp) - mantissa) > 1e-6 || ![1, 2, 5].includes(mantissa) || (wide && mantissa !== 1)) {
          return '';
        }
        if (exp >= -2 && exp <= 3) {
          return `${+v.toPrecision(3)}`;
        }
        return mantissa === 1 ? `1e${exp}` : '';
      },
    },
    ...extra,
  } as ScaleOptions<'logarithmic'>;
}

export function lineChart(
  datasets: ChartDataset<'line', Point[]>[],
  x: ScaleOptions,
  y: ScaleOptions,
  options: { legend?: boolean; tooltipDigits?: number } = {},
): ChartConfiguration<'line', Point[]> {
  const digits = options.tooltipDigits ?? 2;
  return {
    type: 'line',
    data: { datasets },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      parsing: false,
      normalized: true,
      interaction: { mode: 'nearest', axis: 'x', intersect: false },
      plugins: {
        legend: {
          display: options.legend ?? datasets.length > 1,
          position: 'top',
          align: 'end',
          labels: { color: INK.secondary, boxWidth: 14, boxHeight: 2, padding: 12, font: { size: 11 } },
        },
        tooltip: {
          backgroundColor: '#242422',
          borderColor: '#383835',
          borderWidth: 1,
          titleColor: INK.primary,
          bodyColor: INK.secondary,
          padding: 8,
          callbacks: {
            title: (items) => (items.length ? `x = ${Number(items[0].parsed.x).toPrecision(4)}` : ''),
            label: (item) => ` ${item.dataset.label}: ${item.parsed.y === null ? '—' : Number(item.parsed.y).toFixed(digits)}`,
          },
        },
      },
      scales: { x, y },
    },
  } as ChartConfiguration<'line', Point[]>;
}

export function barChart(
  labels: string[],
  datasets: { label: string; data: number[]; color: string | string[] }[],
  y: ScaleOptions,
  options: { horizontal?: boolean; digits?: number } = {},
): ChartConfiguration<'bar'> {
  const digits = options.digits ?? 2;
  return {
    type: 'bar',
    data: {
      labels,
      datasets: datasets.map((d) => ({
        label: d.label,
        data: d.data,
        backgroundColor: d.color,
        borderRadius: 4,
        borderSkipped: 'start',
        maxBarThickness: 34,
        categoryPercentage: 0.7,
      })),
    },
    options: {
      indexAxis: options.horizontal ? 'y' : 'x',
      responsive: true,
      maintainAspectRatio: false,
      plugins: {
        legend: { display: datasets.length > 1, position: 'top', align: 'end', labels: { color: INK.secondary, boxWidth: 10 } },
        tooltip: {
          backgroundColor: '#242422',
          borderColor: '#383835',
          borderWidth: 1,
          callbacks: { label: (item) => ` ${item.dataset.label}: ${Number(item.raw).toFixed(digits)}` },
        },
      },
      scales: options.horizontal
        ? { y: { grid: { display: false }, ticks: { color: INK.secondary } }, x: y as ScaleOptions<'linear'> }
        : { x: { grid: { display: false }, ticks: { color: INK.secondary } }, y: y as ScaleOptions<'linear'> },
    },
  } as ChartConfiguration<'bar'>;
}
