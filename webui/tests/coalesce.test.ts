import { describe, expect, it } from 'vitest';
import { Coalescer } from '../src/lib/coalesce';
import { FrameBatcher, hiddenBatchMs, type FrameEnv } from '../src/lib/frameBatch';

const flat = <V>(items: { op: string; key: string; value: V }[]) => items.map((item) => [item.op, item.key, item.value]);

describe('Coalescer (PERF.16)', () => {
  it('keeps the latest state per key', () => {
    const c = new Coalescer<number>();
    c.upsert('a', 1);
    c.upsert('a', 2);
    c.upsert('a', 3);
    expect(flat(c.drain())).toEqual([['upsert', 'a', 3]]);
  });

  it('an add then a change is one upsert with the latest state', () => {
    const c = new Coalescer<string>();
    c.upsert('a', 'added');
    c.upsert('a', 'progress');
    expect(flat(c.drain())).toEqual([['upsert', 'a', 'progress']]);
  });

  it('a remove after a change wins', () => {
    const c = new Coalescer<number>();
    c.upsert('a', 1);
    c.remove('a', 2);
    expect(flat(c.drain())).toEqual([['remove', 'a', 2]]);
  });

  it('a change after a remove is the remove and then the upsert; a second remove leaves only the remove', () => {
    const c = new Coalescer<number>();
    c.remove('a', 1);
    c.upsert('a', 2);
    expect(flat(c.drain())).toEqual([['remove', 'a', 1], ['upsert', 'a', 2]]);
    c.remove('a', 1);
    c.upsert('a', 2);
    c.remove('a', 3);
    expect(flat(c.drain())).toEqual([['remove', 'a', 3]]);
  });

  it('puts items in the order of their newest event', () => {
    const c = new Coalescer<number>();
    c.upsert('a', 1);
    c.upsert('b', 1);
    c.upsert('a', 2);
    c.remove('c', 0);
    expect(c.drain().map((item) => item.key)).toEqual(['b', 'a', 'c']);
  });

  it('merge can keep what the older event carried', () => {
    const c = new Coalescer<{ kind: string; n: number }>((older, newer) => ({ kind: older.kind === 'completed' ? 'completed' : newer.kind, n: newer.n }));
    c.upsert('a', { kind: 'completed', n: 1 });
    c.upsert('a', { kind: 'progress', n: 2 });
    expect(c.drain()).toEqual([{ op: 'upsert', key: 'a', value: { kind: 'completed', n: 2 } }]);
  });

  it('a reset replaces what is pending for the keys it covers and leaves the rest', () => {
    const c = new Coalescer<number>();
    c.upsert('job:a', 1);
    c.remove('job:b', 1);
    c.upsert('site:x', 1);
    c.reset('snapshot', 9, (key) => key.startsWith('job:') || key === 'snapshot');
    c.upsert('job:a', 2);
    expect(flat(c.drain())).toEqual([['upsert', 'site:x', 1], ['reset', 'snapshot', 9], ['upsert', 'job:a', 2]]);
  });

  it('a later reset with the same key replaces the earlier one', () => {
    const c = new Coalescer<number>();
    c.reset('snapshot', 1);
    c.reset('snapshot', 2);
    expect(flat(c.drain())).toEqual([['reset', 'snapshot', 2]]);
  });

  it('a barrier keeps its place and nothing merges across it; a reset does not drop it', () => {
    const c = new Coalescer<number>();
    c.upsert('a', 1);
    c.barrier('conn', 0);
    c.upsert('a', 2);
    expect(flat(c.drain())).toEqual([['upsert', 'a', 1], ['barrier', 'conn', 0], ['upsert', 'a', 2]]);
    c.upsert('a', 1);
    c.barrier('conn', 0);
    c.reset('snapshot', 9);
    expect(flat(c.drain())).toEqual([['barrier', 'conn', 0], ['reset', 'snapshot', 9]]);
  });

  it('an empty drain returns nothing; memory is bounded by the distinct keys', () => {
    const c = new Coalescer<number>();
    expect(c.drain()).toEqual([]);
    expect(c.pending).toBe(false);
    for (let i = 0; i < 10_000; i++) c.upsert(`job:${i % 3}`, i);
    expect(c.size).toBe(3);
  });

  it('clear drops everything', () => {
    const c = new Coalescer<number>();
    c.upsert('a', 1);
    c.barrier('b', 1);
    c.clear();
    expect(c.pending).toBe(false);
  });
});

