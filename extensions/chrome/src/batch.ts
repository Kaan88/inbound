import { FormGate } from "./form-gate";
import { pickDestinationFolder } from "./pick-destination";
import { renderTargetChoice, ServerFolderBrowser, setTargetChoiceEnabled, type OfferedTarget } from "./destination-target";
import type { TargetId } from "./transport";

interface PageLink {
  url: string;
  text: string;
  kind: string;
  extension: string;
  host: string;
}

interface LinksResponse {
  ok?: boolean;
  tabId?: number;
  pageUrl?: string;
  links?: PageLink[];
  error?: string;
}

interface BatchResponse {
  ok?: boolean;
  accepted?: number;
  failed?: number;
  results?: Array<{ url: string; accepted?: boolean; duplicate?: boolean; error?: string }>;
  error?: string;
}

const params = new URLSearchParams(location.search);
const requestedTabId = Number(params.get("tabId") ?? "");

const filterInput = document.getElementById("filter") as HTMLInputElement;
const selectAll = document.getElementById("selectAll") as HTMLInputElement;
const linksList = document.getElementById("links") as HTMLUListElement;
const countLabel = document.getElementById("count") as HTMLSpanElement;
const selectionLabel = document.getElementById("selection") as HTMLSpanElement;
const destinationInput = document.getElementById("destination") as HTMLInputElement;
const browseDestinationButton = document.getElementById("browseDestination") as HTMLButtonElement;
const startSelect = document.getElementById("startMode") as HTMLSelectElement;
const confirmButton = document.getElementById("confirm") as HTMLButtonElement;
const cancelButton = document.getElementById("cancel") as HTMLButtonElement;
const errorBox = document.getElementById("error") as HTMLParagraphElement;
const successBox = document.getElementById("success") as HTMLParagraphElement;

// EXT.R1: where the batch goes. The background offers the paired server beside this PC.
let target: TargetId = "local";
// The chosen target's default folder, shown in Save in as the folder window shows it. Sent only when changed,
// so the app (or the server) keeps deciding the default when it is left alone.
let defaultDestination = "";
let links: PageLink[] = [];
let pageUrl = "";
let pageTitle = "";
const selected = new Set<string>();
// EXT.B2: Send and Choose... exclude each other; updateSelection draws the buttons from this state.
const gate = new FormGate(() => updateSelection());

function showError(message: string): void {
  errorBox.textContent = message;
  errorBox.classList.toggle("hidden", !message);
  if (message) {
    successBox.classList.add("hidden");
  }
}

function showSuccess(message: string): void {
  successBox.textContent = message;
  successBox.classList.toggle("hidden", !message);
  if (message) {
    errorBox.classList.add("hidden");
  }
}

function visibleLinks(): PageLink[] {
  const query = filterInput.value.trim().toLowerCase();
  if (!query) {
    return links;
  }

  return links.filter((link) =>
    [link.text, link.extension, link.kind, link.host, link.url]
      .some((value) => value.toLowerCase().includes(query)));
}

function updateSelection(visible: PageLink[] = visibleLinks()): void {
  const shownSelected = visible.filter((link) => selected.has(link.url)).length;
  selectAll.checked = visible.length > 0 && shownSelected === visible.length;
  selectAll.indeterminate = shownSelected > 0 && shownSelected < visible.length;
  countLabel.textContent = `${links.length} link${links.length === 1 ? "" : "s"}`;
  selectionLabel.textContent = `${selected.size} selected`;
  confirmButton.disabled = !gate.canSend || selected.size === 0;
  browseDestinationButton.disabled = !gate.canPick;
  cancelButton.disabled = gate.sending;
  setTargetChoiceEnabled(document.getElementById("targetChoice") as HTMLElement, gate.canPick);
  if (lateTargets && gate.canPick) {
    const late = lateTargets;
    lateTargets = null;
    applyTargets(late.offered, late.target, true);
  }
}

