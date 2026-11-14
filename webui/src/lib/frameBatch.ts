// When a Coalescer's batch is taken: once per animation frame while the page is visible. A hidden page gets no
// animation frames, so then a timer takes it (the page still has to see a finished download to say so), and the batch is
// taken at once when the page becomes visible again. With nothing pending no frame or timer is scheduled.

import { Coalescer, type Item } from './coalesce';

export interface FrameEnv {
  requestFrame: (callback: () => void) => number;
  cancelFrame: (handle: number) => void;
  setTimer: (callback: () => void, ms: number) => number;
  clearTimer: (handle: number) => void;
  isHidden: () => boolean;
}

export function browserFrameEnv(): FrameEnv {
  return {
    requestFrame: (callback) => requestAnimationFrame(() => callback()),
    cancelFrame: (handle) => cancelAnimationFrame(handle),
    setTimer: (callback, ms) => window.setTimeout(callback, ms),
    clearTimer: (handle) => window.clearTimeout(handle),
    isHidden: () => document.visibilityState === 'hidden',
  };
}

/** How long a hidden page waits between batches. Browsers hold background timers to about a second anyway. */
export const hiddenBatchMs = 1000;

export class FrameBatcher<V> {
  private frame: number | null = null;
  private timer: number | null = null;

  constructor(
    readonly events: Coalescer<V>,
    private readonly onBatch: (items: Item<V>[]) => void,
    private readonly env: FrameEnv = browserFrameEnv(),
  ) {}

  /** True while a frame or timer is waiting. */
  get scheduled(): boolean {
    return this.frame !== null || this.timer !== null;
  }

  /** Call after posting to the coalescer. Does nothing when a tick is already waiting or nothing is pending. */
  notify(): void {
    if (this.scheduled || !this.events.pending) return;
    if (this.env.isHidden()) this.timer = this.env.setTimer(() => this.run(), hiddenBatchMs);
    else this.frame = this.env.requestFrame(() => this.run());
  }

  /** The page became visible or hidden: a visible one's batch is taken now; a hidden one's waiting frame becomes a timer. */
  visibilityChanged(): void {
    if (this.env.isHidden()) {
      if (this.frame !== null) {
        this.env.cancelFrame(this.frame);
        this.frame = null;
        this.notify();
      }
      return;
    }
    this.flush();
  }

  /** Takes the batch now (for the user's own action, or before a message that must keep its place) and cancels the waiting tick. */
  flush(): void {
    this.cancel();
    this.deliver();
  }

  private run(): void {
    this.frame = null;
    this.timer = null;
    this.deliver();
  }

  private deliver(): void {
    const items = this.events.drain();
    if (items.length > 0) this.onBatch(items);
    // Something posted while the batch was applied has its own tick.
    this.notify();
  }

  private cancel(): void {
    if (this.frame !== null) this.env.cancelFrame(this.frame);
    if (this.timer !== null) this.env.clearTimer(this.timer);
    this.frame = null;
    this.timer = null;
  }
}
