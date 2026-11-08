/// <reference types="chrome" />

import { connectionProblem, readableError } from "./connection-problem";
import { isUnsupportedUrl, UNSUPPORTED_SITE_MESSAGE } from "./unsupported-sites";
import { chooseDownloadFileName } from "./download-filename";
import { removeAcceptedBrowserDownload } from "./browser-download-cleanup";
import { browserCookieHeaders, cookieContextForFrame, type BrowserCookieContext } from "./browser-cookies";
import {
  DEFAULT_SETTINGS, decideCapture, isHttpUrl, migrateSettings, type Settings, type StartMode,
} from "./capture-rules";
import { JOB_ACTION_MESSAGES, latestJobs, type JobAction, type JobSummary } from "./job-list";
import {
  NativeTransport, ServerTransport, SERVER_STORAGE_KEY, TARGET_LABELS, automaticTarget, availableTargets, extractError,
  initialTarget, isNativeHostMissing, pairWithServer, pairingName, readPairing, type Envelope, type ServerPairing,
  type TargetId, type Transport,
} from "./transport";

interface PendingHandoff {
  id: string;
  url: string;
  finalUrl?: string;
  pageUrl?: string;
  referrer?: string;
  userAgent: string;
  suggestedFileName?: string;
  mimeType?: string;
  totalBytes?: number;
  isAutoIntercept: boolean;
  createdAt: number;
  cookieContext?: BrowserCookieContext;
  /** Set for an automatic catch held in the browser while the user chooses a folder (destinationMode "ask"). */
  browserDownloadId?: number;
}

/** A download the extension saw and left in the browser, with the reason, for the popup. */
interface LeftInBrowser {
  fileName: string;
  reason: string;
  at: number;
}

