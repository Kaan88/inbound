/// <reference types="chrome" />

import { connectionProblem, readableError } from "./connection-problem";
import { isUnsupportedUrl, UNSUPPORTED_SITE_MESSAGE } from "./unsupported-sites";
import { chooseDownloadFileName } from "./download-filename";
import { removeAcceptedBrowserDownload } from "./browser-download-cleanup";
import { browserCookieHeaders, cookieContextForFrame, type BrowserCookieContext } from "./browser-cookies";

type StartMode = "now" | "later";

interface Settings {
  enabled: boolean;
  autoIntercept: boolean;
  startMode: StartMode;
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

interface NativeEnvelope {
  v: number;
  type: string;
  id?: string;
  replyTo?: string;
  payload?: unknown;
}

const DEFAULT_SETTINGS: Settings = {
  enabled: true,
  // Off until the user turns it on in Settings, next to the explanation of what it shares.
  autoIntercept: false,
  startMode: "now",
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

// Content scripts run alongside pages this extension does not trust, so they get only what they
// need. Everything else, cookies and settings above all, is for the extension's own pages.
const CONTENT_SCRIPT_MESSAGES = new Set(["dm.getAutoSend"]);
const NATIVE_HOST = "com.kaank.downloadmanager";
const EXTENSION_VERSION = chrome.runtime.getManifest().version;
let settings: Settings = DEFAULT_SETTINGS;
let settingsLoaded = false;
let settingsLoadPromise: Promise<void> | null = null;
let pauseUntil = 0;
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
let autoSendSyncQueue: Promise<void> = Promise.resolve();
async function hasDeleteGesture(url: string): Promise<boolean> {
  // DownloadItem has no tab id. Ask page scripts for the exact initiating link
  // before pausing Chrome, rather than racing a fire-and-forget click message
  // against downloads.onCreated. No page URLs or key history are retained here.
  try {
    const tabs = await chrome.tabs.query({});
    const replies = new Promise<boolean>(resolve => {
      void Promise.all(tabs.filter(tab => tab.id !== undefined).map(async tab => {
        try {
          const response = await chrome.tabs.sendMessage(tab.id!, { type: "dm.consumeKeepInBrowser", url }, { frameId: 0 });
          if (response?.keep === true) resolve(true);
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

// MV3: listeners must be registered synchronously at top level, or the event
// that wakes a suspended worker is dropped. Handlers await settings themselves.
chrome.contextMenus.onClicked.addListener((info, tab) => {
  void onContextMenu(info, tab);
});
// Interception happens while the browser waits for the target file name.
// Chromium adds a download to its UI only after the name is decided, so
// holding the suggestion (returning true) while the Host is asked keeps the
// download bubble from opening. An accepted download is cancelled without ever
// answering; anything kept by the browser gets the default name and resumes.
chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
  let answered = false;
  const answer = (): void => {
    if (answered) return;
    answered = true;
    try { suggest(); } catch { /* the download is already gone */ }
  };
  void onDownloadCreated(item)
    .then((intercepted) => { if (!intercepted) answer(); })
    .catch(() => answer());
  return true;
});
chrome.runtime.onInstalled.addListener((details) => {
  void installMenus();
  if (details.reason === "install") {
    void chrome.runtime.openOptionsPage();
  } else if (details.reason === "update") {
    void keepCatchingAfterUpgrade(details.previousVersion);
  }
});
chrome.runtime.onStartup.addListener(() => {
  void loadSettings().then(syncAutoSendWithTabs);
});

void initialize();

async function initialize(): Promise<void> {
  await ensureSettingsLoaded();
  await installMenus();
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
  const normalized = await chrome.storage.local.get("pauseUntil");
  pauseUntil = typeof normalized.pauseUntil === "number" ? normalized.pauseUntil : 0;
  settingsLoaded = true;
}

// Automatic catching was on from the start before 0.4.0. A copy that never saved its settings was
// relying on that, and should not stop catching downloads because of an update.
async function keepCatchingAfterUpgrade(previousVersion: string | undefined): Promise<void> {
  const [major = 0, minor = 0] = (previousVersion ?? "").split(".").map(Number);
  if (!previousVersion || major > 0 || minor >= 4) return;
  await ensureSettingsLoaded();
  const stored = await chrome.storage.local.get("settings");
  if (stored.settings) return;
  settings = { ...settings, autoIntercept: true };
  await chrome.storage.local.set({ settings });
  await syncAutoSendWithTabs();
}

function automaticSendingActive(): boolean {
  return settings.enabled && settings.autoIntercept;
}

function syncAutoSendWithTabs(): Promise<void> {
  const sync = autoSendSyncQueue.then(async () => {
    const autoSend = automaticSendingActive();
    try {
      const tabs = await chrome.tabs.query({});
      for (const tab of tabs) {
        // A newer Options save has queued its own pass with the latest value; this stale one stops.
        if (autoSend !== automaticSendingActive()) return;
        if (typeof tab.id !== "number") continue;
        try {
          await chrome.tabs.sendMessage(tab.id, { type: "dm.autoSendChanged", autoSend }, { frameId: 0 });
        } catch {
          // The tab may be restricted or may not have loaded the content script yet.
        }
      }
    } catch {
      // Tab synchronization is best effort; each content script also requests the setting on load.
    }
  });
  autoSendSyncQueue = sync.catch(() => undefined);
  return autoSendSyncQueue;
}

async function installMenus(): Promise<void> {
  await chrome.contextMenus.removeAll();
  // One item: two with the same title would show as a submenu of two identical lines on a linked image.
  chrome.contextMenus.create({
    id: "dm.download",
    title: "Download with Inbound Download Manager",
    contexts: ["link", "video", "audio", "image"],
  });
}

async function onContextMenu(info: chrome.contextMenus.OnClickData, tab?: chrome.tabs.Tab): Promise<void> {
  const url = info.linkUrl ?? info.srcUrl;
  if (!url) {
    return;
  }

  if (!isHttpUrl(url)) {
    notify("Inbound Download Manager", "This link type cannot be handed to the download manager.");
    return;
  }

  if ([url, info.pageUrl, tab?.url].some(isUnsupportedUrl)) {
    notify("Inbound Download Manager", UNSUPPORTED_SITE_MESSAGE, true);
    return;
  }

  const id = crypto.randomUUID();
  const pending: PendingHandoff = {
    id,
    url,
    pageUrl: info.pageUrl ?? tab?.url,
    referrer: info.frameUrl ?? info.pageUrl ?? tab?.url,
    userAgent: navigator.userAgent,
    suggestedFileName: inferFileName(url, info.linkUrl ? "link" : "file"),
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

async function onDownloadCreated(item: chrome.downloads.DownloadItem): Promise<boolean> {
  await ensureSettingsLoaded();
  if (!settings.enabled || !settings.autoIntercept) {
    return false;
  }

  if (Date.now() < pauseUntil) {
    return false;
  }

  if (await hasDeleteGesture(item.url)) {
    return false;
  }

  const url = item.finalUrl || item.url;
  if (!isHttpUrl(url)) {
    return false;
  }

  if ([item.url, item.finalUrl, item.referrer].some(isUnsupportedUrl)) {
    return false;
  }

  const fileName = chooseDownloadFileName(item.filename, item.url, item.finalUrl);
  const extension = extensionOf(fileName || url);
  const host = hostOf(url);
  if (!host) {
    return false;
  }

  // Every site involved, not just where a redirect ended up: an excluded site's cookies are read for
  // the original address, and files behind a login often redirect to a storage host.
  const sourceHosts = [item.url, item.finalUrl, item.referrer]
    .map((value) => (value ? hostOf(value) : null))
    .filter((value): value is string => !!value);
  if (settings.excludedHosts.some((pattern) => sourceHosts.some((candidate) => hostMatches(candidate, pattern)))) {
    return false;
  }

  if (extension && settings.excludedExtensions.includes(extension)) {
    return false;
  }

  if (extension && settings.interceptedExtensions.length > 0 &&
      !settings.interceptedExtensions.includes(extension)) {
    return false;
  }

  if (!extension && /^text\/html/i.test(item.mime ?? "")) {
    return false;
  }

  try {
    await chrome.downloads.pause(item.id);
  } catch {
    // While the file name is held the download cannot complete, so a failed
    // pause only matters if the download already finished or was removed.
    const [live] = await chrome.downloads.search({ id: item.id }).catch(() => []);
    if (!live || live.state !== "in_progress") return false;
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
    });

    if (ack.accepted) {
      hostAccepted = true;
      const browserEntryRemoved = await removeAcceptedBrowserDownload(chrome.downloads, item.id);
      downloadRequestIds.delete(item.id);
      notify(
        browserEntryRemoved
          ? ack.duplicate ? "Already in Inbound" : "Sent to Inbound"
          : "Sent to Inbound, but still listed in your browser",
        browserEntryRemoved ? fileName : "The browser's copy could not be removed. Check the browser's download list.",
      );
      return true;
    } else {
      await chrome.downloads.resume(item.id).catch(() => undefined);
      notify("Inbound did not accept the download", `${ack.error ?? "The app refused it."} This download stays in your browser.`);
      lastNativeError = ack.error ?? "handoff rejected";
    }
  } catch (error) {
    if (hostAccepted) {
      // Cleanup/notification failures cannot revoke durable native acceptance.
      // Resuming here would start a second browser transfer of the same file.
      downloadRequestIds.delete(item.id);
      return true;
    }
    await chrome.downloads.resume(item.id).catch(() => undefined);
    downloadRequestIds.delete(item.id);
    const message = error instanceof Error ? error.message : String(error);
    lastNativeError = message;
    notify("Inbound unavailable", `${readableError(message)} This download stays in your browser.`);
  }
  return false;
}

async function requestHandoff(payload: Record<string, unknown>): Promise<any> {
  if ([payload.url, payload.finalUrl, payload.pageUrl].some(isUnsupportedUrl)) {
    throw new Error(UNSUPPORTED_SITE_MESSAGE);
  }

  try {
    return await sendNative("handoff.request", payload, 15000, 1);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    if (/native|not found|disconnected|host/i.test(message)) {
      // One reconnect is already attempted inside sendNative; surface a usable error.
      throw new Error(connectionProblem(message));
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

  throw lastError ?? new Error("Could not reach the desktop app.");
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
      reject(new Error("Timed out waiting for the desktop app."));
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
        finish(null, new Error("This version of the desktop app cannot show a folder picker. Update the app."));
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

function inferFileName(url: string, kind: "link" | "file"): string {
  const name = baseName(url);
  if (name && name !== "download" && /\./i.test(name)) {
    return name;
  }
  return `${kind}-${Date.now()}`;
}

function notify(title: string, message: string, always = false): void {
  if (!always && !settings.notifications) {
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
    .catch((error) => sendResponse({ ok: false, error: readableError(error instanceof Error ? error.message : String(error)) }));
  return true;
});

async function handleRuntimeMessage(message: any, sender?: chrome.runtime.MessageSender): Promise<any> {
  await ensureSettingsLoaded();
  const fromOwnPage = sender?.id === chrome.runtime.id && (sender.url ?? "").startsWith(chrome.runtime.getURL(""));
  if (!fromOwnPage && !CONTENT_SCRIPT_MESSAGES.has(message?.type)) {
    return { ok: false, error: "This request is only available from the extension's own pages." };
  }

  switch (message?.type) {
    case "dm.getAutoSend":
      return { ok: true, autoSend: automaticSendingActive() };

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
        const links = (Array.isArray(response?.links) && !isUnsupportedUrl(response.pageUrl) ? response.links : [])
          .filter((item: any) => typeof item?.url === "string" && isHttpUrl(item.url) && !isUnsupportedUrl(item.url))
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
        : undefined;
      const results: Array<Record<string, unknown>> = [];
      // One Download All batch becomes one package in the list.
      const packageKey = urls.length >= 2 ? crypto.randomUUID() : undefined;
      let packageName: string | undefined;
      if (packageKey) {
        try {
          const tab = typeof message.tabId === "number" ? await chrome.tabs.get(message.tabId) : undefined;
          packageName = (tab?.title || (pageUrl ? new URL(pageUrl).host : "") || "Links").slice(0, 100);
        } catch {
          packageName = pageUrl ? new URL(pageUrl).host : "Links";
        }
      }
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
            packageKey,
            packageName,
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
      // The native port opens lazily; probe once so the popup does not report
      // a working host as disconnected just because nothing used it yet.
      if (nativePort === null) {
        await sendNative("host.ping", {}, 3000, 0).catch(() => undefined);
      }
      return {
        ok: true,
        extensionVersion: EXTENSION_VERSION,
        enabled: settings.enabled,
        autoIntercept: settings.autoIntercept,
        connected: nativePort !== null,
        lastError: lastNativeError,
        pauseUntil,
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
              destination: "",
              startMode: settings.startMode,
            },
          }
        : { ok: false, error: "This download request has expired. Start again from the right-click menu." };
    }

    case "dm.confirmHandoff": {
      const id = String(message.id ?? "");
      const pending = pendingHandoffs.get(id) ??
        (await chrome.storage.session.get(`handoff.${id}`))[`handoff.${id}`] as PendingHandoff | undefined;
      if (!pending) {
        return { ok: false, error: "This download request has expired. Start again from the right-click menu." };
      }

      const startMode: StartMode = message.startMode === "later" ? "later" : "now";
      const destination = typeof message.destination === "string"
        ? message.destination.trim()
        : "";
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

      // Keep a rejected request so the user can correct it and try again.
      if (ack?.accepted) {
        pendingHandoffs.delete(id);
        await chrome.storage.session.remove(`handoff.${id}`);
      }

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
      };
      await chrome.storage.local.set({ settings });
      settingsLoaded = true;
      await syncAutoSendWithTabs();
      return { ok: true, settings };
    }

    case "dm.pauseCatching": {
      pauseUntil = message.cancel === true ? 0 : Date.now() + 5 * 60 * 1000;
      await chrome.storage.local.set({ pauseUntil });
      return { ok: true, pauseUntil };
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
