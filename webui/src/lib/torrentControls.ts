// T3 in the web UI: file priorities, a torrent's own Options tab, Force recheck and Announce now, the Overview's facts,
// free space in the add dialog, and the torrent watch (docs/plans/torrent-mockups b, c, e; docs/ipc-protocol.md). The
// words follow the Windows app's. Nothing here touches the page, so the tests call it directly.

import { bytes } from './format';
import { leafFiles, type FileNode, type FileTree } from './remote';
import { hasTorrentControls, mayRun, nextAnnounceText, seedGoalText } from './torrentDetails';
import type { Job, Settings, Torrent, TorrentFile } from './types';

// ---- File priorities ----

export type FilePriority = 'skip' | 'low' | 'normal' | 'high';

/** The menu's choices, highest first (the mockup's order). */
export const priorityChoices: readonly [FilePriority, string][] = [['high', 'High'], ['normal', 'Normal'], ['low', 'Low'], ['skip', 'Skip']];

export function isPriority(value: unknown): value is FilePriority {
  return value === 'skip' || value === 'low' || value === 'normal' || value === 'high';
}

/** A file's priority; an older server sends none, and then a wanted file is Normal and the rest Skip. */
export function priorityOf(file: Pick<TorrentFile, 'wanted' | 'priority'>): FilePriority {
  if (isPriority(file.priority)) return file.wanted || file.priority === 'skip' ? file.priority : 'skip';
  return file.wanted ? 'normal' : 'skip';
}

export function priorityLabel(priority: FilePriority | 'mixed'): string {
  return priority === 'mixed' ? 'Mixed' : priorityChoices.find(([key]) => key === priority)![1];
}

/** The server knows priorities (T3) when its files carry them; an older one gets only the tick boxes. */
export function prioritiesKnown(files: readonly Pick<TorrentFile, 'priority'>[]): boolean {
  return files.some((file) => isPriority(file.priority));
}

/** The priorities by file id (the file tree's ids are the indexes as text). */
export type PriorityMap = ReadonlyMap<string, FilePriority>;

export function priorityMap(files: readonly Pick<TorrentFile, 'index' | 'wanted' | 'priority'>[]): Map<string, FilePriority> {
  return new Map(files.map((file) => [String(file.index), priorityOf(file)]));
}

/** Every file of a new torrent at Normal. */
export function normalMap(files: readonly Pick<TorrentFile, 'index'>[]): Map<string, FilePriority> {
  return new Map(files.map((file) => [String(file.index), 'normal' as FilePriority]));
}

function leafIds(node: FileNode): string[] {
  return leafFiles(node).map((leaf) => leaf.fileId).filter((id): id is string => !!id);
}

/** A file's or folder's priority: the one its files share, or "mixed". */
export function nodePriority(node: FileNode, map: PriorityMap): FilePriority | 'mixed' {
  const seen = new Set(leafIds(node).map((id) => map.get(id) ?? 'normal'));
  if (seen.size === 1) return [...seen][0];
  return seen.size === 0 ? 'normal' : 'mixed';
}

/** Ticked: none of its files is skipped; null when some are. */
export function nodeWanted(node: FileNode, map: PriorityMap): boolean | null {
  const ids = leafIds(node);
  const wanted = ids.filter((id) => (map.get(id) ?? 'normal') !== 'skip').length;
  if (wanted === ids.length) return ids.length > 0;
  return wanted === 0 ? false : null;
}

/** Every file under the node at one priority. */
export function setNodePriority(node: FileNode, map: PriorityMap, priority: FilePriority): Map<string, FilePriority> {
  const next = new Map(map);
  for (const id of leafIds(node)) next.set(id, priority);
  return next;
}

/** The tick box: on makes a skipped file Normal and keeps the rest; off skips every file under it. */
export function setNodeWanted(node: FileNode, map: PriorityMap, want: boolean): Map<string, FilePriority> {
  if (!want) return setNodePriority(node, map, 'skip');
  const next = new Map(map);
  for (const id of leafIds(node)) if ((next.get(id) ?? 'normal') === 'skip') next.set(id, 'normal');
  return next;
}

