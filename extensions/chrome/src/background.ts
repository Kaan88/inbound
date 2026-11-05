/// <reference types="chrome" />

import { chooseDownloadFileName } from "./download-filename";
import { removeAcceptedBrowserDownload } from "./browser-download-cleanup";
import { browserCookieHeaders, cookieContextForFrame, type BrowserCookieContext } from "./browser-cookies";

type StartMode = "now" | "later";

interface Settings {
  enabled: boolean;
  autoIntercept: boolean;
  mediaDetectionEnabled: boolean;
  startMode: StartMode;
  defaultDestination: string;
  interceptedExtensions: string[];
  excludedExtensions: string[];
  excludedHosts: string[];
  notifications: boolean;
}

interface PendingHandoff {
  id: string;
  url: string;
  pageUrl?: string;
  referrer?: string;
  userAgent: string;
  suggestedFileName?: string;
  mimeType?: string;
  totalBytes?: number;
  isAutoIntercept: boolean;
  createdAt: number;
  cookieContext?: BrowserCookieContext;
}

interface JobSummary {
  id: string;
  fileName: string;
  state: string;
  receivedBytes: number;
  totalBytes?: number;
  progress?: number;
  bytesPerSecond?: number;
  failure?: { message?: string };
  finalFilePath?: string;
}

interface MediaVariant {
  url: string;
  label: string;
  bandwidth?: number;
}

interface MediaItem {
  url: string;
  kind: string;
  label: string;
  variants?: MediaVariant[];
}

interface TabMedia {
  pageUrl: string;
  items: MediaItem[];
  updatedAt: number;
}

interface NativeEnvelope {
  v: number;
  type: string;
  id?: string;
  replyTo?: string;
  payload?: unknown;
}

const DEFAULT_SETTINGS: Settings = {
  enabled: true,
  autoIntercept: true,
  mediaDetectionEnabled: false,
  startMode: "now",
  defaultDestination: "",
  interceptedExtensions: [
    "3gp", "7z", "aac", "ace", "aif", "apk", "arj", "asf", "avi", "bin", "bz2",
    "exe", "gz", "gzip", "img", "iso", "lzh", "m4a", "m4v", "mkv", "mov", "mp3",
    "mpa", "mpe", "mpeg", "mpg", "msi", "msu", "ogg", "ogv", "plj", "pps", "ppt",
    "qt", "ra", "rar", "rm", "rmvb", "sea", "sit", "sitx", "tar", "wav", "wma",
    "wmv", "z", "zip",
  ],
  excludedExtensions: [],
  excludedHosts: [],
  notifications: true,
};

const NATIVE_HOST = "com.kaank.downloadmanager";
const EXTENSION_VERSION = "0.1.3";

let settings: Settings = DEFAULT_SETTINGS;
let settingsLoaded = false;
let settingsLoadPromise: Promise<void> | null = null;
let bypassUntil = 0;
let nativePort: chrome.runtime.Port | null = null;
let lastNativeError = "";
let lastJobs = new Map<string, JobSummary>();
const pendingRequests = new Map<
  string,
  {
    resolve: (payload: any) => void;
    reject: (error: Error) => void;
    timer: ReturnType<typeof setTimeout>;
  }
>();
const pendingHandoffs = new Map<string, PendingHandoff>();
const downloadRequestIds = new Map<number, string>();
const mediaByTab = new Map<number, TabMedia>();
const variantCache = new Map<string, { expires: number; variants: MediaVariant[] }>();
let mediaSyncQueue: Promise<void> = Promise.resolve();
async function hasDeleteGesture(url: string): Promise<boolean> {
  // DownloadItem has no tab id. Ask page scripts for the exact initiating link
  // before pausing Chrome, rather than racing a fire-and-forget click message
  // against downloads.onCreated. No page URLs or key history are retained here.
  try {
    const tabs = await chrome.tabs.query({});
    const replies = new Promise<boolean>(resolve => {
      void Promise.all(tabs.filter(tab => tab.id !== undefined).map(async tab => {
        try {
          const response = await chrome.tabs.sendMessage(tab.id!, { type: "dm.consumeCaptureBypass", url }, { frameId: 0 });
          if (response?.bypass === true) resolve(true);
        } catch { /* Restricted pages have no content script. */ }
      })).then(() => resolve(false));
    });
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      return await Promise.race([
        replies,
        new Promise<boolean>(resolve => { timer = setTimeout(() => resolve(false), 300); }),
      ]);
    } finally { clearTimeout(timer); }
  } catch { return false; }
}

