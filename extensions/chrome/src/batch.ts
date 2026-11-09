// Download All ("Add links", EXT.R8 §3.5): the links of a page in a table like the desktop's Add links, a filter and a
// type list, the package name for two or more, and the save form shared with the folder window and the popup.
// "Start download" and "Download later" add the selection; the primary one follows Options > Default start.

import { FormGate } from "./form-gate";
import { pickDestinationFolder } from "./pick-destination";
import { type OfferedTarget } from "./destination-target";
import { fileIcon } from "./ui/icon";
import { firstTarget, markDefaultStart, SaveForm } from "./ui/save-form";
import { applyTheme, followThemeChanges } from "./ui/theme";
import type { TargetId } from "./transport";
import { fitWindowToContent } from "./fit-window";

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
  pageTitle?: string;
  links?: PageLink[];
  error?: string;
  notWebPage?: boolean;
}

interface BatchResponse {
  ok?: boolean;
  accepted?: number;
  failed?: number;
  results?: Array<{ url: string; accepted?: boolean; duplicate?: boolean; error?: string }>;
  error?: string;
}

type SortKey = "name" | "type" | "host";

const params = new URLSearchParams(location.search);
const requestedTabId = Number(params.get("tabId") ?? "");

const filterInput = document.getElementById("filter") as HTMLInputElement;
const typeFilter = document.getElementById("typeFilter") as HTMLSelectElement;
const selectAll = document.getElementById("selectAll") as HTMLInputElement;
const linksList = document.getElementById("links") as HTMLUListElement;
const countLabel = document.getElementById("count") as HTMLSpanElement;
const pageLabel = document.getElementById("pageTitle") as HTMLSpanElement;
const selectionLabel = document.getElementById("selection") as HTMLSpanElement;
const packageField = document.getElementById("packageField") as HTMLElement;
const packageInput = document.getElementById("packageName") as HTMLInputElement;
const destinationInput = document.getElementById("destination") as HTMLInputElement;
const browseDestinationButton = document.getElementById("browseDestination") as HTMLButtonElement;
const confirmButton = document.getElementById("confirm") as HTMLButtonElement;
const laterButton = document.getElementById("later") as HTMLButtonElement;
const cancelButton = document.getElementById("cancel") as HTMLButtonElement;
const errorBox = document.getElementById("error") as HTMLParagraphElement;
const successBox = document.getElementById("success") as HTMLParagraphElement;

// EXT.B7: until the target list has arrived, where the batch goes (and so whose folders Browse shows) is not known:
// Browse and Start wait, as in the folder window, so a click cannot open this PC's dialog in server mode.
let targetsLoaded = false;
let links: PageLink[] = [];
let pageUrl = "";
let pageTitle = "";
let scanned = false;
let sortKey: SortKey | null = null;
let sortDown = false;
const selected = new Set<string>();
// EXT.B2: Send and Browse exclude each other; updateSelection draws the buttons from this state.
const gate = new FormGate(() => updateSelection());

const form = new SaveForm({
  targetField: document.getElementById("targetField") as HTMLElement,
  targetChoice: document.getElementById("targetChoice") as HTMLElement,
  destination: destinationInput,
  browse: browseDestinationButton,
  freeSpace: document.getElementById("freeSpace") as HTMLElement,
  serverFolders: document.getElementById("serverFolders") as HTMLElement,
}, showError, clearError);

function showError(message: string): void {
  errorBox.textContent = message;
  errorBox.classList.toggle("hidden", !message);
  if (message) {
    successBox.classList.add("hidden");
  }
}

/** Hides the error line: any error, or only this one. */
function clearError(message: string): void {
  if (message && errorBox.textContent !== message) return;
  showError("");
}

function showSuccess(message: string): void {
  successBox.textContent = message;
  successBox.classList.toggle("hidden", !message);
  if (message) {
    errorBox.classList.add("hidden");
  }
}

function typeOf(link: PageLink): string {
  // Upper case like the file types ("RAR", "ISO"), so "LINK" reads as one of them.
  return (link.extension || link.kind).toUpperCase();
}