export function setAllWanted(tree: FileTree, map: PriorityMap, want: boolean): Map<string, FilePriority> {
  let next = new Map(map);
  for (const root of tree.roots) next = setNodeWanted(root, next, want);
  return next;
}

/** The files that aren't skipped, as indexes in order. */
export function wantedIndexes(map: PriorityMap): number[] {
  return [...map].filter(([, priority]) => priority !== 'skip').map(([id]) => Number(id)).filter(Number.isInteger).sort((a, b) => a - b);
}

/** The files whose priority changed, for torrent.setFiles (`priorities` only: every other file keeps its own). */
export function priorityChanges(before: PriorityMap, after: PriorityMap): { index: number; priority: FilePriority }[] {
  const changes: { index: number; priority: FilePriority }[] = [];
  for (const [id, priority] of after) {
    if ((before.get(id) ?? 'normal') === priority) continue;
    const index = Number(id);
    if (Number.isInteger(index)) changes.push({ index, priority });
  }
  return changes.sort((a, b) => a.index - b.index);
}

/** The High and Low files, for torrent.add next to its chosen files (Normal is the default; Skip is the choice itself). */
export function addPriorities(map: PriorityMap | null): { index: number; priority: FilePriority }[] | null {
  if (!map) return null;
  const list = [...map]
    .filter(([, priority]) => priority === 'high' || priority === 'low')
    .map(([id, priority]) => ({ index: Number(id), priority }))
    .filter((entry) => Number.isInteger(entry.index))
    .sort((a, b) => a.index - b.index);
  return list.length > 0 ? list : null;
}

/** "12 of 30 files · 14.9 GB": what the priorities leave wanted. */
export function prioritySummary(files: readonly Pick<TorrentFile, 'index' | 'size'>[], map: PriorityMap): string {
  const wanted = files.filter((file) => (map.get(String(file.index)) ?? 'normal') !== 'skip');
  const size = bytes(wanted.reduce((sum, file) => sum + file.size, 0)) || '0 B';
  const count = files.length === 1 ? '1 file' : `${files.length} files`;
  return wanted.length === files.length ? `${count} · ${size}` : `${wanted.length} of ${count} · ${size}`;
}

export const keepOneFile = 'Keep at least one file.';

// ---- A torrent's own options (the Options tab) ----

export type SeedGoal = 'global' | 'ratio' | 'hours' | 'never';

/** torrent.options' answer (TorrentOptionsDto). */
export interface TorrentOptions {
  jobId?: string;
  downloadLimit?: number | null;
  uploadLimit?: number | null;
  seedGoal?: string | null;
  seedGoalValue?: number | null;
  sequential?: boolean | null;
}

export interface OptionsDraft {
  limitDown: boolean;
  downKb: number | null;
  limitUp: boolean;
  upKb: number | null;
  goal: SeedGoal;
  ratio: number | null;
  hours: number | null;
  sequential: boolean;
  /** The limits as stored and as shown, so an untouched box keeps the stored bytes. */
  downAtOpen: { bytes: number; kb: number | null };
  upAtOpen: { bytes: number; kb: number | null };
}

/** The highest limit the torrent engine takes, in KB/s (2,147,483,647 bytes/s). */
export const mostLimitKb = Math.floor(2_147_483_647 / 1024);

function kbOf(bytesPerSecond: number): number | null {
  return bytesPerSecond > 0 ? Math.max(1, Math.floor(bytesPerSecond / 1024)) : null;
}

function goalOf(value: string | null | undefined): SeedGoal {
  return value === 'ratio' || value === 'hours' || value === 'never' ? value : 'global';
}

