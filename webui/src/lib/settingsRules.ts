// Rules of the Options screen that do not need a page: what the desktop's Options window checks before it saves
// (MainWindow.xaml.cs, the window.Save handler) and the small conversions it makes.
import type { ArchiveSettings, CategoryDefinition, ConnectionProfile, FavoriteFolder, Settings, SiteLogin } from './types';

/**
 * A saved password belongs to a site and user name. When either changed and no new password was typed, the server could
 * not keep the old one, so the form asks for it again (the desktop's wording and rule: site compared without regard to
 * case, user name exactly).
 */
export function loginNeedsPassword(original: Pick<SiteLogin, 'site' | 'username' | 'hasPassword'> | undefined, site: string, username: string, password: string | undefined): boolean {
  if (!original || !original.hasPassword || password) return false;
  return original.site.toLowerCase() !== site.trim().toLowerCase() || original.username !== username.trim();
}

export const loginPasswordMessage = 'Re-enter the password for a login whose site or user name changed.';

// ---- The whole Options draft -------------------------------------------------------------------------------------

export type OptionsPage = 'general' | 'saveto' | 'downloads' | 'connection' | 'proxy' | 'logins' | 'updates' | 'extraction' | 'signin';

export interface LoginRow {
  id: string;
  site: string;
  username: string;
  password: string;
  hasPassword: boolean;
  /** Not saved yet: Remove deletes the row; for a saved login it is staged and can be undone. */
  isNew: boolean;
  removed: boolean;
  originalSite: string;
  originalUser: string;
}

export interface FavoriteRow {
  key: number;
  name: string;
  path: string;
}

export interface CategoryRow {
  id: string;
  name: string;
  /** Space separated, as typed. */
  extensions: string;
  folder: string;
  removed: boolean;
}

export interface ProxyForm {
  mode: string;
  type: number;
  host: string;
  username: string;
  password: string;
  forget: boolean;
  hasPassword: boolean;
}

export interface OptionsForm {
  defaultDownloadDirectory: string;
  useCategoryFolders: boolean;
  categoryFolders: Record<string, string>;
  customCategories: CategoryRow[];
  favorites: FavoriteRow[];
  /** Folders that were favorites when the draft was made, to keep ones saved meanwhile. */
  favoritesAtOpen: string[];
  maxConcurrentDownloads: number | null;
  defaultConnections: number | null;
  maxConnectionsPerHost: number | null;
  speedKb: number | null;
  /** The limit as stored and as shown when the draft was made, so an untouched box keeps the stored bytes. */
  speedBytesAtOpen: number | null;
  speedKbAtOpen: number | null;
  profiles: string;
  autoResume: boolean;
  allowLocalNetwork: boolean;
  browserIntegration: boolean;
  proxy: ProxyForm;
  logins: LoginRow[];
}

/** The built-in categories, in the order of the desktop's Save to page. */
export const categoryNames = ['compressed', 'documents', 'music', 'programs', 'video', 'other'];

export function categoryLabel(name: string): string {
  return name.charAt(0).toUpperCase() + name.slice(1);
}

// Proxy address (src/DownloadManager.Contracts/ProxyAddress.cs).
export const proxyTypes = ['HTTP / HTTPS', 'SOCKS4', 'SOCKS4a', 'SOCKS5'];
const proxySchemes = ['http', 'socks4', 'socks4a', 'socks5'];

/** Splits a stored address into the protocol index and host:port. */
export function splitProxyAddress(address: string | null | undefined): { type: number; host: string } {
  const text = (address ?? '').trim();
  if (!text) return { type: 0, host: '' };
  const at = text.indexOf('://');
  if (at < 0) return { type: 0, host: text };
  const scheme = text.slice(0, at).toLowerCase();
  const index = scheme === 'https' ? 0 : proxySchemes.indexOf(scheme);
  return { type: index < 0 ? 0 : index, host: text.slice(at + 3).replace(/\/+$/, '') };
}

