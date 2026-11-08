// Saved hosts for the header's host switcher (D9). The list lives in this browser, for this web UI's address only
// (localStorage is per origin), so each host's web UI keeps its own. Choosing a host opens that host's own web UI;
// nothing here talks to another host.

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