export function draftFrom(options: TorrentOptions | null | undefined): OptionsDraft {
  const down = options?.downloadLimit && options.downloadLimit > 0 ? options.downloadLimit : 0;
  const up = options?.uploadLimit && options.uploadLimit > 0 ? options.uploadLimit : 0;
  const goal = goalOf(options?.seedGoal);
  const value = typeof options?.seedGoalValue === 'number' ? options.seedGoalValue : null;
  return {
    limitDown: down > 0,
    downKb: kbOf(down),
    limitUp: up > 0,
    upKb: kbOf(up),
    goal,
    ratio: goal === 'ratio' ? value : 1,
    hours: goal === 'hours' ? value : 48,
    sequential: options?.sequential === true,
    downAtOpen: { bytes: down, kb: kbOf(down) },
    upAtOpen: { bytes: up, kb: kbOf(up) },
  };
}

function limitBytes(on: boolean, kb: number | null, atOpen: { bytes: number; kb: number | null }, which: string): { bytes: number } | { error: string } {
  if (!on) return { bytes: 0 };
  const whole = kb === null || kb === undefined || !Number.isFinite(kb) ? null : Math.round(kb);
  if (whole === null || whole < 1 || whole > mostLimitKb) return { error: `Enter a ${which} limit from 1 to ${mostLimitKb.toLocaleString('en-US')} KB/s.` };
  if (whole === atOpen.kb && atOpen.bytes > 0) return { bytes: atOpen.bytes };
  return { bytes: whole * 1024 };
}

/** torrent.setOptions' request from the draft, or the Host's words for a value it would refuse and the box to mark. */
export function optionsRequest(jobId: string, draft: OptionsDraft):
  { ok: true; request: Required<Omit<TorrentOptions, 'seedGoalValue'>> & { seedGoalValue: number | null } } | { ok: false; error: string; field: string } {
  const down = limitBytes(draft.limitDown, draft.downKb, draft.downAtOpen, 'download');
  if ('error' in down) return { ok: false, error: down.error, field: 'opt-down' };
  const up = limitBytes(draft.limitUp, draft.upKb, draft.upAtOpen, 'upload');
  if ('error' in up) return { ok: false, error: up.error, field: 'opt-up' };
  let value: number | null = null;
  if (draft.goal === 'ratio') {
    value = draft.ratio;
    if (value === null || !Number.isFinite(value) || value < 0.01 || value > 100) return { ok: false, error: 'Enter a seeding ratio from 0.01 to 100.', field: 'opt-ratio' };
  } else if (draft.goal === 'hours') {
    value = draft.hours;
    if (value === null || !Number.isFinite(value) || value < 0.1 || value > 8760) return { ok: false, error: 'Enter seeding hours from 0.1 to 8760.', field: 'opt-hours' };
  }
  return {
    ok: true,
    request: { jobId, downloadLimit: down.bytes, uploadLimit: up.bytes, seedGoal: draft.goal, seedGoalValue: value, sequential: draft.sequential },
  };
}

/** "Options › Torrents: ratio 1.0 or 48 h, whichever comes first", the goal "Global" stands for. */
export function globalGoalText(settings: Pick<Settings, 'torrentSeedAfterDownload' | 'torrentSeedRatio' | 'torrentSeedHours'> | null | undefined): string {
  if (settings?.torrentSeedAfterDownload === false) return 'Options › Torrents: no seeding after the download';
  const goal = seedGoalText(settings?.torrentSeedRatio, settings?.torrentSeedHours);
  return `Options › Torrents: ${goal.charAt(0).toLowerCase()}${goal.slice(1)}`;
}

/** A torrent's own seeding goal in words; "Global" names what Options › Torrents says. */
export function goalText(torrent: Pick<Torrent, 'seedGoal' | 'seedGoalValue'>, settings: Parameters<typeof globalGoalText>[0]): string {
  const value = torrent.seedGoalValue ?? 0;
  switch (goalOf(torrent.seedGoal)) {
    case 'ratio': return `Ratio ${value.toFixed(2).replace(/0$/, '')} (this torrent's own)`;
    case 'hours': return `${Math.round(value * 10) / 10} h (this torrent's own)`;
    case 'never': return "Don't seed (this torrent's own)";
    default: return globalGoalText(settings);
  }
}