/** The stored address, or null when the host is empty or the port is missing or outside 1-65535. */
export function composeProxyAddress(type: number, hostPort: string | null | undefined): string | null {
  let text = (hostPort ?? '').trim();
  const at = text.indexOf('://');
  if (at >= 0) text = text.slice(at + 3);
  text = text.replace(/\/+$/, '');
  if (!text || text.includes('/') || text.includes(' ')) return null;
  let host = '';
  try {
    host = new URL('http://' + text).hostname;
  } catch {
    return null;
  }
  if (!host) return null;
  let portText = '';
  if (text.startsWith('[')) {
    const bracket = text.lastIndexOf(']:');
    portText = bracket >= 0 ? text.slice(bracket + 2) : '';
  } else if (text.split(':').length === 2) {
    portText = text.slice(text.lastIndexOf(':') + 1);
  }
  const port = /^\d+$/.test(portText) ? Number(portText) : NaN;
  if (!(port >= 1 && port <= 65535)) return null;
  const index = type >= 0 && type < 4 ? type : 0;
  return index === 0 ? text : `${proxySchemes[index]}://${text}`;
}

// Per-site limits: one "pattern=max" per line.
export function profilesToText(profiles: ConnectionProfile[] | undefined): string {
  return (profiles ?? []).map((p) => `${p.pattern}=${p.maxConnections}`).join('\n');
}

export function parseProfiles(text: string): { profiles: ConnectionProfile[]; error: string | null } {
  const profiles: ConnectionProfile[] = [];
  for (const line of text.split(/[\r\n]+/)) {
    const trimmed = line.trim();
    if (!trimmed) continue;
    const at = trimmed.lastIndexOf('=');
    const limitText = at > 0 ? trimmed.slice(at + 1).trim() : '';
    const limit = /^\d+$/.test(limitText) ? Number(limitText) : NaN;
    if (!(limit >= 1 && limit <= 32)) return { profiles: [], error: `Per-site limit '${trimmed}' must use pattern=max (1-32).` };
    profiles.push({ pattern: trimmed.slice(0, at).trim(), maxConnections: limit });
  }
  return { profiles, error: null };
}

export function parseExtensions(text: string): string[] {
  return text.split(/[\s,;]+/).filter(Boolean);
}

// Extraction (src/DownloadManager.Desktop.WinUI/Remote/ArchiveForm.cs).
export const archiveDestinations = ['nextToArchive', 'subfolder', 'folder'];
export const archiveConflicts = ['rename', 'overwrite', 'skip'];
export const archiveDestinationLabels = ['The download folder, next to the archive', 'A subfolder named after the archive', 'This folder:'];
export const archiveConflictLabels = ['Keep both: give the new file another name', 'Overwrite the existing file', 'Keep the existing file and skip the new one'];

export interface ArchiveValues {
  autoExtract: boolean;
  destination: number;
  folder: string;
  preserveFolders: boolean;
  conflict: number;
  deleteArchives: boolean;
}

export function archiveValues(saved: ArchiveSettings): ArchiveValues {
  const at = (words: string[], word: string | undefined) => Math.max(0, words.findIndex((w) => w.toLowerCase() === (word ?? '').toLowerCase()));
  return {
    autoExtract: saved.autoExtract,
    destination: at(archiveDestinations, saved.destination),
    folder: saved.folder ?? '',
    preserveFolders: saved.preserveFolders,
    conflict: at(archiveConflicts, saved.conflictPolicy),
    deleteArchives: saved.deleteArchives,
  };
}

const fullPath = /^([A-Za-z]:[\\/]|\\\\|\/)/;

/** Only what changed, or null when nothing did; or the reason the page can't be saved. */
export function archiveChanges(saved: ArchiveSettings, values: ArchiveValues): { update: Partial<ArchiveSettings> | null; error: string | null } {
  const folder = values.folder.trim();
  const folderChosen = values.destination === 2;
  if (folderChosen) {
    if (!folder) return { update: null, error: 'Choose the folder to extract to.' };
    if (!fullPath.test(folder)) return { update: null, error: 'The extraction folder must be a full path.' };
  }
  const update: Partial<ArchiveSettings> = {};
  let changed = false;
  if (values.autoExtract !== saved.autoExtract) { update.autoExtract = values.autoExtract; changed = true; }
  const destination = archiveDestinations[Math.min(Math.max(values.destination, 0), 2)];
  if (destination !== saved.destination) { update.destination = destination; changed = true; }
  // While another destination is chosen the folder box is greyed out and keeps what it had.
  if (folderChosen && folder.toLowerCase() !== (saved.folder ?? '').trim().toLowerCase()) { update.folder = folder; changed = true; }
  if (values.preserveFolders !== saved.preserveFolders) { update.preserveFolders = values.preserveFolders; changed = true; }
  const conflict = archiveConflicts[Math.min(Math.max(values.conflict, 0), 2)];
  if (conflict !== saved.conflictPolicy) { update.conflictPolicy = conflict; changed = true; }
  if (values.deleteArchives !== saved.deleteArchives) { update.deleteArchives = values.deleteArchives; changed = true; }
  return { update: changed ? update : null, error: null };
}