// Content scripts run alongside pages this extension does not trust, so they get only what they
// need. Everything else, cookies and settings above all, is for the extension's own pages.
const CONTENT_SCRIPT_MESSAGES = new Set(["dm.getAutoSend"]);
const NATIVE_HOST = "com.kaank.downloadmanager";
const EXTENSION_VERSION = chrome.runtime.getManifest().version;
let settings: Settings = DEFAULT_SETTINGS;
let settingsLoaded = false;
let settingsLoadPromise: Promise<void> | null = null;
let pauseUntil = 0;
let lastJobs = new Map<string, JobSummary>();
// This PC: the desktop app through native messaging. Its shared connection also brings the app's job events.
const native = new NativeTransport(NATIVE_HOST, handleNativeEvent);
// The user's own Inbound server, once paired from Settings (transport.ts); null otherwise.
let server: ServerTransport | null = null;
const pendingHandoffs = new Map<string, PendingHandoff>();
const downloadRequestIds = new Map<number, string>();
// File-name suggestions still owed to the browser for downloads held while the folder window is open.
const heldSuggestions = new Map<number, () => void>();
const HANDOFF_PORT_PREFIX = "dm.handoff:";
// Folder-window requests being sent to the app right now, and those whose window closed meanwhile.
const confirmingHandoffs = new Set<string>();
const closedWhileConfirming = new Set<string>();
// URLs started again in the browser by keepInBrowser; their next download is let through once.
const restartedInBrowser = new Set<string>();
const LEFT_IN_BROWSER_KEY = "leftInBrowser";
const AUTO_OFF_NOTICE_KEY = "autoOffNoticeShown";
let leftInBrowser: LeftInBrowser[] = [];
let badgeTimer: ReturnType<typeof setTimeout> | undefined;
let autoSendSyncQueue: Promise<void> = Promise.resolve();
// The paired server's token is kept in storage.local, and a content script runs on every page. Content scripts use no
// storage at all, so storage.local is closed to them (Chrome lets them read it otherwise). Set at every start, and
// waited for before a token is written.
const storageRestricted = restrictStorageToExtension();
async function restrictStorageToExtension(): Promise<void> {
  try {
    await chrome.storage.local.setAccessLevel?.({ accessLevel: "TRUSTED_CONTEXTS" });
  } catch {
    // A browser without it keeps its default.
  }
}
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
// Firefox has no onDeterminingFilename: there the download is caught from
// onCreated, paused at once, and cancelled or resumed the same way.
if (chrome.downloads.onDeterminingFilename) {
  chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
    if (restartedInBrowser.delete(item.url)) {
      // Started again by keepInBrowser after the worker lost the first one's callback.
      try { suggest(); } catch { /* gone */ }
      return;
    }
    let answered = false;
    const answer = (): void => {
      if (answered) return;
      answered = true;
      heldSuggestions.delete(item.id);
      try { suggest(); } catch { /* the download is already gone */ }
    };
    void onDownloadCreated(item, answer)
      .then((handled) => { if (!handled) answer(); })
      .catch(() => answer());
    return true;
  });
} else {
  chrome.downloads.onCreated.addListener((item) => {
    void onDownloadCreated(item).catch(() => undefined);
  });
}
// The folder window of a held download keeps a port open to this worker. Its pings keep the worker
// (and so the browser's file-name callback, which only lives in memory) alive while the window is
// open, and the port closing is how a window closed without a choice is seen. This replaces a
// windows.onRemoved listener, which woke the worker for every browser window that closed.
chrome.runtime.onConnect.addListener((port) => {
  if (!port.name.startsWith(HANDOFF_PORT_PREFIX)) return;
  const sourcePage = (port.sender?.url ?? "").split(/[?#]/)[0];
  if (port.sender?.id !== chrome.runtime.id || sourcePage !== chrome.runtime.getURL("handoff.html")) {
    try { port.disconnect(); } catch { /* already closed */ }
    return;
  }
  const id = port.name.slice(HANDOFF_PORT_PREFIX.length);
  port.onMessage.addListener(() => { /* a ping: receiving it is the point */ });
  port.onDisconnect.addListener(() => { void handoffWindowClosed(id); });
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
  const migrated = migrateSettings(stored.settings);
  settings = migrated.settings;
  if (migrated.changed) {
    // Once: a copy that never edited the old allow-list now catches everything except the exclude list.
    await chrome.storage.local.set({ settings }).catch(() => undefined);
  }
  const normalized = await chrome.storage.local.get("pauseUntil");
  pauseUntil = typeof normalized.pauseUntil === "number" ? normalized.pauseUntil : 0;
  const pairing = readPairing((await chrome.storage.local.get(SERVER_STORAGE_KEY))[SERVER_STORAGE_KEY]);
  // The same pairing keeps its transport, so its connection state survives a settings reload.
  if (!pairing) server = null;
  else if (server?.pairing.token !== pairing.token || server.pairing.address !== pairing.address) server = new ServerTransport(pairing);
  try {
    const notes = (await chrome.storage.session.get(LEFT_IN_BROWSER_KEY))[LEFT_IN_BROWSER_KEY];
    leftInBrowser = Array.isArray(notes) ? notes.slice(0, 5) : [];
  } catch { leftInBrowser = []; }
  settingsLoaded = true;
  updateBadge();
}

/**
 * The toolbar shows "OFF" whenever downloads are not being caught automatically, so automatic sending
 * turned off in Settings or a pause cannot be mistaken for the extension failing.
 */
function updateBadge(): void {
  const action = (chrome as { action?: typeof chrome.action }).action;
  if (!action?.setBadgeText) return;
  const paused = Date.now() < pauseUntil;
  const off = !automaticSendingActive();
  try {
    void action.setBadgeText({ text: off || paused ? "OFF" : "" })?.catch?.(() => undefined);
    void action.setBadgeBackgroundColor?.({ color: "#6b6b6b" })?.catch?.(() => undefined);
    void action.setTitle?.({
      title: off
        ? "Inbound Download Manager: automatic sending is off"
        : paused
          ? "Inbound Download Manager: catching is paused"
          : "Inbound Download Manager: catching downloads",
    })?.catch?.(() => undefined);
  } catch { /* the badge is a convenience */ }
  if (paused && badgeTimer === undefined) {
    // Best effort: a worker that sleeps through the end of the pause updates the badge when it wakes,
    // opens the popup or sees the next download.
    badgeTimer = setTimeout(() => { badgeTimer = undefined; updateBadge(); }, Math.min(pauseUntil - Date.now() + 500, 5 * 60 * 1000));
    // Never what keeps a process alive (the tests run this under Node).
    (badgeTimer as { unref?: () => void }).unref?.();
  }
}

/** Remembers why a download stayed in the browser, for the popup, and says so when it should. */
async function noteLeftInBrowser(fileName: string, reason: string, quiet: boolean): Promise<void> {
  leftInBrowser = [{ fileName, reason, at: Date.now() }, ...leftInBrowser].slice(0, 5);
  try { await chrome.storage.session.set({ [LEFT_IN_BROWSER_KEY]: leftInBrowser }); } catch { /* popup note only */ }
  if (!quiet) {
    notify("Left in your browser", `${fileName}: ${reason}`);
  }
}

/** Automatic sending is off: said once per browser session, then only in the popup and the badge. */
async function noteAutomaticSendingOff(fileName: string): Promise<void> {
  let shown = false;
  try { shown = (await chrome.storage.session.get(AUTO_OFF_NOTICE_KEY))[AUTO_OFF_NOTICE_KEY] === true; } catch { /* show it */ }
  await noteLeftInBrowser(fileName, "Automatic sending is off. Turn it on in the extension's Settings.", shown);
  if (!shown) {
    try { await chrome.storage.session.set({ [AUTO_OFF_NOTICE_KEY]: true }); } catch { /* shown again next time */ }
  }
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

/**
 * Decides one browser download. Returns true when the extension has taken it over (sent to the app,
 * or held while the folder window is open), so the browser must not be given its file name now.
 */
async function onDownloadCreated(item: chrome.downloads.DownloadItem, answer?: () => void): Promise<boolean> {
  await ensureSettingsLoaded();
  updateBadge();
  const fileName = chooseDownloadFileName(item.filename, item.url, item.finalUrl);
  // blob: and data: downloads are the page's own; nothing is said about them.
  const pageOwned = !isHttpUrl(item.finalUrl || item.url);
  if (!settings.enabled || !settings.autoIntercept) {
    if (!pageOwned) await noteAutomaticSendingOff(fileName);
    return false;
  }

  if (Date.now() < pauseUntil) {
    if (!pageOwned) await noteLeftInBrowser(fileName, "Catching is paused.", true);
    return false;
  }

  // Chromium: the file name is held (answer is set), so the download cannot finish while the checks
  // run. Firefox has no such hold and the file keeps arriving, so there the download is paused as soon
  // as the rules say to catch it, and the slower Delete-gesture lookup (it asks every tab) comes after.
  const holdsName = answer !== undefined;
  if (holdsName && await hasDeleteGesture(item.url)) {
    await noteLeftInBrowser(fileName, "Delete was held when the download started.", true);
    return false;
  }

  const decision = decideCapture({
    url: item.url, finalUrl: item.finalUrl, referrer: item.referrer, mime: item.mime, fileName,
  }, settings);
  if (!decision.capture) {
    if (decision.reason) await noteLeftInBrowser(fileName, decision.reason, decision.quiet);
    return false;
  }

  try {
    await chrome.downloads.pause(item.id);
  } catch {
    const [live] = await chrome.downloads.search({ id: item.id }).catch(() => []);
    if (!live || live.state !== "in_progress") {
      // Finished or removed before it could be paused. Only Firefox gets here in practice: with the
      // name held, Chromium cannot finish the download first.
      if (live?.state === "complete") {
        await noteLeftInBrowser(fileName, "It finished in the browser before it could be sent.", false);
      }
      return false;
    }
    if (!holdsName) {
      // Still arriving and not paused: sending it now would download the same file twice until the
      // app's answer cancels the browser's copy. It stays in the browser.
      await noteLeftInBrowser(fileName, "The browser would not pause it, so it was not sent.", false);
      return false;
    }
    // With the name held the download cannot complete, so it can still be handed over.
  }

  if (!holdsName && await hasDeleteGesture(item.url)) {
    await noteLeftInBrowser(fileName, "Delete was held when the download started.", true);
    await resumeInBrowser(item.id, fileName);
    return false;
  }

  if (settings.destinationMode === "ask") {
    return await holdForFolderChoice(item, fileName, answer);
  }

  let requestId = downloadRequestIds.get(item.id);
  if (!requestId) {
    requestId = crypto.randomUUID();
    downloadRequestIds.set(item.id, requestId);
  }

  const transport = transportFor(automaticTarget(settings.defaultTarget, server !== null));
  const where = targetWords(transport);
  let hostAccepted = false;
  try {
    const ack = await requestHandoff(transport, {
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
      headers: await browserCookieHeaders(item.url, {}, item.finalUrl),
    });

    if (ack?.accepted) {
      hostAccepted = true;
      const browserEntryRemoved = await removeAcceptedBrowserDownload(chrome.downloads, item.id);
      downloadRequestIds.delete(item.id);
      notify(
        browserEntryRemoved
          ? ack.duplicate ? `Already in ${where.name}` : `Sent to ${where.name}`
          : `Sent to ${where.name}, but still listed in your browser`,
        browserEntryRemoved ? fileName : "The browser's copy could not be removed. Check the browser's download list.",
      );
      return true;
    } else {
      const resumed = await resumeInBrowser(item.id, fileName);
      const refusal = ack?.error ?? ack?.message ?? `The ${where.thing} refused it.`;
      notify(`${where.subject} did not accept the download`, `${refusal} ${resumed
        ? "This download stays in your browser."
        : "The browser could not continue it: start it again from the browser's download list."}`);
      setLastError(transport, refusal);
    }
  } catch (error) {
    if (hostAccepted) {
      // Cleanup/notification failures cannot revoke durable native acceptance.
      // Resuming here would start a second browser transfer of the same file.
      downloadRequestIds.delete(item.id);
      return true;
    }
    const resumed = await resumeInBrowser(item.id, fileName);
    downloadRequestIds.delete(item.id);
    const message = error instanceof Error ? error.message : String(error);
    setLastError(transport, message);
    notify(where.unavailable, `${errorFor(transport, message)} ${resumed
      ? "This download stays in your browser."
      : "The browser could not continue it: start it again from the browser's download list."}`);
  }
  return false;
}

/**
 * Lets a paused browser download continue. Firefox cannot continue some paused downloads (a server
 * that does not resume, for one); then the popup keeps a note so the user knows to start it again.
 */
async function resumeInBrowser(downloadId: number, fileName: string): Promise<boolean> {
  try {
    await chrome.downloads.resume(downloadId);
    return true;
  } catch {
    const [live] = await chrome.downloads.search({ id: downloadId }).catch(() => []);
    // Not paused any more (it was never paused, or it went on by itself): nothing is wrong.
    if (live && live.state === "in_progress" && !live.paused) return true;
    await noteLeftInBrowser(fileName, "The browser could not continue it. Start it again from the browser's download list.", true);
    return false;
  }
}

/**
 * "Ask each time": the browser's download stays paused (and, in Chromium, its file name unanswered)
 * while the folder window is open. Download sends it and removes the browser's copy; Keep in browser,
 * or closing the window, resumes it in the browser. The window's port keeps the worker alive while it
 * is open. If the worker restarts anyway, the pending entry (in session storage, with the browser's
 * download id) still works, and keepInBrowser starts the download again because the browser's
 * file-name callback did not survive.
 */
async function holdForFolderChoice(item: chrome.downloads.DownloadItem, fileName: string, answer?: () => void): Promise<boolean> {
  const id = crypto.randomUUID();
  const pending: PendingHandoff = {
    id,
    url: item.url,
    finalUrl: item.finalUrl || undefined,
    pageUrl: item.referrer || undefined,
    referrer: item.referrer || undefined,
    userAgent: navigator.userAgent,
    suggestedFileName: fileName,
    mimeType: item.mime || undefined,
    totalBytes: item.totalBytes > 0 ? item.totalBytes : item.fileSize > 0 ? item.fileSize : undefined,
    isAutoIntercept: true,
    createdAt: Date.now(),
    browserDownloadId: item.id,
  };
  try {
    pendingHandoffs.set(id, pending);
    await chrome.storage.session.set({ [`handoff.${id}`]: pending });
    if (answer) heldSuggestions.set(item.id, answer);
    await chrome.windows.create({
      url: chrome.runtime.getURL(`handoff.html?id=${encodeURIComponent(id)}`),
      type: "popup",
      width: 560,
      height: 680,
    });
    return true;
  } catch {
    // No window, no choice: the browser keeps it, as on any other failure.
    pendingHandoffs.delete(id);
    heldSuggestions.delete(item.id);
    await chrome.storage.session.remove(`handoff.${id}`).catch(() => undefined);
    await chrome.downloads.resume(item.id).catch(() => undefined);
    return false;
  }
}

async function pendingHandoff(id: string): Promise<PendingHandoff | undefined> {
  return pendingHandoffs.get(id) ??
    (await chrome.storage.session.get(`handoff.${id}`))[`handoff.${id}`] as PendingHandoff | undefined;
}

async function forgetHandoff(id: string): Promise<void> {
  pendingHandoffs.delete(id);
  await chrome.storage.session.remove(`handoff.${id}`).catch(() => undefined);
}

/** Gives a held download back to the browser: its file name, then the transfer. */
async function keepInBrowser(pending: PendingHandoff): Promise<void> {
  await forgetHandoff(pending.id);
  const downloadId = pending.browserDownloadId;
  if (typeof downloadId !== "number") return;
  const answer = heldSuggestions.get(downloadId);
  heldSuggestions.delete(downloadId);
  if (answer || !chrome.downloads.onDeterminingFilename) {
    answer?.();
    await resumeInBrowser(downloadId, pending.suggestedFileName ?? "download");
    return;
  }
  // Chromium, and the worker restarted while the window was open: the callback that answers the
  // browser's file-name question is gone, and nothing else can answer it, so the paused download
  // would wait for ever. It is cancelled and started again in the browser, which lets the new one
  // through once. The page's referrer is not carried over; the browser's own cookies are.
  await chrome.downloads.cancel(downloadId).catch(() => undefined);
  await chrome.downloads.erase({ id: downloadId }).catch(() => undefined);
  restartedInBrowser.add(pending.url);
  try {
    await chrome.downloads.download({ url: pending.url, filename: pending.suggestedFileName || undefined });
  } catch {
    restartedInBrowser.delete(pending.url);
    await noteLeftInBrowser(pending.suggestedFileName ?? "download",
      "The browser could not start it again. Download it again from the page.", false);
  }
}

/** The folder window's port closed: a window closed without a choice leaves the download in the browser. */
async function handoffWindowClosed(id: string): Promise<void> {
  if (confirmingHandoffs.has(id)) {
    // Being sent right now; the answer decides (see dm.confirmHandoff).
    closedWhileConfirming.add(id);
    return;
  }
  const pending = await pendingHandoff(id).catch(() => undefined);
  if (typeof pending?.browserDownloadId === "number") await keepInBrowser(pending);
}

/** The transport for a target; the server only while one is paired. */
function transportFor(target: TargetId | undefined): Transport {
  return target === "server" && server ? server : native;
}

/** How notifications name where a download went. */
function targetWords(transport: Transport): { name: string; subject: string; unavailable: string; thing: string } {
  return transport.target === "server"
    ? { name: "the Inbound server", subject: "The Inbound server", unavailable: "Inbound server unavailable", thing: "server" }
    : { name: "Inbound", subject: "Inbound", unavailable: "Inbound unavailable", thing: "app" };
}

function setLastError(transport: Transport, message: string): void {
  (transport as { lastError: string }).lastError = message;
}

/** An error ready to show: the desktop app's connection errors in plain words; the server's are plain already. */
function errorFor(transport: Transport, message: string): string {
  return transport.target === "server" ? message : readableError(message);
}

async function requestHandoff(transport: Transport, payload: Record<string, unknown>): Promise<any> {
  if ([payload.url, payload.finalUrl, payload.pageUrl].some(isUnsupportedUrl)) {
    throw new Error(UNSUPPORTED_SITE_MESSAGE);
  }

  if (transport.target === "server") {
    // One HTTP request; the request id makes a repeat harmless, but nothing here repeats it.
    return await transport.send("handoff.request", payload, 15000, 0);
  }

  try {
    return await transport.send("handoff.request", payload, 15000, 1);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    if (/native|not found|disconnected|host/i.test(message)) {
      // One reconnect is already attempted inside send; surface a usable error.
      throw new Error(connectionProblem(message));
    }
    throw error;
  }
}

function sendNative(type: string, payload: unknown, timeoutMs = 12000, retries = 1): Promise<any> {
  return native.send(type, payload, timeoutMs, retries);
}

async function pickNativeFolder(initialPath?: string): Promise<any> {
  // A modal picker can stay open for minutes.
  const reply = await native.request("host.pickFolder", { initialPath }, 120000, "Folder selection timed out.");
  if (reply.type !== "host.folderPicked") {
    throw new Error("This version of the desktop app cannot show a folder picker. Update the app.");
  }
  return reply.payload;
}

/** Messages on the desktop app's shared connection that answer no request. */
function handleNativeEvent(message: Envelope): void {
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

async function cookieHeaders(url: string): Promise<Record<string, string>> {
  return browserCookieHeaders(url);
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
      if (serverGone(message.target)) return { ok: false, error: SERVER_GONE };
      const transport = transportFor(chosenTarget(message.target));
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
          const ack = await requestHandoff(transport, {
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
            error: ack?.error ?? (ack?.accepted ? undefined : ack?.message),
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
      // a working host as disconnected just because nothing used it yet. A paired server is asked
      // each time, at the same time: it has no connection to keep open.
      await Promise.all([
        native.connected ? undefined : sendNative("host.ping", {}, 3000, 0).catch(() => undefined),
        server?.send("host.ping", {}, 3000).catch(() => undefined),
      ]);
      updateBadge();
      return {
        ok: true,
        extensionVersion: EXTENSION_VERSION,
        enabled: settings.enabled,
        autoIntercept: settings.autoIntercept,
        automaticSending: automaticSendingActive(),
        connected: native.connected,
        lastError: native.lastError,
        localMissing: localMissing(),
        server: serverStatus(),
        automaticTarget: automaticTarget(settings.defaultTarget, server !== null),
        pauseUntil,
        settings,
        siteAccess: await hasSiteAccess(),
        leftInBrowser,
        jobs: latestJobs([...lastJobs.values()]),
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

    case "dm.browseServerFolders": {
      const sourcePage = (sender?.url ?? "").split(/[?#]/)[0];
      if (sender?.id !== chrome.runtime.id ||
          ![chrome.runtime.getURL("handoff.html"), chrome.runtime.getURL("batch.html")].includes(sourcePage ?? "")) {
        return { ok: false, error: "Folder browsing is available from the download destination screen." };
      }
      if (!server) return { ok: false, error: "No server is paired. Pair one in the extension's Settings." };
      // The server's allowed roots decide what can be listed (folders.browse); an empty path lists them.
      const result = await server.send("folders.browse", { path: typeof message.path === "string" ? message.path : "" }, 8000);
      return result && typeof result === "object" ? result : { ok: false, error: "The server did not list that folder." };
    }

    case "dm.pingServer": {
      if (!fromPage(sender, "options.html")) return { ok: false, error: "Only the extension's Settings can do that." };
      if (!server) return { ok: false, error: "No server is paired." };
      const result = await server.send("host.ping", {}, 5000);
      return { ok: result?.ok !== false, server: serverStatus(), error: result?.ok === false ? extractError(result) : undefined };
    }

    case "dm.pairServer": {
      // Only from Settings, which asked the browser for access to the server's address first, on the click.
      if (!fromPage(sender, "options.html")) return { ok: false, error: "Only the extension's Settings can pair a server." };
      const pairing = await pairWithServer(String(message.address ?? ""), String(message.code ?? ""), pairingName(navigator.userAgent));
      await storageRestricted;
      await chrome.storage.local.set({ [SERVER_STORAGE_KEY]: pairing });
      server = new ServerTransport(pairing);
      server.connected = true;
      return { ok: true, server: serverStatus() };
    }

    case "dm.unpairServer": {
      if (!fromPage(sender, "options.html")) return { ok: false, error: "Only the extension's Settings can do that." };
      // The token asks the server to remove it (DELETE /api/pair/self), briefly; it is forgotten here whatever
      // the answer. When that fails, the server's web UI lists it under Paired browsers until removed there.
      const leaving = server;
      server = null;
      await chrome.storage.local.set({ [SERVER_STORAGE_KEY]: null });
      const revoked = leaving ? await leaving.revokeSelf(4000) : false;
      if (settings.defaultTarget === "server") {
        settings = { ...settings, defaultTarget: "local" };
        await chrome.storage.local.set({ settings });
      }
      return { ok: true, settings, server: null, revoked, wasPaired: Boolean(leaving) };
    }

    case "dm.targets": {
      // The batch window's choice of target, each with its default folder, as the folder window gets them.
      // Without a server this is one request to the desktop app, for its default folder.
      if (!fromPage(sender, "batch.html")) return { ok: false, error: "Only the Download All window can do that." };
      return { ok: true, ...await handoffTargets() };
    }

    case "dm.getHandoff": {
      const id = String(message.id ?? "");
      const pending = await pendingHandoff(id);
      if (!pending) {
        return { ok: false, error: "This download request has expired. Start again from the right-click menu." };
      }
      // The window starts at the app's default folder every time; a folder chosen for one download
      // is never carried to the next (UI.R3). Favorites are offered beside it. With a paired server,
      // each target brings its own default folder and favorites.
      const choice = await handoffTargets();
      const first = choice.targets.find((target) => target.id === choice.target) ?? choice.targets[0]!;
      return {
        ok: true,
        handoff: pending,
        held: typeof pending.browserDownloadId === "number",
        defaults: {
          destination: first.defaultFolder,
          favorites: first.favorites,
          startMode: settings.startMode,
        },
        targets: choice.targets,
        target: choice.target,
      };
    }

    case "dm.confirmHandoff": {
      const id = String(message.id ?? "");
      const pending = await pendingHandoff(id);
      if (!pending) {
        return { ok: false, error: "This download request has expired. Start again from the right-click menu." };
      }

      const startMode: StartMode = message.startMode === "later" ? "later" : "now";
      const destination = typeof message.destination === "string"
        ? message.destination.trim()
        : "";
      if (serverGone(message.target)) return { ok: false, error: SERVER_GONE };
      if (confirmingHandoffs.has(id)) {
        return { ok: false, error: "This download is being sent already." };
      }
      confirmingHandoffs.add(id);
      const transport = transportFor(chosenTarget(message.target));
      let ack: any;
      try {
        ack = await requestHandoff(transport, {
        requestId: crypto.randomUUID(),
        extensionId: chrome.runtime.id,
        url: pending.url,
        finalUrl: pending.finalUrl,
        pageUrl: pending.pageUrl,
        referrer: pending.referrer,
        userAgent: pending.userAgent,
        suggestedFileName: message.fileName || pending.suggestedFileName,
        mimeType: pending.mimeType,
        totalBytes: pending.totalBytes,
        startMode,
        isAutoIntercept: pending.isAutoIntercept,
        headers: await browserCookieHeaders(pending.url, pending.cookieContext, pending.finalUrl),
        destinationDirectory: destination || undefined,
        });
      } finally {
        confirmingHandoffs.delete(id);
        // The window closed while this was being sent. Not accepted: nobody is left to choose, so the
        // held download goes back to the browser, as when the window closes at any other time.
        if (closedWhileConfirming.delete(id) && !ack?.accepted && typeof pending.browserDownloadId === "number") {
          await keepInBrowser(pending);
        }
      }

      // Keep a rejected request so the user can correct it and try again.
      if (ack?.accepted) {
        await forgetHandoff(id);
        if (typeof pending.browserDownloadId === "number") {
          // The app holds it now; the browser's paused copy goes, and its file name is never answered.
          heldSuggestions.delete(pending.browserDownloadId);
          await removeAcceptedBrowserDownload(chrome.downloads, pending.browserDownloadId).catch(() => false);
        }
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

      return { ok: true, ack, target: transport.target };
    }

    case "dm.keepInBrowser": {
      const pending = await pendingHandoff(String(message.id ?? ""));
      if (pending) await keepInBrowser(pending);
      return { ok: true };
    }

    case "dm.saveSettings": {
      settings = migrateSettings({
        ...DEFAULT_SETTINGS,
        ...message.settings,
      }).settings;
      await chrome.storage.local.set({ settings });
      settingsLoaded = true;
      updateBadge();
      await syncAutoSendWithTabs();
      return { ok: true, settings };
    }

    case "dm.pauseCatching": {
      pauseUntil = message.cancel === true ? 0 : Date.now() + 5 * 60 * 1000;
      await chrome.storage.local.set({ pauseUntil });
      updateBadge();
      return { ok: true, pauseUntil };
    }

    case "dm.jobs": {
      const local = (async (): Promise<JobsAnswer> => {
        try {
          const snapshot = await sendNative("jobs.query", {}, 6000);
          const jobs = latestJobs((snapshot?.jobs ?? []) as JobSummary[]);
          lastJobs = new Map(jobs.map((job) => [job.id, job]));
          return { ok: true, jobs };
        } catch (error) {
          return { ok: false, error: error instanceof Error ? error.message : String(error), jobs: latestJobs([...lastJobs.values()]) };
        }
      })();
      if (!server) return await local;

      // EXT.R1: with a paired server the popup lists both, each download labelled with where it is.
      const paired = server;
      const remote = (async (): Promise<JobsAnswer> => {
        try {
          const snapshot = await paired.send("jobs.query", {}, 6000);
          return { ok: true, jobs: latestJobs((snapshot?.jobs ?? []) as JobSummary[]) };
        } catch (error) {
          return { ok: false, error: error instanceof Error ? error.message : String(error), jobs: [] };
        }
      })();
      const [mine, theirs] = await Promise.all([local, remote]);
      const groups: Array<JobsAnswer & { target: TargetId }> = [
        ...(localMissing() ? [] : [{ target: "local" as TargetId, ...mine }]),
        { target: "server" as TargetId, ...theirs },
      ];
      return {
        ok: groups.some((group) => group.ok),
        error: groups.find((group) => !group.ok)?.error,
        jobs: groups.flatMap((group) => group.jobs.map((job) => ({ ...job, target: group.target }))),
        groups: groups.map((group) => ({ target: group.target, label: TARGET_LABELS[group.target], ok: group.ok, error: group.error })),
      };
    }

    case "dm.jobAction": {
      const action = message.action as JobAction;
      const type = Object.prototype.hasOwnProperty.call(JOB_ACTION_MESSAGES, action) ? JOB_ACTION_MESSAGES[action] : undefined;
      const jobId = typeof message.jobId === "string" ? message.jobId : "";
      if (!type || !/^[0-9a-f-]{32,36}$/i.test(jobId)) {
        return { ok: false, error: "Unknown download action." };
      }
      if (serverGone(message.target)) return { ok: false, error: SERVER_GONE };
      const transport = transportFor(chosenTarget(message.target));
      // A failed connection throws, and the message listener turns it into a readable error.
      const reply = await transport.request(type, {
        jobId,
        // Deleting the file is asked for only with Delete, and only when the box was ticked.
        deleteFile: action === "remove" && message.deleteFile === true,
      }, 10000, transport.target === "server" ? "Timed out waiting for the Inbound server." : "Timed out waiting for the desktop app.");
      if (reply.type === "handoff.error" || reply.type === "server.incompatible") {
        return { ok: false, error: errorFor(transport, extractError(reply.payload)) };
      }
      const result = reply.payload as any;
      return result?.ok === true
        ? { ok: true, job: result.job }
        : { ok: false, error: result?.error || result?.message || "The app did not do that." };
    }

    default:
      return { ok: false, error: "Unknown message." };
  }
}

interface DownloadFolders {
  defaultFolder: string;
  favorites: Array<{ name: string; path: string }>;
}

/** The app's default folder and favorite folders, or nothing (and why) when the app is too old or unreachable. */
async function downloadFolders(transport: Transport = native): Promise<DownloadFolders & { error?: string }> {
  try {
    const result = await transport.send("settings.downloadFolders", {}, 4000, 0);
    if (result?.ok !== true) return { defaultFolder: "", favorites: [] };
    return {
      defaultFolder: typeof result.defaultFolder === "string" ? result.defaultFolder : "",
      favorites: (Array.isArray(result.favorites) ? result.favorites : [])
        .filter((item: any) => typeof item?.path === "string" && item.path.trim())
        .slice(0, 50)
        .map((item: any) => ({ name: typeof item.name === "string" && item.name.trim() ? item.name : item.path, path: item.path })),
    };
  } catch (error) {
    return { defaultFolder: "", favorites: [], error: error instanceof Error ? error.message : String(error) };
  }
}

interface JobsAnswer {
  ok: boolean;
  error?: string;
  jobs: JobSummary[];
}

interface HandoffTarget extends DownloadFolders {
  id: TargetId;
  label: string;
  /** Where "Choose…" goes: the desktop app's folder dialog, or the server's folder list (folders.browse). */
  chooser: "dialog" | "list";
  /** The server's address, for the label. */
  address?: string;
  error?: string;
}

/**
 * The targets the folder window (and the batch window) offer, each with its default folder and favorites,
 * and the one to start on. Without a paired server this is exactly one request to the desktop app, as
 * before pairing existed.
 */
async function handoffTargets(): Promise<{ targets: HandoffTarget[]; target: TargetId }> {
  const paired = server;
  const [local, remote] = await Promise.all([
    downloadFolders(native),
    paired ? downloadFolders(paired) : Promise.resolve(undefined),
  ]);
  const offered = availableTargets({ serverPaired: paired !== null, localMissing: isNativeHostMissing(local.error) });
  const targets: HandoffTarget[] = offered.map((id): HandoffTarget => id === "server"
    ? { id, label: TARGET_LABELS.server, chooser: "list", address: paired!.address, ...remote! }
    : { id, label: TARGET_LABELS.local, chooser: "dialog", ...local });
  return { targets, target: initialTarget(settings.defaultTarget, offered) };
}

const SERVER_GONE = "The server is not paired any more. Choose This PC, or pair the server again in Settings.";

/**
 * A page asked for the server, but none is paired now (unpaired while the window was open). Refused rather
 * than sent to this PC: the request may carry a server folder, and the user chose the server.
 */
function serverGone(value: unknown): boolean {
  return value === "server" && !server;
}

/** A target a page asked for: the server only while one is paired, otherwise this PC. */
function chosenTarget(value: unknown): TargetId {
  return value === "server" && server ? "server" : "local";
}

/** Whether the desktop app is missing here while a server is paired, so the server is the only target. */
function localMissing(): boolean {
  return server !== null && isNativeHostMissing(native.lastError);
}

/** The paired server as Settings and the popup show it; never the token. */
function serverStatus(): { address: string; name?: string; pairedAt?: number; connected: boolean; lastError: string } | null {
  if (!server) return null;
  const { address, name, pairedAt } = server.pairing as ServerPairing;
  return { address, name, pairedAt, connected: server.connected, lastError: server.lastError };
}

function fromPage(sender: chrome.runtime.MessageSender | undefined, page: string): boolean {
  return sender?.id === chrome.runtime.id && (sender.url ?? "").split(/[?#]/)[0] === chrome.runtime.getURL(page);
}

/**
 * Whether the extension may read cookies and run its page script everywhere. Chrome and Edge grant
 * this at install; Firefox may leave it to the user, so Settings offers to ask for it.
 */
async function hasSiteAccess(): Promise<boolean> {
  try {
    return chrome.permissions?.contains
      ? await chrome.permissions.contains({ origins: ["http://*/*", "https://*/*"] })
      : true;
  } catch {
    return true;
  }
}
