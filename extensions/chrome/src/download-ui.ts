type DownloadsUi = { setUiOptions?: (options: { enabled: boolean }) => Promise<void> };

/** How long a browser-kept download stays visible after it completes or fails. */
export const SETTLED_GRACE_MS = 60_000;

/**
 * Chromium shows its download bubble the moment a download is created, before
 * onCreated can pause and hand it off. While automatic interception is active
 * the browser UI is hidden. It is shown again while any download the browser
 * keeps (not intercepted, rejected, bypassed, or resumed after a failed
 * handoff) is in progress, and for a grace period after it completes or fails
 * so its result (open, show in folder, retry) stays reachable.
 *
 * Calls are serialized: every change recomputes the desired state after the
 * previous setUiOptions call finished, so the final state always matches.
 */
export class DownloadUiController {
  private readonly browserOwned = new Set<number>();
  private readonly graceTimers = new Map<number, ReturnType<typeof setTimeout>>();
  private intercepting = false;
  private applied: boolean | undefined;
  private chain: Promise<void> = Promise.resolve();

  constructor(
    private readonly downloads: DownloadsUi,
    private readonly graceMs = SETTLED_GRACE_MS,
  ) {}

  setIntercepting(value: boolean): Promise<void> {
    this.intercepting = value;
    return this.apply();
  }

  /** The browser keeps this download; show its UI while it runs. */
  keepInBrowser(id: number): Promise<void> {
    this.clearGrace(id);
    this.browserOwned.add(id);
    return this.apply();
  }

  /** A kept download completed or failed: keep the UI for the grace period. */
  settled(id: number): Promise<void> {
    if (!this.browserOwned.has(id) || this.graceTimers.has(id)) return Promise.resolve();
    this.graceTimers.set(id, setTimeout(() => { void this.release(id); }, this.graceMs));
    return Promise.resolve();
  }

  /** The entry is gone (erased) or no longer needs the UI. */
  release(id: number): Promise<void> {
    this.clearGrace(id);
    if (!this.browserOwned.delete(id)) return Promise.resolve();
    return this.apply();
  }

  isKept(id: number): boolean { return this.browserOwned.has(id); }

  get uiEnabled(): boolean | undefined { return this.applied; }

  private clearGrace(id: number): void {
    const timer = this.graceTimers.get(id);
    if (timer !== undefined) { clearTimeout(timer); this.graceTimers.delete(id); }
  }

  private apply(): Promise<void> {
    this.chain = this.chain.then(async () => {
      const enabled = !this.intercepting || this.browserOwned.size > 0;
      if (enabled === this.applied || !this.downloads.setUiOptions) return;
      try {
        await this.downloads.setUiOptions({ enabled });
        this.applied = enabled;
      } catch {
        // Older browsers or a missing downloads.ui permission: keep default UI.
      }
    });
    return this.chain;
  }
}