function visibleLinks(): PageLink[] {
  const query = filterInput.value.trim().toLowerCase();
  const type = typeFilter.value;
  let shown = links.filter((link) => !type || typeOf(link) === type);
  if (query) {
    shown = shown.filter((link) =>
      [link.text, link.extension, link.kind, link.host, link.url]
        .some((value) => value.toLowerCase().includes(query)));
  }
  if (sortKey) {
    const key = sortKey;
    const value = (link: PageLink) => key === "name" ? (link.text || link.url) : key === "type" ? typeOf(link) : link.host;
    shown = [...shown].sort((a, b) => value(a).localeCompare(value(b), undefined, { sensitivity: "base", numeric: true }) * (sortDown ? -1 : 1));
  }
  return shown;
}

/**
 * What Start adds: the ticked links the filter shows. A ticked link the filter hides stays ticked for when the filter is
 * cleared, but is not added, and the footer says how many are left out.
 */
function chosenLinks(visible: PageLink[] = visibleLinks()): PageLink[] {
  return visible.filter((link) => selected.has(link.url));
}

function updateSelection(visible: PageLink[] = visibleLinks()): void {
  const chosen = chosenLinks(visible).length;
  selectAll.checked = visible.length > 0 && chosen === visible.length;
  selectAll.indeterminate = chosen > 0 && chosen < visible.length;
  if (scanned) countLabel.textContent = `${links.length} link${links.length === 1 ? "" : "s"}`;
  const hidden = selected.size - chosen;
  selectionLabel.textContent = hidden > 0 ? `${chosen} selected (${hidden} more hidden by the filter, not added)` : `${chosen} selected`;
  const off = !targetsLoaded || !gate.canSend || chosen === 0;
  confirmButton.disabled = off;
  laterButton.disabled = off;
  browseDestinationButton.disabled = !targetsLoaded || !gate.canPick;
  cancelButton.disabled = gate.sending;
  form.setTargetChoiceEnabled(gate.canPick);
  // The package name only means something for two or more (one Download All batch becomes one package).
  packageField.classList.toggle("hidden", chosen < 2);
}

function render(): void {
  const visible = visibleLinks();
  linksList.replaceChildren();

  if (visible.length === 0) {
    const empty = document.createElement("li");
    empty.className = "empty";
    empty.textContent = links.length === 0 ? "No downloadable links on this page." : "No links match this filter.";
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
    checkbox.setAttribute("aria-label", link.text || link.url);
    checkbox.addEventListener("change", () => {
      if (checkbox.checked) {
        selected.add(link.url);
      } else {
        selected.delete(link.url);
      }

      updateSelection();
    });
    label.append(checkbox);

    const name = document.createElement("span");
    name.className = "link-name";
    const glyph = document.createElement("span");
    glyph.className = "file-glyph";
    try { glyph.append(fileIcon(link.text || link.url, 16)); } catch { /* a page without SVG */ }
    const text = document.createElement("span");
    text.className = "ellipsis";
    text.textContent = link.text || link.url;
    name.append(glyph, text);
    name.title = link.url;
    row.setAttribute("aria-description", link.url);

    const type = document.createElement("span");
    type.className = "link-type";
    type.textContent = typeOf(link);

    const site = document.createElement("span");
    site.className = "link-site ellipsis";
    site.textContent = link.host;
    site.title = link.host;

    row.append(label, name, type, site);
    linksList.append(row);
  }

  updateSelection(visible);
}

function fillTypes(): void {
  const types = [...new Set(links.map(typeOf).filter(Boolean))].sort();
  typeFilter.replaceChildren(typeFilter.options[0] ?? new Option("All types", ""));
  for (const type of types) {
    const option = document.createElement("option");
    option.value = type;
    option.textContent = type;
    typeFilter.append(option);
  }
}

async function loadLinks(): Promise<void> {
  if (!Number.isFinite(requestedTabId) || requestedTabId <= 0) {
    showError("This window is missing its page reference. Open Download all links from the extension popup.");
    countLabel.textContent = "";
    linksList.replaceChildren();
    return;
  }

  try {
    const response = (await chrome.runtime.sendMessage({
      type: "dm.pageLinks",
      tabId: requestedTabId,
    })) as LinksResponse;
    if (!response?.ok) {
      // U-EX-14: no "Scanning…" left on screen, and the reason in words that fit the page.
      showError(response?.error || "Could not scan this page.");
      countLabel.textContent = "";
      linksList.replaceChildren();
      const empty = document.createElement("li");
      empty.className = "empty";
      empty.textContent = response?.notWebPage ? "This is not a web page, so there are no links to collect." : "No links were collected.";
      linksList.append(empty);
      return;
    }

    links = response.links ?? [];
    pageUrl = response.pageUrl ?? "";
    scanned = true;
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
    pageLabel.textContent = pageTitle ? `${pageTitle} ·` : "";
    packageInput.value = response.pageTitle || pageTitle || "Links";
    fillTypes();
    render();
  } catch (error) {
    showError(error instanceof Error ? error.message : String(error));
    countLabel.textContent = "";
    linksList.replaceChildren();
  }
}

