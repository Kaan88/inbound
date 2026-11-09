import { FormGate } from "./form-gate";
import { pickDestinationFolder } from "./pick-destination";
import {
  FreeSpaceLine, renderTargetChoice, ServerFolderBrowser, setTargetChoiceEnabled, type OfferedTarget,
} from "./destination-target";
import type { TargetId } from "./transport";

interface PendingHandoff {
  id: string;
  url: string;
  pageUrl?: string;
  suggestedFileName?: string;
  mimeType?: string;
}

const params = new URLSearchParams(location.search);
const handoffId = params.get("id") ?? "";
// EXT.R7: opened from the toolbar popup's Choose… for This PC, so the app's folder dialog starts at once.
const pickAtOnce = params.get("pick") === "1";
const urlInput = document.getElementById("url") as HTMLInputElement;
const nameInput = document.getElementById("fileName") as HTMLInputElement;
const destinationInput = document.getElementById("destination") as HTMLInputElement;
const browseDestinationButton = document.getElementById("browseDestination") as HTMLButtonElement;
const startSelect = document.getElementById("startMode") as HTMLSelectElement;
const confirmButton = document.getElementById("confirm") as HTMLButtonElement;
const cancelButton = document.getElementById("cancel") as HTMLButtonElement;
const keepButton = document.getElementById("keepInBrowser") as HTMLButtonElement;
const favoritesField = document.getElementById("favoritesField") as HTMLLabelElement;
const favoritesSelect = document.getElementById("favorites") as HTMLSelectElement;
const targetField = document.getElementById("targetField") as HTMLFieldSetElement;
const targetChoice = document.getElementById("targetChoice") as HTMLDivElement;
const localHint = document.getElementById("localHint") as HTMLSpanElement;
const serverHint = document.getElementById("serverHint") as HTMLSpanElement;
// EXT.B7: the free space of the folder in Save in, from the server when the download goes there.
const freeSpace = new FreeSpaceLine(document.getElementById("freeSpace") as HTMLSpanElement);
// The folder the window opened with, for the target chosen. Sent only when changed, so the app (or the
// server) keeps deciding the default.
let defaultDestination = "";
let targets: OfferedTarget[] = [];
let target: TargetId = "local";
let held = false;
let finished = false;
// There is no request to confirm (no id, expired, or the worker could not be reached).
let unavailable = false;
// dm.getHandoff has not answered yet: the target and its folder are not known, so Download and Choose...
// wait. Sending now would go to This PC whatever the default target is.
let loading = true;
const errorBox = document.getElementById("error") as HTMLParagraphElement;
const successBox = document.getElementById("success") as HTMLParagraphElement;
// EXT.B2: Send and Choose... exclude each other; the buttons are drawn from this state only.
const gate = new FormGate(renderButtons);
renderButtons();

function renderButtons(): void {
  confirmButton.disabled = loading || unavailable || finished || !gate.canSend;
  browseDestinationButton.disabled = loading || !gate.canPick;
  // Keep in browser during a send could keep the file in the browser and add it to the app too.
  keepButton.disabled = gate.sending;
  setTargetChoiceEnabled(targetChoice, !loading && gate.canPick);
}

function markUnavailable(message: string): void {
  showError(message);
  unavailable = true;
  loading = false;
  renderButtons();
}

function showError(message: string): void {
  errorBox.textContent = message;
  errorBox.classList.remove("hidden");
  successBox.classList.add("hidden");
}

function showSuccess(message: string): void {
  successBox.textContent = message;
  successBox.classList.remove("hidden");
  errorBox.classList.add("hidden");
}

