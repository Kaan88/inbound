interface FolderPickerResponse {
  ok?: boolean;
  cancelled?: boolean;
  path?: string;
  error?: string;
}

/**
 * Asks the app for a folder. onBusy(true) runs before the request and onBusy(false) after it settles,
 * so a page can hold its send button meanwhile: the folder dialog belongs to the app, not this window,
 * and a send while it is open would go to the folder shown, losing the folder chosen afterwards.
 */
export async function pickDestinationFolder(
  input: HTMLInputElement,
  button: HTMLButtonElement,
  showError: (message: string) => void,
  onBusy?: (busy: boolean) => void,
): Promise<void> {
  button.disabled = true;
  onBusy?.(true);
  try {
    const response = await chrome.runtime.sendMessage({
      type: "dm.pickFolder",
      initialPath: input.value.trim() || undefined,
    }) as FolderPickerResponse;
    if (response?.cancelled) return;
    if (!response?.ok || !response.path) {
      showError(response?.error || "Could not select a destination folder.");
      return;
    }
    input.value = response.path;
  } catch (error) {
    showError(error instanceof Error ? error.message : String(error));
  } finally {
    button.disabled = false;
    onBusy?.(false);
  }
}
