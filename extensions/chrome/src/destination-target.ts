// The folder window's and the batch window's "Save to" choice (EXT.R1): This PC or the paired server, and
// for the server a folder list in place of the desktop app's folder dialog. Shared by handoff.ts and batch.ts.

import type { TargetId } from "./transport";

/** One target as the background offers it (dm.getHandoff, dm.targets). */
export interface OfferedTarget {
  id: TargetId;
  label: string;
  chooser: "dialog" | "list";
  address?: string;
  defaultFolder: string;
  favorites: Array<{ name: string; path: string }>;
  error?: string;
}

/**
 * The radio buttons for the targets, or nothing when there is only one: with one target there is no choice
 * to show. Returns the target that ends up selected.
 */
export function renderTargetChoice(field: HTMLElement, list: HTMLElement, targets: readonly OfferedTarget[],
  selected: TargetId, onChange: (target: OfferedTarget) => void): TargetId {
  list.replaceChildren();
  field.classList.toggle("hidden", targets.length < 2);
  if (targets.length < 2) return targets[0]?.id ?? "local";
  for (const target of targets) {
    const label = document.createElement("label");
    label.className = "target-option";
    const radio = document.createElement("input");
    radio.type = "radio";
    radio.name = "target";
    radio.value = target.id;
    radio.checked = target.id === selected;
    radio.addEventListener("change", () => { if (radio.checked) onChange(target); });
    const text = document.createElement("span");
    text.textContent = target.label;
    label.append(radio, text);
    if (target.address) {
      const address = document.createElement("span");
      address.className = "muted small";
      address.textContent = target.address.replace(/^https?:\/\//, "");
      label.append(address);
    }
    list.append(label);
  }
  return selected;
}

interface FolderListing {
  ok?: boolean;
  error?: string;
  path?: string | null;
  parent?: string | null;
  roots?: string[];
  folders?: Array<{ name: string; path: string }>;
  truncated?: boolean;
  freeBytes?: number | null;
}

function formatBytes(bytes: number): string {
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let index = 0;
  while (value >= 1024 && index < units.length - 1) { value /= 1024; index++; }
  return `${value.toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

/**
 * The server's folders, one level at a time (folders.browse through the background). Only folders under the
 * server's allowed roots are listed; the server checks every path again when the download is added.
 */
export class ServerFolderBrowser {
  private readonly pathText: HTMLSpanElement;
  private readonly freeText: HTMLSpanElement;
  private readonly upButton: HTMLButtonElement;
  private readonly useButton: HTMLButtonElement;
  private readonly list: HTMLUListElement;
  private current: FolderListing | null = null;

  constructor(private readonly container: HTMLElement, private readonly onPick: (path: string) => void,
    private readonly showError: (message: string) => void) {
    container.classList.add("folder-browser");
    const head = document.createElement("div");
    head.className = "folder-browser-head";
    this.upButton = document.createElement("button");
    this.upButton.type = "button";
    this.upButton.textContent = "Up";
    this.upButton.title = "The folder one up";
    this.upButton.addEventListener("click", () => void this.show(this.current?.parent ?? ""));
    this.pathText = document.createElement("span");
    this.pathText.className = "folder-browser-path";
    this.freeText = document.createElement("span");
    this.freeText.className = "muted small";
    head.append(this.upButton, this.pathText, this.freeText);

    this.list = document.createElement("ul");
    this.list.className = "folder-list";
    this.list.setAttribute("aria-label", "Folders on the server");

    const actions = document.createElement("div");
    actions.className = "row";
    this.useButton = document.createElement("button");
    this.useButton.type = "button";
    this.useButton.className = "primary";
    this.useButton.textContent = "Use this folder";
    this.useButton.addEventListener("click", () => {
      if (!this.current?.path) return;
      this.onPick(this.current.path);
      this.close();
    });
    const cancel = document.createElement("button");
    cancel.type = "button";
    cancel.textContent = "Cancel";
    cancel.addEventListener("click", () => this.close());
    actions.append(this.useButton, cancel);
    container.replaceChildren(head, this.list, actions);
    this.close();
  }

  get isOpen(): boolean {
    return !this.container.classList.contains("hidden");
  }

  /** Opens at a folder, or at the roots when that folder can't be listed (a Windows path typed in, say). */
  async open(startPath: string): Promise<void> {
    this.container.classList.remove("hidden");
    if (!await this.show(startPath.trim(), true)) await this.show("");
  }

  close(): void {
    this.container.classList.add("hidden");
  }

  private async show(path: string, quiet = false): Promise<boolean> {
    let listing: FolderListing;
    try {
      listing = await chrome.runtime.sendMessage({ type: "dm.browseServerFolders", path }) as FolderListing;
    } catch (error) {
      if (!quiet) this.showError(error instanceof Error ? error.message : String(error));
      return false;
    }
    if (!listing?.ok) {
      if (!quiet || !path) this.showError(listing?.error || "The server did not list that folder.");
      return false;
    }

    this.current = listing;
    const atRoots = !listing.path;
    this.pathText.textContent = atRoots ? "Folders you can save to" : listing.path!;
    this.pathText.title = this.pathText.textContent;
    this.freeText.textContent = typeof listing.freeBytes === "number" ? `${formatBytes(listing.freeBytes)} free` : "";
    this.upButton.disabled = atRoots;
    this.useButton.disabled = atRoots;
    const entries = atRoots && !(listing.folders?.length)
      ? (listing.roots ?? []).map((root) => ({ name: root, path: root }))
      : listing.folders ?? [];
    this.list.replaceChildren(...entries.map((entry) => {
      const item = document.createElement("li");
      const button = document.createElement("button");
      button.type = "button";
      button.className = "folder-entry";
      button.textContent = entry.name;
      button.title = entry.path;
      button.addEventListener("click", () => void this.show(entry.path));
      item.append(button);
      return item;
    }));
    if (entries.length === 0) {
      const empty = document.createElement("li");
      empty.className = "empty";
      empty.textContent = "No folders inside.";
      this.list.append(empty);
    }
    if (listing.truncated) {
      const more = document.createElement("li");
      more.className = "muted small";
      more.textContent = "More folders are here than can be listed. Type the path instead.";
      this.list.append(more);
    }
    return true;
  }
}