void initialize();

async function initialize(): Promise<void> {
  await ensureSettingsLoaded();
  await installMenus();
  chrome.contextMenus.onClicked.addListener((info, tab) => {
    void onContextMenu(info, tab);
  });
  chrome.downloads.onCreated.addListener((item) => {
    void onDownloadCreated(item);
  });
  chrome.tabs.onRemoved.addListener((tabId) => {
    mediaByTab.delete(tabId);
  });
  chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
    if (changeInfo.status === "loading") {
      mediaByTab.delete(tabId);
      void updateBadge(tabId, 0);
    }
  });
  chrome.runtime.onInstalled.addListener(() => {
    void installMenus();
  });
  chrome.runtime.onStartup.addListener(() => {
    void loadSettings().then(syncMediaDetectionWithTabs);
  });
}

async function ensureSettingsLoaded(): Promise<void> {
  if (settingsLoaded) return;
  settingsLoadPromise ??= loadSettings();
  try {
    await settingsLoadPromise;
  } finally {
    settingsLoadPromise = null;
  }
}

async function loadSettings(): Promise<void> {
  const stored = await chrome.storage.local.get("settings");
  settings = {
    ...DEFAULT_SETTINGS,
    ...(stored.settings ?? {}),
  };
  // Earlier releases saved one-off handoff destinations as a global override.
  // Browser downloads now defer to the application's configured default.
  let changed = false;
  if (settings.defaultDestination) {
    settings.defaultDestination = "";
    changed = true;
  }
  if (typeof stored.settings?.mediaDetectionEnabled !== "boolean") {
    changed = true;
  }
  if (changed) {
    await chrome.storage.local.set({ settings });
  }
  const normalized = await chrome.storage.local.get("bypassUntil");
  bypassUntil = typeof normalized.bypassUntil === "number" ? normalized.bypassUntil : 0;
  settingsLoaded = true;
}

function syncMediaDetectionWithTabs(): Promise<void> {
  const sync = mediaSyncQueue.then(async () => {
    const enabled = settings.mediaDetectionEnabled === true;
    try {
      const tabs = await chrome.tabs.query({});
      // A newer Options save may have happened during the query. Its queued sync
      // will publish the latest value; this stale pass must not clear/broadcast.
      if (enabled !== (settings.mediaDetectionEnabled === true)) return;
      if (!enabled) {
        mediaByTab.clear();
        await Promise.all(tabs
          .filter(tab => typeof tab.id === "number")
          .map(tab => updateBadge(tab.id!, 0)));
      }
      for (const tab of tabs) {
        if (enabled !== (settings.mediaDetectionEnabled === true)) return;
        if (typeof tab.id !== "number") continue;
        try {
          await chrome.tabs.sendMessage(
            tab.id, { type: "dm.mediaDetectionChanged", enabled }, { frameId: 0 });
        } catch {
          // The tab may be restricted or may not have loaded the content script yet.
        }
      }
    } catch {
      // Tab synchronization is best effort; each content script also requests the setting on load.
    }
  });
  mediaSyncQueue = sync.catch(() => undefined);
  return mediaSyncQueue;
}

async function installMenus(): Promise<void> {
  await chrome.contextMenus.removeAll();
  chrome.contextMenus.create({
    id: "dm.download-link",
    title: "Download with Inbound Download Manager",
    contexts: ["link"],
  });
  chrome.contextMenus.create({
    id: "dm.download-media",
    title: "Download with Inbound Download Manager",
    contexts: ["video", "audio", "image"],
  });
}

