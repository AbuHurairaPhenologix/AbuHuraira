// Pure helpers for the mathematical pages (unit-tested in math-format.spec.ts).
import { cssColor } from '../shared/colormaps';

const SUPERSCRIPT: Record<string, string> = {
  '-': '⁻',
  '0': '⁰',
  '1': '¹',
  '2': '²',
  '3': '³',
  '4': '⁴',
  '5': '⁵',
  '6': '⁶',
  '7': '⁷',
  '8': '⁸',
  '9': '⁹',
};

/** Scientific notation with a proper superscript exponent: 1.2·10⁻⁸ (values ≥ 0.01 and < 1000 stay plain). */
export function pow10(value: number | null | undefined, digits = 1): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return '—';
  }
  if (value === 0) {
    return '0';
  }
  const abs = Math.abs(value);
  if (abs >= 0.01 && abs < 1000) {
    return value.toFixed(Math.max(digits, abs < 1 ? 3 : 2));
  }
  const exp = Math.floor(Math.log10(abs));
  const mantissa = value / Math.pow(10, exp);
  const sup = String(exp)
    .split('')
    .map((c) => SUPERSCRIPT[c] ?? c)
    .join('');
  return `${mantissa.toFixed(digits)}·10${sup}`;
}

/** Observed order p = log(e_coarse/e_fine) / log(h_coarse/h_fine). */
export function observedOrder(errorCoarse: number, errorFine: number, ratio = 2): number {
  return Math.log(errorCoarse / errorFine) / Math.log(ratio);
}

/** Least-squares slope of log(y) against log(x) — the empirical convergence rate of a whole refinement sequence. */
export function logLogSlope(points: { x: number; y: number }[]): number {
  const pts = points.filter((p) => p.x > 0 && p.y > 0);
  if (pts.length < 2) {
    return Number.NaN;
  }
  const lx = pts.map((p) => Math.log(p.x));
  const ly = pts.map((p) => Math.log(p.y));
  const mx = lx.reduce((a, b) => a + b, 0) / lx.length;
  const my = ly.reduce((a, b) => a + b, 0) / ly.length;
  let num = 0;
  let den = 0;
  for (let i = 0; i < lx.length; i++) {
    num += (lx[i] - mx) * (ly[i] - my);
    den += (lx[i] - mx) * (lx[i] - mx);
  }
  return num / den;
}

/** Reference line y = y0·(x/x0)^order through the first point (for dashed O(hᵖ) guides on log–log plots). */
export function referenceLine(xs: number[], y0: number, order: number): { x: number; y: number }[] {
  if (!xs.length) {
    return [];
  }
  const x0 = xs[0];
  return xs.map((x) => ({ x, y: y0 * Math.pow(x / x0, order) }));
}

/** Colour of a correlation coefficient r ∈ [−1, 1] on the diverging map (blue −1, gray 0, red +1). */
export function correlationColor(r: number): string {
  return cssColor('diverging', (Math.max(-1, Math.min(1, r)) + 1) / 2);
}

/** Unique undirected edges of a triangulation (for drawing the mesh once per edge). */
export function meshEdges(triangles: number[][]): [number, number][] {
  const seen = new Set<string>();
  const edges: [number, number][] = [];
  for (const t of triangles) {
    for (let k = 0; k < 3; k++) {
      const a = t[k];
      const b = t[(k + 1) % 3];
      const key = a < b ? `${a}-${b}` : `${b}-${a}`;
      if (!seen.has(key)) {
        seen.add(key);
        edges.push(a < b ? [a, b] : [b, a]);
      }
    }
  }
  return edges;
}

/** Groups items by a key, preserving first-seen order of the groups. */
export function groupBy<T>(items: T[], key: (item: T) => string): Map<string, T[]> {
  const map = new Map<string, T[]>();
  for (const item of items) {
    const k = key(item);
    const list = map.get(k);
    if (list) {
      list.push(item);
    } else {
      map.set(k, [item]);
    }
  }
  return map;
}

/** Ratio formatted as a speed-up, e.g. 4.2×. */
export function times(value: number | null | undefined, digits = 1): string {
  return value === null || value === undefined || !Number.isFinite(value) ? '—' : `${value.toFixed(digits)}×`;
}

/** Converts a JSON number that may have been serialised as the string "NaN" into a number or null. */
export function num(value: number | string | null | undefined): number | null {
  if (value === null || value === undefined) {
    return null;
  }
  const n = typeof value === 'string' ? Number(value) : value;
  return Number.isFinite(n) ? n : null;
}
