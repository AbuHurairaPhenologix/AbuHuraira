import { describe, expect, it } from 'vitest';
import { clock, energy, fmt, mm, pct } from './format';

describe('format helpers', () => {
  it('formats numbers and handles missing values', () => {
    expect(fmt(44.876, 2)).toBe('44.88');
    expect(fmt(null)).toBe('—');
    expect(pct(0.244, 1)).toBe('24.4 %');
    expect(mm(0.138)).toBe('138');
  });

  it('formats simulation clock and energy', () => {
    expect(clock(1210)).toBe('20:10');
    expect(energy(911)).toBe('911 J');
    expect(energy(270000)).toBe('270.0 kJ');
  });
});
