function filenameFromUrl(value: string | undefined): string {
  if (!value) return "";
  try {
    const path = new URL(value).pathname;
    const encoded = path.split("/").pop() ?? "";
    // A decoded separator belongs to the filename, never to a local path.
    try { return decodeURIComponent(encoded).replace(/[\\/]/g, "_"); } catch { return encoded; }
  } catch {
    return "";
  }
}

function isOpaqueGuidName(value: string): boolean {
  const name = value.split(/[\\/]/).pop()?.trim() ?? "";
  return /^(?:uuid)?[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}(?:\.[a-z0-9]{1,10})?$/i.test(name);
}

/** Selects a browser-generated filename for automatic interception only. */
export function chooseDownloadFileName(
  chromeName: string | undefined,
  originalUrl: string,
  finalUrl?: string,
): string {
  const actual = chromeName?.trim() ?? "";
  if (actual && !isOpaqueGuidName(actual)) return actual.split(/[\\/]/).pop() ?? actual;

  const originalName = filenameFromUrl(originalUrl);
  if (originalName && !isOpaqueGuidName(originalName)) return originalName;

  const finalName = filenameFromUrl(finalUrl);
  if (finalName && !isOpaqueGuidName(finalName)) return finalName;
  return actual || originalName || finalName || "download";
}
