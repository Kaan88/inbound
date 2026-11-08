import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { tickUntil } from '../src/lib/ticker';

describe('tickUntil (countdowns that only wake the page while they count)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-11-08T12:00:00Z'));
  });
  afterEach(() => vi.useRealTimers());

  it('starts no timer when there is nothing to count down', () => {
    const tick = vi.fn();
    expect(tickUntil(0, 500, tick)).toBeUndefined();
    expect(tickUntil(Date.now(), 500, tick)).toBeUndefined();
    expect(tick).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('ticks until the end has passed, then stops by itself', () => {
    const seen: number[] = [];
    const start = Date.now();
    tickUntil(start + 2000, 500, (now) => seen.push(now - start));
    expect(seen).toEqual([0]);
    expect(vi.getTimerCount()).toBe(1);
    vi.advanceTimersByTime(2000);
    expect(seen).toEqual([0, 500, 1000, 1500, 2000]);
    // The last tick is at the end, so a countdown shows 0; after it nothing runs.
    expect(vi.getTimerCount()).toBe(0);
    vi.advanceTimersByTime(10_000);
    expect(seen.length).toBe(5);
  });

  it('stops early when its effect is cleaned up', () => {
    const tick = vi.fn();
    const stop = tickUntil(Date.now() + 60_000, 1000, tick);
    vi.advanceTimersByTime(3000);
    stop?.();
    expect(vi.getTimerCount()).toBe(0);
    const calls = tick.mock.calls.length;
    vi.advanceTimersByTime(10_000);
    expect(tick.mock.calls.length).toBe(calls);
  });
});