filterInput.addEventListener("input", () => render());
typeFilter.addEventListener("change", () => render());

for (const button of Array.from(document.querySelectorAll?.<HTMLButtonElement>(".link-sort") ?? [])) {
  button.addEventListener("click", () => {
    const key = button.dataset.sort as SortKey;
    sortDown = sortKey === key ? !sortDown : false;
    sortKey = key;
    for (const other of Array.from(document.querySelectorAll<HTMLButtonElement>(".link-sort"))) {
      other.setAttribute("aria-sort", other === button ? (sortDown ? "descending" : "ascending") : "none");
    }
    render();
  });
}

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

async function start(startMode: "now" | "later"): Promise<void> {
  if (!targetsLoaded || chosenLinks().length === 0 || !gate.canSend) {
    return;
  }

  // The server's folder list stays shut while a send runs: a folder picked then would not be the one sent.
  form.serverFolders.close();
  await gate.send(() => sendToApp(startMode));
}

confirmButton.addEventListener("click", () => start("now"));
laterButton.addEventListener("click", () => start("later"));

async function sendToApp(startMode: "now" | "later"): Promise<void> {
  showError("");
  showSuccess("");
  const chosen = chosenLinks().map((link) => link.url);
  selectionLabel.textContent = `Adding ${chosen.length} download${chosen.length === 1 ? "" : "s"}…`;

  try {
    const response = (await chrome.runtime.sendMessage({
      type: "dm.confirmBatch",
      pageUrl,
      tabId: Number.isFinite(requestedTabId) ? requestedTabId : undefined,
      destination: form.destinationToSend(),
      startMode,
      target: form.target,
      packageName: chosen.length >= 2 ? packageInput.value.trim() : undefined,
      urls: chosen.map((url) => ({ url })),
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
      for (const url of chosen) selected.delete(url);
      render();
      setTimeout(() => window.close(), 1400);
    }
  } catch (error) {
    showError(error instanceof Error ? error.message : String(error));
  }
}

cancelButton.addEventListener("click", () => window.close());

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && !gate.sending) {
    if (form.serverFolders.isOpen) form.serverFolders.close();
    else window.close();
  }
});

async function loadTargets(): Promise<void> {
  try {
    const response = await chrome.runtime.sendMessage({ type: "dm.targets" }) as {
      ok?: boolean; targets?: OfferedTarget[]; target?: TargetId; startMode?: string; theme?: "system" | "light" | "dark";
    };
    const offered = response?.ok && Array.isArray(response.targets) ? response.targets : [];
    if (response?.ok) {
      markDefaultStart(response.startMode, confirmButton, laterButton);
      if (response.theme) applyTheme(response.theme);
    }
    if (offered.length > 0) applyTargets(offered, response.target);
  } catch { /* this PC, as before a server could be paired */ }
  targetsLoaded = true;
  updateSelection();
}

/** Shows the offered targets, starting on the default one. */
function applyTargets(offered: OfferedTarget[], preferred: TargetId | undefined): void {
  const first = firstTarget(offered, preferred);
  // Destination folder starts empty; anything in it now was typed or picked while the list was on its way.
  form.showTargets(offered, first, first.id === form.target && destinationInput.value.trim() !== "");
  document.getElementById("targetLabel")?.classList.toggle("hidden", offered.length < 2);
  form.setTargetChoiceEnabled(gate.canPick);
}

browseDestinationButton.addEventListener("click", async () => {
  if (!targetsLoaded) return;
  if (form.target === "server") {
    if (!gate.canPick) return;
    void form.toggleServerFolders();
    return;
  }
  await gate.pick(async () => {
    const before = destinationInput.value;
    await pickDestinationFolder(destinationInput, browseDestinationButton, showError);
    if (destinationInput.value !== before) clearError("");
  });
});

followThemeChanges();
void loadTargets();

void loadLinks();

void fitWindowToContent().catch(() => undefined);