async function onContextMenu(info: chrome.contextMenus.OnClickData, tab?: chrome.tabs.Tab): Promise<void> {
  const url = info.linkUrl ?? info.srcUrl;
  if (!url) {
    return;
  }

  if (!isHttpUrl(url)) {
    notify("Download Manager", "This link type cannot be handed to the download manager.");
    return;
  }

  const id = crypto.randomUUID();
  const pending: PendingHandoff = {
    id,
    url,
    pageUrl: info.pageUrl ?? tab?.url,
    referrer: info.frameUrl ?? info.pageUrl ?? tab?.url,
    userAgent: navigator.userAgent,
    suggestedFileName: inferFileName(url, info.linkUrl ? "link" : "media"),
    isAutoIntercept: false,
    createdAt: Date.now(),
    cookieContext: await cookieContextForFrame(tab?.id, info.frameId ?? 0),
  };
  pendingHandoffs.set(id, pending);
  await chrome.storage.session.set({ [`handoff.${id}`]: pending });
  await chrome.windows.create({
    url: chrome.runtime.getURL(`handoff.html?id=${encodeURIComponent(id)}`),
    type: "popup",
    width: 560,
    height: 680,
  });
}

async function onDownloadCreated(item: chrome.downloads.DownloadItem): Promise<void> {
  if (!settings.enabled || !settings.autoIntercept) {
    return;
  }

  if (Date.now() < bypassUntil) {
    return;
  }

  if (await hasDeleteGesture(item.url)) {
    return;
  }

  const url = item.finalUrl || item.url;
  if (!isHttpUrl(url)) {
    return;
  }

  const fileName = chooseDownloadFileName(item.filename, item.url, item.finalUrl);
  const extension = extensionOf(fileName || url);
  const host = hostOf(url);
  if (!host) {
    return;
  }

  if (settings.excludedHosts.some((h) => hostMatches(host, h))) {
    return;
  }

  if (extension && settings.excludedExtensions.includes(extension)) {
    return;
  }

  if (extension && settings.interceptedExtensions.length > 0 &&
      !settings.interceptedExtensions.includes(extension)) {
    return;
  }

  if (!extension && /^text\/html/i.test(item.mime ?? "")) {
    return;
  }

  try {
    await chrome.downloads.pause(item.id);
  } catch {
    // The download may have completed before we could pause it.
    return;
  }

  let requestId = downloadRequestIds.get(item.id);
  if (!requestId) {
    requestId = crypto.randomUUID();
    downloadRequestIds.set(item.id, requestId);
  }

  let hostAccepted = false;
  try {
    const ack = await requestHandoff({
      requestId,
      extensionId: chrome.runtime.id,
      url: item.url,
      finalUrl: item.finalUrl || undefined,
      pageUrl: item.referrer || undefined,
      referrer: item.referrer || undefined,
      userAgent: navigator.userAgent,
      suggestedFileName: fileName,
      mimeType: item.mime || undefined,
      totalBytes: item.totalBytes > 0 ? item.totalBytes : item.fileSize > 0 ? item.fileSize : undefined,
      startMode: settings.startMode,
      isAutoIntercept: true,
      headers: await cookieHeaders(item.url),
      destinationDirectory: settings.defaultDestination || undefined,
    });

    if (ack.accepted) {
      hostAccepted = true;
      const browserEntryRemoved = await removeAcceptedBrowserDownload(chrome.downloads, item.id);
      downloadRequestIds.delete(item.id);
      notify(
        browserEntryRemoved
          ? ack.duplicate ? "Already in Download Manager" : "Sent to Download Manager"
          : "Sent to Download Manager; browser cleanup incomplete",
        browserEntryRemoved ? fileName : "The browser entry could not be cleared. Check the browser's Downloads menu.",
      );
    } else {
      await chrome.downloads.resume(item.id).catch(() => undefined);
      notify("Download Manager did not accept the download", ack.error ?? "The host rejected the request.");
      lastNativeError = ack.error ?? "handoff rejected";
    }
  } catch (error) {
    if (hostAccepted) {
      // Cleanup/notification failures cannot revoke durable native acceptance.
      // Resuming here would start a second browser transfer of the same file.
      downloadRequestIds.delete(item.id);
      return;
    }
    await chrome.downloads.resume(item.id).catch(() => undefined);
    downloadRequestIds.delete(item.id);
    const message = error instanceof Error ? error.message : String(error);
    lastNativeError = message;
    notify("Download Manager unavailable", `${message}. Chrome will keep downloading.`);
  }
}

async function requestHandoff(payload: Record<string, unknown>): Promise<any> {
  try {
    return await sendNative("handoff.request", payload, 15000, 1);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    if (/native|not found|disconnected|host/i.test(message)) {
      // One reconnect is already attempted inside sendNative; surface a usable error.
      throw new Error(`Could not reach the Download Manager host: ${message}`);
    }
    throw error;
  }
}

