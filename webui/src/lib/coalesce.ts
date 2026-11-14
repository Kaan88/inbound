// Gathers events and hands them over as one batch (PERF.16, PERF.19), the web UI's counterpart of the window's
// EventCoalescer. Pure: no timers, no DOM. frameBatch.ts decides when a batch is taken.
//
// Per key: the newest upsert wins (merge() may keep more of the older one, such as a "completed" kind); a remove after
// an upsert wins; an upsert after a remove is kept as the remove and then the upsert; a reset drops what is pending for
// the keys it covers; a barrier keeps its place and nothing merges across it. Items come out in the order of their
// newest event. Memory is bounded by the number of distinct keys, however long nothing takes a batch.
//
// A new event family joins by choosing keys that include the family (`job:<id>`, `site:<id>`), posting with upsert and
// remove from the message handler, and applying the items in the one place that takes the batch.

export type Op = 'upsert' | 'remove' | 'reset' | 'barrier';

export interface Item<V> {
  op: Op;
  key: string;
  value: V;
}

interface Entry<V> extends Item<V> {
  seq: number;
  removedBefore?: { value: V; seq: number };
}

export class Coalescer<V> {
  private current = new Map<string, Entry<V>>();
  private sealed: Entry<V>[] = [];
  private seq = 0;

  constructor(private readonly merge: (older: V, newer: V) => V = (_older, newer) => newer) {}

  get size(): number {
    return this.current.size + this.sealed.length;
  }

  get pending(): boolean {
    return this.size > 0;
  }

  upsert(key: string, value: V): void {
    const entry = this.current.get(key);
    if (!entry) {
      this.current.set(key, { op: 'upsert', key, value, seq: ++this.seq });
      return;
    }
    if (entry.op === 'remove') {
      entry.removedBefore = { value: entry.value, seq: entry.seq };
      entry.value = value;
    } else {
      entry.value = entry.op === 'upsert' ? this.merge(entry.value, value) : value;
    }
    entry.op = 'upsert';
    entry.seq = ++this.seq;
  }

  remove(key: string, value: V): void {
    this.current.set(key, { op: 'remove', key, value, seq: ++this.seq });
  }

  /** A whole-state replacement under `key`. What is pending for keys `covers` accepts (default: all) is dropped, barriers excepted. */
  reset(key: string, value: V, covers: (key: string) => boolean = () => true): void {
    for (const pending of [...this.current.keys()]) if (covers(pending)) this.current.delete(pending);
    this.sealed = this.sealed.filter((entry) => entry.op === 'barrier' || !covers(entry.key));
    this.current.set(key, { op: 'reset', key, value, seq: ++this.seq });
  }

  /** An event that keeps its place: what came before it is sealed, so nothing posted later merges into it. */
  barrier(key: string, value: V): void {
    this.sealed.push(...this.current.values());
    this.current.clear();
    this.sealed.push({ op: 'barrier', key, value, seq: ++this.seq });
  }

  /** Takes everything pending, oldest event first. */
  drain(): Item<V>[] {
    if (this.size === 0) return [];
    const taken = [...this.sealed, ...this.current.values()];
    this.sealed = [];
    this.current = new Map();
    const out: { seq: number; item: Item<V> }[] = [];
    for (const entry of taken) {
      if (entry.removedBefore) out.push({ seq: entry.removedBefore.seq, item: { op: 'remove', key: entry.key, value: entry.removedBefore.value } });
      out.push({ seq: entry.seq, item: { op: entry.op, key: entry.key, value: entry.value } });
    }
    out.sort((a, b) => a.seq - b.seq);
    return out.map((pair) => pair.item);
  }

  clear(): void {
    this.current.clear();
    this.sealed = [];
  }
}
