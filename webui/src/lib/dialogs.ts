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

/**
 * UI.R36: where an arrow key moves the focus among a dialog's buttons (its check boxes first, then the footer's buttons,
 * in reading order). Left and Up go back, Right and Down go on; at either end, or when the focused control is not one
 * of them (`at` -1), null leaves the key alone. Cancel stays the default; this only lets the keys reach the others.
 */
export function arrowTarget<T>(list: readonly T[], at: number, key: string, rightToLeft = false): T | null {
  if (list.length < 2 || at < 0 || at >= list.length) return null;
  let back: boolean;
  if (key === 'ArrowLeft') back = !rightToLeft;
  else if (key === 'ArrowRight') back = rightToLeft;
  else if (key === 'ArrowUp') back = true;
  else if (key === 'ArrowDown') back = false;
  else return null;
  const next = back ? at - 1 : at + 1;
  return next < 0 || next >= list.length ? null : list[next];
}

/** The part of `window.history` that is used, so tests need no browser. */
export interface HistoryLike {
  readonly state: unknown;
  pushState(data: unknown, unused: string): void;
  back(): void;
}

/** A dialog, and the address its history entry has (the address when it opened). */
interface Entry { id: number; close: () => void; href: string }

/**
 * The open dialogs, newest last. Each one adds a history entry with the same address, so Back takes that entry away
 * and closes the dialog, and the view behind it stays. A dialog closed some other way takes its own entry back out.
 * A Back of our own is still on its way while a new dialog opens (one dialog replacing another): the new one adds its
 * entry only after that Back has landed, or the Back would take the new entry instead.
 *
 * A #fragment navigation (a link, a typed address) fires popstate too. It is told from a Back by where the page lands:
 * a Back out of a dialog's entry lands on the same address, a navigation on another one, and a Back or Forward onto the
 * top dialog's own entry is no Back out of it. Those close nothing.
 */
export class DialogStack {
  private open: Entry[] = [];
  private nextId = 1;
  private ownBacks = 0;
  private waiting: number[] = [];

  constructor(
    private readonly history: () => HistoryLike | null,
    /** The page's address now (`location.href`). */
    private readonly here: () => string = () => '',
  ) {}

  /** A dialog opened; returns its id. */
  opened(close: () => void): number {
    const id = this.nextId++;
    this.open.push({ id, close, href: this.address() });
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
    const top = this.open[this.open.length - 1];
    if (!top) return;
    const state = this.safe()?.state;
    if (isMarker(state, top.id)) return;
    if (this.address() !== top.href) return;
    top.close();
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

  private address(): string {
    try { return this.here(); } catch { return ''; }
  }

  private safe(): HistoryLike | null {
    try { return this.history(); } catch { return null; }
  }
}

function isMarker(state: unknown, id: number): boolean {
  return !!state && typeof state === 'object' && (state as { inboundDialog?: unknown }).inboundDialog === id;
}
