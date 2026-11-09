// Which browser downloads the extension catches automatically, and why it leaves one alone.
// Pure functions, so the rules can be tested without a browser.

import { isUnsupportedUrl } from "./unsupported-sites";

export type StartMode = "now" | "later";
/** "default": send straight to the app's default folder. "ask": open the folder window first. */
export type DestinationMode = "default" | "ask";
/** Where a download goes: the desktop app on this PC, or the user's paired Inbound server (transport.ts). */
export type DefaultTarget = "local" | "server";
/** The extension pages' theme (EXT.R8): the browser's, or always light or dark. */
export type ThemeChoice = "system" | "light" | "dark";

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
  if (settings.destinationMode !== "ask") settings.destinationMode = "default";
  if (settings.defaultTarget !== "server") settings.defaultTarget = "local";
  if (settings.theme !== "light" && settings.theme !== "dark") settings.theme = "system";
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

/** The settings a page may change, by name: a save sends only the ones its user changed (U-EX-6). */
export const SETTING_KEYS: ReadonlyArray<keyof Settings> = [
  "enabled", "autoIntercept", "startMode", "destinationMode", "defaultTarget", "interceptedExtensions",
  "excludedExtensions", "excludedHosts", "notifications", "theme",
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
    return { capture: false, reason: `${excludedHost} is on your list of sites never to catch.`, quiet: false };
  }

  const extension = extensionOf(item.fileName || url);
  if (extension && settings.excludedExtensions.includes(extension)) {
    return {
      capture: false,
      reason: `.${extension} files are on your list of file types never to catch.`,
      quiet: DEFAULT_EXCLUDED_EXTENSIONS.includes(extension),
    };
  }

  if (extension && settings.interceptedExtensions.length > 0 && !settings.interceptedExtensions.includes(extension)) {
    return { capture: false, reason: `.${extension} is not on your list of file types to catch.`, quiet: false };
  }

  if (!extension && /^text\/html/i.test(item.mime ?? "")) {
    return { capture: false, reason: "It is a web page.", quiet: true };
  }

  return { capture: true };
}
