// Saved hosts for the header's host switcher (D9). The list lives in this browser, for this web UI's address only
// (localStorage is per origin), so each host's web UI keeps its own. Choosing a host opens that host's own web UI;
// nothing here talks to another host.
//
// Getting back (U-WU-1): a jump to another host carries this host's address and the names and addresses of the list
// in the link's #fragment, which the browser never sends to a server. Nothing else goes: no password, token or cookie,
// and the server sends no Referer. The other host's page takes the fragment off the address at once, offers "Back to"
// that host for that tab once it is in this browser's list, and offers the hosts it did not know yet, that one first;
// they are kept only when the user adds them, since anyone could make such a link and the Back link carries this list.

export interface SavedHost {
  id: string;
  name: string;
  /** `http(s)://host[:port]`, no path. */
  origin: string;
}

export type Normalized = { ok: true; origin: string; cleartext: boolean } | { ok: false; error: string };

/** The same rules as the browser extension's server address: http by default, the path dropped, credentials refused. */
export function normalizeHostAddress(input: string): Normalized {
  const text = input.trim();
  if (!text) return { ok: false, error: "Type the host's address, for example http://192.168.1.20:8080." };
  const withScheme = /^[a-z][a-z0-9+.-]*:\/\//i.test(text) ? text : `http://${text}`;
  let url: URL;
  try { url = new URL(withScheme); } catch { return { ok: false, error: 'That is not a web address.' }; }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return { ok: false, error: "The host's address starts with http:// or https://." };
  if (url.username || url.password) return { ok: false, error: 'Leave the user name and password out of the address.' };
  if (!url.hostname) return { ok: false, error: 'That address has no host name.' };
  const loopback = url.hostname === 'localhost' || url.hostname === '[::1]' || /^127(\.\d{1,3}){3}$/.test(url.hostname);
  return { ok: true, origin: url.origin, cleartext: url.protocol === 'http:' && !loopback };
}

export const cleartextNote = 'This address uses plain http, so the password and the downloads list cross the network unencrypted. Use it only on a network you trust.';

export const maxHostName = 40;

