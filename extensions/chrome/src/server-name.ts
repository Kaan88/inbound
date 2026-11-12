// The paired server's name as the extension shows it (EXT.R9, GitHub #2). Pure, so content scripts and the
// settings code can use it without the transport.

/** The longest server name kept or shown; the server cuts its own to the same length. */
export const MAX_SERVER_NAME = 100;

/** A server name as kept: no control characters, trimmed, at most MAX_SERVER_NAME code points long; "" for none. */
export function cleanServerName(value: unknown): string {
  if (typeof value !== "string") return "";
  // Cut by code point, so an emoji at the cut is kept whole or left out, never halved.
  return Array.from(value.replace(/[\u0000-\u001f\u007f-\u009f]/g, "").trim()).slice(0, MAX_SERVER_NAME).join("").trim();
}

/** A server address's host, the name the popup and the forms call it by when nothing better is known. */
export function hostName(address: string): string {
  try { return new URL(address).hostname || address; } catch { return address.replace(/^https?:\/\//, ""); }
}

/**
 * The server's name as the extension shows it (EXT.R9, GitHub #2): the user's own name for it (Settings, Server),
 * else the name the server gave when it was paired, else the host of its address.
 */
export function serverDisplayName(address: string, serverName?: string, customName?: string): string {
  return cleanServerName(customName) || cleanServerName(serverName) || hostName(address);
}
