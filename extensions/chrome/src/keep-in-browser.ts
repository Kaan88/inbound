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