document.addEventListener("DOMContentLoaded", async () => {
  if (!handoffId) {
    markUnavailable("This window has no download to confirm. Start again from the right-click menu.");
    return;
  }

  try {
    const response = (await chrome.runtime.sendMessage({ type: "dm.getHandoff", id: handoffId })) as {
      ok?: boolean;
      handoff?: PendingHandoff;
      held?: boolean;
      defaults?: { destination?: string; favorites?: Array<{ name: string; path: string }>; startMode?: string };
      targets?: OfferedTarget[];
      target?: TargetId;
      draft?: { target?: TargetId; fileName?: string; destination?: string; startMode?: string };
      error?: string;
    };
    if (!response?.ok || !response.handoff) {
      markUnavailable(response?.error || "This download request has expired. Start again from the right-click menu.");
      return;
    }

    urlInput.value = response.handoff.url;
    nameInput.value = response.handoff.suggestedFileName || "";
    startSelect.value = response.defaults?.startMode === "later" ? "later" : "now";
    // EXT.R1: This PC or the paired server, each with its own default folder. One target: no choice shown.
    targets = Array.isArray(response.targets) && response.targets.length > 0
      ? response.targets
      : [{ id: "local", label: "This PC", chooser: "dialog", defaultFolder: response.defaults?.destination ?? "",
        favorites: response.defaults?.favorites ?? [] }];
    // EXT.R7: moved here by the toolbar popup's Choose...: start from what the user set there.
    const draft = response.draft;
    const first = targets.find((offered) => offered.id === draft?.target) ??
      targets.find((offered) => offered.id === response.target) ?? targets[0]!;
    renderTargetChoice(targetField, targetChoice, targets, first.id, useTarget);
    useTarget(first);
    if (draft) {
      if (draft.fileName) nameInput.value = draft.fileName;
      if (draft.startMode === "later" || draft.startMode === "now") startSelect.value = draft.startMode;
      if (draft.destination && first.id === draft.target) {
        destinationInput.value = draft.destination;
        freeSpace.update(first.id, draft.destination);
      }
    }
    // A caught download waits in the browser while this window is open; leaving it there is a choice.
    held = response.held === true;
    if (held) holdWorker();
    keepButton.classList.toggle("hidden", !held);
    cancelButton.classList.toggle("hidden", held);
    loading = false;
    renderButtons();
    // UI.R4: the favorites row can make the form taller than at first load; fit again, within the screen.
    void fitWindowToContent().catch(() => undefined);
    if (pickAtOnce && target === "local" && !browseDestinationButton.disabled) browseDestinationButton.click();
  } catch (error) {
    markUnavailable(error instanceof Error ? error.message : String(error));
  }
});

confirmButton.addEventListener("click", async () => {
  if (loading || unavailable || finished || !gate.canSend) return;
  // The server's folder list stays shut while a send runs: a folder picked then would not be the one sent.
  serverFolders.close();
  await gate.send(sendToApp);
});

async function sendToApp(): Promise<void> {
  errorBox.classList.add("hidden");
  try {
    const response = (await chrome.runtime.sendMessage({
      type: "dm.confirmHandoff",
      id: handoffId,
      fileName: nameInput.value.trim(),
      destination: destinationInput.value.trim() === defaultDestination ? "" : destinationInput.value.trim(),
      startMode: startSelect.value,
      target,
    })) as {
      ok?: boolean;
      ack?: { accepted?: boolean; duplicate?: boolean; error?: string; job?: { fileName?: string } };
      error?: string;
    };

    if (!response?.ok) {
      showError(response?.error || "The download could not be sent to the app.");
      return;
    }

    if (!response.ack?.accepted) {
      showError(response.ack?.error || (response.ack as { message?: string } | undefined)?.message ||
        (target === "server" ? "The Inbound server rejected this request." : "Inbound Download Manager rejected this request."));
      return;
    }

    finished = true;
    showSuccess(
      response.ack.duplicate
        ? "This request was already accepted."
        : `Added: ${response.ack.job?.fileName || "download"}. You can close this window.`,
    );
    setTimeout(() => window.close(), 1200);
  } catch (error) {
    showError(error instanceof Error ? error.message : String(error));
  }
}

cancelButton.addEventListener("click", () => window.close());

