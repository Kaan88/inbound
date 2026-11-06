// The IDs the desktop app accepts: BrowserBridge.cs holds the same two. Only these can connect.
const STORE_EXTENSION_ID = "olhbcdbjdnfoiinllnlpandggadfabfm";
const UNPACKED_EXTENSION_ID = "pknddmneejadnnngffojnoecojhdfalc";

/** True when the error means the desktop app is not on this computer at all. */
export function isAppMissing(error: string | undefined): boolean {
  return /not found/i.test(error ?? "");
}

/** What a connection error means to someone using the extension, in place of Chrome's wording. */
export function connectionProblem(
  error: string | undefined,
  extensionId: string | undefined = globalThis.chrome?.runtime?.id,
): string {
  if (isAppMissing(error)) {
    return "This extension needs the Inbound Download Manager desktop app for Windows, which is not installed.";
  }
  if (/forbidden/i.test(error ?? "")) {
    // Updating the app cannot help a copy whose ID came from its own folder.
    if (extensionId && extensionId !== STORE_EXTENSION_ID && extensionId !== UNPACKED_EXTENSION_ID) {
      return `The desktop app does not accept this copy of the extension (ID ${extensionId}). ` +
        "A store package loaded unpacked gets a new ID from its folder. " +
        "Install from the Chrome Web Store, or load the \"extension\" folder from the app's install folder.";
    }
    return "Update the Inbound Download Manager desktop app. The installed version does not recognise this extension.";
  }
  return "Start Inbound Download Manager, or run its installer again.";
}

/** An error ready to show: connection errors in plain words, anything else as it is. */
export function readableError(message: string): string {
  return /native|could not reach/i.test(message) ? connectionProblem(message) : message;
}
