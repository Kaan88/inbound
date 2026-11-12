// Which browser downloads the extension catches automatically, and why it leaves one alone.
// Pure functions, so the rules can be tested without a browser.

import { cleanServerName } from "./server-name";
import { isUnsupportedUrl } from "./unsupported-sites";

export type StartMode = "now" | "later";
/** "default": send straight to the app's default folder. "ask": open the folder window first. */
export type DestinationMode = "default" | "ask";
/** Where a download goes: the desktop app on this PC, or the user's paired Inbound server (transport.ts). */
export type DefaultTarget = "local" | "server";
/** The extension pages' theme (EXT.R8): the browser's, or always light or dark. */
export type ThemeChoice = "system" | "light" | "dark";
/**
 * EXT.R10: where "Ask where to save" starts. "default": the target's default folder every time (UI.R3, the default).
 * "last": the last folder a download was sent to from a form, per target. "custom": a folder set here, for This PC.
 */
export type AskStartsOn = "default" | "last" | "custom";
/**
 * EXT.R12: what the toolbar icon does when a download sent from the browser finishes: open the toolbar popup (where the
 * browser allows it; otherwise the icon flashes), flash the icon a few times, or nothing.
 */
export type FinishAction = "popup" | "flash" | "nothing";

export interface Settings {
  enabled: boolean;
  autoIntercept: boolean;
  startMode: StartMode;
  destinationMode: DestinationMode;
  /**
   * Where automatic catches go, and which target the folder window starts on. "server" counts only while a
   * server is paired; otherwise everything goes to this PC.
   */
  defaultTarget: DefaultTarget;
  /** Empty means every file type that is not excluded. */
  interceptedExtensions: string[];
  excludedExtensions: string[];
  excludedHosts: string[];
  notifications: boolean;
  /** The extension pages' theme; mirrored to localStorage["inbound.theme"] for theme.js (see ui/theme.ts). */
  theme: ThemeChoice;
  /**
   * The user's own name for the paired server (EXT.R9, GitHub #2); "" uses the name the server gave, else its
   * address. Kept apart from the pairing, so it can be set before pairing and pairing again never replaces it.
   */
  serverCustomName: string;
  /**
   * EXT.R10: the default folder for browser downloads on This PC. Empty: the desktop app's default folder. Automatic
   * sends to This PC use it, and the forms call it "Default folder" for This PC. The server keeps its own default.
   */
  browserFolder: string;
  /** EXT.R10: where "Ask where to save" (and the right-click and Download All windows) start. */
  askStartsOn: AskStartsOn;
  /** EXT.R10: the folder on This PC that "custom" starts on. */
  askStartFolder: string;
  /**
   * EXT.R12: the app opens its progress and finished windows for downloads sent from the browser. Off by default: the
   * browser keeps the focus and the toolbar icon shows the progress, as for the browser's own downloads.
   */
  openAppWindows: boolean;
  /** EXT.R12: what happens when a download sent from the browser finishes. */
  onFinish: FinishAction;
  /** Which version of the default rules these settings were written against (see migrateSettings). */
  rulesVersion: number;
}

/**
 * Version 2 (EXT.B1): catch everything except an exclude list. Version 1 caught only a fixed list of
 * file types, which missed flac, mp4, pdf, epub and many others.
 */
export const RULES_VERSION = 2;

/** The allow-list every copy before rules version 2 shipped with. Kept only to recognise it. */
export const LEGACY_INTERCEPTED_EXTENSIONS: readonly string[] = [
  "3gp", "7z", "aac", "ace", "aif", "apk", "arj", "asf", "avi", "bin", "bz2",
  "exe", "gz", "gzip", "img", "iso", "lzh", "m4a", "m4v", "mkv", "mov", "mp3",
  "mpa", "mpe", "mpeg", "mpg", "msi", "msu", "ogg", "ogv", "plj", "pps", "ppt",
  "qt", "ra", "rar", "rm", "rmvb", "sea", "sit", "sitx", "tar", "wav", "wma",
  "wmv", "z", "zip",
];

/**
 * Left in the browser by default: saved web pages and their parts, and images, which "Save image as"
 * produces all the time and which are small. Everything else is caught. The list stays editable.
 */
export const DEFAULT_EXCLUDED_EXTENSIONS: readonly string[] = [
  "htm", "html", "xhtml", "mht", "mhtml", "php", "asp", "aspx", "jsp",
  "js", "css", "json", "xml",
  "jpg", "jpeg", "png", "gif", "webp", "bmp", "ico", "svg", "avif",
];

