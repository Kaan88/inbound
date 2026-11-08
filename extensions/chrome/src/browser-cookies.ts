/// <reference types="chrome" />

export interface BrowserCookieContext {
  storeId?: string;
  partitionKey?: chrome.cookies.CookiePartitionKey;
  unavailable?: boolean;
}

export async function cookieContextForFrame(tabId?: number, frameId = 0): Promise<BrowserCookieContext> {
  if (tabId === undefined) return {};
  const context: BrowserCookieContext = {};
  try {
    const stores = await chrome.cookies.getAllCookieStores();
    const store = stores.find(item => item.tabIds.includes(tabId));
    if (!store) return { unavailable: true };
    context.storeId = store.id;
  } catch {
    // Never substitute another profile's cookie store for a known source tab.
    return { unavailable: true };
  }
  if (typeof chrome.cookies.getPartitionKey === "function") {
    try {
      context.partitionKey = (await chrome.cookies.getPartitionKey({ tabId, frameId })).partitionKey;
    } catch { /* Older browsers/closed frames still support unpartitioned cookies. */ }
  }
  return context;
}

async function cookiesFor(url: string, context: BrowserCookieContext): Promise<chrome.cookies.Cookie[] | null> {
  let cookies: chrome.cookies.Cookie[];
  try {
    cookies = (await chrome.cookies.getAll({ url, storeId: context.storeId }))
      .filter(cookie => !cookie.partitionKey);
  } catch { return null; }
  if (context.partitionKey?.topLevelSite) {
    try {
      const partitioned = await chrome.cookies.getAll({ url, storeId: context.storeId, partitionKey: context.partitionKey });
      cookies.push(...partitioned.filter(cookie =>
        cookie.partitionKey?.topLevelSite === context.partitionKey!.topLevelSite &&
        !!cookie.partitionKey?.hasCrossSiteAncestor === !!context.partitionKey!.hasCrossSiteAncestor));
    } catch { /* Keep ordinary session cookies if partition queries are unsupported. */ }
  }
  return cookies;
}

function authorityOf(url: string): string | null {
  try {
    const parsed = new URL(url);
    return `${parsed.protocol}//${parsed.host}`.toLowerCase();
  } catch { return null; }
}

/**
 * The Cookie header for a download that started at the first address and may have been redirected to
 * the others (a download's url and finalUrl). Cookies of a redirect target on the same origin are
 * added, without duplicates, since a path-scoped cookie for the final address is not sent to the first
 * one. A target on another origin adds nothing: the app starts again from the first address and sends
 * these cookies only there, so another site's cookies would go to the wrong site and be of no use.
 */
export async function browserCookieHeaders(url: string, context: BrowserCookieContext = {}, ...redirectTargets: Array<string | undefined>): Promise<Record<string, string>> {
  if (context.unavailable) return {};
  const origin = authorityOf(url);
  const addresses = [url, ...redirectTargets.filter((target): target is string =>
    !!target && target !== url && origin !== null && authorityOf(target) === origin)];
  const cookies: chrome.cookies.Cookie[] = [];
  const seen = new Set<string>();
  for (const address of addresses) {
    const found = await cookiesFor(address, context);
    if (found === null) {
      if (address === url) return {};
      continue;
    }
    for (const cookie of found) {
      const key = JSON.stringify([cookie.name, cookie.domain ?? "", cookie.path, cookie.partitionKey?.topLevelSite ?? ""]);
      if (seen.has(key)) continue;
      seen.add(key);
      cookies.push(cookie);
    }
  }
  // Preserve same-name cookies at different paths, as browsers do. A longer
  // path goes first; stable sorting preserves each query's creation order.
  cookies.sort((left, right) => right.path.length - left.path.length);
  return cookies.length ? { Cookie: cookies.map(cookie => `${cookie.name}=${cookie.value}`).join("; ") } : {};
}
