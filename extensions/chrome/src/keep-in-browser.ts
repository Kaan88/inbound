export interface KeepInBrowserToken {
  url: string;
  expires: number;
}

export function normalizeDownloadUrl(value: string): string {
  try {
    const url = new URL(value);
    if (url.protocol !== "http:" && url.protocol !== "https:") return "";
    url.hash = "";
    return url.href;
  } catch {
    return "";
  }
}

// A gesture keeps one transfer in the browser. Match the original link so redirects
// cannot make the token apply to unrelated downloads at the final host.
export function consumeKeepInBrowser(tokens: KeepInBrowserToken[], url: string, now: number): boolean {
  const normalized = normalizeDownloadUrl(url);
  const index = tokens.findIndex(token => token.expires > now && token.url === normalized);
  if (index < 0) return false;
  tokens.splice(index, 1);
  return true;
}

const AGAIN = "Download it again from the page.";
const REFUSED = "The browser asked the site for it again and the site refused it (a link that works only once, or only " +
  "from its page). " + AGAIN;

/**
 * What to tell the user when a download the browser started again for keepInBrowser was interrupted, from the
 * browser's interrupt reason (chrome.downloads.InterruptReason). undefined when nothing needs saying: the user
 * cancelled it, or the browser was closed (it offers to resume it itself). Only a server reason is the site
 * refusing the second request (audit 3 M4 review).
 */
export function restartInterruptedText(reason: string | undefined): string | undefined {
  const code = (reason ?? "").toUpperCase();
  if (code === "USER_CANCELED" || code === "USER_SHUTDOWN") return undefined;
  if (code.startsWith("SERVER_")) return REFUSED;
  if (code === "FILE_NO_SPACE") return "The browser could not save it: the disk is full. Free some space and " + AGAIN.toLowerCase();
  if (code === "FILE_ACCESS_DENIED") return "The browser could not save it: it may not write to its download folder. " + AGAIN;
  if (code === "FILE_NAME_TOO_LONG") return "The browser could not save it: the file name is too long. " + AGAIN;
  if (code === "FILE_TOO_LARGE") return "The browser could not save it: the file is too large for the disk. " + AGAIN;
  if (code === "FILE_VIRUS_INFECTED" || code === "FILE_BLOCKED" || code === "FILE_SECURITY_CHECK_FAILED")
    return "The browser or Windows blocked the file it downloaded again.";
  if (code.startsWith("FILE_")) return "The browser could not save it. " + AGAIN;
  if (code.startsWith("NETWORK_"))
    return "The connection failed while the browser downloaded it again. Resume it from the browser's downloads list, " +
      "or " + AGAIN.charAt(0).toLowerCase() + AGAIN.slice(1);
  if (code === "CRASH") return "The browser stopped while downloading it again. Resume it from the browser's downloads list.";
  return "The browser's second download of it stopped. " + AGAIN;
}

/**
 * True when a restarted download finished as a web page although the first response was a file: the site answered
 * the second request with its error or sign-in page, saved under the file's name (audit 3 M4 review). Without the
 * first response's type nothing is known, so false.
 */
export function restartSavedWebPage(expectedMime: string | undefined, savedMime: string | undefined): boolean {
  const html = (mime: string | undefined) => /^(text\/html|application\/xhtml\+xml)\b/i.test((mime ?? "").trim());
  if (!expectedMime || !savedMime) return false;
  return html(savedMime) && !html(expectedMime);
}

export const RESTART_SAVED_WEB_PAGE_TEXT = "The site answered the browser's second request with a web page, not the " +
  "file (a link that works only once, or only from its page), so the file saved under that name is that page. " + AGAIN;