/** "↓ 500 KB/s · ↑ 1.0 MB/s" for a torrent's own limits; '' when it has none. */
export function limitsText(torrent: Pick<Torrent, 'downloadLimit' | 'uploadLimit'>): string {
  const parts: string[] = [];
  if (torrent.downloadLimit && torrent.downloadLimit > 0) parts.push(`↓ ${bytes(torrent.downloadLimit)}/s`);
  if (torrent.uploadLimit && torrent.uploadLimit > 0) parts.push(`↑ ${bytes(torrent.uploadLimit)}/s`);
  return parts.join(' · ');
}

/** What the tab says once the options were saved. */
export function optionsSaid(running: boolean, older: string | null | undefined): string {
  if (older) return `Saved. ${older}`;
  return running ? 'Saved and applied now.' : 'Saved. They apply when the torrent starts.';
}

// ---- Force recheck and Announce now ----

/** Force recheck may be asked: the files are known and there may be some on disk (the Host says the rest). */
export function canRecheck(job: Pick<Job, 'state' | 'torrent'>): boolean {
  const torrent = job.torrent;
  if (!hasTorrentControls(job) || !torrent?.hasMetadata || torrent.checking || job.state === 'cancelled') return false;
  return job.state !== 'completed' || torrent.seeding === true;
}

/** Announce now asks the trackers (and the DHT) for peers: only while the torrent runs and isn't paused. */
export function canAnnounce(job: Pick<Job, 'state' | 'torrent'>): boolean {
  return hasTorrentControls(job) && mayRun(job) && job.state !== 'paused';
}

/** What the page says once a check was asked for. */
export function recheckSaid(running: boolean): string {
  return running ? 'Checking the files now.' : 'The files are checked when the torrent starts.';
}

export const announceSaid = 'Asked the trackers for peers now.';

// ---- The Overview's facts (torrent.details) ----

export interface TorrentDetails {
  ok?: boolean;
  running?: boolean;
  source?: string;
  infoHash?: string;
  infoHashV2?: string | null;
  private?: boolean | null;
  addedAt?: string;
  destinationDirectory?: string;
  pieceCount?: number;
  pieceLength?: number;
  wasted?: number | null;
  hashFailures?: number | null;
  connections?: number | null;
  connectionsLimit?: number | null;
  distributedCopies?: number | null;
  nextAnnounce?: number | null;
  pex?: boolean | null;
  dhtNodes?: number | null;
  options?: TorrentOptions;
}

export function sourceText(source: string | null | undefined): string {
  return source === 'file' ? 'A .torrent file' : 'A magnet link';
}

export interface FactRow {
  label: string;
  value: string;
  /** Long values (hashes, folders) may break anywhere. */
  wide?: boolean;
}

/** The Overview's rows below the summary: what the Host keeps, then what the engine tells while the torrent runs. */
export function overviewRows(details: TorrentDetails | null, torrent: Torrent, settings: Parameters<typeof globalGoalText>[0]): FactRow[] {
  const rows: FactRow[] = [];
  rows.push({ label: 'Added from', value: sourceText(details?.source ?? torrent.source) });
  if (details?.infoHashV2) rows.push({ label: 'Info hash v2', value: details.infoHashV2, wide: true });
  if (details && details.private !== undefined) {
    rows.push({
      label: 'Private',
      value: details.private === true ? 'Yes · DHT, PEX and local peers are off for it' : details.private === false ? 'No' : 'Not known yet',
    });
  }
  if (details?.pieceCount && details.pieceCount > 0) {
    rows.push({ label: 'Pieces', value: `${details.pieceCount.toLocaleString('en-US')} × ${bytes(details.pieceLength ?? 0) || '0 B'}` });
  }
  rows.push({ label: 'Seeding goal', value: goalText(torrent, settings) });
  const limits = limitsText(torrent);
  if (limits) rows.push({ label: 'Own limits', value: limits });
  if (torrent.sequential) rows.push({ label: 'Order', value: 'Pieces in order' });
  if (torrent.recheckPending) rows.push({ label: 'File check', value: 'When the torrent starts' });
  if (!details?.running) return rows;
  if (typeof details.connections === 'number') {
    const limit = details.connectionsLimit && details.connectionsLimit > 0 ? ` of ${details.connectionsLimit.toLocaleString('en-US')}` : '';
    rows.push({ label: 'Connections', value: `${details.connections.toLocaleString('en-US')}${limit}` });
  }
  if (typeof details.distributedCopies === 'number' && details.distributedCopies >= 0) {
    rows.push({ label: 'Copies among peers', value: details.distributedCopies.toFixed(2) });
  }
  const next = nextAnnounceText(details.nextAnnounce);
  if (next) rows.push({ label: 'Next announce', value: next });
  if (typeof details.pex === 'boolean') rows.push({ label: 'Peer exchange', value: details.pex ? 'On' : 'Off' });
  if (typeof details.dhtNodes === 'number') rows.push({ label: 'DHT nodes', value: details.dhtNodes < 0 ? 'DHT is off' : details.dhtNodes.toLocaleString('en-US') });
  return rows;
}

