/// <reference types="chrome" />

type StartMode = "now" | "later";

interface Settings {
  enabled: boolean;
  autoIntercept: boolean;
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

interface MediaItem {
  url: string;
  kind: string;
  label: string;
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
const EXTENSION_VERSION = "0.1.0";

let settings: Settings = DEFAULT_SETTINGS;
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

void initialize();

async function initialize(): Promise<void> {
  await loadSettings();
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
    void loadSettings();
  });
}

async function loadSettings(): Promise<void> {
  const stored = await chrome.storage.local.get("settings");
  settings = {
    ...DEFAULT_SETTINGS,
    ...(stored.settings ?? {}),
  };
  const normalized = await chrome.storage.local.get("bypassUntil");
  bypassUntil = typeof normalized.bypassUntil === "number" ? normalized.bypassUntil : 0;
}

async function installMenus(): Promise<void> {
  await chrome.contextMenus.removeAll();
  chrome.contextMenus.create({
    id: "dm.download-link",
    title: "Download with Download Manager",
    contexts: ["link"],
  });
  chrome.contextMenus.create({
    id: "dm.download-media",
    title: "Download with Download Manager",
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
    referrer: info.pageUrl ?? tab?.url,
    userAgent: navigator.userAgent,
    suggestedFileName: inferFileName(url, info.linkUrl ? "link" : "media"),
    isAutoIntercept: false,
    createdAt: Date.now(),
  };
  pendingHandoffs.set(id, pending);
  await chrome.storage.session.set({ [`handoff.${id}`]: pending });
  await chrome.windows.create({
    url: chrome.runtime.getURL(`handoff.html?id=${encodeURIComponent(id)}`),
    type: "popup",
    width: 560,
    height: 500,
  });
}

async function onDownloadCreated(item: chrome.downloads.DownloadItem): Promise<void> {
  if (!settings.enabled || !settings.autoIntercept) {
    return;
  }

  if (Date.now() < bypassUntil) {
    return;
  }

  const url = item.finalUrl || item.url;
  if (!isHttpUrl(url)) {
    return;
  }

  const fileName = baseName(item.filename || url);
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
      await chrome.downloads.cancel(item.id).catch(() => undefined);
      await chrome.downloads.erase({ id: item.id }).catch(() => undefined);
      downloadRequestIds.delete(item.id);
      notify(
        ack.duplicate ? "Already in Download Manager" : "Sent to Download Manager",
        fileName,
      );
    } else {
      await chrome.downloads.resume(item.id).catch(() => undefined);
      notify("Download Manager did not accept the download", ack.error ?? "The host rejected the request.");
      lastNativeError = ack.error ?? "handoff rejected";
    }
  } catch (error) {
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
  try {
    const cookies = await chrome.cookies.getAll({ url });
    if (cookies.length === 0) {
      return {};
    }

    return {
      Cookie: cookies.map((c) => `${c.name}=${c.value}`).join("; "),
    };
  } catch {
    return {};
  }
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
  switch (message?.type) {
    case "dm.mediaDetected": {
      const tabId = sender?.tab?.id;
      if (typeof tabId !== "number") {
        return { ok: false, error: "No sender tab." };
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

      const media = typeof tabId === "number" ? mediaByTab.get(tabId) : undefined;
      return {
        ok: true,
        tabId,
        pageUrl: media?.pageUrl ?? "",
        items: media?.items ?? [],
      };
    }

    case "dm.queueMedia": {
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
        height: 500,
      });
      return { ok: true, id };
    }

    case "dm.status":
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
      return pending ? { ok: true, handoff: pending } : { ok: false, error: "Handoff request expired." };
    }

    case "dm.confirmHandoff": {
      const id = String(message.id ?? "");
      const pending = pendingHandoffs.get(id) ??
        (await chrome.storage.session.get(`handoff.${id}`))[`handoff.${id}`] as PendingHandoff | undefined;
      if (!pending) {
        return { ok: false, error: "Handoff request expired." };
      }

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
        startMode: message.startMode === "later" ? "later" : "now",
        isAutoIntercept: pending.isAutoIntercept,
        headers: await cookieHeaders(pending.url),
        destinationDirectory: typeof message.destination === "string" && message.destination.trim()
          ? message.destination.trim()
          : settings.defaultDestination || undefined,
      });

      pendingHandoffs.delete(id);
      await chrome.storage.session.remove(`handoff.${id}`);
      return { ok: true, ack };
    }

    case "dm.saveSettings": {
      settings = {
        ...DEFAULT_SETTINGS,
        ...message.settings,
      };
      await chrome.storage.local.set({ settings });
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
