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
    showError("This window is missing its handoff request.");
    confirmButton.disabled = true;
    return;
  }

  try {
    const response = (await chrome.runtime.sendMessage({ type: "dm.getHandoff", id: handoffId })) as {
      ok?: boolean;
      handoff?: PendingHandoff;
      defaults?: { destination?: string; startMode?: string };
      error?: string;
    };
    if (!response?.ok || !response.handoff) {
      showError(response?.error || "This handoff request has expired. Start it again from the link menu.");
      confirmButton.disabled = true;
      return;
    }

    urlInput.value = response.handoff.url;
    nameInput.value = response.handoff.suggestedFileName || "";
    destinationInput.value = response.defaults?.destination ?? "";
    startSelect.value = response.defaults?.startMode === "later" ? "later" : "now";
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
      destination: destinationInput.value.trim(),
      startMode: startSelect.value,
    })) as {
      ok?: boolean;
      ack?: { accepted?: boolean; duplicate?: boolean; error?: string; job?: { fileName?: string } };
      error?: string;
    };

    if (!response?.ok) {
      showError(response?.error || "The handoff failed.");
      confirmButton.disabled = false;
      return;
    }

    if (!response.ack?.accepted) {
      showError(response.ack?.error || "Inbound Download Manager rejected this request.");
      confirmButton.disabled = false;
      return;
    }

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

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape") window.close();
  else if (event.key === "Enter" && !confirmButton.disabled &&
           (event.target === nameInput || event.target === destinationInput)) confirmButton.click();
});

browseDestinationButton.addEventListener("click", () => {
  void pickDestinationFolder(destinationInput, browseDestinationButton, showError);
});
import { fitWindowToContent } from "./fit-window";

void fitWindowToContent().catch(() => undefined);