function render(): void {
  const visible = visibleLinks();
  linksList.replaceChildren();

  if (visible.length === 0) {
    const empty = document.createElement("li");
    empty.className = "empty";
    empty.textContent = links.length === 0 ? "No downloadable links were found." : "No links match this filter.";
    linksList.append(empty);
    updateSelection(visible);
    return;
  }

  for (const link of visible) {
    const row = document.createElement("li");
    row.className = "link-row";

    const label = document.createElement("label");
    label.className = "link-check";
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.checked = selected.has(link.url);
    checkbox.addEventListener("change", () => {
      if (checkbox.checked) {
        selected.add(link.url);
      } else {
        selected.delete(link.url);
      }

      updateSelection();
    });

    const name = document.createElement("span");
    name.className = "link-name";
    name.textContent = link.text || link.url;
    name.title = link.text || link.url;
    label.append(checkbox, name);

    const meta = document.createElement("span");
    meta.className = "link-meta";
    meta.textContent = [link.extension ? link.extension.toUpperCase() : link.kind, link.host]
      .filter(Boolean)
      .join(" · ");

    const url = document.createElement("span");
    url.className = "link-url";
    url.textContent = link.url;
    url.title = link.url;

    row.append(label, meta, url);
    linksList.append(row);
  }

  updateSelection(visible);
}

async function loadLinks(): Promise<void> {
  if (!Number.isFinite(requestedTabId) || requestedTabId <= 0) {
    showError("This window is missing its page reference. Open Download All from the extension popup.");
    linksList.replaceChildren();
    return;
  }

  try {
    const response = (await chrome.runtime.sendMessage({
      type: "dm.pageLinks",
      tabId: requestedTabId,
    })) as LinksResponse;
    if (!response?.ok) {
      showError(response?.error || "Could not scan this page.");
      linksList.replaceChildren();
      return;
    }

    links = response.links ?? [];
    pageUrl = response.pageUrl ?? "";
    // Pre-tick links that point at files; plain page links stay unticked.
    selected.clear();
    for (const link of links) {
      if (link.extension && !["htm", "html", "php", "asp", "aspx", "jsp"].includes(link.extension.toLowerCase())) {
        selected.add(link.url);
      }
    }
    try {
      pageTitle = new URL(pageUrl).host;
    } catch {
      pageTitle = "";
    }

    if (pageTitle) {
      (document.getElementById("pageTitle") as HTMLElement).textContent = `Links on ${pageTitle}`;
    }

    render();
  } catch (error) {
    showError(error instanceof Error ? error.message : String(error));
    linksList.replaceChildren();
  }
}

filterInput.addEventListener("input", () => render());

selectAll.addEventListener("change", () => {
  for (const link of visibleLinks()) {
    if (selectAll.checked) {
      selected.add(link.url);
    } else {
      selected.delete(link.url);
    }
  }

  render();
});

confirmButton.addEventListener("click", async () => {
  if (selected.size === 0 || !gate.canSend) {
    return;
  }

  // The server's folder list stays shut while a send runs: a folder picked then would not be the one sent.
  serverFolders.close();
  await gate.send(sendToApp);
});

async function sendToApp(): Promise<void> {
  showError("");
  showSuccess("");
  selectionLabel.textContent = `Adding ${selected.size} download${selected.size === 1 ? "" : "s"}...`;

  try {
    const response = (await chrome.runtime.sendMessage({
      type: "dm.confirmBatch",
      pageUrl,
      tabId: Number.isFinite(requestedTabId) ? requestedTabId : undefined,
      destination: destinationInput.value.trim() === defaultDestination ? "" : destinationInput.value.trim(),
      startMode: startSelect.value,
      target,
      urls: [...selected].map((url) => ({ url })),
    })) as BatchResponse;

    if (!response?.ok) {
      showError(response?.error || "The downloads could not be sent to the app.");
      return;
    }

    const accepted = response.accepted ?? 0;
    const failed = response.failed ?? 0;
    if (accepted > 0) {
      showSuccess(`Added ${accepted} download${accepted === 1 ? "" : "s"}${failed > 0 ? `; ${failed} failed` : ""}.`);
    }

    if (failed > 0) {
      const firstError = response.results?.find((result) => !result.accepted)?.error;
      showError(`${failed} link${failed === 1 ? "" : "s"} could not be added${firstError ? `: ${firstError}` : "."}`);
    }

    if (accepted > 0 && failed === 0) {
      selected.clear();
      render();
      setTimeout(() => window.close(), 1400);
    }
  } catch (error) {
    showError(error instanceof Error ? error.message : String(error));
  }
}

