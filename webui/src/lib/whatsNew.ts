// UPD.N1: What's new in the web UI. The server sends its changelog (changelog.get); the page shows the lines about the
// web UI and the server (tagged webui, server, docker or all), folds the rest under one line, and hides torrent lines
// while torrents are off, TorBox lines without an account and Linux lines always. The desktop window has the same rules
// (src/DownloadManager.Contracts/Changelog/ChangelogFilter.cs).
//
// It opens by itself once per browser per server, after the server's version changed. The version last seen is kept in
// this browser for this server's address (localStorage is per origin, and the host switcher opens another server's own
// page, so each server's notes are its own). A browser that never saw this server shows nothing and only notes the version.

import { compareVersions } from './versionStatus';

export interface ChangelogItem {
  text: string;
  tags: string[];
}

export interface ChangelogSection {
  title: string;
  items: ChangelogItem[];
}

export interface ChangelogVersion {
  version: string;
  date?: string | null;
  sections: ChangelogSection[];
}

export interface Changelog {
  versions: ChangelogVersion[];
}

export interface Features {
  torrents: boolean;
  /** The ids of the providers with an account set, lower case: a line tagged with one shows only then. */
  providers: readonly string[];
}

/** Any tag that isn't a place or `torrents` is a provider's id (ChangelogTags.IsProvider). */
function isProvider(tag: string): boolean {
  return /^[a-z][a-z0-9-]{1,31}$/.test(tag) && tag !== 'torrents' && !places.includes(tag);
}

export interface ShownVersion {
  version: string;
  date?: string | null;
  sections: { title: string; lines: string[] }[];
}

export interface ElsewhereLine {
  place: string;
  text: string;
}

export interface WhatsNewView {
  versions: ShownVersion[];
  elsewhere: ElsewhereLine[];
}

const places = ['all', 'windows', 'webui', 'server', 'docker', 'linux', 'extension'];
const mine = ['webui', 'server', 'docker'];
const labels: Record<string, string> = { windows: 'Windows', webui: 'Web UI', extension: 'Extension' };
const placeNames: Record<string, string> = { Windows: 'the Windows app', 'Web UI': 'the web UI', Extension: 'the browser extension', Server: 'the server' };

/** 'here', the label of the place it is folded under, or null when it is hidden. */
function placeOf(tags: readonly string[], features: Features): string | null {
  if (tags.includes('torrents') && !features.torrents) return null;
  if (tags.some((tag) => isProvider(tag) && !features.providers.includes(tag))) return null;
  const tagged = tags.filter((tag) => places.includes(tag));
  if (tagged.length === 0 || tagged.includes('all') || tagged.some((tag) => mine.includes(tag))) return 'here';
  const other = tagged.find((tag) => tag !== 'linux');
  return other ? (labels[other] ?? 'Server') : null;
}

export function filterChangelog(versions: readonly ChangelogVersion[], features: Features): WhatsNewView {
  const shown: ShownVersion[] = [];
  const elsewhere: ElsewhereLine[] = [];
  for (const version of versions) {
    const sections: ShownVersion['sections'] = [];
    for (const section of version.sections ?? []) {
      const lines: string[] = [];
      for (const item of section.items ?? []) {
        if (typeof item?.text !== 'string' || !item.text.trim()) continue;
        const place = placeOf(Array.isArray(item.tags) ? item.tags : [], features);
        if (place === 'here') lines.push(item.text);
        else if (place) elsewhere.push({ place, text: item.text });
      }
      if (lines.length) sections.push({ title: section.title, lines });
    }
    if (sections.length) shown.push({ version: version.version, date: version.date, sections });
  }
  return { versions: shown, elsewhere };
}

/** "the Windows app and the browser extension", in the order the lines name them. */
export function placesText(elsewhere: readonly ElsewhereLine[]): string {
  const names = [...new Set(elsewhere.map((line) => placeNames[line.place] ?? 'the server'))];
  if (names.length <= 1) return names[0] ?? '';
  return `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`;
}

/** "3 changes in the Windows app and the browser extension"; empty for none. */
export function elsewhereText(elsewhere: readonly ElsewhereLine[]): string {
  if (!elsewhere.length) return '';
  return `${elsewhere.length} change${elsewhere.length === 1 ? '' : 's'} in ${placesText(elsewhere)}`;
}

/** The versions after `after` (null: none before) up to and including `upTo`, newest first. */
export function between(versions: readonly ChangelogVersion[], after: string | null, upTo: string): ChangelogVersion[] {
  return versions
    .filter((entry) => (compareVersions(entry.version, upTo) ?? 1) <= 0 && (after === null || (compareVersions(entry.version, after) ?? -1) > 0))
    .sort((a, b) => compareVersions(b.version, a.version) ?? 0);
}

/** The entry for exactly this version ("0.7" and "0.7.0" are the same), or undefined. */
export function findVersion(versions: readonly ChangelogVersion[], version: string): ChangelogVersion | undefined {
  return versions.find((entry) => compareVersions(entry.version, version) === 0);
}

