// What every dialog shares: where Tab goes at the ends, which dialog is on top, and the browser's Back button, which
// closes the top dialog instead of changing the view behind it (U-WU-7). Pure, so it is tested without a browser.

/**
 * Where Tab or Shift+Tab goes when it has to wrap. `at` is the focused element's place in `list` (-1 for the panel
 * itself, a control that is not in the list, or something outside the dialog). Null lets the browser move as usual.
 */
export function wrapTarget<T>(list: readonly T[], at: number, shift: boolean): T | null {
  if (list.length === 0) return null;
  if (shift) return at <= 0 ? list[list.length - 1] : null;
  return at < 0 || at === list.length - 1 ? list[0] : null;
}

/** The part of `window.history` that is used, so tests need no browser. */
export interface HistoryLike {
  readonly state: unknown;
  pushState(data: unknown, unused: string): void;
  back(): void;
}

interface Entry { id: number; close: () => void }

/**
 * The open dialogs, newest last. Each one adds a history entry with the same address, so Back takes that entry away
 * and closes the dialog, and the view behind it stays. A dialog closed some other way takes its own entry back out.
 * A Back of our own is still on its way while a new dialog opens (one dialog replacing another): the new one adds its
 * entry only after that Back has landed, or the Back would take the new entry instead.
 */
export class DialogStack {
  private open: Entry[] = [];
  private nextId = 1;
  private ownBacks = 0;
  private waiting: number[] = [];

  constructor(private readonly history: () => HistoryLike | null) {}

  /** A dialog opened; returns its id. */
  opened(close: () => void): number {
    const id = this.nextId++;
    this.open.push({ id, close });
    if (this.ownBacks > 0) this.waiting.push(id);
    else this.push(id);
    return id;
  }

  /** A dialog closed (its own button, Escape, a choice, or the screen behind it went away). */
  closed(id: number): void {
    this.open = this.open.filter((entry) => entry.id !== id);
    const queued = this.waiting.indexOf(id);
    if (queued >= 0) {
      this.waiting.splice(queued, 1);
      return;
    }
    const history = this.safe();
    if (history && isMarker(history.state, id)) {
      this.ownBacks++;
      try { history.back(); } catch { this.ownBacks--; }
    }
  }

  /** The browser went back (popstate). Ours: nothing to do. The user's: the top dialog closes. */
  popped(): void {
    if (this.ownBacks > 0) {
      this.ownBacks--;
      if (this.ownBacks === 0) {
        const waiting = this.waiting;
        this.waiting = [];
        for (const id of waiting) if (this.open.some((entry) => entry.id === id)) this.push(id);
      }
      return;
    }
    this.open[this.open.length - 1]?.close();
  }

  isTop(id: number): boolean {
    return this.open[this.open.length - 1]?.id === id;
  }

  get count(): number {
    return this.open.length;
  }

  private push(id: number) {
    try { this.safe()?.pushState({ inboundDialog: id }, ''); } catch { /* no history: Back simply isn't caught */ }
  }

  private safe(): HistoryLike | null {
    try { return this.history(); } catch { return null; }
  }
}

function isMarker(state: unknown, id: number): boolean {
  return !!state && typeof state === 'object' && (state as { inboundDialog?: unknown }).inboundDialog === id;
}
