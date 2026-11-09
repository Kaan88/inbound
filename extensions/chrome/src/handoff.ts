// The folder window ("Download file info", EXT.R8 §3.4): a right-click download, or a caught one when the popup can't
// ask. The save form is the popup's (ui/save-form.ts). "Start download" and "Download later" send this download only;
// which one is primary comes from Options > Default start (owner decision, 2026-11-09).

import { FormGate } from "./form-gate";
import { pickDestinationFolder } from "./pick-destination";
import { type OfferedTarget } from "./destination-target";
import { firstTarget, markDefaultStart, SaveForm } from "./ui/save-form";
import { applyTheme, followThemeChanges } from "./ui/theme";
import type { TargetId } from "./transport";
import { fitWindowToContent } from "./fit-window";

interface PendingHandoff {
  id: string;
  url: string;
  pageUrl?: string;
  suggestedFileName?: string;
  mimeType?: string;
}

const params = new URLSearchParams(location.search);
const handoffId = params.get("id") ?? "";
// EXT.R7: opened from the toolbar popup's Browse… for This PC, so the app's folder dialog starts at once.
const pickAtOnce = params.get("pick") === "1";
const urlText = document.getElementById("url") as HTMLElement;
const copyButton = document.getElementById("copyUrl") as HTMLButtonElement;
const nameInput = document.getElementById("fileName") as HTMLInputElement;
const destinationInput = document.getElementById("destination") as HTMLInputElement;
const browseDestinationButton = document.getElementById("browseDestination") as HTMLButtonElement;
const confirmButton = document.getElementById("confirm") as HTMLButtonElement;
const laterButton = document.getElementById("later") as HTMLButtonElement;
const cancelButton = document.getElementById("cancel") as HTMLButtonElement;
const keepButton = document.getElementById("keepInBrowser") as HTMLButtonElement;
const errorBox = document.getElementById("error") as HTMLParagraphElement;
const successBox = document.getElementById("success") as HTMLParagraphElement;
let held = false;
let finished = false;
// There is no request to confirm (no id, expired, or the worker could not be reached).
let unavailable = false;
// dm.getHandoff has not answered yet: the target and its folder are not known, so Start and Browse wait.
// Sending now would go to This PC whatever the default target is.
let loading = true;
// Options > Default start: the primary button, and the one Enter presses.
let defaultStart: "now" | "later" = "now";
// EXT.B2: Send and Browse exclude each other; the buttons are drawn from this state only.
const gate = new FormGate(renderButtons);

const form = new SaveForm({
  targetField: document.getElementById("targetField") as HTMLElement,
  targetChoice: document.getElementById("targetChoice") as HTMLElement,
  destination: destinationInput,
  browse: browseDestinationButton,
  favoritesField: document.getElementById("favoritesField") as HTMLElement,
  favorites: document.getElementById("favorites") as HTMLSelectElement,
  freeSpace: document.getElementById("freeSpace") as HTMLElement,
  serverFolders: document.getElementById("serverFolders") as HTMLElement,
  localHint: document.getElementById("localHint") as HTMLElement,
  serverHint: document.getElementById("serverHint") as HTMLElement,
}, showError, clearError);
renderButtons();

function renderButtons(): void {
  const off = loading || unavailable || finished || !gate.canSend;
  confirmButton.disabled = off;
  laterButton.disabled = off;
  const sending = gate.sending ? "Sending…" : "";
  confirmButton.textContent = sending && defaultStart === "now" ? sending : "Start download";
  laterButton.textContent = sending && defaultStart === "later" ? sending : "Download later";
  browseDestinationButton.disabled = loading || !gate.canPick;
  // Keep in browser during a send could keep the file in the browser and add it to the app too.
  keepButton.disabled = gate.sending;
  form.setTargetChoiceEnabled(!loading && gate.canPick);
}

function markUnavailable(message: string): void {
  // EXT.R8 §3.4: nothing to fill in, only the reason and Close.
  document.body?.classList.add("unavailable");
  cancelButton.textContent = "Close";
  showError(message);
  unavailable = true;
  loading = false;
  renderButtons();
  focusFirst(cancelButton);
}

/** Focuses a button when nothing else has the focus yet (the window opens with none). */
function focusFirst(button: HTMLButtonElement): void {
  const active = document.activeElement;
  if (button.disabled || (active && active !== document.body)) return;
  try { button.focus(); } catch { /* not shown */ }
}

function showError(message: string): void {
  errorBox.textContent = message;
  errorBox.classList.remove("hidden");
  successBox.classList.add("hidden");
  void fitWindowToContent().catch(() => undefined);
}

/** Hides the error line: any error, or only this one. */
function clearError(message: string): void {
  if (message && errorBox.textContent !== message) return;
  errorBox.textContent = "";
  errorBox.classList.add("hidden");
}

function showSuccess(message: string): void {
  successBox.textContent = message;
  successBox.classList.remove("hidden");
  errorBox.classList.add("hidden");
}