/** What to call a host that was saved without a name: its address without the scheme. */
export function defaultName(origin: string): string {
  return origin.replace(/^https?:\/\//, '');
}

export type Checked = { ok: true; host: SavedHost; cleartext: boolean } | { ok: false; error: string };

/**
 * Checks a name and an address against the saved list. `editing` is the id being edited (it may keep its own address);
 * `currentOrigin` is this web UI's own address, which is already "This host".
 */
export function checkHost(hosts: SavedHost[], name: string, address: string, currentOrigin: string, editing?: string): Checked {
  const normal = normalizeHostAddress(address);
  if (!normal.ok) return normal;
  if (normal.origin === currentOrigin) return { ok: false, error: 'That is the host you are on now.' };
  if (hosts.some((host) => host.origin === normal.origin && host.id !== editing)) return { ok: false, error: 'That address is already in the list.' };
  const trimmed = name.trim().slice(0, maxHostName);
  return { ok: true, cleartext: normal.cleartext, host: { id: editing ?? '', name: trimmed || defaultName(normal.origin), origin: normal.origin } };
}

/** The part of Storage the list uses, so tests need no browser. */
export interface KeyValue {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

const storageKey = 'inbound.hosts';

/** Reads the list; anything that is not a valid host is dropped, so a damaged value never reaches a link. */
export function parseHosts(text: string | null): SavedHost[] {
  if (!text) return [];
  let raw: unknown;
  try { raw = JSON.parse(text); } catch { return []; }
  if (!Array.isArray(raw)) return [];
  const seen = new Set<string>();
  const hosts: SavedHost[] = [];
  for (const entry of raw) {
    if (!entry || typeof entry !== 'object') continue;
    const { id, name, origin } = entry as Record<string, unknown>;
    if (typeof id !== 'string' || !id || typeof origin !== 'string') continue;
    const normal = normalizeHostAddress(origin);
    if (!normal.ok || normal.origin !== origin || seen.has(origin)) continue;
    seen.add(origin);
    hosts.push({ id, name: typeof name === 'string' && name.trim() ? name.trim().slice(0, maxHostName) : defaultName(origin), origin });
  }
  return hosts;
}

/** The saved hosts of this browser. Every storage call is guarded: a private window keeps the list until the page closes. */
export class HostList {
  private hosts: SavedHost[] = [];
  private nextId = 1;

  constructor(private readonly storage: () => KeyValue | null = browserStorage) {
    try { this.hosts = parseHosts(this.storage()?.getItem(storageKey) ?? null); } catch { this.hosts = []; }
  }

  get all(): SavedHost[] {
    return this.hosts.slice();
  }

  add(host: Omit<SavedHost, 'id'>): SavedHost {
    const saved = { ...host, id: this.newId() };
    this.hosts = [...this.hosts, saved];
    this.save();
    return saved;
  }

  update(id: string, host: Omit<SavedHost, 'id'>): void {
    this.hosts = this.hosts.map((candidate) => (candidate.id === id ? { id, ...host } : candidate));
    this.save();
  }

  remove(id: string): void {
    this.hosts = this.hosts.filter((candidate) => candidate.id !== id);
    this.save();
  }

  private newId(): string {
    let id: string;
    do { id = `h${Date.now().toString(36)}${(this.nextId++).toString(36)}`; } while (this.hosts.some((host) => host.id === id));
    return id;
  }

  private save(): void {
    try { this.storage()?.setItem(storageKey, JSON.stringify(this.hosts)); } catch { /* not stored: kept for this page only */ }
  }
}

function browserStorage(): KeyValue | null {
  try { return typeof localStorage === 'undefined' ? null : localStorage; } catch { return null; }
}

// ---- Passing the list along when jumping to another host ----

export const handoffParam = 'inbound-hosts';
/** At most this many hosts travel in one link. */
export const maxHandoff = 30;

export interface Handoff {
  /** The host the jump came from. */
  from: string;
  /** The hosts that host knows (names and addresses only), this one included when it was named there. */
  hosts: { name: string; origin: string }[];
}

/** The address of `target`'s web UI with this host and its list in the fragment. */
export function handoffUrl(target: string, here: string, hosts: readonly SavedHost[]): string {
  const list = hosts.filter((host) => host.origin !== here).slice(0, maxHandoff).map((host) => ({ n: host.name, o: host.origin }));
  const data = JSON.stringify({ v: 1, f: here, h: list });
  return `${target}/#${handoffParam}=${encodeURIComponent(data)}`;
}

/** Reads a handoff from an address's #fragment; anything malformed is dropped, as with the saved list. */
export function readHandoff(hash: string): Handoff | null {
  const prefix = `#${handoffParam}=`;
  if (!hash.startsWith(prefix)) return null;
  let raw: unknown;
  try { raw = JSON.parse(decodeURIComponent(hash.slice(prefix.length))); } catch { return null; }
  if (!raw || typeof raw !== 'object') return null;
  const { v, f, h } = raw as Record<string, unknown>;
  if (v !== 1 || typeof f !== 'string') return null;
  const from = normalizeHostAddress(f);
  if (!from.ok || from.origin !== f) return null;
  const hosts: Handoff['hosts'] = [];
  const seen = new Set<string>();
  for (const entry of Array.isArray(h) ? h.slice(0, maxHandoff) : []) {
    if (!entry || typeof entry !== 'object') continue;
    const { n, o } = entry as Record<string, unknown>;
    if (typeof o !== 'string') continue;
    const normal = normalizeHostAddress(o);
    if (!normal.ok || normal.origin !== o || seen.has(o)) continue;
    seen.add(o);
    hosts.push({ name: typeof n === 'string' && n.trim() ? n.trim().slice(0, maxHostName) : defaultName(o), origin: o });
  }
  return { from: from.origin, hosts };
}

export interface Arrival {
  /**
   * Where "Back to" goes, and what to call it: only a host saved in this browser's list. Anyone can craft the fragment,
   * and the Back link carries this list, so a host the list doesn't have is first offered with Add (U-WU-1).
   */
  back: { name: string; origin: string } | null;
  /** The host the link says it came from, saved or not, for the wording. */
  from: { name: string; origin: string } | null;
  /** What the other host calls this one, if it named it. */
  selfName: string;
  /** Hosts the other host knows and this browser's list for this address doesn't (the one it came from first). */
  offered: { name: string; origin: string }[];
}

/** What a page makes of a handoff, given its own address and its saved list. */
export function arrival(handoff: Handoff | null, here: string, saved: readonly SavedHost[]): Arrival {
  if (!handoff || handoff.from === here) return { back: null, from: null, selfName: '', offered: [] };
  const known = new Set(saved.map((host) => host.origin));
  const savedName = saved.find((host) => host.origin === handoff.from)?.name;
  const from = { name: savedName ?? defaultName(handoff.from), origin: handoff.from };
  const selfName = handoff.hosts.find((host) => host.origin === here)?.name ?? '';
  const offered = [from, ...handoff.hosts].filter((host, index, all) =>
    host.origin !== here && !known.has(host.origin) && all.findIndex((other) => other.origin === host.origin) === index);
  return { back: savedName === undefined ? null : from, from, selfName: selfName === defaultName(here) ? '' : selfName, offered };
}

const arrivalKey = 'inbound.hostHandoff';

/**
 * Takes a handoff off this page's address (so a reload or a bookmark doesn't carry it) and keeps it for this tab.
 * Called by both the sign-in page and the app before they draw. Returns the handoff of this tab, new or kept.
 */
export function takeHandoff(): Handoff | null {
  if (typeof location === 'undefined') return null;
  const found = readHandoff(location.hash);
  if (location.hash.startsWith(`#${handoffParam}=`)) {
    try { history.replaceState(history.state, '', location.pathname + location.search); } catch { /* the fragment stays */ }
  }
  try {
    if (found) sessionStorage.setItem(arrivalKey, JSON.stringify(found));
    else return keptHandoff(sessionStorage.getItem(arrivalKey));
  } catch {
    // No session storage: the handoff lasts until the page closes.
  }
  return found;
}

/** A handoff kept for this tab, checked again as if it had just come in a link. */
export function keptHandoff(text: string | null): Handoff | null {
  if (!text) return null;
  try {
    const kept = JSON.parse(text) as Partial<Handoff>;
    if (typeof kept.from !== 'string' || !Array.isArray(kept.hosts)) return null;
    const data = { v: 1, f: kept.from, h: kept.hosts.map((host) => ({ n: host?.name, o: host?.origin })) };
    return readHandoff(`#${handoffParam}=${encodeURIComponent(JSON.stringify(data))}`);
  } catch {
    return null;
  }
}

/** This tab's handoff, taken by the page's entry point before it draws. */
export const tabHandoff: { value: Handoff | null } = { value: null };