async function sendNative(
  type: string,
  payload: unknown,
  timeoutMs = 12000,
  retries = 1,
): Promise<any> {
  let lastError: Error | null = null;
  for (let attempt = 0; attempt <= retries; attempt++) {
    try {
      return await sendNativeOnce(type, payload, timeoutMs);
    } catch (error) {
      lastError = error instanceof Error ? error : new Error(String(error));
      if (attempt < retries) {
        disconnectPort();
        await new Promise((resolve) => setTimeout(resolve, 300));
      }
    }
  }

  throw lastError ?? new Error("Native messaging failed.");
}

async function sendNativeOnce(type: string, payload: unknown, timeoutMs: number): Promise<any> {
  const port = ensurePort();
  const envelope: NativeEnvelope = {
    v: 1,
    type,
    id: crypto.randomUUID(),
    payload,
  };

  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      pendingRequests.delete(envelope.id!);
      reject(new Error(`Timed out waiting for the native host (${type}).`));
    }, timeoutMs);

    pendingRequests.set(envelope.id!, {
      resolve: (result) => {
        clearTimeout(timer);
        resolve(result);
      },
      reject: (error) => {
        clearTimeout(timer);
        reject(error);
      },
      timer,
    });

    try {
      port.postMessage(envelope);
    } catch (error) {
      pendingRequests.delete(envelope.id!);
      clearTimeout(timer);
      reject(error instanceof Error ? error : new Error(String(error)));
    }
  });
}

function ensurePort(): chrome.runtime.Port {
  if (nativePort) {
    return nativePort;
  }

  const port = chrome.runtime.connectNative(NATIVE_HOST);
  port.onMessage.addListener((message: NativeEnvelope) => {
    handleNativeMessage(message);
  });
  port.onDisconnect.addListener(() => {
    const message = chrome.runtime.lastError?.message ?? "Native host disconnected.";
    lastNativeError = message;
    nativePort = null;
    rejectAll(new Error(message));
  });
  nativePort = port;
  lastNativeError = "";
  return port;
}

function pickNativeFolder(initialPath?: string): Promise<any> {
  // A modal picker can stay open for minutes. Use its own native connection so
  // it cannot block automatic handoffs on the normal sequential IPC connection.
  const port = chrome.runtime.connectNative(NATIVE_HOST);
  const id = crypto.randomUUID();
  return new Promise((resolve, reject) => {
    let finished = false;
    const finish = (result: any, error?: Error): void => {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      try { port.disconnect(); } catch { /* already closed */ }
      if (error) reject(error); else resolve(result);
    };
    const timer = setTimeout(() => finish(null, new Error("Folder selection timed out.")), 120000);
    port.onMessage.addListener((message: NativeEnvelope) => {
      if (message.replyTo !== id) return;
      if (message.type !== "host.folderPicked") {
        finish(null, new Error("This Host does not support folder selection. Update the desktop application."));
      } else finish(message.payload);
    });
    port.onDisconnect.addListener(() => finish(null,
      new Error(chrome.runtime.lastError?.message ?? "The folder selection connection closed.")));
    try { port.postMessage({ v: 1, type: "host.pickFolder", id, payload: { initialPath } }); }
    catch (error) { finish(null, error instanceof Error ? error : new Error(String(error))); }
  });
}

function disconnectPort(): void {
  if (!nativePort) {
    return;
  }

  try {
    nativePort.disconnect();
  } catch {
    // ignore
  }
  nativePort = null;
}

function handleNativeMessage(message: NativeEnvelope): void {
  if (message.replyTo) {
    const pending = pendingRequests.get(message.replyTo);
    if (pending) {
      pendingRequests.delete(message.replyTo);
      if (message.type === "handoff.error" || message.type === "server.incompatible") {
        pending.reject(new Error(extractError(message.payload)));
      } else {
        pending.resolve(message.payload);
      }
      return;
    }
  }

  if (message.type === "job.event") {
    const event = message.payload as { job?: JobSummary; eventType?: string };
    if (event.job?.id) {
      lastJobs.set(event.job.id, event.job);
      if (lastJobs.size > 20) {
        const oldest = lastJobs.keys().next().value;
        if (oldest) {
          lastJobs.delete(oldest);
        }
      }
      void chrome.runtime.sendMessage({ type: "dm.jobEvent", job: event.job }).catch(() => undefined);
    }
  }
}

