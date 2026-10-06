export type ColormapName = 'inferno' | 'source' | 'diverging';

type Rgb = [number, number, number];

// Perceptually uniform, lightness-monotonic ramps (no rainbow).
const STOPS: Record<ColormapName, Rgb[]> = {
  // Inferno (matplotlib), 9 samples — thermal-imaging convention for temperature.
  inferno: [
    [0, 0, 4],
    [31, 12, 72],
    [85, 15, 109],
    [136, 34, 106],
    [186, 54, 85],
    [227, 89, 51],
    [249, 140, 10],
    [249, 201, 50],
    [252, 255, 164],
  ],
  // Single-hue orange ramp for heat generation (W/m³): dark = no heat, bright = strong source.
  source: [
    [26, 26, 25],
    [62, 38, 28],
    [109, 49, 26],
    [163, 62, 27],
    [217, 89, 38],
    [240, 142, 92],
    [250, 196, 160],
    [255, 236, 220],
  ],
  // Diverging blue ↔ red with a neutral gray midpoint for signed errors.
  diverging: [
    [16, 66, 129],
    [42, 120, 214],
    [134, 182, 239],
    [56, 56, 53],
    [236, 131, 90],
    [227, 73, 72],
    [150, 30, 30],
  ],
};

export function colorAt(name: ColormapName, t: number): Rgb {
  const stops = STOPS[name];
  const x = Math.min(1, Math.max(0, Number.isFinite(t) ? t : 0)) * (stops.length - 1);
  const i = Math.min(stops.length - 2, Math.floor(x));
  const f = x - i;
  const a = stops[i];
  const b = stops[i + 1];
  return [a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f];
}

export function cssColor(name: ColormapName, t: number): string {
  const [r, g, b] = colorAt(name, t);
  return `rgb(${r.toFixed(0)}, ${g.toFixed(0)}, ${b.toFixed(0)})`;
}
