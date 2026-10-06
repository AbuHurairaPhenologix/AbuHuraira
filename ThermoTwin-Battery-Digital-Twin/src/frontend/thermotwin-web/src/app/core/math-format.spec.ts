import { describe, expect, it } from 'vitest';
import { correlationColor, groupBy, logLogSlope, meshEdges, num, observedOrder, pow10, referenceLine, times } from './math-format';

describe('math formatting helpers', () => {
  it('formats small and large numbers with superscript exponents', () => {
    expect(pow10(1.17e-8)).toBe('1.2·10⁻⁸');
    expect(pow10(2.5e5, 2)).toBe('2.50·10⁵');
    expect(pow10(0.25)).toBe('0.250');
    expect(pow10(null)).toBe('—');
    expect(pow10(Number.NaN)).toBe('—');
    expect(pow10(0)).toBe('0');
  });

  it('computes observed convergence orders', () => {
    expect(observedOrder(4e-2, 1e-2)).toBeCloseTo(2, 12);
    expect(observedOrder(1, 0.5, 2)).toBeCloseTo(1, 12);
  });

  it('fits the log–log slope of a refinement sequence', () => {
    const second = [0.01, 0.005, 0.0025].map((h) => ({ x: h, y: 3 * h * h }));
    expect(logLogSlope(second)).toBeCloseTo(2, 10);
    expect(Number.isNaN(logLogSlope([{ x: 1, y: 1 }]))).toBe(true);
  });

  it('builds reference lines through the first point', () => {
    const line = referenceLine([1, 2, 4], 8, -1);
    expect(line.map((p) => p.y)).toEqual([8, 4, 2]);
    expect(referenceLine([], 1, 2)).toEqual([]);
  });

  it('extracts each mesh edge exactly once', () => {
    // Two triangles sharing the diagonal 0–3 of a square: 5 unique edges.
    const edges = meshEdges([
      [0, 1, 3],
      [0, 3, 2],
    ]);
    expect(edges.length).toBe(5);
    expect(edges).toContainEqual([0, 3]);
  });

  it('maps correlations onto the diverging colormap and clamps', () => {
    expect(correlationColor(0)).toBe(correlationColor(0));
    expect(correlationColor(2)).toBe(correlationColor(1));
    expect(correlationColor(-1)).not.toBe(correlationColor(1));
  });

  it('groups, formats ratios and parses NaN strings', () => {
    const groups = groupBy([{ k: 'a', v: 1 }, { k: 'b', v: 2 }, { k: 'a', v: 3 }], (x) => x.k);
    expect([...groups.keys()]).toEqual(['a', 'b']);
    expect(groups.get('a')!.map((x) => x.v)).toEqual([1, 3]);
    expect(times(4.243)).toBe('4.2×');
    expect(num('NaN')).toBeNull();
    expect(num(3)).toBe(3);
  });
});
