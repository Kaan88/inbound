// A touch long press on a list row opens its menu; the click that ends that press must not also act on the row. The
// swallow is kept short: any new pointer press (touch, mouse or pen) clears it, and so does a short wait after the
// finger lifts, so a press whose click never came can't eat a later mouse click.

export interface LongPressTimers {
  set(run: () => void, ms: number): unknown;
  clear(handle: unknown): void;
}

const browserTimers: LongPressTimers = {
  set: (run, ms) => setTimeout(run, ms),
  clear: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
};

export class LongPress {
  /** How long a finger stays down before the menu opens. */
  static readonly holdMs = 550;
  /** How long after the finger lifts the ending click may still come. */
  static readonly clickWindowMs = 400;
  /** How far the finger may move before it is a scroll, not a press. */
  static readonly slopPx = 8;

  private press: { timer: unknown; x: number; y: number } | null = null;
  private swallowing = false;
  private expiry: unknown = null;

  constructor(private readonly timers: LongPressTimers = browserTimers) {}

  /** Any pointer press. Only a touch starts a long press; every press ends an earlier swallow. */
  down(pointerType: string, x: number, y: number, fire: () => void): void {
    this.stopSwallowing();
    this.cancel();
    if (pointerType !== 'touch') return;
    this.press = {
      x,
      y,
      timer: this.timers.set(() => {
        this.press = null;
        this.swallowing = true;
        fire();
      }, LongPress.holdMs),
    };
  }

  move(x: number, y: number): void {
    if (this.press && Math.hypot(x - this.press.x, y - this.press.y) > LongPress.slopPx) this.cancel();
  }

  /** The finger lifted: a press not yet fired is dropped, and a fired one's click must come soon. */
  up(): void {
    this.cancel();
    if (!this.swallowing) return;
    if (this.expiry !== null) this.timers.clear(this.expiry);
    this.expiry = this.timers.set(() => {
      this.expiry = null;
      this.swallowing = false;
    }, LongPress.clickWindowMs);
  }

  /** The browser took the touch over (a scroll, say): no click follows. */
  cancelled(): void {
    this.cancel();
    this.stopSwallowing();
  }

  /** Called by the row's click: true when this click ends a long press and must do nothing else. */
  takeClick(): boolean {
    if (!this.swallowing) return false;
    this.stopSwallowing();
    return true;
  }

  private cancel(): void {
    if (this.press) this.timers.clear(this.press.timer);
    this.press = null;
  }

  private stopSwallowing(): void {
    this.swallowing = false;
    if (this.expiry !== null) this.timers.clear(this.expiry);
    this.expiry = null;
  }
}
