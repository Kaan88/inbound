import { pickDestinationFolder } from "./pick-destination";

interface PendingHandoff {
  id: string;
  url: string;
  pageUrl?: string;
  suggestedFileName?: string;
  mimeType?: string;
}

const params = new URLSearchParams(location.search);
const handoffId = params.get("id") ?? "";
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
// The folder the window opened with. Sent only when changed, so the app keeps deciding the default.
let defaultDestination = "";
let held = false;
let finished = false;
const errorBox = document.getElementById("error") as HTMLParagraphElement;
const successBox = document.getElementById("success") as HTMLParagraphElement;

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
    showError("This window has no download to confirm. Start again from the right-click menu.");
    confirmButton.disabled = true;
    return;
  }

  try {
    const response = (await chrome.runtime.sendMessage({ type: "dm.getHandoff", id: handoffId })) as {
      ok?: boolean;
      handoff?: PendingHandoff;
      held?: boolean;
      defaults?: { destination?: string; favorites?: Array<{ name: string; path: string }>; startMode?: string };
      error?: string;
    };
    if (!response?.ok || !response.handoff) {
      showError(response?.error || "This download request has expired. Start again from the right-click menu.");
      confirmButton.disabled = true;
      return;
    }

    urlInput.value = response.handoff.url;
    nameInput.value = response.handoff.suggestedFileName || "";
    defaultDestination = response.defaults?.destination ?? "";
    destinationInput.value = defaultDestination;
    startSelect.value = response.defaults?.startMode === "later" ? "later" : "now";
    const favorites = response.defaults?.favorites ?? [];
    for (const favorite of favorites) {
      const option = document.createElement("option");
      option.value = favorite.path;
      option.textContent = favorite.name === favorite.path ? favorite.path : `${favorite.name} (${favorite.path})`;
      favoritesSelect.append(option);
    }
    favoritesField.classList.toggle("hidden", favorites.length === 0);
    // A caught download waits in the browser while this window is open; leaving it there is a choice.
    held = response.held === true;
    if (held) holdWorker();
    keepButton.classList.toggle("hidden", !held);
    cancelButton.classList.toggle("hidden", held);
    // UI.R4: the favorites row can make the form taller than at first load; fit again, within the screen.
    void fitWindowToContent().catch(() => undefined);
  } catch (error) {
    showError(error instanceof Error ? error.message : String(error));
    confirmButton.disabled = true;
  }
});

confirmButton.addEventListener("click", async () => {
  confirmButton.disabled = true;
  errorBox.classList.add("hidden");
  try {
    const response = (await chrome.runtime.sendMessage({
      type: "dm.confirmHandoff",
      id: handoffId,
      fileName: nameInput.value.trim(),
      destination: destinationInput.value.trim() === defaultDestination ? "" : destinationInput.value.trim(),
      startMode: startSelect.value,
    })) as {
      ok?: boolean;
      ack?: { accepted?: boolean; duplicate?: boolean; error?: string; job?: { fileName?: string } };
      error?: string;
    };

    if (!response?.ok) {
      showError(response?.error || "The download could not be sent to the app.");
      confirmButton.disabled = false;
      return;
    }

    if (!response.ack?.accepted) {
      showError(response.ack?.error || "Inbound Download Manager rejected this request.");
      confirmButton.disabled = false;
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
    confirmButton.disabled = false;
  }
});

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
  if (finished || !held) return;
  finished = true;
  try { await chrome.runtime.sendMessage({ type: "dm.keepInBrowser", id: handoffId }); } catch { /* closing the window does the same */ }
}

keepButton.addEventListener("click", async () => {
  keepButton.disabled = true;
  await keepInBrowser();
  window.close();
});

favoritesSelect.addEventListener("change", () => {
  if (favoritesSelect.value) destinationInput.value = favoritesSelect.value;
  favoritesSelect.value = "";
});

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape") {
    if (held) void keepInBrowser().then(() => window.close());
    else window.close();
  }
  else if (event.key === "Enter" && !confirmButton.disabled &&
           (event.target === nameInput || event.target === destinationInput)) confirmButton.click();
});

// Send waits while the app's folder dialog is open, then gets back the state it had before.
let confirmDisabledBeforePick = false;
browseDestinationButton.addEventListener("click", () => {
  void pickDestinationFolder(destinationInput, browseDestinationButton, showError, (busy) => {
    if (busy) {
      confirmDisabledBeforePick = confirmButton.disabled;
      confirmButton.disabled = true;
    } else {
      confirmButton.disabled = confirmDisabledBeforePick;
    }
  });
});
import { fitWindowToContent } from "./fit-window";

void fitWindowToContent().catch(() => undefined);