/** The server answers a message type it does not know with this; the page then has nothing to show. */
export function isUnsupportedReply(message: string): boolean {
  return /^Unknown message type/i.test(message);
}

let rowKey = 0;
export function nextRowKey(): number {
  return ++rowKey;
}

export function formFrom(settings: Settings): OptionsForm {
  const split = splitProxyAddress(settings.proxy?.address);
  const folders: Record<string, string> = {};
  const saved = settings.categoryFolders ?? {};
  for (const name of categoryNames) {
    const key = Object.keys(saved).find((k) => k.toLowerCase() === name);
    folders[name] = key ? saved[key] : '';
  }
  const speedBytes = settings.globalSpeedLimitBytesPerSecond && settings.globalSpeedLimitBytesPerSecond > 0 ? settings.globalSpeedLimitBytesPerSecond : null;
  // Whole KB/s, cut off like the desktop, but never shown as 0 while a limit is set.
  const speedShown = speedBytes === null ? null : Math.max(1, Math.floor(speedBytes / 1024));
  return {
    defaultDownloadDirectory: settings.defaultDownloadDirectory,
    useCategoryFolders: settings.useCategoryFolders,
    categoryFolders: folders,
    customCategories: (settings.customCategories ?? []).map((c) => ({
      id: c.id, name: c.name, extensions: (c.extensions ?? []).join(' '), folder: c.destinationDirectory ?? '', removed: false,
    })),
    favorites: (settings.favoriteFolders ?? []).map((f) => ({ key: nextRowKey(), name: f.name, path: f.path })),
    favoritesAtOpen: (settings.favoriteFolders ?? []).map((f) => f.path),
    maxConcurrentDownloads: settings.maxConcurrentDownloads,
    defaultConnections: settings.defaultConnections,
    maxConnectionsPerHost: settings.maxConnectionsPerHost,
    speedKb: speedShown,
    speedBytesAtOpen: speedBytes,
    speedKbAtOpen: speedShown,
    profiles: profilesToText(settings.connectionProfiles),
    autoResume: settings.autoResumeUnfinishedOnStartup,
    allowLocalNetwork: settings.allowLocalNetwork,
    browserIntegration: settings.browserIntegrationEnabled,
    proxy: {
      mode: settings.proxy?.mode ?? 'none', type: split.type, host: split.host, username: settings.proxy?.username ?? '',
      password: '', forget: false, hasPassword: !!settings.proxy?.hasPassword,
    },
    logins: (settings.siteLogins ?? []).map((l) => ({
      id: l.id, site: l.site, username: l.username, password: '', hasPassword: l.hasPassword, isNew: false, removed: false,
      originalSite: l.site, originalUser: l.username,
    })),
  };
}

export interface Problem {
  message: string;
  page: OptionsPage;
}

/** The desktop's upper bound for the speed box (KB/s). */
export const maxSpeedKb = 10_000_000;

const whole = (value: number | null | undefined) => (value === null || value === undefined || !Number.isFinite(value) ? null : Math.round(value));

/** What the desktop checks before it saves, in its order; the first problem and the page it is on. */
export function validateForm(form: OptionsForm, archive?: { saved: ArchiveSettings; values: ArchiveValues } | null): Problem | null {
  if (archive) {
    const { error } = archiveChanges(archive.saved, archive.values);
    if (error) return { message: error, page: 'extraction' };
  }
  for (const row of form.logins.filter((r) => !r.removed)) {
    if (!row.site.trim() || !row.username.trim()) return { message: 'Every site login needs a site and user name. Remove unused rows before saving.', page: 'logins' };
    if (!row.isNew && loginNeedsPassword({ site: row.originalSite, username: row.originalUser, hasPassword: row.hasPassword }, row.site, row.username, row.password))
      return { message: loginPasswordMessage, page: 'logins' };
  }
  const max = whole(form.maxConcurrentDownloads);
  if (max === null || max < 1 || max > 16) return { message: 'Maximum simultaneous downloads must be between 1 and 16.', page: 'connection' };
  const connections = whole(form.defaultConnections);
  if (connections === null || connections < 1 || connections > 32) return { message: 'Connections per download must be between 1 and 32.', page: 'connection' };
  const perSite = whole(form.maxConnectionsPerHost);
  if (perSite === null || perSite < 1 || perSite > 64) return { message: 'Connections per site must be between 1 and 64.', page: 'connection' };
  const limit = whole(form.speedKb);
  const speedUntouched = limit === form.speedKbAtOpen;
  if (!speedUntouched && limit !== null && (limit < 1 || limit > maxSpeedKb)) return { message: `Speed limit must be between 1 and ${maxSpeedKb} KB/s.`, page: 'connection' };
  if (form.proxy.mode === 'manual' && composeProxyAddress(form.proxy.type, form.proxy.host) === null)
    return { message: 'Enter the proxy address as host:port (port 1-65535).', page: 'proxy' };
  const profiles = parseProfiles(form.profiles);
  if (profiles.error) return { message: profiles.error, page: 'connection' };
  return null;
}

