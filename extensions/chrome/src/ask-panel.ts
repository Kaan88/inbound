// EXT.R7: "ask where to save" in the toolbar popup, anchored to the extension's icon like the browser's own
// download bubble. The background keeps the queue of caught downloads waiting for a folder (dm.askQueue) and
// opens the popup for them; this panel shows one, with the save form of the folder window (ui/save-form.ts):
// Send to This PC or the server, the folder, favorites, then Start download, Download later or Keep in browser.
// Closing the popup without a choice leaves what still waits to the browser (the background sees the port close).
//
// EXT.B7: every folder shown here comes from the place the download goes: the server's default folder, favorites,
// folder list and free space when the server is chosen, the desktop app's otherwise.
// EXT.R8: the layout is "Download file info" of the desktop app; ‹ › step through what waits.

import { bytes } from "../../../webui/src/lib/format";
import { FormGate } from "./form-gate";
import { type OfferedTarget } from "./destination-target";
import { fileIcon } from "./ui/icon";
import { firstTarget, markDefaultStart, SaveForm } from "./ui/save-form";
import { refusalText, type TargetId } from "./transport";

interface AskItem {
  id: string;
  fileName: string;
  url: string;
  totalBytes?: number;
}

interface HandoffAnswer {
  ok?: boolean;
  error?: string;
  defaults?: { destination?: string; favorites?: Array<{ name: string; path: string }>; startMode?: string };
  targets?: OfferedTarget[];
  target?: TargetId;
}

function byId<T extends HTMLElement>(id: string): T {
  return document.getElementById(id) as T;
}

function hostOf(url: string): string {
  try { return new URL(url).host; } catch { return ""; }
}

export class AskPanel {
  private readonly card = byId<HTMLElement>("askCard");
  private readonly count = byId<HTMLSpanElement>("askCount");
  private readonly source = byId<HTMLParagraphElement>("askSource");
  private readonly title = byId<HTMLElement>("askFileTitle");
  private readonly fileGlyph = byId<HTMLElement>("askFileIcon");
  private readonly nameInput = byId<HTMLInputElement>("askFileName");
  private readonly destinationInput = byId<HTMLInputElement>("askDestination");
  private readonly browseButton = byId<HTMLButtonElement>("askBrowse");
  private readonly confirmButton = byId<HTMLButtonElement>("askConfirm");
  private readonly laterButton = byId<HTMLButtonElement>("askLater");
  private readonly keepButton = byId<HTMLButtonElement>("askKeep");
  private readonly closeButton = byId<HTMLButtonElement>("askClose");
  private readonly prevButton = byId<HTMLButtonElement>("askPrev");
  private readonly nextButton = byId<HTMLButtonElement>("askNext");
  private readonly errorBox = byId<HTMLParagraphElement>("askError");
  private readonly others = byId<HTMLParagraphElement>("askOthers");
  private readonly form: SaveForm;
  // EXT.B2: Download and Choose... exclude each other, as in the folder window.
  private readonly gate = new FormGate(() => this.renderButtons());
  private currentId = "";
  private items: AskItem[] = [];
  // The current request has not loaded: its target and folder are not known yet, so Start and Browse wait.
  private loading = true;
  // The current request could not be loaded (expired): only Keep in browser is left.
  private unavailable = false;
  // Options > Default start: which of the two buttons is the primary one and Enter's.
  private defaultStart: "now" | "later" = "now";
  private refreshing: Promise<void> = Promise.resolve();

  constructor(private readonly onWaitingChange: (waiting: boolean) => void) {
    this.form = new SaveForm({
      targetField: byId("askTargetField"),
      targetChoice: byId("askTargetChoice"),
      destination: this.destinationInput,
      browse: this.browseButton,
      favoritesField: byId("askFavoritesField"),
      favorites: byId<HTMLSelectElement>("askFavorites"),
      freeSpace: byId("askFreeSpace"),
      serverFolders: byId("askServerFolders"),
      localHint: byId("askLocalHint"),
      serverHint: byId("askServerHint"),
    }, (message) => this.showError(message), (message) => this.clearError(message));
    this.confirmButton.addEventListener("click", () => this.confirm("now"));
    this.laterButton.addEventListener("click", () => this.confirm("later"));
    this.keepButton.addEventListener("click", () => this.keep());
    this.closeButton.addEventListener("click", () => this.keep());
    this.browseButton.addEventListener("click", () => this.browse());
    this.prevButton.addEventListener("click", () => this.step(-1));
    this.nextButton.addEventListener("click", () => this.step(1));
    for (const input of [this.nameInput, this.destinationInput]) {
      input.addEventListener("keydown", (event) => {
        if ((event as KeyboardEvent).key === "Enter") void this.confirm(this.defaultStart);
      });
    }
    document.addEventListener("keydown", (event) => {
      if (event.key === "Escape" && this.waiting && !this.form.serverFolders.isOpen) void this.keep();
    });
    this.renderButtons();
  }