/**
 * While a caught download waits on this window, a port to the background worker stays open. Its pings
 * keep the worker, which holds the browser's file-name callback in memory, from being stopped as idle;
 * the port closing with this window tells the worker the window was closed without a choice.
 */
function holdWorker(reconnects = 0): void {
  let port: chrome.runtime.Port;
  try { port = chrome.runtime.connect({ name: `dm.handoff:${handoffId}` }); } catch { return; }
  const timer = setInterval(() => {
    try { port.postMessage({ type: "ping" }); } catch { /* reconnected below */ }
  }, 20000);
  port.onDisconnect.addListener(() => {
    clearInterval(timer);
    // The worker restarted anyway: tell the new one this window is still open. A few tries at most.
    if (!finished && reconnects < 5) holdWorker(reconnects + 1);
  });
}

async function keepInBrowser(): Promise<void> {
  if (finished || !held || gate.sending) return;
  finished = true;
  try { await chrome.runtime.sendMessage({ type: "dm.keepInBrowser", id: handoffId }); } catch { /* closing the window does the same */ }
}

keepButton.addEventListener("click", async () => {
  if (gate.sending) return;
  keepButton.disabled = true;
  await keepInBrowser();
  window.close();
});

favoritesSelect.addEventListener("change", () => {
  if (favoritesSelect.value) destinationInput.value = favoritesSelect.value;
  favoritesSelect.value = "";
  freeSpace.update(target, destinationInput.value);
});

destinationInput.addEventListener("input", () => freeSpace.update(target, destinationInput.value));

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape") {
    if (gate.sending) return; // the send decides; closing now could also resume the browser's copy
    if (held) void keepInBrowser().then(() => window.close());
    else window.close();
  }
  else if (event.key === "Enter" && !confirmButton.disabled &&
           (event.target === nameInput || event.target === destinationInput)) confirmButton.click();
});

const serverFolders = new ServerFolderBrowser(document.getElementById("serverFolders") as HTMLDivElement, (path) => {
  destinationInput.value = path;
  freeSpace.update(target, path);
}, showError);

/** Switches the window to one target: its default folder, its favorites, its wording. */
function useTarget(offered: OfferedTarget): void {
  target = offered.id;
  defaultDestination = offered.defaultFolder ?? "";
  destinationInput.value = defaultDestination;
  destinationInput.placeholder = offered.id === "server" ? "The server's default folder" : "The app's default folder";
  serverFolders.close();
  favoritesSelect.replaceChildren(favoritesSelect.options[0] ?? new Option("Pick a favorite folder…", ""));
  for (const favorite of offered.favorites ?? []) {
    const option = document.createElement("option");
    option.value = favorite.path;
    option.textContent = favorite.name === favorite.path ? favorite.path : `${favorite.name} (${favorite.path})`;
    favoritesSelect.append(option);
  }
  favoritesField.classList.toggle("hidden", (offered.favorites ?? []).length === 0);
  // The server did not answer when the window opened: say so now rather than only after Download.
  if (offered.id === "server" && offered.error) showError(offered.error);
  else errorBox.classList.add("hidden");
  localHint.classList.toggle("hidden", offered.id === "server");
  serverHint.classList.toggle("hidden", offered.id !== "server");
  freeSpace.update(offered.id, defaultDestination);
  void fitWindowToContent().catch(() => undefined);
}

browseDestinationButton.addEventListener("click", async () => {
  // This PC: the desktop app's folder dialog. The server: its own folders, listed here.
  if (loading) return;
  if (target === "server") {
    if (!gate.canPick) return;
    if (serverFolders.isOpen) serverFolders.close();
    else void serverFolders.open(destinationInput.value).then(() => fitWindowToContent().catch(() => undefined));
    return;
  }
  await gate.pick(() => pickDestinationFolder(destinationInput, browseDestinationButton, showError));
});
import { fitWindowToContent } from "./fit-window";

void fitWindowToContent().catch(() => undefined);
