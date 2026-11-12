// The single-address Add URL flow and the pieces the add dialogs share, in the desktop's rules (MainWindow.xaml.cs
// AddButton_Click, FolderChooser.cs, JobListFile.cs). Pure functions: the tests run them without a browser.

import { spaceSize } from './addview';
import { bytes } from './format';
import type { FolderSpace, Job, Settings } from './types';

export interface CategoryChoice {
  /** Null is "Automatic (file extension)". */
  id: string | null;
  name: string;
}

export interface CustomCategory {
  id: string;
  name: string;
  destinationDirectory?: string;
  extensions: string[];
}

const builtIn = ['compressed', 'documents', 'music', 'programs', 'video'];

const byExtension: Record<string, string[]> = {
  compressed: ['zip', '7z', 'rar', 'gz', 'gzip', 'bz2', 'tar', 'xz', 'zst', 'iso', 'cab', 'ace', 'arj', 'lzh'],
  documents: ['pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'txt', 'rtf', 'epub', 'odt', 'ods', 'odp', 'csv'],
  music: ['mp3', 'flac', 'wav', 'm4a', 'aac', 'ogg', 'wma', 'opus', 'aif', 'ra'],
  programs: ['exe', 'msi', 'apk', 'msix', 'appx', 'bat', 'cmd', 'deb', 'rpm', 'dmg', 'pkg'],
  video: ['mp4', 'mkv', 'avi', 'mov', 'wmv', 'webm', 'm4v', 'mpg', 'mpeg', 'flv', 'ts', '3gp', 'rm', 'rmvb'],
};

export function customCategories(settings: Settings | null | undefined): CustomCategory[] {
  const list = settings?.customCategories;
  return Array.isArray(list) ? (list as CustomCategory[]).map((c) => ({ ...c, extensions: c.extensions ?? [] })) : [];
}

function capital(word: string): string {
  return word[0].toUpperCase() + word.slice(1);
}

/** "Automatic (file extension)", the built-in categories, Other, then the custom ones. */
export function categoryChoices(settings: Settings | null | undefined): CategoryChoice[] {
  return [
    { id: null, name: 'Automatic (file extension)' },
    ...[...builtIn, 'other'].map((id) => ({ id, name: capital(id) })),
    ...customCategories(settings).map((c) => ({ id: c.id, name: c.name })),
  ];
}

export function extensionOf(fileName: string): string {
  const name = fileName.split(/[\\/]/).pop() ?? '';
  const dot = name.lastIndexOf('.');
  return dot < 0 ? '' : name.slice(dot + 1).toLowerCase();
}

/** The category a file name falls into: a custom category's extension first, then the built-in ones, else Other. */
export function categoryOf(fileName: string, custom: CustomCategory[] = []): string {
  const extension = extensionOf(fileName);
  if (!extension) return 'other';
  const own = custom.find((c) => c.extensions.some((e) => e.replace(/^\./, '').toLowerCase() === extension));
  if (own) return own.id;
  return Object.keys(byExtension).find((id) => byExtension[id].includes(extension)) ?? 'other';
}

/** The file name an address ends in, unescaped. */
export function nameFromUrl(url: string): string {
  try {
    return decodeURIComponent(new URL(url).pathname.split('/').filter(Boolean).pop() ?? '');
  } catch {
    return '';
  }
}

/** The folder for a file: its category's folder when the setting is on and the category has one, else the default folder. */
export function destinationFor(settings: Settings | null | undefined, fileName: string, categoryId: string | null): string {
  const fallback = settings?.defaultDownloadDirectory ?? '';
  if (!settings) return fallback;
  const custom = customCategories(settings);
  const category = categoryId ?? categoryOf(fileName, custom);
  if (!settings.useCategoryFolders) return fallback;
  let folder: string | undefined = custom.find((c) => c.id === category)?.destinationDirectory;
  if (!folder?.trim()) {
    const folders = settings.categoryFolders as Record<string, string> | undefined;
    folder = folders ? Object.entries(folders).find(([key]) => key.toLowerCase() === category.toLowerCase())?.[1] : undefined;
  }

  return folder?.trim() ? folder : fallback;
}

// ---- Authentication for one download ----

/** A user name that can go in HTTP Basic authentication: not empty, no colon, no line break. */
export function validUserName(user: string): boolean {
  return user.trim().length > 0 && !/[:\r\n]/.test(user);
}

export function basicAuthorization(user: string, password: string): string {
  const bytes = new TextEncoder().encode(`${user}:${password}`);
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return `Basic ${btoa(binary)}`;
}

// ---- Free space under a folder box ----

/** What the line under the folder box says, and whether it's a warning. */
export function spaceLine(measured: FolderSpace | null | undefined, needed?: number | null): { text: string; problem: boolean } {
  if (!measured || measured.freeBytes == null) return { text: 'Free space unknown', problem: false };
  const free = measured.freeBytes;
  if (measured.insideContainer) return { text: "This folder isn't on a mounted volume, so files saved here are lost when the container is rebuilt.", problem: true };
  if (needed != null && needed > 0 && needed > free) return { text: `Not enough space: needs ${spaceSize(needed)}, ${spaceSize(free)} free`, problem: true };
  return { text: measured.totalBytes != null ? `${spaceSize(free)} free of ${spaceSize(measured.totalBytes)}` : `${spaceSize(free)} free`, problem: false };
}

// ---- File information ----

export interface FileInfo {
  ok: boolean;
  fileName?: string;
  totalBytes?: number;
  contentType?: string;
  error?: string;
}

/** The name the server gave, unless it is only the end of an address that holds a key. */
export function serverNameToShow(name: string | undefined, url: string, providerDirect: boolean): string | null {
  if (!name?.trim()) return null;
  if (!providerDirect) return name;
  const letters = (text: string) => text.replace(/[^\p{L}\p{N}]/gu, '').toLowerCase();
  let address = '';
  try {
    const parsed = new URL(url);
    address = decodeURIComponent(parsed.pathname + parsed.search);
  } catch {
    return null;
  }

  return letters(address).includes(letters(name)) ? null : name;
}

/**
 * What the file check found, as the desktop's AddRules.FileInfoText writes it (review 5:A2a, A5): the size in the list's
 * format, and the server's name for the file only when the File name box says something else.
 */
export function metadataText(info: FileInfo, serverName: string | null, nameInBox: string | null): string {
  const size = info.totalBytes != null ? bytes(info.totalBytes) : 'Size unknown';
  const shown = nameInBox?.trim() ?? '';
  return !serverName || !shown || shown.toLowerCase() === serverName.trim().toLowerCase() ? size : `${size}\nThe server names it ${serverName}`;
}

/** The exact count and the type, for the file-info line's tooltip. */
export function metadataTitle(info: FileInfo): string {
  const type = info.contentType ?? 'Type unknown';
  return info.totalBytes != null ? `${info.totalBytes.toLocaleString('en-US')} bytes · ${type}` : type;
}

/** The check button's words; once a check has run it can only check again. */
export function checkButtonText(checkedBefore: boolean): string {
  return checkedBefore ? 'Check again' : 'Check file info';
}

/**
 * What a failed file check says, in words (U-WU-16): .NET's "Response status code does not indicate success: 404 (Not
 * Found)." becomes a sentence about the file. Anything else is passed on as it came.
 */
export function fileInfoProblem(message: string): string {
  const text = message.trim();
  const match = /status code does not indicate success:\s*(\d{3})/i.exec(text);
  if (!match) return text;
  const code = Number(match[1]);
  const said: Record<number, string> = {
    400: "The server didn't accept the request (400).",
    401: 'The server wants a user name and password for this file (401). Add them under Authentication.',
    403: "The server won't let this app have the file (403).",
    404: "The server says this file doesn't exist (404).",
    410: 'The server says this file is gone (410).',
    429: 'The server says too many requests were made (429). Try again later.',
  };
  if (said[code]) return said[code];
  if (code >= 500) return `The server had a problem answering (${code}).`;
  return `The server answered ${code}.`;
}

// ---- Several links: the package and duplicates ----

const archivePart = /^(.+?)(\.part\d+\.rar|\.r\d{2,3}|\.z\d{2}|\.rar|\.zip|\.\d{3})$/i;

/** A package of split-archive parts is named after the archive; otherwise undefined and the server names it. */
export function packageNameFor(urls: string[]): string | undefined {
  const bases = new Map<string, string>();
  for (const url of urls) {
    const match = archivePart.exec(nameFromUrl(url));
    if (!match) return undefined;
    if (!bases.has(match[1].toLowerCase())) bases.set(match[1].toLowerCase(), match[1]);
  }

  return bases.size === 1 ? [...bases.values()][0] : undefined;
}

/** The job already on the list for this address, ignoring cancelled ones. */
export function findExisting(jobs: Iterable<Job>, url: string): Job | undefined {
  const wanted = url.trim().toLowerCase();
  for (const job of jobs) {
    if (job.url && job.url.toLowerCase() === wanted && job.state !== 'cancelled') return job;
  }

  return undefined;
}

// ---- The conflict choice ----

export type ConflictPolicy = 'rename' | 'overwrite' | 'fail';

export const conflictOptions: { value: ConflictPolicy; label: string }[] = [
  { value: 'rename', label: 'Rename the new file' },
  { value: 'overwrite', label: 'Overwrite the existing file' },
  { value: 'fail', label: 'Fail the new download' },
];

// ---- A saved download list (JobListFile.cs) ----

export interface ListEntry {
  url: string;
  fileName?: string;
  categoryId?: string;
  destinationDirectory?: string;
  description?: string;
  queueId: number;
  requestedConnections: number;
  startMode: string;
  conflictPolicy: string;
  expectedSha256?: string;
  /** The content type the browser hand-off gave; sent again with job.add so a stream stays a stream. */
  contentType?: string;
  /** A torrent's own part (TorrentListEntry); its url is then the magnet. */
  torrent?: TorrentListEntry;
}

export interface TorrentListEntry {
  infoHash: string;
  name?: string;
  magnet?: string;
  /** The .torrent file (base64) when the torrent came from one and the Host still has it. */
  data?: string;
  /** The files chosen; absent when every file is wanted. */
  fileIndexes?: number[];
}

/** Half a message frame (Protocol.MaxFrameBytes / 2, TorrentListPaging.cs): the most one torrent.import request carries. */
export const TORRENT_PAGE_CHARS = 4 * 1024 * 1024;

const textLength = (value?: string) => value?.length ?? 0;

/** An upper bound of the characters a torrent part takes in JSON, as TorrentListPaging.SizeOf counts it. */
function torrentPartSize(t?: TorrentListEntry): number {
  return t ? textLength(t.data) + 6 * (textLength(t.magnet) + textLength(t.name) + textLength(t.infoHash)) + 12 * (t.fileIndexes?.length ?? 0) + 256 : 0;
}

/** An upper bound of the characters an entry takes in JSON (text may be escaped to six characters each). */
export function listEntrySize(entry: ListEntry): number {
  return torrentPartSize(entry.torrent) + 6 * (textLength(entry.url) + textLength(entry.fileName) + textLength(entry.destinationDirectory) +
    textLength(entry.description) + textLength(entry.categoryId) + textLength(entry.expectedSha256) + textLength(entry.contentType)) + 512;
}

/**
 * Torrent entries in groups that each fit one torrent.import request, in order. A .torrent too large for a request on
 * its own is left out, and its magnet stands for it.
 */
export function chunkTorrentEntries(entries: ListEntry[], limit = TORRENT_PAGE_CHARS): ListEntry[][] {
  const chunks: ListEntry[][] = [];
  let current: ListEntry[] = [];
  let size = 0;
  for (const original of entries) {
    let entry = original;
    if (entry.torrent?.data && torrentPartSize(entry.torrent) > limit) {
      const { data: _dropped, ...rest } = entry.torrent;
      entry = { ...entry, torrent: rest };
    }
    const entrySize = listEntrySize(entry);
    if (current.length && size + entrySize > limit) {
      chunks.push(current);
      current = [];
      size = 0;
    }
    current.push(entry);
    size += entrySize;
  }
  if (current.length) chunks.push(current);
  return chunks;
}

/** An entry that is a torrent: it goes to torrent.import, not job.add. */
export function isTorrentEntry(entry: Pick<ListEntry, 'url' | 'torrent'>): boolean {
  return !!entry.torrent || entry.url.trim().toLowerCase().startsWith('magnet:');
}

/**
 * The list as the desktop writes it: no provider files and no masked addresses, because neither can download anything.
 * `torrents` is torrent.listEntries' answer by job id; a torrent missing from it is saved with its magnet only.
 */
export function serializeList(jobs: Iterable<Job>, torrents?: Record<string, TorrentListEntry>): string {
  const entries: ListEntry[] = [];
  for (const job of jobs) {
    if (!job.url?.trim() || job.isRemote || job.addressHidden) continue;
    const torrent = job.torrent ? (torrents?.[job.id] ?? { infoHash: job.torrent.infoHash, name: job.torrent.name, magnet: job.url }) : undefined;
    entries.push({
      url: job.url,
      fileName: job.fileName,
      categoryId: job.categoryId,
      destinationDirectory: job.destinationDirectory,
      description: job.description,
      queueId: job.queueId,
      requestedConnections: job.requestedConnections ?? 0,
      startMode: job.startMode ?? 'later',
      conflictPolicy: job.conflictPolicy ?? 'rename',
      expectedSha256: job.expectedSha256,
      ...(!torrent && job.handoffContentType ? { contentType: job.handoffContentType } : {}),
      ...(torrent ? { torrent } : {}),
    });
  }

  return JSON.stringify(entries, null, 2);
}

/** Reads a saved list. Throws an Error with the desktop's words when the file is not one. */
export function parseList(text: string): ListEntry[] {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch (error) {
    throw new Error(`The download list is not valid JSON: ${error instanceof Error ? error.message : String(error)}`);
  }

  if (!Array.isArray(parsed)) throw new Error('The download list is empty.');
  return (parsed as Partial<ListEntry>[])
    .filter((entry): entry is ListEntry => !!entry && typeof entry.url === 'string' && entry.url.trim().length > 0)
    .map((entry) => ({ ...entry, queueId: entry.queueId ?? 0, requestedConnections: entry.requestedConnections ?? 0, startMode: entry.startMode ?? 'later', conflictPolicy: entry.conflictPolicy ?? 'rename' }));
}

/** The desktop's words after an import. */
export function importSummary(accepted: number, total: number, failed: number, firstError: string | undefined, toReview: number, skipped = 0, skipReason?: string): string {
  const added = `Added ${accepted} of ${total} downloads${toReview > 0 ? ' directly' : ''}`;
  let outcome = added + (failed > 0 ? `; ${failed} failed. ${firstError ?? ''}`.trimEnd() : '.');
  if (skipped > 0) outcome += ` ${skipped === 1 ? '1 torrent' : `${skipped} torrents`} skipped${skipReason?.trim() ? `: ${skipReason.trim()}` : '.'}`;
  if (toReview === 0) return outcome;
  const entries = toReview === 1 ? '1 entry isn\'t' : `${toReview} entries aren't`;
  const them = toReview === 1 ? 'it' : 'them';
  return `${outcome} ${entries} added yet: the next dialog shows what can be done with ${them}. Saved names, folders and checksums from the file don't carry over to ${them}.`;
}

/** The recent addresses that match what is typed, newest first, at most 20. */
export function recentAddresses(settings: Settings | null | undefined): string[] {
  const list = settings?.recentUrls;
  return Array.isArray(list) ? (list as string[]).filter((item) => typeof item === 'string' && item.trim()).slice(0, 20) : [];
}
