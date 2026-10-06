export const fmt = (value: number | null | undefined, digits = 2, suffix = ''): string =>
  value === null || value === undefined || Number.isNaN(value) ? '—' : `${value.toFixed(digits)}${suffix}`;

export const pct = (value: number | null | undefined, digits = 0): string =>
  value === null || value === undefined ? '—' : `${(value * 100).toFixed(digits)} %`;

export const mm = (metres: number | null | undefined, digits = 0): string =>
  metres === null || metres === undefined ? '—' : `${(metres * 1000).toFixed(digits)}`;

export const sci = (value: number | null | undefined, digits = 2): string =>
  value === null || value === undefined ? '—' : value.toExponential(digits);

export const clock = (seconds: number): string => {
  const m = Math.floor(seconds / 60);
  const s = Math.round(seconds % 60);
  return `${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
};

export const energy = (joules: number | null | undefined): string => {
  if (joules === null || joules === undefined) {
    return '—';
  }
  return joules >= 10000 ? `${(joules / 1000).toFixed(1)} kJ` : `${joules.toFixed(0)} J`;
};