  /** Whether a download is waiting here. */
  get waiting(): boolean {
    return this.currentId !== "";
  }

  /** Reads the queue again (on open, and whenever the background says it changed). Calls never overlap. */
  refresh(): Promise<void> {
    this.refreshing = this.refreshing.then(() => this.load(), () => this.load());
    return this.refreshing;
  }

  private async load(): Promise<void> {
    let items: AskItem[] = [];
    try {
      const answer = await chrome.runtime.sendMessage({ type: "dm.askQueue" }) as { ok?: boolean; items?: AskItem[] };
      items = answer?.ok && Array.isArray(answer.items) ? answer.items : [];
    } catch { /* the worker may be restarting; the port's reconnect asks again */ }
    this.items = items;

    if (items.length === 0) {
      const was = this.waiting;
      this.currentId = "";
      this.form.serverFolders.close();
      this.card.classList.add("hidden");
      if (was) this.onWaitingChange(false);
      return;
    }

    const current = items.find((item) => item.id === this.currentId);
    const shown = current ?? items[0]!;
    this.renderQueue(shown);
    this.card.classList.remove("hidden");
    if (!this.waiting) {
      this.currentId = shown.id;
      this.onWaitingChange(true);
    }
    // A send in progress keeps its download on screen; the next one comes when it is answered.
    if (current || this.gate.sending) return;
    await this.show(shown);
  }

  private renderQueue(shown: AskItem): void {
    const items = this.items;
    this.count.textContent = items.length > 1 ? `${items.indexOf(shown) + 1} of ${items.length}` : "";
    const rest = items.filter((item) => item.id !== shown.id).map((item) => item.fileName || hostOf(item.url) || "download");
    this.others.textContent = rest.length ? `Also waiting: ${rest.join(", ")}` : "";
    this.others.classList.toggle("hidden", rest.length === 0);
    this.prevButton.classList.toggle("hidden", items.length < 2);
    this.nextButton.classList.toggle("hidden", items.length < 2);
  }

  /** ‹ and ›: another waiting download, unless a send or a folder pick is running. */
  private async step(by: number): Promise<void> {
    if (!this.gate.canSend || this.items.length < 2) return;
    const at = this.items.findIndex((item) => item.id === this.currentId);
    const next = this.items[(at + by + this.items.length) % this.items.length]!;
    this.renderQueue(next);
    await this.show(next);
  }

  private async show(item: AskItem): Promise<void> {
    this.currentId = item.id;
    this.loading = true;
    this.unavailable = false;
    this.renderButtons();
    this.clearError("");
    this.form.serverFolders.close();
    this.nameInput.value = item.fileName;
    this.title.textContent = item.fileName || hostOf(item.url) || "Download";
    this.title.title = this.title.textContent;
    try { this.fileGlyph.replaceChildren(fileIcon(item.fileName || "", 24)); } catch { /* a page without SVG */ }
    const size = typeof item.totalBytes === "number" && item.totalBytes > 0 ? bytes(item.totalBytes) : "";
    this.source.textContent = [size, hostOf(item.url)].filter(Boolean).join(" · ");
    this.source.title = item.url;

    let answer: HandoffAnswer | undefined;
    try {
      answer = await chrome.runtime.sendMessage({ type: "dm.getHandoff", id: item.id }) as HandoffAnswer;
    } catch (error) {
      answer = { ok: false, error: error instanceof Error ? error.message : String(error) };
    }
    if (this.currentId !== item.id) return;
    if (!answer?.ok) {
      this.unavailable = true;
      this.loading = false;
      this.showError(answer?.error || "This download request has expired.");
      this.renderButtons();
      this.focusStart(this.keepButton);
      return;
    }

    this.defaultStart = markDefaultStart(answer.defaults?.startMode, this.confirmButton, this.laterButton);
    const targets = Array.isArray(answer.targets) && answer.targets.length > 0
      ? answer.targets
      : [{ id: "local" as TargetId, label: "This PC", chooser: "dialog" as const, defaultFolder: answer.defaults?.destination ?? "",
        favorites: answer.defaults?.favorites ?? [] }];
    this.form.showTargets(targets, firstTarget(targets, answer.target));
    this.loading = false;
    this.renderButtons();
    this.focusStart(this.defaultStart === "later" ? this.laterButton : this.confirmButton);
  }

