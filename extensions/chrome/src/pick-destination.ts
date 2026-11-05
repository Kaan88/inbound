interface FolderPickerResponse {
  ok?: boolean;
  cancelled?: boolean;
  path?: string;
  error?: string;
}

export async function pickDestinationFolder(
  input: HTMLInputElement,
  button: HTMLButtonElement,
  showError: (message: string) => void,
): Promise<void> {
  button.disabled = true;
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
  }
}