function rejectAll(error: Error): void {
  for (const pending of pendingRequests.values()) {
    pending.reject(error);
  }
  pendingRequests.clear();
}

function extractError(payload: unknown): string {
  if (payload && typeof payload === "object") {
    const record = payload as Record<string, unknown>;
    if (typeof record.error === "string") {
      return record.error;
    }
    if (typeof record.message === "string") {
      return record.message;
    }
  }
  return "The native host reported an error.";
}

async function cookieHeaders(url: string): Promise<Record<string, string>> {
  return browserCookieHeaders(url);
}

function isHttpUrl(url: string): boolean {
  return /^https?:\/\//i.test(url);
}

function hostOf(url: string): string | null {
  try {
    return new URL(url).hostname.toLowerCase();
  } catch {
    return null;
  }
}

function hostMatches(host: string, pattern: string): boolean {
  const normalized = pattern.trim().toLowerCase().replace(/^\*\./, "");
  if (!normalized) {
    return false;
  }

  return host === normalized || host.endsWith(`.${normalized}`);
}

function baseName(value: string): string {
  try {
    const path = new URL(value).pathname;
    const name = decodeURIComponent(path.split("/").pop() ?? "");
    if (name) {
      return name;
    }
  } catch {
    // fall through
  }

  const cleaned = value.split(/[\\/]/).pop() ?? "download";
  return cleaned.split("?")[0] || "download";
}

function extensionOf(value: string): string {
  const clean = (value.split("?")[0] ?? "").split("#")[0] ?? "";
  const match = /\.([a-z0-9]{1,10})$/i.exec(clean);
  return match ? match[1]!.toLowerCase() : "";
}

function inferFileName(url: string, kind: "link" | "media"): string {
  const name = baseName(url);
  if (name && name !== "download" && /\./i.test(name)) {
    return name;
  }
  return `${kind}-${Date.now()}`;
}

function parseHlsVariants(text: string, baseUrl: string): MediaVariant[] {
  const lines = text.split(/\r?\n/);
  const variants: MediaVariant[] = [];
  for (let i = 0; i < lines.length; i++) {
    const line = (lines[i] ?? "").trim();
    if (!line.startsWith("#EXT-X-STREAM-INF:")) {
      continue;
    }

    let bandwidth = 0;
    let resolution = "";
    let name = "";
    for (const attribute of line.slice("#EXT-X-STREAM-INF:".length).split(",")) {
      const separator = attribute.indexOf("=");
      if (separator < 0) {
        continue;
      }

      const key = attribute.slice(0, separator).trim().toUpperCase();
      const value = attribute.slice(separator + 1).trim().replace(/^"|"$/g, "");
      if (key === "BANDWIDTH") {
        bandwidth = Number(value) || 0;
      } else if (key === "RESOLUTION") {
        resolution = value;
      } else if (key === "NAME") {
        name = value;
      }
    }

    let uri = "";
    for (let j = i + 1; j < lines.length; j++) {
      const candidate = (lines[j] ?? "").trim();
      if (!candidate || candidate.startsWith("#")) {
        continue;
      }

      uri = candidate;
      break;
    }

    if (!uri) {
      continue;
    }

    try {
      const url = new URL(uri, baseUrl).href;
      const height = Number((resolution.split("x")[1] ?? "").replace("p", ""));
      const label = name || (height > 0
        ? `${height}p`
        : bandwidth > 0 ? `${Math.round(bandwidth / 1000)} kbps` : "variant");
      variants.push({ url, label, bandwidth });
    } catch {
      // ignore malformed variant entries
    }
  }

  return variants.sort((a, b) => (b.bandwidth ?? 0) - (a.bandwidth ?? 0));
}