class FakeEnv implements FrameEnv {
  hidden = false;
  frames = new Map<number, () => void>();
  timers = new Map<number, { callback: () => void; ms: number }>();
  private next = 1;
  requestFrame = (callback: () => void) => {
    const id = this.next++;
    this.frames.set(id, callback);
    return id;
  };
  cancelFrame = (id: number) => void this.frames.delete(id);
  setTimer = (callback: () => void, ms: number) => {
    const id = this.next++;
    this.timers.set(id, { callback, ms });
    return id;
  };
  clearTimer = (id: number) => void this.timers.delete(id);
  isHidden = () => this.hidden;
  fireFrame() {
    const [id, callback] = [...this.frames][0];
    this.frames.delete(id);
    callback();
  }
  fireTimer() {
    const [id, { callback }] = [...this.timers][0];
    this.timers.delete(id);
    callback();
  }
  get waiting() {
    return this.frames.size + this.timers.size;
  }
}

function make() {
  const env = new FakeEnv();
  const batches: { op: string; key: string; value: number }[][] = [];
  const events = new Coalescer<number>();
  const batcher = new FrameBatcher<number>(events, (items) => batches.push(items), env);
  const post = (key: string, value: number) => {
    events.upsert(key, value);
    batcher.notify();
  };
  return { env, batches, events, batcher, post };
}

describe('FrameBatcher', () => {
  it('asks for one frame for a burst and applies it once', () => {
    const { env, batches, post } = make();
    for (let i = 0; i < 20; i++) post('job:a', i);
    expect(env.frames.size).toBe(1);
    env.fireFrame();
    expect(batches).toEqual([[{ op: 'upsert', key: 'job:a', value: 19 }]]);
  });

  it('leaves nothing scheduled when idle, and asks again for the next event', () => {
    const { env, batches, post } = make();
    expect(env.waiting).toBe(0);
    post('job:a', 1);
    env.fireFrame();
    expect(env.waiting).toBe(0);
    post('job:a', 2);
    expect(env.frames.size).toBe(1);
    env.fireFrame();
    expect(batches.length).toBe(2);
    expect(env.waiting).toBe(0);
  });

  it('a hidden page gets a timer, not a frame, and keeps only the latest per job', () => {
    const { env, batches, events, post } = make();
    env.hidden = true;
    for (let i = 0; i < 5000; i++) post(`job:${i % 4}`, i);
    expect(env.frames.size).toBe(0);
    expect(env.timers.size).toBe(1);
    expect([...env.timers.values()][0].ms).toBe(hiddenBatchMs);
    expect(events.size).toBe(4);
    env.fireTimer();
    expect(batches[0].length).toBe(4);
    expect(env.waiting).toBe(0);
  });

  it('applies what waited when the page becomes visible again, and drops the timer', () => {
    const { env, batches, batcher, post } = make();
    env.hidden = true;
    post('job:a', 1);
    env.hidden = false;
    batcher.visibilityChanged();
    expect(batches).toEqual([[{ op: 'upsert', key: 'job:a', value: 1 }]]);
    expect(env.waiting).toBe(0);
  });

  it('a page that hides while a frame waits gets a timer instead', () => {
    const { env, batches, batcher, post } = make();
    post('job:a', 1);
    env.hidden = true;
    batcher.visibilityChanged();
    expect(env.frames.size).toBe(0);
    expect(env.timers.size).toBe(1);
    env.fireTimer();
    expect(batches.length).toBe(1);
  });

  it('flush takes the batch at once and cancels the waiting tick; flushing nothing does nothing', () => {
    const { env, batches, batcher, post } = make();
    batcher.flush();
    expect(batches).toEqual([]);
    post('job:a', 1);
    batcher.flush();
    expect(batches.length).toBe(1);
    expect(env.waiting).toBe(0);
  });

  it('an event posted while a batch is applied is scheduled for the next tick', () => {
    const env = new FakeEnv();
    const events = new Coalescer<number>();
    const seen: number[] = [];
    const batcher: FrameBatcher<number> = new FrameBatcher<number>(
      events,
      (items) => {
        seen.push(items[0].value);
        if (items[0].value === 1) {
          events.upsert('job:a', 2);
          batcher.notify();
        }
      },
      env,
    );
    events.upsert('job:a', 1);
    batcher.notify();
    env.fireFrame();
    expect(env.frames.size).toBe(1);
    env.fireFrame();
    expect(seen).toEqual([1, 2]);
    expect(env.waiting).toBe(0);
  });
});