document.addEventListener("DOMContentLoaded", async () => {
  followThemeChanges();
  if (!handoffId) {
    markUnavailable("This window has no download to confirm. Start again from the right-click menu.");
    return;
  }

  try {
    const response = (await chrome.runtime.sendMessage({ type: "dm.getHandoff", id: handoffId })) as {
      ok?: boolean;
      handoff?: PendingHandoff;
      held?: boolean;
      defaults?: { destination?: string; favorites?: Array<{ name: string; path: string }>; startMode?: string; theme?: string };
      targets?: OfferedTarget[];
      target?: TargetId;
      draft?: { target?: TargetId; fileName?: string; destination?: string };
      error?: string;
    };
    if (!response?.ok || !response.handoff) {
      markUnavailable(response?.error || "This download request has expired. Start again from the right-click menu.");
      return;
    }

    urlText.textContent = response.handoff.url;
    urlText.title = response.handoff.url;
    nameInput.value = response.handoff.suggestedFileName || "";
    defaultStart = markDefaultStart(response.defaults?.startMode, confirmButton, laterButton);
    // EXT.R1: This PC or the paired server, each with its own default folder. One target: no choice shown.
    const targets: OfferedTarget[] = Array.isArray(response.targets) && response.targets.length > 0
      ? response.targets
      : [{ id: "local", label: "This PC", chooser: "dialog", defaultFolder: response.defaults?.destination ?? "",
        favorites: response.defaults?.favorites ?? [] }];
    // EXT.R7: moved here by the toolbar popup's Browse…: start from what the user set there.
    const draft = response.draft;
    const first = targets.find((offered) => offered.id === draft?.target) ?? firstTarget(targets, response.target);
    form.showTargets(targets, first);
    if (draft) {
      if (draft.fileName) nameInput.value = draft.fileName;
      if (draft.destination && first.id === draft.target) {
        destinationInput.value = draft.destination;
        form.freeSpace.update(first.id, draft.destination);
      }
    }
    // A caught download waits in the browser while this window is open; leaving it there is a choice.
    held = response.held === true;
    if (held) holdWorker();
    keepButton.classList.toggle("hidden", !held);
    cancelButton.classList.toggle("hidden", held);
    loading = false;
    renderButtons();
    // Decision 1: Enter presses the primary button, so it has the focus once the download is loaded.
    if (!pickAtOnce) focusFirst(defaultStart === "later" ? laterButton : confirmButton);
    // UI.R4: the favorites row can make the form taller than at first load; fit again, within the screen.
    void fitWindowToContent().catch(() => undefined);
    if (pickAtOnce && form.target === "local" && !browseDestinationButton.disabled) browseDestinationButton.click();
  } catch (error) {
    markUnavailable(error instanceof Error ? error.message : String(error));
  }
});

async function start(startMode: "now" | "later"): Promise<void> {
  if (loading || unavailable || finished || !gate.canSend) return;
  // The server's folder list stays shut while a send runs: a folder picked then would not be the one sent.
  form.serverFolders.close();
  await gate.send(() => sendToApp(startMode));
}

confirmButton.addEventListener("click", () => start("now"));
laterButton.addEventListener("click", () => start("later"));

async function sendToApp(startMode: "now" | "later"): Promise<void> {
  errorBox.classList.add("hidden");
  const target = form.target;
  try {
    const response = (await chrome.runtime.sendMessage({
      type: "dm.confirmHandoff",
      id: handoffId,
      fileName: nameInput.value.trim(),
      destination: form.destinationToSend(),
      startMode,
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

copyButton.addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(urlText.textContent ?? "");
    copyButton.textContent = "Copied";
    setTimeout(() => { copyButton.textContent = "Copy"; }, 1500);
  } catch { /* the address can still be selected */ }
});

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

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape") {
    if (form.serverFolders.isOpen) { form.serverFolders.close(); return; }
    if (gate.sending) return; // the send decides; closing now could also resume the browser's copy
    if (held) void keepInBrowser().then(() => window.close());
    else window.close();
  }
  else if (event.key === "Enter" && (event.target === nameInput || event.target === destinationInput)) {
    (defaultStart === "later" ? laterButton : confirmButton).click();
  }
});

browseDestinationButton.addEventListener("click", async () => {
  // This PC: the desktop app's folder dialog. The server: its own folders, listed here.
  if (loading) return;
  if (form.target === "server") {
    if (!gate.canPick) return;
    void form.toggleServerFolders().then(() => fitWindowToContent().catch(() => undefined));
    return;
  }
  await gate.pick(async () => {
    const before = destinationInput.value;
    await pickDestinationFolder(destinationInput, browseDestinationButton, showError);
    // U-EX-12: a folder was chosen, so an error about the one before is stale.
    if (destinationInput.value !== before) clearError("");
  });
});

// The theme from the stored settings, for a window opened before theme.js could know it.
void chrome.storage?.local?.get?.("settings")?.then?.((stored: { settings?: { theme?: "system" | "light" | "dark" } }) =>
  applyTheme(stored?.settings?.theme))?.catch?.(() => undefined);

void fitWindowToContent().catch(() => undefined);
