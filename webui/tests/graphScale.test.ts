import { describe, expect, it } from 'vitest';
import { GraphScale } from '../src/lib/graphScale';

const KB = 1024;
const MB = 1024 * 1024;

describe('speed graph scale (UI.B23)', () => {
  it('grows at once', () => {
    const scale = new GraphScale();
    expect(scale.update(100 * KB, 0)).toBe(128 * KB);
    expect(scale.update(3 * MB, 1000)).toBe(4 * MB);
  });

  it('holds while the peak moves under the top', () => {
    const scale = new GraphScale();
    scale.update(3.5 * MB, 0);
    for (let i = 1; i <= 60; i++) expect(scale.update(i % 2 ? 3.6 * MB : 2.5 * MB, i * 1000)).toBe(4 * MB);
  });

  it('shrinks after a hold, one step at a time', () => {
    const scale = new GraphScale();
    scale.update(3.5 * MB, 0);
    const tops: number[] = [];
    for (let i = 1; i <= 20; i++) tops.push(scale.update(100 * KB, i * 1000));
    expect(tops.slice(0, 5).every((top) => top === 4 * MB)).toBe(true);
    expect(tops[5]).toBe(2 * MB);
    for (let i = 1; i < tops.length; i++) expect([tops[i - 1], tops[i - 1] / 2]).toContain(tops[i]);
    expect(tops[tops.length - 1]).toBeLessThan(2 * MB);
  });

  it('fits at once on reset', () => {
    const scale = new GraphScale();
    scale.update(3.5 * MB, 0);
    expect(scale.reset(100 * KB)).toBe(128 * KB);
  });
});