/** "Wasted 0 B · hash failures 0" for the Pieces & Speed tab; '' while not known. */
export function wasteText(details: Pick<TorrentDetails, 'wasted' | 'hashFailures'> | null | undefined): string {
  if (!details || (typeof details.wasted !== 'number' && typeof details.hashFailures !== 'number')) return '';
  return `Wasted ${bytes(details.wasted ?? 0) || '0 B'} · hash failures ${(details.hashFailures ?? 0).toLocaleString('en-US')}`;
}

/** How often the Overview and Pieces tabs ask torrent.details while the torrent runs. */
export const detailsPollMs = 5000;

// ---- Free space in the add dialog ----

/** "612 GB free in D:\Downloads"; '' when not measured. */
export function freeText(result: { destination?: string | null; freeBytes?: number | null } | null | undefined): string {
  if (!result || typeof result.freeBytes !== 'number' || result.freeBytes < 0) return '';
  const free = bytes(result.freeBytes) || '0 B';
  return result.destination ? `${free} free in ${result.destination}` : `${free} free`;
}

/**
 * Why a torrent won't fit: the chosen files' size against the free bytes when only some are chosen (the Host's words
 * are for every file), else the Host's own words. Null when it fits or isn't known.
 */
export function spaceProblem(result: { freeBytes?: number | null; spaceProblem?: string | null } | null | undefined, wanted: number | null, partial: boolean): string | null {
  if (!result) return null;
  if (partial && typeof result.freeBytes === 'number' && wanted !== null) {
    return wanted > result.freeBytes ? `The chosen files need ${bytes(wanted)}, and only ${bytes(result.freeBytes) || '0 B'} is free.` : null;
  }
  return result.spaceProblem?.trim() || null;
}

// ---- Watching torrents (torrent.watch) ----

/** The lease asked for, and how often it is renewed while torrents show. */
export const watchLeaseSeconds = 60;
export const watchRenewMs = 45_000;

/** A watch is held while the page is visible, connected, the server has torrents and a torrent shows. */
export function watchWanted(state: { visible: boolean; connected: boolean; capable: boolean; showsTorrents: boolean }): boolean {
  return state.visible && state.connected && state.capable && state.showsTorrents;
}

/**
 * The details Files tab's reads against its changes. One read is on its way at a time; a read wanted meanwhile is asked
 * once that one is back. A read asked before a change was sent may carry the old priorities, so its answer is dropped
 * and the list is read again.
 */
export class FileReadGate {
  private changes = 0;
  private reading = false;
  private again = false;

  /** The change count the read is asked at, or null when a read is on its way (it is asked again after that one). */
  begin(): number | null {
    if (this.reading) {
      this.again = true;
      return null;
    }
    this.reading = true;
    return this.changes;
  }

  /** A change is being sent, or was taken. */
  changed(): void {
    this.changes++;
  }

  /** True when the answer of a read asked at `asked` may be shown; false drops it and asks again. */
  fresh(asked: number): boolean {
    if (asked === this.changes) return true;
    this.again = true;
    return false;
  }

  /** The read is back; true when another one should be asked now. */
  end(): boolean {
    this.reading = false;
    const again = this.again;
    this.again = false;
    return again;
  }
}