async function enrichMediaItem(item: MediaItem): Promise<MediaItem> {
  const isHls = item.kind === "hls" || /\.m3u8(\?|#|$)/i.test(item.url);
  if (!isHls) {
    return item;
  }

  const cached = variantCache.get(item.url);
  if (cached && cached.expires > Date.now()) {
    return { ...item, variants: cached.variants.length ? cached.variants : undefined };
  }

  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 4000);
    let response: Response;
    try {
      response = await fetch(item.url, { credentials: "include", signal: controller.signal });
    } finally {
      clearTimeout(timer);
    }

    if (!response.ok) {
      return item;
    }

    const variants = parseHlsVariants(await response.text(), response.url || item.url);
    variantCache.set(item.url, { expires: Date.now() + 5 * 60_000, variants });
    return { ...item, variants: variants.length ? variants : undefined };
  } catch {
    return item;
  }
}

async function updateBadge(tabId: number, count: number): Promise<void> {
  try {
    await chrome.action.setBadgeText({ tabId, text: count > 0 ? String(count) : "" });
    if (count > 0) {
      await chrome.action.setBadgeBackgroundColor({ tabId, color: "#4F8CFF" });
    }
  } catch {
    // The tab may have closed.
  }
}

function notify(title: string, message: string): void {
  if (!settings.notifications) {
    return;
  }

  try {
    chrome.notifications.create({
      type: "basic",
      iconUrl: "icons/icon128.png",
      title,
      message: message.slice(0, 300),
    });
  } catch {
    // notifications are optional
  }
}

chrome.runtime.onMessage.addListener((message: any, sender, sendResponse) => {
  void handleRuntimeMessage(message, sender)
    .then((result) => sendResponse(result))
    .catch((error) => sendResponse({ ok: false, error: error instanceof Error ? error.message : String(error) }));
  return true;
});

