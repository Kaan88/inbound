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

export type OptionsPage = 'general' | 'saveto' | 'downloads' | 'connection' | 'proxy' | 'logins' | 'updates' | 'extraction' | 'torrents' | 'signin';

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
  /** A manual proxy whose address the server did not send (a web session sees only the mode); the host keeps its own. */
  addressHidden?: boolean;
}

export interface OptionsForm {
  defaultDownloadDirectory: string;
  useCategoryFolders: boolean;
  categoryFolders: Record<string, string>;
  /** T14: torrent category folders; null when the server predates them (the group is not shown). */
  useTorrentCategoryFolders: boolean | null;
  /** T14: the torrent folder; empty for the same folders as other downloads. */
  torrentFolder: string;
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
  /** Per-site limits, a row each (review 5:F6b). */
  profiles: ProfileRow[];
  autoResume: boolean;
  allowLocalNetwork: boolean;
  browserIntegration: boolean;
  /** Options > Torrents (shown only when the hello lists `torrents`). Where only the PC may turn them on, the box is locked. */
  torrentsEnabled: boolean;
  /** `ask`, `thisPc` or `provider`; offered only with an account that takes torrents. */
  torrentRoute: string;
  torrentChooseFiles: boolean;
  /** T2 seeding, shown when the server sends it (an older one doesn't, and then nothing is written back). */
  seeding: SeedingForm | null;
  /** T3 peers, privacy and connections, shown when the server sends them (an older one doesn't). */
  peers: TorrentPeerForm | null;
  proxy: ProxyForm;
  logins: LoginRow[];
}

/** Options > Torrents > Seeding: the Host's TorrentSeedAfterDownload, TorrentSeedRatio, TorrentSeedHours and TorrentUploadLimit. */
export interface SeedingForm {
  afterDownload: boolean;
  ratio: number | null;
  hours: number | null;
  /** KB/s; empty is none of its own (the total speed limit, or no limit). */
  uploadKb: number | null;
  /** The limit as stored and as shown when the draft was made, so an untouched box keeps the stored bytes. */
  uploadBytesAtOpen: number;
  uploadKbAtOpen: number | null;
}

/** The Host's bounds (DownloadManagerService.TorrentSeedSettingsError). */
export const maxSeedRatio = 100;
export const maxSeedHours = 8760;

export function seedingFrom(settings: Settings): SeedingForm | null {
  if (typeof settings.torrentSeedRatio !== 'number' || typeof settings.torrentSeedHours !== 'number') return null;
  const bytes = typeof settings.torrentUploadLimit === 'number' && settings.torrentUploadLimit > 0 ? settings.torrentUploadLimit : 0;
  const kb = bytes > 0 ? Math.max(1, Math.floor(bytes / 1024)) : null;
  return {
    afterDownload: settings.torrentSeedAfterDownload !== false,
    ratio: settings.torrentSeedRatio,
    hours: settings.torrentSeedHours,
    uploadKb: kb,
    uploadBytesAtOpen: bytes,
    uploadKbAtOpen: kb,
  };
}

/** The Host's own words for a seeding value it would refuse, with the box to mark; null when it would take them. */
export function seedingProblem(form: SeedingForm): { message: string; field: string } | null {
  const ratio = form.ratio;
  if (ratio === null || ratio === undefined || !Number.isFinite(ratio) || ratio < 0 || ratio > maxSeedRatio)
    return { message: 'Enter a seeding ratio from 0 to 100 (0 is no ratio limit).', field: 'seed-ratio' };
  const hours = form.hours;
  if (hours === null || hours === undefined || !Number.isFinite(hours) || hours < 0 || hours > maxSeedHours)
    return { message: 'Enter seeding hours from 0 to 8760 (0 is no time limit).', field: 'seed-hours' };
  if (ratio === 0 && hours === 0) return { message: "Seeding needs a ratio or a time limit; both can't be 0.", field: 'seed-ratio' };
  const kb = form.uploadKb;
  if (kb !== null && kb !== undefined) {
    // The Host's rule and words for a negative limit; the top bound is the page's own, like the other speed boxes.
    if (kb < 0) return { message: "The torrent upload limit can't be below 0.", field: 'seed-upload' };
    if (!Number.isFinite(kb) || kb > maxSpeedKb)
      return { message: `The torrent upload limit can be at most ${maxSpeedKb} KB/s. 0 or empty is none.`, field: 'seed-upload' };
  }
  return null;
}