export const DEFAULT_SETTINGS: Settings = {
  enabled: true,
  // On from a fresh install (owner decision, 2026-11-08; 0.4.0 had it off). Settings opens on install
  // with the explanation of what it shares, and turning it off there is one click.
  autoIntercept: true,
  startMode: "now",
  destinationMode: "default",
  defaultTarget: "local",
  interceptedExtensions: [],
  excludedExtensions: [...DEFAULT_EXCLUDED_EXTENSIONS],
  excludedHosts: [],
  notifications: true,
  theme: "system",
  serverCustomName: "",
  browserFolder: "",
  askStartsOn: "default",
  askStartFolder: "",
  openAppWindows: false,
  onFinish: "flash",
  rulesVersion: RULES_VERSION,
};

function sameSet(left: readonly string[], right: readonly string[]): boolean {
  const a = new Set(left.map((item) => item.toLowerCase()));
  const b = new Set(right.map((item) => item.toLowerCase()));
  return a.size === b.size && [...a].every((item) => b.has(item));
}

/**
 * Settings as stored, brought up to the current rules. A copy that still has the old allow-list
 * exactly as shipped never chose it, so it moves to "everything except the exclude list". Someone who
 * edited the list keeps it. Returns whether anything changed, so the caller can store the result once.
 */
export function migrateSettings(stored: Partial<Settings> | undefined | null): { settings: Settings; changed: boolean } {
  if (!stored || typeof stored !== "object") {
    return { settings: { ...DEFAULT_SETTINGS, excludedExtensions: [...DEFAULT_SETTINGS.excludedExtensions] }, changed: false };
  }

  const settings: Settings = { ...DEFAULT_SETTINGS, ...stored };
  // Audit 3 L12: a stored value of the wrong type (a damaged or hand-edited store) falls back to the default, so
  // decideCapture can't throw on it and leave every download in the browser without a word.
  for (const key of ["enabled", "autoIntercept", "notifications", "openAppWindows"] as const) {
    if (typeof settings[key] !== "boolean") settings[key] = DEFAULT_SETTINGS[key];
  }
  for (const key of ["interceptedExtensions", "excludedExtensions", "excludedHosts"] as const) {
    const value: unknown = settings[key];
    settings[key] = Array.isArray(value)
      ? value.filter((item): item is string => typeof item === "string")
      : [...DEFAULT_SETTINGS[key]];
  }
  if (settings.startMode !== "later") settings.startMode = "now";
  if (typeof settings.rulesVersion !== "number") settings.rulesVersion = DEFAULT_SETTINGS.rulesVersion;
  if (settings.destinationMode !== "ask") settings.destinationMode = "default";
  if (settings.defaultTarget !== "server") settings.defaultTarget = "local";
  if (settings.theme !== "light" && settings.theme !== "dark") settings.theme = "system";
  settings.serverCustomName = cleanServerName(settings.serverCustomName);
  settings.browserFolder = folderSetting(settings.browserFolder);
  if (settings.askStartsOn !== "last" && settings.askStartsOn !== "custom") settings.askStartsOn = "default";
  settings.askStartFolder = folderSetting(settings.askStartFolder);
  if (settings.onFinish !== "popup" && settings.onFinish !== "nothing") settings.onFinish = "flash";
  if ((stored.rulesVersion ?? 1) >= RULES_VERSION) {
    return { settings, changed: false };
  }

  const untouchedList = Array.isArray(stored.interceptedExtensions) &&
    sameSet(stored.interceptedExtensions, LEGACY_INTERCEPTED_EXTENSIONS);
  if (untouchedList) {
    settings.interceptedExtensions = [];
    const excluded = Array.isArray(stored.excludedExtensions) ? stored.excludedExtensions : [];
    // The old default exclude list was empty, so anything there was the user's and stays. The new
    // defaults are added to it either way: under the old allow-list a page or an image was never
    // caught, and one exclude of the user's own must not start sending those to the app.
    // Lower case, as the rules compare them.
    settings.excludedExtensions = [...new Set([...excluded, ...DEFAULT_EXCLUDED_EXTENSIONS]
      .map((extension) => String(extension).trim().toLowerCase())
      .filter((extension) => extension.length > 0))];
  }
  settings.rulesVersion = RULES_VERSION;
  return { settings, changed: true };
}

/** A folder setting as stored: a trimmed string, at most 4096 characters; anything else is empty. */
export function folderSetting(value: unknown): string {
  return typeof value === "string" ? value.trim().slice(0, 4096) : "";
}

/** The settings a page may change, by name: a save sends only the ones its user changed (U-EX-6). */
export const SETTING_KEYS: ReadonlyArray<keyof Settings> = [
  "enabled", "autoIntercept", "startMode", "destinationMode", "defaultTarget", "interceptedExtensions",
  "excludedExtensions", "excludedHosts", "notifications", "theme", "serverCustomName",
  "browserFolder", "askStartsOn", "askStartFolder", "openAppWindows", "onFinish",
];

/**
 * A page's changes laid over the settings stored now, never a whole copy of the page's older view: a second
 * Options tab, the popup's This PC / Server switch or a save elsewhere since the page opened keeps what it set
 * (U-EX-6). Unknown names are dropped.
 */
