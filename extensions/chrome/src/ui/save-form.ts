// The one save form (EXT.R8 §1.6) of the right-click window (handoff.ts), the popup's ask view (ask-panel.ts) and
// Download All (batch.ts): "Send to" This PC or the server, "Destination folder" with the target's favorites and
// Browse…, the server's folder panel and the free space. One layout, one set of words, one folder browser.
//
// EXT.B7: every folder shown comes from the place the download goes: switching the target refills the default folder
// and the favorites, and drops a folder typed for the other place.

import {
  FreeSpaceLine, renderTargetChoice, ServerFolderBrowser, setTargetChoiceEnabled, type OfferedTarget,
} from "../destination-target";
import type { TargetId } from "../transport";
import { attachPathField } from "./path-field";

export interface SaveFormElements {
  targetField: HTMLElement;
  targetChoice: HTMLElement;
  destination: HTMLInputElement;
  browse: HTMLButtonElement;
  favoritesField?: HTMLElement;
  favorites?: HTMLSelectElement;
  freeSpace: HTMLElement;
  serverFolders: HTMLElement;
  /** The "What is sent" text for each place; the one that does not apply is hidden. */
  localHint?: HTMLElement;
  serverHint?: HTMLElement;
}

export const FOLDER_PLACEHOLDER: Readonly<Record<TargetId, string>> = {
  local: "The app's default folder",
  server: "The server's default folder",
};

/**
 * U-EX-11: the target a form starts on. The default one, unless it is the server and the server did not answer while
 * this PC did: then This PC, so Start is not one click away from failing. The server stays offered.
 */
export function firstTarget(targets: readonly OfferedTarget[], preferred: TargetId | undefined): OfferedTarget {
  const wanted = targets.find((offered) => offered.id === preferred) ?? targets[0]!;
  if (wanted.id === "server" && wanted.error) {
    const local = targets.find((offered) => offered.id === "local" && !offered.error);
    if (local) return local;
  }
  return wanted;
}

export class SaveForm {
  readonly freeSpace: FreeSpaceLine;
  readonly serverFolders: ServerFolderBrowser;
  target: TargetId = "local";
  /** The target's default folder. A folder equal to it is sent as "", so the app (or server) keeps deciding. */
  defaultDestination = "";
  private targetError = "";

  constructor(private readonly elements: SaveFormElements, private readonly showError: (message: string) => void,
    private readonly clearError: (message: string) => void = () => undefined) {
    // A long folder shows its end, with the middle left out, and the whole path as the tooltip.
    attachPathField(elements.destination);
    this.freeSpace = new FreeSpaceLine(elements.freeSpace);
    this.serverFolders = new ServerFolderBrowser(elements.serverFolders, (path) => {
      elements.destination.value = path;
      this.freeSpace.update(this.target, path);
    }, showError, () => this.clearStaleError());
    elements.favorites?.addEventListener("change", () => {
      const select = elements.favorites!;
      if (select.value) {
        elements.destination.value = select.value;
        this.clearStaleError();
      }
      select.value = "";
      this.freeSpace.update(this.target, elements.destination.value);
    });
    elements.destination.addEventListener("input", () => this.freeSpace.update(this.target, elements.destination.value));
  }

  /** Draws the targets, starting on one, and switches to it. */
  showTargets(targets: readonly OfferedTarget[], first: OfferedTarget, keepTyped = false): void {
    renderTargetChoice(this.elements.targetField, this.elements.targetChoice, targets, first.id, (offered) => this.useTarget(offered));
    this.useTarget(first, keepTyped);
  }

  /** Switches the form to one target: its default folder, its favorites, its words (EXT.B7). */
  useTarget(offered: OfferedTarget, keepTyped = false): void {
    const { destination, browse, favorites, favoritesField, localHint, serverHint } = this.elements;
    this.target = offered.id;
    this.defaultDestination = offered.defaultFolder ?? "";
    // A folder typed for one target means nothing on the other. keepTyped: a folder was set for this same target
    // while the list was on its way.
    if (!keepTyped) destination.value = this.defaultDestination;
    destination.placeholder = FOLDER_PLACEHOLDER[offered.id];
    browse.title = offered.id === "server" ? "Choose a folder on the server" : "Choose a folder on this PC";
    this.serverFolders.close();
    if (favorites) {
      favorites.replaceChildren(favorites.options[0] ?? new Option("Favorite folders", ""));
      for (const favorite of offered.favorites ?? []) {
        const option = document.createElement("option");
        option.value = favorite.path;
        option.textContent = favorite.name === favorite.path ? favorite.path : `${favorite.name} (${favorite.path})`;
        favorites.append(option);
      }
      favoritesField?.classList.toggle("hidden", (offered.favorites ?? []).length === 0);
    }
    // The server did not answer when the form opened: say so now rather than only after Start. Only that message
    // goes on a switch back, so another error shown meanwhile stays.
    if (this.targetError) this.clearError(this.targetError);
    this.targetError = offered.id === "server" && offered.error ? offered.error : "";
    if (this.targetError) this.showError(this.targetError);
    localHint?.classList.toggle("hidden", offered.id === "server");
    serverHint?.classList.toggle("hidden", offered.id !== "server");
    this.freeSpace.update(offered.id, destination.value);
  }

  /** The folder to send: "" when the target's default was left as it is. */
  destinationToSend(): string {
    const typed = this.elements.destination.value.trim();
    return typed === this.defaultDestination ? "" : typed;
  }

  setTargetChoiceEnabled(enabled: boolean): void {
    setTargetChoiceEnabled(this.elements.targetChoice, enabled);
  }

  /** The server's folder panel opens or closes; for This PC the page asks the app's dialog itself. */
  async toggleServerFolders(): Promise<void> {
    if (this.serverFolders.isOpen) this.serverFolders.close();
    else await this.serverFolders.open(this.elements.destination.value);
  }

  /** U-EX-12: a folder was chosen, so an error about the folder typed before no longer applies. */
  private clearStaleError(): void {
    this.clearError("");
  }
}

/**
 * Decision 1 (owner, 2026-11-09): "Start download" and "Download later" in place of a Start list. The one that
 * matches Options > Default start is the primary button and the one Enter presses; the choice is for this download only.
 */
export function markDefaultStart(startMode: string | undefined, startButton: HTMLButtonElement, laterButton: HTMLButtonElement): "now" | "later" {
  const mode = startMode === "later" ? "later" : "now";
  startButton.className = mode === "now" ? "btn-primary" : "btn";
  laterButton.className = mode === "later" ? "btn-primary" : "btn";
  // Guidelines §6: the primary comes first, so with Download later as the default the two swap places.
  const [first, second] = mode === "later" ? [laterButton, startButton] : [startButton, laterButton];
  if (first.parentElement && first.parentElement === second.parentElement && first.nextElementSibling !== second) {
    first.parentElement.insertBefore(first, second);
  }
  return mode;
}
