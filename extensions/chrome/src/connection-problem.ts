/** True when the error means the desktop app is not on this computer at all. */
export function isAppMissing(error: string | undefined): boolean {
  return /not found/i.test(error ?? "");
}

/** What a connection error means to someone using the extension, in place of Chrome's wording. */
export function connectionProblem(error: string | undefined): string {
  if (isAppMissing(error)) {
    return "This extension needs the Inbound Download Manager desktop app for Windows, which is not installed.";
  }
  if (/forbidden/i.test(error ?? "")) {
    return "Update the Inbound Download Manager desktop app. The installed version does not recognise this extension.";
  }
  return "Start Inbound Download Manager, or run its installer again.";
}

/** An error ready to show: connection errors in plain words, anything else as it is. */
export function readableError(message: string): string {
  return /native|could not reach/i.test(message) ? connectionProblem(message) : message;
}
