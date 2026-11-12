// T3: magnets and .torrent files that reach the page (Ctrl+V anywhere, drag and drop) and the TorBox cache check of the
// torrent dialog's lines. The rules and words are the Windows app's (MainWindow.TorrentAdd, TorrentAddLines), so the two
// read alike. Nothing here touches the page, so the tests call it directly.

import { lineState, magnetsIn, type TorrentLines } from './torrents';
import type { ClassifyResult } from './types';

// ---- Ctrl+V and drops on the page ----

export const noTorrentsOnClipboard = 'The clipboard holds no magnet link or .torrent file.';
export const onlyTorrentsDrop = 'Only magnet links and .torrent files can be dropped here.';

/** Where a paste is the field's own: a text box, a list box or anything editable. */
export function isEditableTarget(target: { tagName?: string; isContentEditable?: boolean; type?: string } | null | undefined): boolean {
  if (!target) return false;
  if (target.isContentEditable) return true;
  const tag = (target.tagName ?? '').toLowerCase();
  if (tag === 'textarea' || tag === 'select') return true;
  if (tag !== 'input') return false;
  // A checkbox or button that has the focus takes no text: the paste is the page's.
  return !['checkbox', 'radio', 'button', 'submit', 'reset', 'file', 'range', 'color', 'image'].includes((target.type ?? 'text').toLowerCase());
}

/** The page takes a paste or a drop: torrents can be added, no dialog is open, and it isn't typed into a field. */
export function pageTakes(canAdd: boolean, dialogOpen: boolean, editable = false): boolean {
  return canAdd && !dialogOpen && !editable;
}

/** A drag that may carry torrents: files, or text that may hold magnets. */
export function dragMayHoldTorrents(types: readonly string[] | null | undefined): boolean {
  return !!types && types.some((type) => type === 'Files' || type === 'text/uri-list' || type === 'text/plain');
}

export function isTorrentFileName(name: string): boolean {
  return /\.torrent$/i.test(name.trim());
}

/** The .torrent files among dropped or pasted files, in order. */
export function torrentFilesIn<T extends { name: string }>(files: Iterable<T> | null | undefined): T[] {
  return files ? [...files].filter((file) => isTorrentFileName(file.name)) : [];
}

/** What the page found: the magnets in the text and the .torrent files, or the words to say when there are none. */
export function intake<T extends { name: string }>(text: string | null | undefined, files: Iterable<T> | null | undefined, dropped: boolean):
  { magnets: string[]; files: T[]; empty: string | null } {
  const magnets = magnetsIn(text);
  const torrents = torrentFilesIn(files);
  const empty = magnets.length + torrents.length > 0 ? null : dropped ? onlyTorrentsDrop : noTorrentsOnClipboard;
  return { magnets, files: torrents, empty };
}

/** Sends a .torrent file to the server (POST /api/upload/torrent); its reference, never its path there. */
export async function uploadTorrent(file: Blob & { name: string }, send: typeof fetch = fetch): Promise<{ input: string; label: string }> {
  const response = await send('/api/upload/torrent', { method: 'POST', body: file, credentials: 'same-origin', headers: { 'Content-Type': 'application/x-bittorrent' } });
  let body: { ok?: boolean; upload?: string; error?: string };
  try {
    body = (await response.json()) as typeof body;
  } catch {
    throw new Error('The upload failed.');
  }
  if (!body.ok || !body.upload) throw new Error(body.error ?? 'The upload failed.');
  return { input: body.upload, label: file.name };
}

// ---- The TorBox cache check of the torrent dialog's lines (TorrentAddLines) ----

/** Not asked (no account, or not read yet), being asked, the answer, or `unknown` when it couldn't be asked or failed. */
export type CacheState = 'checking' | 'cached' | 'notCached' | 'unknown';
export type CacheStates = Record<string, CacheState>;

/** A v1 info hash (40 hex) as a bare magnet, so no tracker and no .torrent file is sent; null for anything else. */
export function cacheQuery(infoHash: string | null | undefined): string | null {
  return infoHash && /^[0-9a-f]{40}$/i.test(infoHash) ? `magnet:?xt=urn:btih:${infoHash.toLowerCase()}` : null;
}

/**
 * The lines whose check can start now, each with its question, and the states with them marked `checking` (a line is
 * asked about once). Only with an account that takes torrents, and only for a line that was read and is new; a line
 * with no usable hash is `unknown` at once. The states of lines that went are dropped.
 */
export function startCacheChecks(lines: TorrentLines, states: CacheStates): { states: CacheStates; start: { input: string; query: string }[] } {
  const next: CacheStates = {};
  for (const line of lines.lines) if (states[line.input]) next[line.input] = states[line.input];
  const start: { input: string; query: string }[] = [];
  if (!lines.providerName) return { states: next, start };
  for (const line of lines.lines) {
    if (next[line.input] || lineState(line) !== 'ready') continue;
    const query = cacheQuery(line.result?.infoHash);
    if (query) {
      next[line.input] = 'checking';
      start.push({ input: line.input, query });
    } else {
      next[line.input] = 'unknown';
    }
  }
  return { states: next, start };
}

/** The provider's answer for a line. Only the cache words change, never the route. Nothing for a line that went. */
export function cacheChecked(states: CacheStates, input: string, result: Pick<ClassifyResult, 'ok' | 'cached'> | null): CacheStates {
  if (!(input in states)) return states;
  const state: CacheState = result?.ok && result.cached === true ? 'cached' : result?.ok && result.cached === false ? 'notCached' : 'unknown';
  return { ...states, [input]: state };
}

/** The cache words beside a line's detail ("Cached on TorBox"), or '' when nothing was asked. */
export function cacheText(state: CacheState | undefined, providerName: string | null): string {
  if (!providerName || !state) return '';
  switch (state) {
    case 'checking': return `Checking ${providerName}…`;
    case 'cached': return `Cached on ${providerName}`;
    case 'notCached': return `Not cached on ${providerName}`;
    case 'unknown': return `Couldn't check ${providerName}`;
  }
}
