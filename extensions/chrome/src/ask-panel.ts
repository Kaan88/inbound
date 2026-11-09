// EXT.R7: "ask where to save" in the toolbar popup, anchored to the extension's icon like the browser's own
// download bubble. The background keeps the queue of caught downloads waiting for a folder (dm.askQueue) and
// opens the popup for them; this panel shows the oldest, with the same choices as the folder window (handoff.ts):
// This PC or the server, the folder, favorites, Start, Download or Keep in browser. Closing the popup without a
// choice leaves what still waits to the browser (the background sees the popup's port close).
//
// EXT.B7: every folder shown here comes from the place the download goes: the server's default folder, favorites,
// folder list and free space when the server is chosen, the desktop app's otherwise.

import { FormGate } from "./form-gate";
import {
  FreeSpaceLine, renderTargetChoice, ServerFolderBrowser, setTargetChoiceEnabled, type OfferedTarget,
} from "./destination-target";
import type { TargetId } from "./transport";

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

function formatBytes(bytes: number): string {
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let index = 0;
  while (value >= 1024 && index < units.length - 1) { value /= 1024; index++; }
  return `${value.toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

function hostOf(url: string): string {
  try { return new URL(url).host; } catch { return ""; }
}

export class AskPanel {
  private readonly card = byId<HTMLElement>("askCard");
  private readonly count = byId<HTMLSpanElement>("askCount");
  private readonly source = byId<HTMLParagraphElement>("askSource");
  private readonly nameInput = byId<HTMLInputElement>("askFileName");
  private readonly targetField = byId<HTMLFieldSetElement>("askTargetField");
  private readonly targetChoice = byId<HTMLDivElement>("askTargetChoice");
  private readonly destinationInput = byId<HTMLInputElement>("askDestination");
  private readonly browseButton = byId<HTMLButtonElement>("askBrowse");
  private readonly favoritesField = byId<HTMLLabelElement>("askFavoritesField");
  private readonly favoritesSelect = byId<HTMLSelectElement>("askFavorites");
  private readonly startSelect = byId<HTMLSelectElement>("askStartMode");
  private readonly confirmButton = byId<HTMLButtonElement>("askConfirm");
  private readonly keepButton = byId<HTMLButtonElement>("askKeep");
  private readonly errorBox = byId<HTMLParagraphElement>("askError");
  private readonly others = byId<HTMLParagraphElement>("askOthers");
  private readonly localHint = byId<HTMLSpanElement>("askLocalHint");
  private readonly serverHint = byId<HTMLSpanElement>("askServerHint");
  private readonly freeSpace = new FreeSpaceLine(byId<HTMLSpanElement>("askFreeSpace"));
  private readonly serverFolders: ServerFolderBrowser;
  // EXT.B2: Download and Choose... exclude each other, as in the folder window.
  private readonly gate = new FormGate(() => this.renderButtons());
  private currentId = "";
  // The current request has not loaded: its target and folder are not known yet, so Download and Choose... wait.
  private loading = true;
  // The current request could not be loaded (expired): only Keep in browser is left.
  private unavailable = false;
  private target: TargetId = "local";
  private defaultDestination = "";
  private refreshing: Promise<void> = Promise.resolve();

  constructor(private readonly onWaitingChange: (waiting: boolean) => void) {
    this.serverFolders = new ServerFolderBrowser(byId<HTMLDivElement>("askServerFolders"), (path) => {
      this.destinationInput.value = path;
      this.freeSpace.update(this.target, path);
    }, (message) => this.showError(message));
    this.confirmButton.addEventListener("click", () => this.confirm());
    this.keepButton.addEventListener("click", () => this.keep());
    this.browseButton.addEventListener("click", () => this.browse());
    this.favoritesSelect.addEventListener("change", () => {
      if (this.favoritesSelect.value) this.destinationInput.value = this.favoritesSelect.value;
      this.favoritesSelect.value = "";
      this.freeSpace.update(this.target, this.destinationInput.value);
    });
    this.destinationInput.addEventListener("input", () => this.freeSpace.update(this.target, this.destinationInput.value));
    for (const input of [this.nameInput, this.destinationInput]) {
      input.addEventListener("keydown", (event) => {
        if ((event as KeyboardEvent).key === "Enter" && !this.confirmButton.disabled) void this.confirm();
      });
    }
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

    if (items.length === 0) {
      const was = this.waiting;
      this.currentId = "";
      this.serverFolders.close();
      this.card.classList.add("hidden");
      if (was) this.onWaitingChange(false);
      return;
    }

    const current = items.find((item) => item.id === this.currentId);
    const shown = current ?? items[0]!;
    this.count.textContent = items.length > 1 ? `${items.indexOf(shown) + 1} of ${items.length}` : "";
    const rest = items.filter((item) => item.id !== shown.id).map((item) => item.fileName || hostOf(item.url) || "download");
    this.others.textContent = rest.length ? `Also waiting: ${rest.join(", ")}` : "";
    this.others.classList.toggle("hidden", rest.length === 0);
    this.card.classList.remove("hidden");
    if (!this.waiting) {
      this.currentId = shown.id;
      this.onWaitingChange(true);
    }
    // A send in progress keeps its download on screen; the next one comes when it is answered.
    if (current || this.gate.sending) return;
    await this.show(shown);
  }

  private async show(item: AskItem): Promise<void> {
    this.currentId = item.id;
    this.loading = true;
    this.unavailable = false;
    this.renderButtons();
    this.errorBox.classList.add("hidden");
    this.serverFolders.close();
    this.nameInput.value = item.fileName;
    const size = typeof item.totalBytes === "number" && item.totalBytes > 0 ? formatBytes(item.totalBytes) : "";
    this.source.textContent = [hostOf(item.url), size].filter(Boolean).join(" · ");
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
      return;
    }

    this.startSelect.value = answer.defaults?.startMode === "later" ? "later" : "now";
    const targets = Array.isArray(answer.targets) && answer.targets.length > 0
      ? answer.targets
      : [{ id: "local" as TargetId, label: "This PC", chooser: "dialog" as const, defaultFolder: answer.defaults?.destination ?? "",
        favorites: answer.defaults?.favorites ?? [] }];
    const first = targets.find((offered) => offered.id === answer!.target) ?? targets[0]!;
    renderTargetChoice(this.targetField, this.targetChoice, targets, first.id, (offered) => this.useTarget(offered));
    this.useTarget(first);
    this.loading = false;
    this.renderButtons();
  }

  /** Switches the form to one target: its default folder, its favorites, its wording (EXT.B7). */
  private useTarget(offered: OfferedTarget): void {
    this.target = offered.id;
    this.defaultDestination = offered.defaultFolder ?? "";
    this.destinationInput.value = this.defaultDestination;
    this.destinationInput.placeholder = offered.id === "server" ? "The server's default folder" : "The app's default folder";
    this.browseButton.title = offered.id === "server"
      ? "Choose a folder on the server"
      : "Choose a folder on this PC. The app's folder dialog closes this popup, so this download moves to a window.";
    this.serverFolders.close();
    this.favoritesSelect.replaceChildren(this.favoritesSelect.options[0] ?? new Option("Pick a favorite folder…", ""));
    for (const favorite of offered.favorites ?? []) {
      const option = document.createElement("option");
      option.value = favorite.path;
      option.textContent = favorite.name === favorite.path ? favorite.path : `${favorite.name} (${favorite.path})`;
      this.favoritesSelect.append(option);
    }
    this.favoritesField.classList.toggle("hidden", (offered.favorites ?? []).length === 0);
    if (offered.id === "server" && offered.error) this.showError(offered.error);
    else this.errorBox.classList.add("hidden");
    this.localHint.classList.toggle("hidden", offered.id === "server");
    this.serverHint.classList.toggle("hidden", offered.id !== "server");
    this.freeSpace.update(offered.id, this.defaultDestination);
  }

  private renderButtons(): void {
    const ready = !this.loading && !this.unavailable;
    this.confirmButton.disabled = !ready || !this.gate.canSend;
    this.browseButton.disabled = !ready || !this.gate.canPick;
    this.keepButton.disabled = this.gate.sending || !this.currentId;
    setTargetChoiceEnabled(this.targetChoice, ready && this.gate.canPick);
  }

  private showError(message: string): void {
    this.errorBox.textContent = message;
    this.errorBox.classList.remove("hidden");
  }

  private async confirm(): Promise<void> {
    if (this.loading || this.unavailable || !this.gate.canSend || !this.currentId) return;
    this.serverFolders.close();
    const id = this.currentId;
    await this.gate.send(async () => {
      this.errorBox.classList.add("hidden");
      try {
        const typed = this.destinationInput.value.trim();
        const response = await chrome.runtime.sendMessage({
          type: "dm.confirmHandoff",
          id,
          fileName: this.nameInput.value.trim(),
          destination: typed === this.defaultDestination ? "" : typed,
          startMode: this.startSelect.value,
          target: this.target,
        }) as { ok?: boolean; error?: string; ack?: { accepted?: boolean; error?: string; message?: string } };
        if (!response?.ok) {
          this.showError(response?.error || "The download could not be sent.");
          return;
        }
        if (!response.ack?.accepted) {
          this.showError(response.ack?.error || response.ack?.message ||
            (this.target === "server" ? "The Inbound server rejected this request." : "Inbound Download Manager rejected this request."));
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
    if (this.target === "server") {
      // The server's own folders, listed here: nothing takes the focus away from the popup.
      if (this.serverFolders.isOpen) this.serverFolders.close();
      else await this.serverFolders.open(this.destinationInput.value);
      return;
    }
    // This PC: the desktop app's folder dialog would take the focus and so close the popup, giving the download
    // back to the browser. The waiting downloads move to folder windows, and this one's dialog opens there.
    await this.gate.pick(async () => {
      try {
        // The window starts from what is set here, not from the defaults (the target above all: its dialog opens
        // only for This PC).
        const typed = this.destinationInput.value.trim();
        const draft = {
          target: this.target,
          fileName: this.nameInput.value.trim(),
          destination: typed === this.defaultDestination ? "" : typed,
          startMode: this.startSelect.value === "later" ? "later" : "now",
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
