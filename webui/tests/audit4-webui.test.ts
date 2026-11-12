// Audit 4: the web UI's timers stop on a hidden tab or a download that doesn't move (M11), the segment map goes back when
// a download starts over (L21), the speed graph survives a browser clock that is ahead (L22), and the two fixes that
// live in the markup (M9, M10) stay in place.
import { afterEach, describe, expect, it, vi } from 'vitest';
import listScreenSource from '../src/screens/ListScreen.svelte?raw';
import { mergeSegments, segmentsMove } from '../src/lib/details';
import { anyActive, ServerClock, speedSample } from '../src/lib/list';
import { everyWhileVisible, type VisibilitySource } from '../src/lib/ticker';
import type { Job } from '../src/lib/types';

afterEach(() => vi.useRealTimers());

class FakePage implements VisibilitySource {
  visibilityState = 'visible';
  private listeners: (() => void)[] = [];
  addEventListener(_type: 'visibilitychange', listener: () => void): void {
    this.listeners.push(listener);
  }
  removeEventListener(_type: 'visibilitychange', listener: () => void): void {
    this.listeners = this.listeners.filter((l) => l !== listener);
  }
  set(state: 'visible' | 'hidden'): void {
    this.visibilityState = state;
    for (const listener of this.listeners) listener();
  }
  get listening(): number {
    return this.listeners.length;
  }
}

describe('everyWhileVisible (M11)', () => {
  it('runs while the tab shows, not while it is hidden, and once at once when it shows again', () => {
    vi.useFakeTimers();
    const page = new FakePage();
    const run = vi.fn();
    const stop = everyWhileVisible(1000, run, page);
    vi.advanceTimersByTime(3000);
    expect(run).toHaveBeenCalledTimes(3);
    page.set('hidden');
    vi.advanceTimersByTime(10_000);
    expect(run).toHaveBeenCalledTimes(3);
    page.set('visible');
    expect(run).toHaveBeenCalledTimes(4);
    vi.advanceTimersByTime(1000);
    expect(run).toHaveBeenCalledTimes(5);
    stop();
    vi.advanceTimersByTime(5000);
    expect(run).toHaveBeenCalledTimes(5);
    expect(page.listening).toBe(0);
  });

  it('starts nothing on a hidden tab', () => {
    vi.useFakeTimers();
    const page = new FakePage();
    page.visibilityState = 'hidden';
    const run = vi.fn();
    everyWhileVisible(1000, run, page);
    vi.advanceTimersByTime(5000);
    expect(run).not.toHaveBeenCalled();
  });
});

describe('segmentsMove (M11)', () => {
  it('is true only while a download transfers or is about to', () => {
    const job = (state: string) => ({ state, isTerminal: false, torrent: undefined }) as unknown as Job;
    for (const state of ['connecting', 'downloading', 'verifying', 'retryWaiting']) expect(segmentsMove(job(state))).toBe(true);
    for (const state of ['paused', 'queued', 'completed', 'failed', 'cancelled']) expect(segmentsMove(job(state))).toBe(false);
  });
});

describe('mergeSegments (L21)', () => {
  it('goes back when the download starts over', () => {
    const highWater = new Map<number, number>();
    const saved = [{ start: 0, end: 999, completed: 800, isComplete: false, fraction: 0.8 }];
    expect(mergeSegments(saved, [], { totalBytes: 1000, receivedBytes: 800 }, highWater)[0].completed).toBe(800);
    const restarted = [{ start: 0, end: 999, completed: 50, isComplete: false, fraction: 0.05 }];
    expect(mergeSegments(restarted, [], { totalBytes: 1000, receivedBytes: 50 }, highWater)[0].completed).toBe(50);
  });

  it('still never moves back between refreshes of the same run', () => {
    const highWater = new Map<number, number>();
    mergeSegments([{ start: 0, end: 999, completed: 600, isComplete: false, fraction: 0.6 }], [], { totalBytes: 1000, receivedBytes: 600 }, highWater);
    // A checkpoint that lags the live count, with the download's total still ahead.
    expect(mergeSegments([{ start: 0, end: 999, completed: 500, isComplete: false, fraction: 0.5 }], [], { totalBytes: 1000, receivedBytes: 650 }, highWater)[0].completed).toBe(600);
  });
});

describe('speedSample with the server clock (L22)', () => {
  const at = (iso: number) => new Date(iso).toISOString();
  const serverNow = Date.parse('2026-11-12T10:00:00Z');

  it('counts a download that is updating although this browser is a minute ahead', () => {
    const jobs = [{ id: 'a', state: 'downloading', bytesPerSecond: 100, updatedAt: at(serverNow) }] as unknown as Job[];
    const browserNow = serverNow + 60_000;
    expect(speedSample(jobs, true, true, browserNow)).toBe(0);
    const clock = new ServerClock();
    clock.observe(jobs, browserNow);
    expect(speedSample(jobs, true, true, browserNow, clock.offsetMs)).toBe(100);
  });

  it('still counts a download that went quiet as 0', () => {
    const clock = new ServerClock();
    const fresh = [{ id: 'a', state: 'downloading', bytesPerSecond: 100, updatedAt: at(serverNow) }] as unknown as Job[];
    clock.observe(fresh, serverNow + 60_000);
    expect(speedSample(fresh, true, true, serverNow + 60_000 + 11_000, clock.offsetMs)).toBe(0);
  });

  it('tells whether anything transfers', () => {
    expect(anyActive([{ state: 'paused' }, { state: 'completed' }] as unknown as Job[])).toBe(false);
    expect(anyActive([{ state: 'paused' }, { state: 'retryWaiting' }] as unknown as Job[])).toBe(true);
  });
});

const sources = import.meta.glob(['../src/**/*.ts', '../src/**/*.svelte'], { query: '?raw', import: 'default', eager: true }) as Record<string, string>;

describe('markup fixes', () => {
  it('makes ids with newUuid everywhere, which works over plain http from another computer too (M9)', () => {
    expect(Object.keys(sources).length).toBeGreaterThan(50);
    const callers = Object.entries(sources).filter(([file, text]) => !file.endsWith('/ids.ts') && /crypto\.randomUUID\s*\(/.test(text)).map(([file]) => file);
    expect(callers).toEqual([]);
  });

  it('makes a new details dialog for another download (M10)', () => {
    expect(listScreenSource).toMatch(/\{#key details\}\s*<JobDetails jobId=\{details\}/);
  });
});