/** Bytes per second to save: an untouched box keeps the stored value; empty or 0 is none. */
export function uploadLimitBytes(form: SeedingForm): number {
  const kb = form.uploadKb === null || form.uploadKb === undefined || !Number.isFinite(form.uploadKb) ? null : Math.round(form.uploadKb);
  if (kb === form.uploadKbAtOpen) return form.uploadBytesAtOpen;
  return kb !== null && kb > 0 ? kb * 1024 : 0;
}

/**
 * Options > Torrents > Finding peers and Connections (T3): the Host's TorrentDht, TorrentPex, TorrentLsd,
 * TorrentEncryption, TorrentUseProxy, TorrentSeedsAtOnce, TorrentConnectionsPerTorrent and TorrentMaxConnections. From
 * the Windows app's web UI only the three numbers may change (WebSettingsRules); the rest stay with the PC.
 */
export interface TorrentPeerForm {
  dht: boolean;
  pex: boolean;
  lsd: boolean;
  /** `prefer`, `require` or `off`. */
  encryption: string;
  useProxy: boolean;
  /** Empty or 0: all of them / no limit of its own / the torrent program's own default. */
  seedsAtOnce: number | null;
  perTorrent: number | null;
  total: number | null;
}

/** The encryption choices, as the Host names them (TorrentEncryptionModes). */
export const torrentEncryptions: readonly [string, string][] = [
  ['prefer', 'Prefer encrypted'],
  ['require', 'Require encrypted (leaves out peers that can\'t)'],
  ['off', 'Off (unencrypted only)'],
];

/** The settings the Windows app's web UI may only look at (WebSettingsRules.PipeOnly). */
export const pcOnlyTorrentPeerSettings = ['torrentDht', 'torrentPex', 'torrentLsd', 'torrentEncryption', 'torrentUseProxy'] as const;

export function peersFrom(settings: Settings): TorrentPeerForm | null {
  if (typeof settings.torrentDht !== 'boolean' || typeof settings.torrentSeedsAtOnce !== 'number') return null;
  const count = (value: number | undefined) => (typeof value === 'number' && value > 0 ? value : null);
  return {
    dht: settings.torrentDht,
    pex: settings.torrentPex !== false,
    lsd: settings.torrentLsd === true,
    encryption: torrentEncryptions.some(([key]) => key === settings.torrentEncryption) ? settings.torrentEncryption! : 'prefer',
    useProxy: settings.torrentUseProxy === true,
    seedsAtOnce: count(settings.torrentSeedsAtOnce),
    perTorrent: count(settings.torrentConnectionsPerTorrent),
    total: count(settings.torrentMaxConnections),
  };
}

function countOf(value: number | null | undefined): number | null {
  if (value === null || value === undefined || (typeof value === 'string' && value === '')) return 0;
  return Number.isFinite(value) && Number.isInteger(Number(value)) ? Number(value) : null;
}