/** "12 Nov 2026" from "2026-11-12"; null without a date. */
export function displayDate(date: string | null | undefined): string | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date ?? '');
  if (!match) return null;
  const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  const month = months[Number(match[2]) - 1];
  return month ? `${Number(match[3])} ${month} ${match[1]}` : null;
}

export function releaseNotesUrl(version: string): string {
  return `https://github.com/Kaan88/inbound/releases/tag/v${version.trim().replace(/^v/i, '')}`;
}

/** The part of Storage this uses, so tests need no browser. */
export interface KeyValue {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

export function seenKey(origin: string): string {
  return `inbound.whatsNewSeen:${origin}`;
}

export interface SeenDecision {
  /** Show the notes after `after` up to the server's version. */
  open: boolean;
  after: string | null;
  /** False when `after` is a guess: this browser used the web UI before the mark was kept (0.7.1 and older). */
  afterKnown: boolean;
  /** The version to keep as seen; null keeps what is there. */
  record: string | null;
}

/** 0.7.2 is the first web UI that keeps the mark; a browser with web UI state and no mark came from this or older. */
export const lastWithoutMark = '0.7.1';

const nothing: SeenDecision = { open: false, after: null, afterKnown: false, record: null };

/**
 * What to do when the page meets a server of `serverVersion`, having last seen `lastSeen` there. `hadStateBefore`: this
 * browser already kept other web UI choices here (theme, sort, hosts…) before this page loaded.
 */
export function decide(lastSeen: string | null, serverVersion: string | null | undefined, hadStateBefore = false): SeenDecision {
  if (!serverVersion || compareVersions(serverVersion, serverVersion) === null) return nothing;
  if (!lastSeen || compareVersions(lastSeen, lastSeen) === null) {
    // Used here before the mark was kept: an upgrade from 0.7.1 or older, so the notes after 0.7.1 apply.
    if (hadStateBefore && (compareVersions(serverVersion, lastWithoutMark) ?? 0) > 0)
      return { open: true, after: lastWithoutMark, afterKnown: false, record: serverVersion };
    // A browser that never saw this server: nothing to tell; from now on its version is kept.
    return { ...nothing, record: serverVersion };
  }
  const order = compareVersions(serverVersion, lastSeen) ?? 0;
  if (order > 0) return { open: true, after: lastSeen, afterKnown: true, record: serverVersion };
  // An older server than last time (a rollback, or another server at this address) keeps the newer mark.
  return nothing;
}

/** Any web UI choice kept in this browser other than the What's new mark: read once, as the page loads. */
export function hadWebState(storage: Pick<Storage, 'length' | 'key'> | null): boolean {
  try {
    if (!storage) return false;
    for (let i = 0; i < storage.length; i++) {
      const key = storage.key(i);
      if (key?.startsWith('inbound.') && !key.startsWith('inbound.whatsNewSeen:')) return true;
    }
  } catch {
    // Storage that can't be read: as if empty.
  }
  return false;
}

/** Reads the mark and decides, writing nothing. Storage that can't be used (a private window) shows nothing. */
export function decideSeen(storage: KeyValue | null, origin: string, serverVersion: string | null | undefined, hadStateBefore = false): SeenDecision {
  try {
    if (!storage) return nothing;
    return decide(storage.getItem(seenKey(origin)), serverVersion, hadStateBefore);
  } catch {
    return nothing;
  }
}

/**
 * Keeps `version` as seen. False when it can't be kept: then the notes aren't shown either, or they would show at every
 * visit. Called only once the notes are on screen, or when there is nothing to show, so a page that couldn't show them
 * (another dialog was open) shows them next time.
 */
export function recordSeen(storage: KeyValue | null, origin: string, version: string): boolean {
  try {
    if (!storage) return false;
    storage.setItem(seenKey(origin), version);
    return true;
  } catch {
    return false;
  }
}

/** Decides and keeps the new mark at once (for a page that will show the notes whenever they apply). */
export function decideAndRecord(storage: KeyValue | null, origin: string, serverVersion: string | null | undefined, hadStateBefore = false): SeenDecision {
  const decision = decideSeen(storage, origin, serverVersion, hadStateBefore);
  if (decision.record && !recordSeen(storage, origin, decision.record)) return nothing;
  return decision;
}

/** Only well-formed versions, sections and lines go on; anything else from the server is dropped. */
export function readChangelog(payload: unknown): ChangelogVersion[] {
  const versions = (payload as Partial<Changelog> | null | undefined)?.versions;
  if (!Array.isArray(versions)) return [];
  return versions
    .filter((entry): entry is ChangelogVersion => !!entry && typeof entry.version === 'string' && Array.isArray(entry.sections))
    .map((entry) => ({
      version: entry.version,
      date: typeof entry.date === 'string' ? entry.date : null,
      sections: entry.sections
        .filter((section) => section && typeof section.title === 'string' && Array.isArray(section.items))
        .map((section) => ({
          title: section.title,
          items: section.items
            .filter((item) => item && typeof item.text === 'string')
            .map((item) => ({ text: item.text, tags: Array.isArray(item.tags) ? item.tags.filter((tag) => typeof tag === 'string') : [] })),
        })),
    }));
}
