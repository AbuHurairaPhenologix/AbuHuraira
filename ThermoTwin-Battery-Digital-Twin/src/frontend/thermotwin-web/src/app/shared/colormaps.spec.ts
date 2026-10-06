import { describe, expect, it } from 'vitest';
import { colorAt } from './colormaps';

const luminance = ([r, g, b]: [number, number, number]) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

describe('colormaps', () => {
  it('inferno is lightness-monotonic (sequential, not rainbow)', () => {
    let previous = -1;
    for (let t = 0; t <= 1.0001; t += 0.05) {
      const l = luminance(colorAt('inferno', t));
      expect(l).toBeGreaterThan(previous);
      previous = l;
    }
  });

  it('diverging map has a neutral gray midpoint and clamps out-of-range input', () => {
    const [r, g, b] = colorAt('diverging', 0.5);
    expect(Math.abs(r - g)).toBeLessThan(5);
    expect(Math.abs(g - b)).toBeLessThan(5);
    expect(colorAt('diverging', -3)).toEqual(colorAt('diverging', 0));
    expect(colorAt('source', Number.NaN)).toEqual(colorAt('source', 0));
  });
});