/** The Host's own words for a peer or connection value it would refuse (TorrentPeerSettingsError), with the box to mark. */
export function peersProblem(form: TorrentPeerForm): { message: string; field: string } | null {
  const seeds = countOf(form.seedsAtOnce);
  if (seeds === null || seeds < 0 || seeds > 1000) return { message: 'Enter torrents seeding at once from 0 to 1000 (0 is all of them).', field: 'tor-seeds' };
  const per = countOf(form.perTorrent);
  if (per === null || per < 0 || per === 1 || per > 2000) return { message: 'Enter connections per torrent from 2 to 2000, or 0 for no limit of its own.', field: 'tor-per' };
  const total = countOf(form.total);
  if (total === null || total < 0 || (total > 0 && total < 10) || total > 20000)
    return { message: "Enter torrent connections in all from 10 to 20000, or 0 for the torrent program's own default.", field: 'tor-total' };
  return null;
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

// Per-site limits (review 5:F6b): a row each, a site and its connections, as Site logins has; no typed "pattern=max".
export interface ProfileRow { key: number; pattern: string; max: number | null }

export const siteLimitNeedsSite = 'Enter a site for each per-site limit, or remove it.';

export function profileRows(profiles: ConnectionProfile[] | undefined): ProfileRow[] {
  return (profiles ?? []).map((p) => ({ key: nextRowKey(), pattern: p.pattern, max: p.maxConnections }));
}

/**
 * The limits the rows give, or the first row's problem and its field. A row left wholly empty is skipped. The Windows
 * app's OptionsSettingsRebase.SiteLimits by the same rule and words.
 */
export function readProfiles(rows: readonly ProfileRow[]): { profiles: ConnectionProfile[]; error: string | null; field?: string } {
  const profiles: ConnectionProfile[] = [];
  for (const row of rows) {
    const site = row.pattern.trim();
    const max = row.max === null || row.max === undefined || Number.isNaN(row.max) ? null : row.max;
    if (!site && max === null) continue;
    if (!site) return { profiles: [], error: siteLimitNeedsSite, field: `profile-site-${row.key}` };
    if (max === null || !Number.isInteger(max) || max < 1 || max > 32)
      return { profiles: [], error: `The limit for ${site} must be 1 to 32 connections.`, field: `profile-max-${row.key}` };
    profiles.push({ pattern: site, maxConnections: max });
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

/** Where magnets and .torrent files go when an account takes torrents too (TorrentRoutes): ask, this PC or server, the account. */
export const torrentRoutes = ['ask', 'thisPc', 'provider'];

/** T9: Settings offers two choices. The saved `ask` now starts each torrent on this PC too (each one can still be
 * switched), so it is shown as `thisPc`. */
export function routeShown(saved: string | null | undefined): 'thisPc' | 'provider' {
  return saved === 'provider' ? 'provider' : 'thisPc';
}

/** What is saved for the chosen option: a saved `ask` stays when this PC is kept, so opening and saving changes nothing. */
export function routeToSave(shown: string, saved: string | null | undefined): string {
  if (shown === 'provider') return 'provider';
  return saved === 'ask' ? 'ask' : 'thisPc';
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
    useTorrentCategoryFolders: typeof settings.useTorrentCategoryFolders === 'boolean' ? settings.useTorrentCategoryFolders : null,
    torrentFolder: settings.torrentCategoryFoldersRoot ?? '',
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
    profiles: profileRows(settings.connectionProfiles),
    autoResume: settings.autoResumeUnfinishedOnStartup,
    allowLocalNetwork: settings.allowLocalNetwork,
    browserIntegration: settings.browserIntegrationEnabled,
    torrentsEnabled: settings.torrentsEnabled === true,
    torrentRoute: routeShown(settings.torrentRoute),
    torrentChooseFiles: settings.torrentChooseFiles !== false,
    seeding: seedingFrom(settings),
    peers: peersFrom(settings),
    proxy: {
      mode: settings.proxy?.mode ?? 'none', type: split.type, host: split.host, username: settings.proxy?.username ?? '',
      password: '', forget: false, hasPassword: !!settings.proxy?.hasPassword,
      addressHidden: settings.proxy?.mode === 'manual' && !settings.proxy?.address,
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
  /** The id of the box to mark and focus, when there is one. */
  field?: string;
}

/** The desktop's upper bound for the speed box (KB/s). */
export const maxSpeedKb = 10_000_000;

const whole = (value: number | null | undefined) => (value === null || value === undefined || !Number.isFinite(value) ? null : Math.round(value));

/**
 * Every problem in the draft, in the desktop's order (U-WU-12: the page showed only the first and marked nothing). Each
 * names its page and its box.
 */
export function validateAll(
  form: OptionsForm,
  archive?: { saved: ArchiveSettings; values: ArchiveValues } | null,
  context: { pcOnly?: boolean } = {},
): Problem[] {
  const problems: Problem[] = [];
  if (archive) {
    const { error } = archiveChanges(archive.saved, archive.values);
    if (error) problems.push({ message: error, page: 'extraction', field: 'archive-folder' });
  }
  for (const row of form.logins.filter((r) => !r.removed)) {
    if (!row.site.trim() || !row.username.trim()) {
      problems.push({ message: 'Every site login needs a site and user name. Remove unused rows before saving.', page: 'logins', field: row.site.trim() ? `login-user-${row.id}` : `login-site-${row.id}` });
      break;
    }
    if (!row.isNew && loginNeedsPassword({ site: row.originalSite, username: row.originalUser, hasPassword: row.hasPassword }, row.site, row.username, row.password)) {
      problems.push({ message: loginPasswordMessage, page: 'logins', field: `login-pass-${row.id}` });
      break;
    }
  }
  const max = whole(form.maxConcurrentDownloads);
  if (max === null || max < 1 || max > 16) problems.push({ message: 'Maximum simultaneous downloads must be between 1 and 16.', page: 'connection', field: 'set-concurrent' });
  const connections = whole(form.defaultConnections);
  if (connections === null || connections < 1 || connections > 32) problems.push({ message: 'Connections per download must be between 1 and 32.', page: 'connection', field: 'set-connections' });
  const perSite = whole(form.maxConnectionsPerHost);
  if (perSite === null || perSite < 1 || perSite > 64) problems.push({ message: 'Connections per site must be between 1 and 64.', page: 'connection', field: 'set-host' });
  const limit = whole(form.speedKb);
  const speedUntouched = limit === form.speedKbAtOpen;
  if (!speedUntouched && limit !== null && (limit < 1 || limit > maxSpeedKb)) problems.push({ message: `Speed limit must be between 1 and ${maxSpeedKb} KB/s.`, page: 'connection', field: 'set-speed' });
  // Not where only the PC may change the proxy, nor while the hidden address is left empty: the host keeps its own.
  const proxyKept = context.pcOnly || (form.proxy.addressHidden && form.proxy.host.trim() === '');
  if (!proxyKept && form.proxy.mode === 'manual' && composeProxyAddress(form.proxy.type, form.proxy.host) === null)
    problems.push({ message: 'Enter the proxy address as host:port (port 1-65535).', page: 'proxy', field: 'proxy-address' });
  const profiles = readProfiles(form.profiles);
  if (profiles.error) problems.push({ message: profiles.error, page: 'connection', field: profiles.field ?? 'profile-add' });
  const seeding = form.seeding ? seedingProblem(form.seeding) : null;
  if (seeding) problems.push({ ...seeding, page: 'torrents' });
  const peers = form.peers ? peersProblem(form.peers) : null;
  if (peers) problems.push({ ...peers, page: 'torrents' });
  return problems;
}

/** What the desktop checks before it saves, in its order; the first problem and the page it is on. */
export function validateForm(form: OptionsForm, archive?: { saved: ArchiveSettings; values: ArchiveValues } | null, context: { pcOnly?: boolean } = {}): Problem | null {
  return validateAll(form, archive, context)[0] ?? null;
}

/** The footer's words for the problems: one says itself; several are counted and each names its page. */
export function problemsText(problems: readonly Problem[], pageLabel: (page: OptionsPage) => string): string {
  if (problems.length === 0) return '';
  if (problems.length === 1) return problems[0].message;
  return `${problems.length} values need fixing. ${problems.map((problem) => `${pageLabel(problem.page)}: ${problem.message}`).join(' ')}`;
}

/** T14: the torrent folder box when empty (the desktop's TorrentFolderText says the same). */
export const torrentSameFolders = 'Same folders as other downloads';

export const torrentFoldersHint =
  'Torrents go to the torrent folder, in a folder for their category (Torrents\\Video). Turned off, every torrent goes straight to the torrent folder. Leave the torrent folder empty to use the same folders as other downloads.';

/**
 * T14: what an empty Save to in the torrent dialog means, as the server's TorrentFolderRules decides (the desktop's
 * TorrentFolderText says the same): each torrent's category folder while torrent category folders are on (with no
 * torrent folder, only while other downloads' are on too), else the torrent folder, or with none the folder other
 * downloads go to. A server that predates them doesn't say: as before.
 */
export function torrentSaveToPlaceholder(
  settings: Partial<Pick<Settings, 'useTorrentCategoryFolders' | 'torrentCategoryFoldersRoot' | 'useCategoryFolders'>> | null | undefined,
): string {
  const each = "Each torrent's category folder";
  if (typeof settings?.useTorrentCategoryFolders !== 'boolean') return each;
  const torrentOn = settings.useTorrentCategoryFolders;
  if (settings.torrentCategoryFoldersRoot?.trim()) return torrentOn ? each : 'The torrent folder';
  return torrentOn && settings.useCategoryFolders !== false ? each : 'Same folder as other downloads';
}

/**
 * The browser-capture box's words (review 5:H4). On a desktop Host (the Windows app, or the Linux one, which has native
 * messaging too) it is the desktop's box; on a server only browsers paired with it (Sign-in and browsers) can send it
 * downloads, so it says that.
 */
export function captureLabel(hostKind: string | null | undefined): string {
  return hostKind === 'windows' || hostKind === 'linux' ? 'Capture downloads from your browser' : 'Accept downloads from paired browsers';
}

/** The settings' names as Options writes them, for a server refusal that names fields (U-WR-14). */
const settingLabels: Record<string, string> = {
  defaultDownloadDirectory: 'Default download folder',
  categoryFolders: 'Folders by category',
  torrentCategoryFoldersRoot: 'Torrent folder',
  favoriteFolders: 'Favorite folders',
  proxy: 'Proxy',
  siteLogins: 'Site logins',
  browserIntegrationEnabled: 'Capture downloads from your browser',
  allowLocalNetwork: 'Allow downloads from addresses on the local network',
  clipboardMonitoring: 'Add links I copy to the download list',
  launchAtStartup: 'Start with Windows',
  notifyOnCompletion: 'Notify when a download finishes',
  playCompletionSound: 'Completion sound',
  showCompletionDialog: 'Completion window',
  preventSleepWhileDownloading: 'Prevent sleep while downloading',
  excludedFileExtensions: 'File types not captured',
  excludedHosts: 'Sites not captured',
  interceptedFileExtensions: 'File types captured',
  closeWindowCompletely: 'Closing the window',
  theme: 'Theme',
  torrentsEnabled: 'Download torrents',
  torrentHandler: 'Open magnet links and .torrent files with Inbound',
  torrentListenPort: 'Listening port',
  torrentUpnp: 'Ask my router to forward the port',
  torrentSeedAfterDownload: 'Keep seeding after the download finishes',
  torrentSeedRatio: 'Stop seeding at ratio',
  torrentSeedHours: 'Stop seeding after',
  torrentUploadLimit: 'Torrent upload limit',
  torrentDht: 'DHT',
  torrentPex: 'Peer exchange (PEX)',
  torrentLsd: 'Local peers (LSD)',
  torrentEncryption: 'Encryption',
  torrentUseProxy: 'Use the proxy for torrents',
  torrentSeedsAtOnce: 'Torrents seeding at once',
  torrentConnectionsPerTorrent: 'Connections per torrent',
  torrentMaxConnections: 'Torrent connections in all',
};

/** The server's "These settings can only be changed in the app on this PC: a, b." with the names Options uses. */
export function friendlyRefusal(message: string): string {
  return message.replace(/(can only be changed in the app on this PC: )([^.]+)\./, (_whole, lead: string, list: string) => {
    const names = list.split(',').map((name) => settingLabels[name.trim()] ?? name.trim());
    return `${lead}${names.join(', ')}.`;
  });
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
  settings.connectionProfiles = readProfiles(form.profiles).profiles;
  settings.useCategoryFolders = form.useCategoryFolders;
  // T14: only to a server that knows them; an older one is sent neither.
  if (form.useTorrentCategoryFolders !== null) {
    settings.useTorrentCategoryFolders = form.useTorrentCategoryFolders;
    settings.torrentCategoryFoldersRoot = form.torrentFolder.trim();
  }
  settings.autoResumeUnfinishedOnStartup = form.autoResume;
  settings.allowLocalNetwork = form.allowLocalNetwork;
  settings.browserIntegrationEnabled = form.browserIntegration;
  settings.torrentsEnabled = form.torrentsEnabled;
  settings.torrentRoute = routeToSave(form.torrentRoute, settings.torrentRoute);
  settings.torrentChooseFiles = form.torrentChooseFiles;
  if (form.seeding) {
    settings.torrentSeedAfterDownload = form.seeding.afterDownload;
    settings.torrentSeedRatio = Number(form.seeding.ratio);
    settings.torrentSeedHours = Number(form.seeding.hours);
    settings.torrentUploadLimit = uploadLimitBytes(form.seeding);
  }
  if (form.peers) {
    settings.torrentDht = form.peers.dht;
    settings.torrentPex = form.peers.pex;
    settings.torrentLsd = form.peers.lsd;
    settings.torrentEncryption = form.peers.encryption;
    settings.torrentUseProxy = form.peers.useProxy;
    settings.torrentSeedsAtOnce = countOf(form.peers.seedsAtOnce) ?? 0;
    settings.torrentConnectionsPerTorrent = countOf(form.peers.perTorrent) ?? 0;
    settings.torrentMaxConnections = countOf(form.peers.total) ?? 0;
  }

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

/**
 * What Options sends on OK or Apply (audit 4 H1): the server's current settings with only the fields this form changed
 * since it opened. A field is changed when the form as opened and the form now write it differently; such a field is
 * written onto the current settings (so favorites or categories added elsewhere meanwhile are still kept, see
 * applyForm). Every other field stays as the server holds it now, so a setting, queue or schedule changed elsewhere
 * while Options was open is not put back, and the Windows app's settings that only the PC may change are sent as they are.
 */
export function mergeEditedSettings(current: Settings, opened: Settings, formAtOpen: OptionsForm, draft: OptionsForm): Settings {
  const copy = (settings: Settings) => JSON.parse(JSON.stringify(settings)) as Settings;
  const before = copy(opened);
  applyForm(before, formAtOpen);
  const after = copy(opened);
  applyForm(after, draft);
  const written = copy(current);
  applyForm(written, draft);
  const result = copy(current) as unknown as Record<string, unknown>;
  const was = before as unknown as Record<string, unknown>;
  const now = after as unknown as Record<string, unknown>;
  const onCurrent = written as unknown as Record<string, unknown>;
  for (const key of new Set([...Object.keys(was), ...Object.keys(now)])) {
    if (JSON.stringify(was[key]) === JSON.stringify(now[key])) continue;
    if (onCurrent[key] === undefined) delete result[key];
    else result[key] = onCurrent[key];
  }
  return result as unknown as Settings;
}

/**
 * Where a change of the page address (Back, Forward, a typed address) would go, when it has to wait for the Options
 * question "Leave without saving?"; null when it may go on. The shell calls this before it swaps the screen: a
 * listener of the Options screen cannot run first, because the browser calls a window's listeners in the order they
 * were added and the shell's is the older one.
 */
export function heldLeave(newURL: string, dirty: boolean, asking: boolean): string | null {
  if (!dirty || asking) return null;
  let to = '';
  try {
    to = new URL(newURL).hash;
  } catch {
    return null;
  }
  return to === '#/settings' ? null : to || '#/all';
}
