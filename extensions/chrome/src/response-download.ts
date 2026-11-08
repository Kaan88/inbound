// EXT.B5: Firefox shows its own Save dialog before downloads.onCreated fires, so there a download is caught from
// its response headers instead, before Firefox decides what to do with it. This file only decides whether a
// response is one Firefox would download; background.ts does the rest.

/** What onHeadersReceived gives for one response, as far as this needs it. */
export interface ResponseDetails {
  url: string;
  method: string;
  statusCode: number;
  type: string;
  responseHeaders?: Array<{ name: string; value?: string }>;
}

/** A response Firefox would save rather than show. */
export interface ResponseDownload {
  /** From Content-Disposition, or "" when it names none. */
  fileName: string;
  mime: string;
  /** Content-Length of a whole (200) response, else undefined. */
  totalBytes?: number;
}

// Types Firefox shows in the tab rather than downloads. When unsure, a type is treated as shown: a download
// missed here still reaches onCreated as before, while a page taken from the tab by mistake would be lost.
const SHOWN_TYPES = new Set([
  "application/pdf", "application/json", "application/xml", "application/xhtml+xml", "application/javascript",
  "application/x-javascript", "application/ecmascript", "application/rss+xml", "application/atom+xml",
  "application/manifest+json", "application/ld+json", "application/wasm", "application/x-shockwave-flash",
  "video/mp4", "video/webm", "video/ogg", "audio/mpeg", "audio/mp3", "audio/mp4", "audio/ogg", "audio/webm",
  "audio/wav", "audio/x-wav", "audio/wave", "audio/flac", "audio/x-flac", "audio/aac", "audio/opus",
]);

function header(details: ResponseDetails, name: string): string {
  const wanted = name.toLowerCase();
  return details.responseHeaders?.find((item) => item.name.toLowerCase() === wanted)?.value?.trim() ?? "";
}

function isShownType(mime: string): boolean {
  if (!mime) return true; // Firefox sniffs a response without a type; that is its call to make.
  if (SHOWN_TYPES.has(mime)) return true;
  if (mime.startsWith("text/") || mime.startsWith("multipart/")) return true;
  if (mime.endsWith("+xml") || mime.endsWith("+json")) return true;
  // Images Firefox draws. Others (TIFF, Photoshop, camera raw) are saved.
  if (mime.startsWith("image/")) return !/^image\/(?:tiff|vnd\.|x-(?:canon|nikon|sony|adobe|raw|dcraw|panasonic|olympus|fuji))/.test(mime);
  return false;
}

/** The file name a Content-Disposition value gives: filename* (RFC 5987) first, then filename. */
export function contentDispositionFileName(value: string): string {
  const extended = /filename\*\s*=\s*([^;]+)/i.exec(value);
  if (extended) {
    const raw = extended[1]!.trim().replace(/^"(.*)"$/, "$1");
    const quote = raw.indexOf("''");
    const encoded = quote >= 0 ? raw.slice(quote + 2) : raw;
    try {
      const decoded = decodeURIComponent(encoded);
      if (decoded) return safeName(decoded);
    } catch { /* fall back to filename= */ }
  }
  const plain = /filename\s*=\s*("((?:[^"\\]|\\.)*)"|[^;]+)/i.exec(value);
  if (!plain) return "";
  // Only \" and \\ are unescaped: servers send Windows paths with bare backslashes, and those must stay
  // separators so that only the last part of the path is kept.
  const name = plain[2] !== undefined ? plain[2].replace(/\\(["\\])/g, "$1") : plain[1]!.trim();
  return safeName(name);
}

/** A name only, never a path: the last segment, with nothing that would climb out of a folder. */
function safeName(value: string): string {
  const last = value.split(/[\\/]/).pop()?.trim() ?? "";
  return last === "." || last === ".." ? "" : last;
}

/**
 * Whether Firefox would download this response rather than show it. Only a whole or partial GET answer to a
 * page or frame load counts: redirects and errors are not files, a form POST cannot be sent again, and fetches
 * made by a page's scripts are the page's own.
 */
export function responseDownload(details: ResponseDetails): ResponseDownload | null {
  if (details.method !== "GET") return null;
  if (details.type !== "main_frame" && details.type !== "sub_frame") return null;
  if (details.statusCode !== 200 && details.statusCode !== 206) return null;
  if (!/^https?:/i.test(details.url)) return null;

  const disposition = header(details, "content-disposition");
  const mime = header(details, "content-type").split(";")[0]!.trim().toLowerCase();
  const attachment = /^\s*attachment\b/i.test(disposition);
  if (!attachment && isShownType(mime)) return null;

  const length = Number(header(details, "content-length"));
  // A compressed answer's length is the compressed size, not the file's.
  const encoding = header(details, "content-encoding").toLowerCase();
  const whole = details.statusCode === 200 && (encoding === "" || encoding === "identity");
  return {
    fileName: disposition ? contentDispositionFileName(disposition) : "",
    mime,
    totalBytes: whole && Number.isFinite(length) && length > 0 ? length : undefined,
  };
}