export function applySettingChanges(current: Settings, changes: unknown): Settings {
  const next: Settings = { ...current };
  if (!changes || typeof changes !== "object") return next;
  for (const key of SETTING_KEYS) {
    if (Object.prototype.hasOwnProperty.call(changes, key)) {
      (next as unknown as Record<string, unknown>)[key] = (changes as Record<string, unknown>)[key];
    }
  }
  return migrateSettings(next).settings;
}

export function isHttpUrl(url: string | undefined | null): boolean {
  return !!url && /^https?:\/\//i.test(url);
}

export function hostOf(url: string): string | null {
  try {
    return new URL(url).hostname.toLowerCase();
  } catch {
    return null;
  }
}

export function hostMatches(host: string, pattern: string): boolean {
  const normalized = pattern.trim().toLowerCase().replace(/^\*\./, "");
  if (!normalized) {
    return false;
  }

  return host === normalized || host.endsWith(`.${normalized}`);
}

export function extensionOf(value: string): string {
  const clean = (value.split("?")[0] ?? "").split("#")[0] ?? "";
  const match = /\.([a-z0-9]{1,10})$/i.exec(clean);
  return match ? match[1]!.toLowerCase() : "";
}

/**
 * An HLS playlist or a DASH manifest, by its name or type, as the app decides it. A plain ".m3u" list (internet radio,
 * a music playlist) is often sent with the HLS type and is still an ordinary file.
 */
export function isStreamPlaylist(nameOrUrl: string, mime: string | undefined): boolean {
  const extension = extensionOf(nameOrUrl);
  if (extension === "m3u8" || extension === "mpd") return true;
  const type = (mime ?? "").split(";")[0]!.trim().toLowerCase();
  if (type === "application/dash+xml") return true;
  return type.includes("mpegurl") && extension !== "m3u";
}

export interface CandidateDownload {
  url: string;
  finalUrl?: string;
  referrer?: string;
  mime?: string;
  /** The name the file would get, already chosen from the browser's suggestion and the addresses. */
  fileName: string;
}

export type CaptureDecision =
  | { capture: true }
  /** reason: plain words for the user, or null when nothing should be said (blob:, data: and the like). */
  | { capture: false; reason: string | null; quiet: boolean };

/**
 * Whether one browser download matches the rules. The state checks (on, paused, Delete held) come
 * first in the caller, since they don't depend on the download.
 * quiet: the reason is shown in the popup only, not as a notification. That is for things that are
 * expected to stay in the browser all the time (web pages, images), where a notification each time
 * would be noise.
 */
export function decideCapture(item: CandidateDownload, settings: Settings): CaptureDecision {
  const url = item.finalUrl || item.url;
  // blob: and data: downloads are made by the page itself and can't be fetched again. Nothing to say.
  if (!isHttpUrl(url)) {
    return { capture: false, reason: null, quiet: true };
  }

  if ([item.url, item.finalUrl, item.referrer].some(isUnsupportedUrl)) {
    return { capture: false, reason: "Downloads from this site stay in the browser.", quiet: true };
  }

  // The app no longer downloads streams (cut in its 0.7.2), so a playlist the browser saves stays a file there.
  if (isStreamPlaylist(item.fileName || url, item.mime) || isStreamPlaylist(url, undefined)) {
    return { capture: false, reason: "Streaming playlists stay in the browser.", quiet: false };
  }

  const host = hostOf(url);
  if (!host) {
    return { capture: false, reason: null, quiet: true };
  }

  // Every site involved, not just where a redirect ended up: an excluded site's cookies are read for
  // the original address, and files behind a login often redirect to a storage host.
  const sourceHosts = [item.url, item.finalUrl, item.referrer]
    .map((value) => (value ? hostOf(value) : null))
    .filter((value): value is string => !!value);
  const excludedHost = settings.excludedHosts.find((pattern) => sourceHosts.some((candidate) => hostMatches(candidate, pattern)));
  if (excludedHost) {
    return { capture: false, reason: `${excludedHost} is on your list of sites never to send from.`, quiet: false };
  }

  const extension = extensionOf(item.fileName || url);
  if (extension && settings.excludedExtensions.includes(extension)) {
    return {
      capture: false,
      reason: `.${extension} files are on your list of file types never to send.`,
      quiet: DEFAULT_EXCLUDED_EXTENSIONS.includes(extension),
    };
  }

  if (extension && settings.interceptedExtensions.length > 0 && !settings.interceptedExtensions.includes(extension)) {
    return { capture: false, reason: `.${extension} is not on your list of file types to send.`, quiet: false };
  }

  if (!extension && /^text\/html/i.test(item.mime ?? "")) {
    return { capture: false, reason: "It is a web page.", quiet: true };
  }

  return { capture: true };
}