cancelButton.addEventListener("click", () => window.close());

const serverFolders = new ServerFolderBrowser(document.getElementById("serverFolders") as HTMLDivElement, (path) => {
  destinationInput.value = path;
}, showError);

function useTarget(offered: OfferedTarget, keepTyped = false): void {
  target = offered.id;
  serverFolders.close();
  // A folder typed for one target means nothing on the other: Save in shows the new target's default.
  // keepTyped: the list arrived late (it waits on the desktop app) and a folder was already chosen for this target.
  defaultDestination = offered.defaultFolder ?? "";
  if (!keepTyped) destinationInput.value = defaultDestination;
  destinationInput.placeholder = offered.id === "server" ? "Use the server's default folder" : "Use the app's default folder";
  // The server did not answer when the window opened: say so now rather than only after Download. Only that
  // message goes on a switch back, so a page-scan error shown meanwhile stays.
  if (shownTargetError && errorBox.textContent === shownTargetError) showError("");
  shownTargetError = offered.id === "server" && offered.error ? offered.error : "";
  if (shownTargetError) showError(shownTargetError);
}

let shownTargetError = "";
// EXT.B2: a target list that arrived while a folder pick or a send was running. Switching target then would
// put the folder being picked (a This PC path) under the server, so it waits for the gate to be free.
let lateTargets: { offered: OfferedTarget[]; target?: TargetId } | null = null;

async function loadTargets(): Promise<void> {
  try {
    const response = await chrome.runtime.sendMessage({ type: "dm.targets" }) as { ok?: boolean; targets?: OfferedTarget[]; target?: TargetId };
    const offered = response?.ok && Array.isArray(response.targets) ? response.targets : [];
    if (offered.length === 0) return;
    if (!gate.canPick) {
      lateTargets = { offered, target: response.target };
      return;
    }
    applyTargets(offered, response.target, false);
  } catch { /* this PC, as before a server could be paired */ }
}

/** Shows the offered targets. userActed: a pick or send ran meanwhile, so the target it used is kept if offered. */
function applyTargets(offered: OfferedTarget[], preferred: TargetId | undefined, userActed: boolean): void {
  const first = (userActed ? offered.find((item) => item.id === target) : undefined)
    ?? offered.find((item) => item.id === preferred) ?? offered[0]!;
  const choice = document.getElementById("targetChoice") as HTMLElement;
  renderTargetChoice(document.getElementById("targetField") as HTMLElement, choice, offered, first.id, useTarget);
  // Save in starts empty; anything in it now was typed or picked while the list was on its way.
  useTarget(first, first.id === target && destinationInput.value.trim() !== "");
  setTargetChoiceEnabled(choice, gate.canPick);
}

browseDestinationButton.addEventListener("click", async () => {
  if (target === "server") {
    if (!gate.canPick) return;
    if (serverFolders.isOpen) serverFolders.close();
    else void serverFolders.open(destinationInput.value);
    return;
  }
  await gate.pick(() => pickDestinationFolder(destinationInput, browseDestinationButton, showError));
});

void loadTargets();

void loadLinks();
import { fitWindowToContent } from "./fit-window";

void fitWindowToContent().catch(() => undefined);