/** Writes the draft into a copy of the server's current settings (which keeps every field the page has no box for). */
export function applyForm(settings: Settings, form: OptionsForm): void {
  const manual = form.proxy.mode === 'manual';
  settings.proxy = {
    mode: form.proxy.mode,
    address: manual ? composeProxyAddress(form.proxy.type, form.proxy.host) ?? undefined : undefined,
    // Like the desktop, the user name is kept in every mode (the box is only greyed out).
    username: form.proxy.username.trim() || undefined,
    // Empty keeps the saved password unless "Forget" is ticked.
    password: manual && !form.proxy.forget && form.proxy.password ? form.proxy.password : undefined,
    clearPassword: form.proxy.forget,
    hasPassword: form.proxy.hasPassword && !form.proxy.forget,
  };
  settings.defaultDownloadDirectory = form.defaultDownloadDirectory.trim();
  settings.siteLogins = form.logins.filter((row) => !row.removed).map((row) => ({
    id: row.id, site: row.site.trim(), username: row.username.trim(), password: row.password || undefined, hasPassword: row.hasPassword,
  }));
  settings.maxConcurrentDownloads = whole(form.maxConcurrentDownloads) ?? settings.maxConcurrentDownloads;
  settings.defaultConnections = whole(form.defaultConnections) ?? settings.defaultConnections;
  settings.maxConnectionsPerHost = whole(form.maxConnectionsPerHost) ?? settings.maxConnectionsPerHost;
  const limit = whole(form.speedKb);
  // An untouched box keeps the stored bytes (a stored 1536 B/s is not rewritten as 2048).
  settings.globalSpeedLimitBytesPerSecond = limit === form.speedKbAtOpen && form.speedBytesAtOpen !== null
    ? form.speedBytesAtOpen
    : limit !== null && limit > 0 ? limit * 1024 : undefined;
  settings.connectionProfiles = parseProfiles(form.profiles).profiles;
  settings.useCategoryFolders = form.useCategoryFolders;
  settings.autoResumeUnfinishedOnStartup = form.autoResume;
  settings.allowLocalNetwork = form.allowLocalNetwork;
  settings.browserIntegrationEnabled = form.browserIntegration;

  const currentFavorites: FavoriteFolder[] = settings.favoriteFolders ?? [];
  const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();
  const edited = form.favorites.filter((row) => row.path.trim()).map((row) => ({ name: row.name.trim(), path: row.path.trim() }));
  // A favorite saved meanwhile (not there when the draft was made, not in the rows) is kept.
  const savedMeanwhile = currentFavorites.filter((f) => !form.favoritesAtOpen.some((p) => same(p, f.path)) && !edited.some((e) => same(e.path, f.path)));
  settings.favoriteFolders = [...edited, ...savedMeanwhile];

  const folders: Record<string, string> = {};
  for (const [name, folder] of Object.entries(form.categoryFolders)) if (folder.trim()) folders[name] = folder.trim();
  settings.categoryFolders = folders;

  const original: CategoryDefinition[] = settings.customCategories ?? [];
  const defined: CategoryDefinition[] = [];
  const removed: string[] = [];
  for (const row of form.customCategories) {
    if (row.removed) {
      removed.push(row.id);
      continue;
    }
    defined.push({ id: row.id, name: row.name.trim(), extensions: parseExtensions(row.extensions), destinationDirectory: row.folder.trim() || undefined });
  }
  // Categories the draft did not know (added elsewhere meanwhile) stay.
  for (const c of original) if (!form.customCategories.some((row) => row.id === c.id)) defined.push(c);
  settings.customCategories = defined;
  settings.deleteCategoryIds = removed;
}
