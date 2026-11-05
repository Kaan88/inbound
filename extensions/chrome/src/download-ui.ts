type DownloadsUi = { setUiOptions?: (options: { enabled: boolean }) => Promise<void> };

/**
 * Chromium shows its download bubble the moment a download is created, before
 * onCreated can pause and hand it off. While automatic interception is on the
 * browser UI is hidden; it is re-enabled for as long as any download that stays
 * in the browser (not intercepted, rejected, or resumed after a failed handoff)
 * is still in progress, so those remain visible.
 */
export class DownloadUiController {
  private readonly browserOwned = new Set<number>();
  private intercepting = false;
  private applied: boolean | undefined;

  constructor(private readonly downloads: DownloadsUi) {}

  setIntercepting(value: boolean): Promise<void> {
    this.intercepting = value;
    return this.apply();
  }

  /** The browser keeps this download; show its UI until it settles. */
  keepInBrowser(id: number): Promise<void> {
    this.browserOwned.add(id);
    return this.apply();
  }

  settled(id: number): Promise<void> {
    if (!this.browserOwned.delete(id)) return Promise.resolve();
    return this.apply();
  }

  get uiEnabled(): boolean | undefined { return this.applied; }

  private async apply(): Promise<void> {
    const enabled = !this.intercepting || this.browserOwned.size > 0;
    if (enabled === this.applied || !this.downloads.setUiOptions) return;
    try {
      await this.downloads.setUiOptions({ enabled });
      this.applied = enabled;
    } catch {
      // Older browsers or a missing downloads.ui permission: keep default UI.
    }
  }
}
