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

export async function browserCookieHeaders(url: string, context: BrowserCookieContext = {}): Promise<Record<string, string>> {
  if (context.unavailable) return {};
  let cookies: chrome.cookies.Cookie[];
  try {
    cookies = (await chrome.cookies.getAll({ url, storeId: context.storeId }))
      .filter(cookie => !cookie.partitionKey);
  } catch { return {}; }
  if (context.partitionKey?.topLevelSite) {
    try {
      const partitioned = await chrome.cookies.getAll({ url, storeId: context.storeId, partitionKey: context.partitionKey });
      cookies.push(...partitioned.filter(cookie =>
        cookie.partitionKey?.topLevelSite === context.partitionKey!.topLevelSite &&
        !!cookie.partitionKey?.hasCrossSiteAncestor === !!context.partitionKey!.hasCrossSiteAncestor));
    } catch { /* Keep ordinary session cookies if partition queries are unsupported. */ }
  }
  // Preserve same-name cookies at different paths, as browsers do. A longer
  // path goes first; stable sorting preserves each query's creation order.
  cookies.sort((left, right) => right.path.length - left.path.length);
  return cookies.length ? { Cookie: cookies.map(cookie => `${cookie.name}=${cookie.value}`).join("; ") } : {};
}