async function handleRuntimeMessage(message: any, sender?: chrome.runtime.MessageSender): Promise<any> {
  await ensureSettingsLoaded();
  switch (message?.type) {
    case "dm.getMediaDetectionEnabled":
      return { ok: true, enabled: settings.mediaDetectionEnabled === true };

    case "dm.mediaDetected": {
      const tabId = sender?.tab?.id;
      if (typeof tabId !== "number") {
        return { ok: false, error: "No sender tab." };
      }
      if (settings.mediaDetectionEnabled !== true) {
        mediaByTab.delete(tabId);
        await updateBadge(tabId, 0);
        return { ok: true, count: 0, disabled: true };
      }

      const items = (Array.isArray(message.items) ? message.items : [])
        .filter((item: any) => typeof item?.url === "string" && isHttpUrl(item.url))
        .slice(0, 40)
        .map((item: any) => ({
          url: item.url,
          kind: typeof item.kind === "string" ? item.kind : "media",
          label: typeof item.label === "string" ? item.label.slice(0, 200) : "media",
        })) as MediaItem[];
      mediaByTab.set(tabId, {
        pageUrl: typeof message.pageUrl === "string" ? message.pageUrl : sender?.tab?.url ?? "",
        items,
        updatedAt: Date.now(),
      });
      await updateBadge(tabId, items.length);
      return { ok: true, count: items.length };
    }

    case "dm.rescan": {
      if (settings.mediaDetectionEnabled !== true) return { ok: true, disabled: true };
      const tabs = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
      const tabId = tabs[0]?.id;
      if (typeof tabId === "number") {
        try {
          await chrome.tabs.sendMessage(tabId, { type: "dm.scan" });
        } catch {
          // Pages without a content script (chrome://, store, etc.).
        }
      }
      return { ok: true };
    }

    case "dm.media": {
      let tabId: number | undefined = typeof message.tabId === "number" ? message.tabId : undefined;
      if (tabId === undefined) {
        const tabs = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
        tabId = tabs[0]?.id;
      }

      if (settings.mediaDetectionEnabled !== true) {
        return { ok: true, tabId, pageUrl: "", items: [] };
      }

      const media = typeof tabId === "number" ? mediaByTab.get(tabId) : undefined;
      const items = await Promise.all((media?.items ?? []).map(enrichMediaItem));
      return {
        ok: true,
        tabId,
        pageUrl: media?.pageUrl ?? "",
        items,
      };
    }

    case "dm.queueMedia": {
      if (settings.mediaDetectionEnabled !== true) {
        return { ok: false, error: "Media detection is disabled in extension settings." };
      }
      const url = String(message.url ?? "");
      if (!isHttpUrl(url)) {
        return { ok: false, error: "This media URL cannot be downloaded." };
      }

      const pageUrl = typeof message.pageUrl === "string" ? message.pageUrl : undefined;
      const id = crypto.randomUUID();
      const pending: PendingHandoff = {
        id,
        url,
        pageUrl,
        referrer: pageUrl,
        userAgent: navigator.userAgent,
        suggestedFileName: inferFileName(url, "media"),
        mimeType: typeof message.mimeType === "string" ? message.mimeType : undefined,
        isAutoIntercept: false,
        createdAt: Date.now(),
      };
      pendingHandoffs.set(id, pending);
      await chrome.storage.session.set({ [`handoff.${id}`]: pending });
      await chrome.windows.create({
        url: chrome.runtime.getURL(`handoff.html?id=${encodeURIComponent(id)}`),
        type: "popup",
        width: 560,
        height: 680,
      });
      return { ok: true, id };
    }

    case "dm.openBatch": {
      const tabs = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
      const tab = tabs[0];
      if (typeof tab?.id !== "number") {
        return { ok: false, error: "No active page to scan." };
      }

      await chrome.windows.create({
        url: chrome.runtime.getURL(`batch.html?tabId=${tab.id}`),
        type: "popup",
        width: 780,
        height: 680,
      });
      return { ok: true, tabId: tab.id };
    }

    case "dm.pageLinks": {
      let tabId: number | undefined = typeof message.tabId === "number" ? message.tabId : undefined;
      if (tabId === undefined) {
        const tabs = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
        tabId = tabs[0]?.id;
      }

      if (typeof tabId !== "number") {
        return { ok: false, error: "No active page to scan." };
      }

      try {
        const response = (await chrome.tabs.sendMessage(tabId, { type: "dm.collectLinks" })) as {
          ok?: boolean;
          pageUrl?: string;
          links?: any[];
        };
        const links = (Array.isArray(response?.links) ? response.links : [])
          .filter((item: any) => typeof item?.url === "string" && isHttpUrl(item.url))
          .slice(0, 2000)
          .map((item: any) => ({
            url: item.url,
            text: typeof item.text === "string" ? item.text.slice(0, 160) : "link",
            kind: typeof item.kind === "string" ? item.kind.slice(0, 32) : "link",
            extension: typeof item.extension === "string" ? item.extension.slice(0, 16) : "",
            host: typeof item.host === "string" ? item.host.slice(0, 255) : "",
          }));
        return {
          ok: true,
          tabId,
          pageUrl: typeof response?.pageUrl === "string" ? response.pageUrl : "",
          links,
        };
      } catch {
        return {
          ok: false,
          error: "This page cannot be scanned. Reload it and try again.",
        };
      }
    }

    case "dm.confirmBatch": {
      const urls = (Array.isArray(message.urls) ? message.urls : [])
        .filter((item: any) => typeof item?.url === "string" && isHttpUrl(item.url))
        .slice(0, 200);
      if (urls.length === 0) {
        return { ok: false, error: "No HTTP(S) links were selected." };
      }

      const pageUrl = typeof message.pageUrl === "string" ? message.pageUrl : undefined;
      const startMode = message.startMode === "later" ? "later" : "now";
      const destination = typeof message.destination === "string" && message.destination.trim()
        ? message.destination.trim()
        : settings.defaultDestination || undefined;
      const results: Array<Record<string, unknown>> = [];
      for (const item of urls) {
        try {
          const ack = await requestHandoff({
            requestId: crypto.randomUUID(),
            extensionId: chrome.runtime.id,
            url: item.url,
            pageUrl,
            referrer: pageUrl,
            userAgent: navigator.userAgent,
            suggestedFileName: typeof item.fileName === "string" && item.fileName.trim()
              ? item.fileName.trim().slice(0, 200)
              : inferFileName(item.url, "link"),
            startMode,
            isAutoIntercept: false,
            headers: await cookieHeaders(item.url),
            destinationDirectory: destination,
          });
          results.push({
            url: item.url,
            accepted: !!ack?.accepted,
            duplicate: !!ack?.duplicate,
            error: ack?.error,
            job: ack?.job,
          });
        } catch (error) {
          results.push({
            url: item.url,
            accepted: false,
            error: error instanceof Error ? error.message : String(error),
          });
        }
      }

      const accepted = results.filter((result) => result.accepted === true).length;
      return {
        ok: true,
        accepted,
        failed: results.length - accepted,
        results,
      };
    }

    case "dm.status":
      return {
        ok: true,
        extensionVersion: EXTENSION_VERSION,
        enabled: settings.enabled,
        autoIntercept: settings.autoIntercept,
        connected: nativePort !== null,
        lastError: lastNativeError,
        bypassUntil,
        settings,
        jobs: [...lastJobs.values()],
      };

    case "dm.openApp": {
      await sendNative("host.openUi", { action: message.page ?? "" }, 8000);
      return { ok: true };
    }

    case "dm.pickFolder": {
      const sourcePage = (sender?.url ?? "").split(/[?#]/)[0];
      if (sender?.id !== chrome.runtime.id ||
          ![chrome.runtime.getURL("handoff.html"), chrome.runtime.getURL("batch.html")].includes(sourcePage ?? "")) {
        return { ok: false, error: "Folder selection is available from the download destination screen." };
      }
      return await pickNativeFolder(typeof message.initialPath === "string" ? message.initialPath : undefined);
    }

    case "dm.ping": {
      const result = await sendNative("host.ping", {}, 5000);
      return { ok: true, result };
    }

    case "dm.getHandoff": {
      const id = String(message.id ?? "");
      let pending = pendingHandoffs.get(id);
      if (!pending) {
        const stored = await chrome.storage.session.get(`handoff.${id}`);
        pending = stored[`handoff.${id}`] as PendingHandoff | undefined;
      }
      return pending
        ? {
            ok: true,
            handoff: pending,
            defaults: {
              destination: settings.defaultDestination,
              startMode: settings.startMode,
            },
          }
        : { ok: false, error: "Handoff request expired." };
    }

    case "dm.confirmHandoff": {
      const id = String(message.id ?? "");
      const pending = pendingHandoffs.get(id) ??
        (await chrome.storage.session.get(`handoff.${id}`))[`handoff.${id}`] as PendingHandoff | undefined;
      if (!pending) {
        return { ok: false, error: "Handoff request expired." };
      }

      const startMode: StartMode = message.startMode === "later" ? "later" : "now";
      const destination = typeof message.destination === "string"
        ? message.destination.trim()
        : settings.defaultDestination;
      const ack = await requestHandoff({
        requestId: crypto.randomUUID(),
        extensionId: chrome.runtime.id,
        url: pending.url,
        pageUrl: pending.pageUrl,
        referrer: pending.referrer,
        userAgent: pending.userAgent,
        suggestedFileName: message.fileName || pending.suggestedFileName,
        mimeType: pending.mimeType,
        totalBytes: pending.totalBytes,
        startMode,
        isAutoIntercept: pending.isAutoIntercept,
        headers: await browserCookieHeaders(pending.url, pending.cookieContext),
        destinationDirectory: destination || undefined,
      });

      pendingHandoffs.delete(id);
      await chrome.storage.session.remove(`handoff.${id}`);

      // A folder selected here belongs only to this request. The configured
      // default destination is changed exclusively in extension Options.
      if (ack?.accepted) {
        settings = {
          ...settings,
          startMode,
        };
        await chrome.storage.local.set({ settings });
      }

      return { ok: true, ack };
    }

    case "dm.saveSettings": {
      settings = {
        ...DEFAULT_SETTINGS,
        ...message.settings,
        // An Options page left open across an extension reload may still send
        // the removed persistent-folder field. It must not restore the override.
        defaultDestination: "",
      };
      await chrome.storage.local.set({ settings });
      settingsLoaded = true;
      await syncMediaDetectionWithTabs();
      return { ok: true, settings };
    }

    case "dm.bypass": {
      bypassUntil = Date.now() + 5 * 60 * 1000;
      await chrome.storage.local.set({ bypassUntil });
      return { ok: true, bypassUntil };
    }

    case "dm.jobs": {
      try {
        const snapshot = await sendNative("jobs.query", {}, 6000);
        const jobs = (snapshot?.jobs ?? []) as JobSummary[];
        for (const job of jobs) {
          lastJobs.set(job.id, job);
        }
        return { ok: true, jobs };
      } catch (error) {
        return { ok: false, error: error instanceof Error ? error.message : String(error), jobs: [...lastJobs.values()] };
      }
    }

    default:
      return { ok: false, error: "Unknown message." };
  }
}
