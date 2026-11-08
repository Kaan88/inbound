/**
 * EXT.B2: one state model for a window with a Send button and a Browse (Choose...) button.
 *
 * The folder dialog belongs to the app, not this window, so a send while it is open would go to the
 * folder still shown and lose the folder chosen afterwards. Sending and picking therefore exclude each
 * other: Send is allowed only when neither runs, Browse only when neither runs. Each path sets and
 * clears only its own flag, in finally, and the page redraws its buttons from the flags afterwards.
 */
export class FormGate {
  private pickingNow = false;
  private sendingNow = false;

  constructor(private readonly render: () => void) {}

  get picking(): boolean { return this.pickingNow; }
  get sending(): boolean { return this.sendingNow; }
  /** Send may start (the page adds its own conditions, such as a loaded request or a selection). */
  get canSend(): boolean { return !this.pickingNow && !this.sendingNow; }
  /** Browse may start. */
  get canPick(): boolean { return !this.pickingNow && !this.sendingNow; }

  /** Runs a folder pick unless one is open or a send is in progress. Returns whether it ran. */
  async pick(run: () => Promise<void>): Promise<boolean> {
    if (!this.canPick) return false;
    this.pickingNow = true;
    this.render();
    try {
      await run();
    } finally {
      this.pickingNow = false;
      this.render();
    }
    return true;
  }

  /** Runs a send unless a folder pick is open or another send is in progress. Returns whether it ran. */
  async send(run: () => Promise<void>): Promise<boolean> {
    if (!this.canSend) return false;
    this.sendingNow = true;
    this.render();
    try {
      await run();
    } finally {
      this.sendingNow = false;
      this.render();
    }
    return true;
  }
}