  /**
   * Decision 1: Enter presses the primary button. The popup opens with nothing focused, so the primary takes the focus
   * once the download is loaded, unless the user is already in the form (typing a name, on ‹ ›).
   */
  private focusStart(button: HTMLButtonElement): void {
    const active = document.activeElement as HTMLElement | null;
    const busy = active && active !== document.body && this.card.contains(active) && !(active as HTMLButtonElement).disabled;
    if (busy || button.disabled) return;
    try { button.focus(); } catch { /* not shown */ }
  }

  private renderButtons(): void {
    const ready = !this.loading && !this.unavailable;
    this.confirmButton.disabled = !ready || !this.gate.canSend;
    this.laterButton.disabled = !ready || !this.gate.canSend;
    // Spec §3.4: the primary reads "Sending…", whichever of the two it is.
    const sending = this.gate.sending ? "Sending…" : "";
    this.confirmButton.textContent = sending && this.defaultStart === "now" ? sending : "Start download";
    this.laterButton.textContent = sending && this.defaultStart === "later" ? sending : "Download later";
    this.browseButton.disabled = !ready || !this.gate.canPick;
    this.keepButton.disabled = this.gate.sending || !this.currentId;
    this.closeButton.disabled = this.keepButton.disabled;
    this.form.setTargetChoiceEnabled(ready && this.gate.canPick);
  }

  private showError(message: string): void {
    this.errorBox.textContent = message;
    this.errorBox.classList.remove("hidden");
  }

  /** Hides the error line: any error, or only this one. */
  private clearError(message: string): void {
    if (message && this.errorBox.textContent !== message) return;
    this.errorBox.textContent = "";
    this.errorBox.classList.add("hidden");
  }

  private async confirm(startMode: "now" | "later"): Promise<void> {
    if (this.loading || this.unavailable || !this.gate.canSend || !this.currentId) return;
    this.form.serverFolders.close();
    const id = this.currentId;
    const target = this.form.target;
    await this.gate.send(async () => {
      this.clearError("");
      try {
        const response = await chrome.runtime.sendMessage({
          type: "dm.confirmHandoff",
          id,
          fileName: this.nameInput.value.trim(),
          destination: this.form.destinationToSend(),
          startMode,
          target,
        }) as { ok?: boolean; error?: string; ack?: { accepted?: boolean; ok?: boolean; error?: string; message?: string } };
        if (!response?.ok) {
          this.showError(response?.error || "The download could not be sent.");
          return;
        }
        if (!response.ack?.accepted) {
          // Only a refusal (ok: false) carries its reason in message; an ack's own text is never an error.
          this.showError(refusalText(response.ack,
            target === "server" ? "The Inbound server rejected this request." : "Inbound Download Manager rejected this request."));
          return;
        }
        // Accepted: the background took it out of the queue; the refresh below shows the next one, or the list.
      } catch (error) {
        this.showError(error instanceof Error ? error.message : String(error));
      }
    });
    await this.refresh();
  }

  private async keep(): Promise<void> {
    if (this.gate.sending || !this.currentId) return;
    const id = this.currentId;
    this.keepButton.disabled = true;
    try { await chrome.runtime.sendMessage({ type: "dm.keepInBrowser", id }); } catch { /* closing the popup does the same */ }
    await this.refresh();
  }

  private async browse(): Promise<void> {
    if (this.loading || this.unavailable || !this.gate.canPick || !this.currentId) return;
    if (this.form.target === "server") {
      // The server's own folders, listed here: nothing takes the focus away from the popup.
      await this.form.toggleServerFolders();
      return;
    }
    // This PC: the desktop app's folder dialog would take the focus and so close the popup, giving the download
    // back to the browser. The waiting downloads move to folder windows, and this one's dialog opens there.
    await this.gate.pick(async () => {
      try {
        // The window starts from what is set here, not from the defaults (the target above all: its dialog opens
        // only for This PC). The folder as shown: the window may start elsewhere (EXT.R10), so "the default" is
        // not left to it.
        const draft = {
          target: this.form.target,
          fileName: this.nameInput.value.trim(),
          destination: this.destinationInput.value.trim(),
        };
        const moved = await chrome.runtime.sendMessage({ type: "dm.askInWindow", id: this.currentId, pick: true, draft }) as { ok?: boolean; error?: string };
        if (!moved?.ok) {
          this.showError(moved?.error || "Could not open the folder window.");
          return;
        }
        window.close();
      } catch (error) {
        this.showError(error instanceof Error ? error.message : String(error));
      }
    });
  }
}
