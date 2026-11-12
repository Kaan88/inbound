// T1 torrents in the web UI (docs/plans/torrent-t1-design.md, sections 5 and 6). The rules and words are the Windows
// app's (TorrentRules, TorrentAddLines, ProgressOverviewText in src/DownloadManager.Desktop.WinUI), so the two read
// alike. Nothing here touches the page, so the tests call it directly.

import { bytes } from './format';
import { addPriorities, spaceProblem, wantedIndexes, type FilePriority, type PriorityMap } from './torrentControls';
import { seedingStatus } from './torrentDetails';
import type { Failure, Job, RemoteAccount, RemoteFile, Settings, Torrent, TorrentFile, TorrentInspectResult } from './types';

// ---- When torrents show at all ----

/** Torrents can be added and are shown: the Host has the engine (hello `torrents`) and they are on in its Options. */
export function torrentsApply(capabilities: readonly string[] | null | undefined, settings: Pick<Settings, 'torrentsEnabled'> | null | undefined): boolean {
  return (capabilities?.includes('torrents') ?? false) && settings?.torrentsEnabled === true;
}

/** The Torrents view shows while torrents apply, and while torrent jobs are on the list whatever the setting. */
export function sidebarShows(applies: boolean, anyTorrentJobs: boolean): boolean {
  return applies || anyTorrentJobs;
}

export function isTorrent(job: Pick<Job, 'torrent'> | null | undefined): boolean {
  return !!job?.torrent;
}

export function isMagnetInput(text: string): boolean {
  return /^magnet:\?/i.test(text.trim());
}

/** A reference to an uploaded file (POST /api/upload/torrent): the server's name for it, never its path. */
export function isUploadInput(text: string): boolean {
  return text.trim().startsWith('upload:');
}

/** The account that also takes torrents, when there is one with a usable key (MainWindow.TorrentProviderAccount). */
export function torrentProvider(remote: boolean, accounts: readonly RemoteAccount[]): RemoteAccount | null {
  if (!remote) return null;
  return accounts.find((a) => a.hasKey && !a.keyUnreadable && a.capabilities.some((c) => c.toLowerCase() === 'torrents')) ?? null;
}

/** The magnets in a text, each once, in order. Trackers and all: they go to the Host, never on the page. */
export function magnetsIn(text: string | null | undefined): string[] {
  if (!text) return [];
  const found: string[] = [];
  for (const match of text.matchAll(/magnet:\?[^\s"'<>`]+/gi)) {
    const magnet = match[0].replace(/[),.;\]]+$/, '');
    if (!found.some((m) => m.toLowerCase() === magnet.toLowerCase())) found.push(magnet);
  }
  return found;
}

/** How the dialogs name the machine that downloads a This PC torrent: the PC for the Windows app, else the server. */
export function hereName(hostKind: string | null | undefined): { short: string; upper: string; the: string; own: string } {
  return hostKind === 'windows'
    ? { short: 'This PC', upper: 'This PC', the: 'the PC', own: "this PC's" }
    : { short: 'This server', upper: 'The server', the: 'the server', own: "the server's" };
}

// ---- A torrent row ----

export function isActive(job: Pick<Job, 'state'>): boolean {
  return job.state === 'connecting' || job.state === 'downloading' || job.state === 'verifying';
}

export function peersText(peers: number): string {
  return peers === 1 ? '1 peer' : `${Math.max(0, peers)} peers`;
}

/** The Status column of a torrent row, or null for the usual words. */
export function torrentStatusText(job: Pick<Job, 'state' | 'torrent'> & { failure?: Pick<Failure, 'kind'> }): string | null {
  const torrent = job.torrent;
  if (!torrent) return null;
  if (torrent.awaitingFileChoice) return 'Choose files to download';
  if (job.state === 'paused' && job.failure?.kind === 'diskFull') return 'Paused · The disk is full';
  // T3: the files on disk are being checked (Force recheck, or a check before it starts).
  if (torrent.checking) return 'Checking files';
  // T2: a finished torrent shared with other peers.
  if (torrent.seeding) return seedingStatus(torrent);
  if (!isActive(job)) return null;
  const peers = peersText(torrent.peers);
  return torrent.hasMetadata ? `Downloading · ${peers}` : `Getting file list · ${peers}`;
}

/** "1 file", "11 files" or "3 of 11 files"; "File list not known yet" before the metadata. */
export function filesSummary(torrent: Pick<Torrent, 'hasMetadata' | 'fileCount' | 'wantedCount'>): string {
  if (!torrent.hasMetadata) return 'File list not known yet';
  const total = torrent.fileCount === 1 ? '1 file' : `${torrent.fileCount} files`;
  return torrent.wantedCount >= torrent.fileCount ? total : `${torrent.wantedCount} of ${total}`;
}

/** A short magnet that names the torrent, without trackers: what a torrent shows as its address. */
export function shortMagnet(torrent: Pick<Torrent, 'infoHash'>): string {
  return `magnet:?xt=urn:btih:${torrent.infoHash}`;
}

/** The status bar's chip while a torrent runs: "Torrents: 2 active · 47 peers"; null when none runs. */
export function chipText(jobs: readonly Job[]): string | null {
  const active = jobs.filter((job) => job.torrent && isActive(job));
  if (active.length === 0) return null;
  return `Torrents: ${active.length} active · ${peersText(active.reduce((sum, job) => sum + (job.torrent?.peers ?? 0), 0))}`;
}

/** A torrent row opens to list its files when it has more than one and they are known. */
export function showsFiles(torrent: Torrent | null | undefined): boolean {
  return !!torrent && torrent.hasMetadata && torrent.fileCount > 1;
}

/** The row's primary action (double-click, Enter) is the file choice it waits for. */
export function primaryIsChooseFiles(job: Pick<Job, 'torrent'> | null | undefined): boolean {
  return !!job?.torrent && job.torrent.hasMetadata && job.torrent.awaitingFileChoice;
}

/** A torrent can't be moved, renamed, given another address or redownloaded from the list (T1). */
export function blocksAddressCommands(selection: readonly Pick<Job, 'torrent'>[]): boolean {
  return selection.some(isTorrent);
}

/** The reason the desktop's menu gives a command a torrent can't use. */
export const torrentReason = 'not for torrents';

/** "Choose files…" applies to one torrent whose files are known and that hasn't finished. */
export function canChooseFiles(selection: readonly Pick<Job, 'torrent' | 'state'>[]): boolean {
  return selection.length === 1 && !!selection[0].torrent?.hasMetadata && selection[0].state !== 'completed' && selection[0].state !== 'cancelled';
}

/** A torrent's files can't change once it has finished or been cancelled. */
export function filesFixed(job: Pick<Job, 'state'>): boolean {
  return job.state === 'completed' || job.state === 'cancelled';
}

// ---- Its files ----

/** The most file rows an open torrent row lists; the rest are in the details. */
export const mostFileRows = 200;

/** How a file stands: "Skipped", "Done", "71%", or "" when not known. A finished torrent has every wanted file. */
export function fileProgress(file: TorrentFile, torrentComplete = false): string {
  if (!file.wanted) return 'Skipped';
  if (file.done === null || file.done === undefined) return torrentComplete ? 'Done' : '';
  if (file.size <= 0 || file.done >= file.size) return 'Done';
  return `${Math.min(99, Math.floor((file.done * 100) / file.size))}%`;
}

/** A file row's name: the last part of its path. */
export function fileRowName(path: string): string {
  const trimmed = path.replace(/[\\/]+$/, '');
  const cut = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'));
  return cut < 0 ? trimmed : trimmed.slice(cut + 1);
}

/** A file row's Status: "Skipped", how far the file is, or, when that isn't known, its torrent's state word. */
export function fileRowStatus(file: TorrentFile, parentWord: string, torrentComplete = false): string {
  return fileProgress(file, torrentComplete) || parentWord;
}

/** The row under the first {@link mostFileRows} files of a bigger torrent. */
export function moreFiles(count: number): string {
  return count === 1 ? '1 more file: open Progress Details' : `${count} more files: open Progress Details`;
}

/** How long an open torrent row waits before reading its files again while it downloads: longer for a big list. */
export function filesRefreshMs(fileCount: number): number {
  return fileCount <= 1000 ? 3000 : 30000;
}

/** What the page says after a file choice was sent. */
export function choiceSaid(name: string, chosen: number, total: number): string {
  const files = chosen === 1 ? '1 file' : `${chosen} files`;
  return chosen >= total ? `${name}: downloading all ${files}.` : `${name}: downloading ${files} of ${total}.`;
}

/** The file tree's view of a torrent's files: the index is the file's id. (The tree can't tick an empty file.) */
export function treeFiles(files: readonly TorrentFile[]): RemoteFile[] {
  return files.map((file) => ({ fileId: String(file.index), relativePath: file.path, size: file.size, infected: false, selectable: true }));
}

/** The wanted files' ids, for the tree's starting choice. */
export function wantedIds(files: readonly TorrentFile[]): Set<string> {
  return new Set(files.filter((file) => file.wanted).map((file) => String(file.index)));
}

/** The tree's choice as torrent file indexes, in order; null when an id isn't an index. */
export function indexesOf(chosen: ReadonlySet<string>, files: readonly TorrentFile[]): number[] | null {
  const indexes: number[] = [];
  for (const id of chosen) {
    if (!/^\d+$/.test(id)) return null;
    const index = Number(id);
    if (files.some((file) => file.index === index)) indexes.push(index);
  }
  return indexes.sort((a, b) => a - b);
}

/**
 * The tree's choice as the indexes to send: every file when every file with bytes is ticked (the tree can't tick an
 * empty one), else the ticked ones. Null when the choice can't be read.
 */
export function choiceIndexes(chosen: ReadonlySet<string>, files: readonly TorrentFile[]): number[] | null {
  const indexes = indexesOf(chosen, files);
  if (indexes === null) return null;
  const picked = new Set(indexes);
  const sized = files.filter((file) => file.size > 0);
  return sized.length > 0 && sized.every((file) => picked.has(file.index)) ? files.map((file) => file.index).sort((a, b) => a - b) : indexes;
}

/** The torrent's Downloaded figure: "2.6 GB of 4.2 GB". */
export function downloadedText(received: number, total: number): string {
  return total > 0 ? `${bytes(received) || '0 B'} of ${bytes(total)}` : bytes(received) || '0 B';
}

/** The torrent's Peers figure: "41 (12 seeds)". */
export function peersFigure(peers: number, seeds: number): string {
  return `${Math.max(0, peers)} (${seeds === 1 ? '1 seed' : `${Math.max(0, seeds)} seeds`})`;
}

// ---- The add dialog (TorrentAddLines) ----

export type TorrentRoute = 'unset' | 'thisPc' | 'provider';

/** The route a new line starts with, from the setting (`ask`, `thisPc`, `provider`). */
export function defaultRoute(setting: string | null | undefined, providerAvailable: boolean): TorrentRoute {
  if (!providerAvailable) return 'thisPc';
  return setting === 'thisPc' ? 'thisPc' : setting === 'provider' ? 'provider' : 'unset';
}

export type TorrentLineState = 'reading' | 'ready' | 'failed' | 'listed' | 'duplicate';

/** One magnet or uploaded .torrent file in the add dialog. */
export interface TorrentLine {
  /** The magnet as typed, or the upload's reference. Never shown whole for a magnet. */
  input: string;
  /** The uploaded file's own name. */
  label?: string;
  /** It came from the paste box, so it goes when the box no longer holds it. */
  fromPaste: boolean;
  route: TorrentRoute;
  result: TorrentInspectResult | null;
  /** The input of the line above that turned out to be the same torrent. */
  sameAs: string | null;
  /** The chosen files' indexes; null means every file. */
  chosen: number[] | null;
  /** T3: each file's priority by index (as text); null is every file at Normal. Skip is the same as not chosen. */
  priorities: PriorityMap | null;
  wanted: boolean;
}

export interface TorrentLines {
  lines: TorrentLine[];
  /** The account that also takes torrents, or null: then every line is This PC. */
  providerName: string | null;
  /** The setting "Ask which files to download before starting". */
  chooseFiles: boolean;
  defaultRoute: TorrentRoute;
  capped: boolean;
}

/** More than this many lines are not listed: each one is a row and a question to the server. */
export const mostLines = 200;

export function newLines(route: TorrentRoute, chooseFiles: boolean, providerName: string | null): TorrentLines {
  const provider = providerName?.trim() ? providerName.trim() : null;
  return { lines: [], providerName: provider, chooseFiles, defaultRoute: provider ? route : 'thisPc', capped: false };
}

export function isMagnetLine(line: TorrentLine): boolean {
  return isMagnetInput(line.input);
}

export function lineState(line: TorrentLine): TorrentLineState {
  if (!line.result) return 'reading';
  if (!line.result.ok) return 'failed';
  if (line.sameAs !== null) return 'duplicate';
  if (line.result.existingJobId) return 'listed';
  return 'ready';
}

/** Only a line that was read and is new can be ticked. */
export function canSelectLine(line: TorrentLine): boolean {
  return lineState(line) === 'ready';
}

/** Ticked: a line still being read keeps its tick, and one that can't be added shows none. */
export function isSelected(line: TorrentLine): boolean {
  const state = lineState(line);
  return line.wanted && (state === 'ready' || state === 'reading');
}

export function lineKind(line: TorrentLine): string {
  return isMagnetLine(line) ? 'Magnet' : '.torrent file';
}

/** The torrent's name once it is known, else "Magnet link" or the file's name. */
export function lineName(line: TorrentLine): string {
  const name = line.result?.ok ? line.result.name?.trim() : '';
  if (name) return name;
  if (isMagnetLine(line)) return 'Magnet link';
  return line.label?.trim() || '.torrent file';
}

export function lineFileCount(line: TorrentLine): number | null {
  return line.result?.ok && line.result.files ? line.result.files.length : null;
}

export function lineTotal(line: TorrentLine): number | null {
  return line.result?.ok && typeof line.result.totalBytes === 'number' ? line.result.totalBytes : null;
}

/** Some files are left out: only on This PC, where the file choice is used. */
export function isPartial(line: TorrentLine): boolean {
  const count = lineFileCount(line);
  return line.route !== 'provider' && line.chosen !== null && count !== null && line.chosen.length < count;
}

/** What it will download, when known. */
export function wantedBytes(line: TorrentLine): number | null {
  if (!isPartial(line) || !line.result?.files) return lineTotal(line);
  const chosen = new Set(line.chosen);
  return line.result.files.filter((file) => chosen.has(file.index)).reduce((sum, file) => sum + file.size, 0);
}

/** The words under the name: kind · size · file count, or what keeps the line from being added. */
export function lineDetail(line: TorrentLine): string {
  const kind = lineKind(line);
  switch (lineState(line)) {
    case 'reading': return `${kind} · ${isMagnetLine(line) ? 'Checking…' : 'Reading…'}`;
    case 'failed': return line.result?.error?.trim() || "This torrent couldn't be read.";
    case 'listed': return `${kind} · Already in the list`;
    case 'duplicate': return `${kind} · Same torrent as above`;
  }
  const parts = [kind];
  const total = lineTotal(line);
  if (total === null) parts.push('Size not known yet');
  else parts.push(isPartial(line) ? `${bytes(wantedBytes(line) ?? 0)} of ${bytes(total)}` : bytes(total));
  const count = lineFileCount(line);
  if (count !== null) {
    const files = count === 1 ? '1 file' : `${count} files`;
    parts.push(isPartial(line) ? `${line.chosen!.length} of ${files}` : files);
  }
  return parts.join(' · ');
}

/** For the tooltip: a magnet only by its info hash (no trackers), a file by its name. */
export function lineTip(line: TorrentLine): string {
  return isMagnetLine(line) && line.result?.ok && line.result.infoHash ? shortMagnet({ infoHash: line.result.infoHash }) : lineName(line);
}

/** Marks the later of two lines with the same info hash; the first one in list order is kept. */
function recompute(lines: TorrentLine[]): TorrentLine[] {
  const first = new Map<string, string>();
  return lines.map((line) => {
    const hash = line.result?.ok ? line.result.infoHash?.toLowerCase() : undefined;
    if (!hash) return line.sameAs === null ? line : { ...line, sameAs: null };
    const earlier = first.get(hash);
    if (earlier === undefined) first.set(hash, line.input);
    const sameAs = earlier ?? null;
    return line.sameAs === sameAs ? line : { ...line, sameAs };
  });
}

/** Adds a line for each magnet or upload reference not listed yet; returns the new state and the new lines. */
export function addLines(state: TorrentLines, inputs: readonly { input: string; label?: string }[], fromPaste: boolean): { state: TorrentLines; added: TorrentLine[] } {
  const lines = [...state.lines];
  const added: TorrentLine[] = [];
  let capped = state.capped;
  for (const raw of inputs) {
    const input = raw.input.trim();
    if (!isMagnetInput(input) && !isUploadInput(input)) continue;
    if (lines.some((line) => line.input.toLowerCase() === input.toLowerCase())) continue;
    if (lines.length >= mostLines) {
      capped = true;
      continue;
    }
    const line: TorrentLine = { input, label: raw.label, fromPaste, route: state.defaultRoute, result: null, sameAs: null, chosen: null, priorities: null, wanted: true };
    lines.push(line);
    added.push(line);
  }
  return { state: { ...state, lines: recompute(lines), capped }, added };
}

/** Follows the paste box: a line for each magnet in it, and the lines that came from it and are no longer in it go. */
export function syncPaste(state: TorrentLines, text: string): { state: TorrentLines; added: TorrentLine[] } {
  const inputs = magnetsIn(text);
  const kept = new Set(inputs.map((input) => input.toLowerCase()));
  const lines = state.lines.filter((line) => !line.fromPaste || kept.has(line.input.toLowerCase()));
  return addLines({ ...state, lines, capped: false }, inputs.map((input) => ({ input })), true);
}

/** The server's answer for a line; the state stays as it was when the line is no longer listed. */
export function inspected(state: TorrentLines, input: string, result: TorrentInspectResult | null): TorrentLines {
  if (!state.lines.some((line) => line.input === input)) return state;
  const answer = result ?? { ok: false, error: "This torrent couldn't be read." };
  return { ...state, lines: recompute(state.lines.map((line) => (line.input === input ? { ...line, result: answer } : line))) };
}

/** A second read for a new Save to folder: only the free space changes. A line that was read keeps its files and choice,
 * and a failed second read only forgets the free space it had for the old folder. */
export function spaceRechecked(state: TorrentLines, input: string, result: TorrentInspectResult | null): TorrentLines {
  const line = state.lines.find((entry) => entry.input === input);
  if (!line) return state;
  if (!line.result?.ok) return inspected(state, input, result);
  const space = result?.ok
    ? { destination: result.destination ?? null, freeBytes: result.freeBytes ?? null, spaceProblem: result.spaceProblem ?? null }
    : { destination: null, freeBytes: null, spaceProblem: null };
  return { ...state, lines: recompute(state.lines.map((entry) => (entry.input === input ? { ...entry, result: { ...entry.result!, ...space } } : entry))) };
}

export function removeLine(state: TorrentLines, input: string): TorrentLines {
  return { ...state, lines: recompute(state.lines.filter((line) => line.input !== input)) };
}

export function selectLine(state: TorrentLines, input: string, selected: boolean): TorrentLines {
  return { ...state, lines: state.lines.map((line) => (line.input === input && canSelectLine(line) ? { ...line, wanted: selected } : line)) };
}

/** Ticks every line that can be added, or clears them all when they all are ticked. */
export function toggleAll(state: TorrentLines): TorrentLines {
  const all = state.lines.filter(canSelectLine).every(isSelected);
  return { ...state, lines: state.lines.map((line) => (canSelectLine(line) ? { ...line, wanted: !all } : line)) };
}

/** True, false, or null for some, as Select all shows it. */
export function selectAllState(state: TorrentLines): boolean | null {
  const selectable = state.lines.filter(canSelectLine);
  if (selectable.length === 0 || selectable.every((line) => !isSelected(line))) return false;
  return selectable.every(isSelected) ? true : null;
}

/** This PC or the provider. Without a provider every line stays on This PC. */
export function setRoute(state: TorrentLines, input: string, route: TorrentRoute): TorrentLines {
  if (!state.providerName || route === 'unset') return state;
  return { ...state, lines: state.lines.map((line) => (line.input === input ? { ...line, route } : line)) };
}

/** The file choice can be made here: a .torrent with several files, going to This PC, with the setting on. */
export function canChooseFilesFor(state: TorrentLines, line: TorrentLine): boolean {
  return state.chooseFiles && lineState(line) === 'ready' && !isMagnetLine(line) && line.route !== 'provider' && (lineFileCount(line) ?? 0) > 1;
}

/** The file tree's choice for a line: null when every file is chosen. */
export function setFileChoice(state: TorrentLines, input: string, chosenIds: ReadonlySet<string>): TorrentLines {
  return {
    ...state,
    lines: state.lines.map((line) => {
      if (line.input !== input || !line.result?.files) return line;
      const files = line.result.files;
      const indexes = choiceIndexes(chosenIds, files);
      return { ...line, chosen: indexes === null || indexes.length >= files.length ? null : indexes };
    }),
  };
}

/**
 * T3: the file tree's priorities for a line. The files not skipped are its choice (null when that is every file), so
 * the size, the count and "Choose at least one file" follow the priorities.
 */
export function setLinePriorities(state: TorrentLines, input: string, priorities: ReadonlyMap<string, FilePriority>): TorrentLines {
  return {
    ...state,
    lines: state.lines.map((line) => {
      if (line.input !== input || !line.result?.files) return line;
      const files = line.result.files;
      const wanted = wantedIndexes(priorities).filter((index) => files.some((file) => file.index === index));
      return { ...line, priorities: new Map(priorities), chosen: wanted.length >= files.length ? null : wanted };
    }),
  };
}

/** T3: why a ticked line won't fit where it goes (the chosen files when only some are chosen), or null. */
export function lineSpaceProblem(line: TorrentLine): string | null {
  if (lineState(line) !== 'ready' || line.route === 'provider') return null;
  return spaceProblem(line.result, wantedBytes(line), isPartial(line));
}

/** The lines that will be added. */
export function chosenLines(state: TorrentLines): TorrentLine[] {
  return state.lines.filter((line) => isSelected(line) && lineState(line) === 'ready');
}

function hasNoFiles(state: TorrentLines, line: TorrentLine): boolean {
  return canChooseFilesFor(state, line) && line.chosen !== null && line.chosen.length === 0;
}

/** Add can be pressed: something is ticked, nothing ticked is still being read, and each has a route and files. */
export function canAddLines(state: TorrentLines): boolean {
  const chosen = chosenLines(state);
  const stillReading = state.lines.some((line) => isSelected(line) && lineState(line) === 'reading');
  return chosen.length > 0 && !stillReading && chosen.every((line) => line.route !== 'unset' && !hasNoFiles(state, line));
}

/** "Add" or "Add 3". */
export function primaryText(state: TorrentLines): string {
  const count = chosenLines(state).length;
  return count >= 2 ? `Add ${count}` : 'Add';
}

/** "Selected 2.1 GB in 2 torrents", counting only the sizes that are known. */
export function summary(state: TorrentLines): string {
  const chosen = chosenLines(state);
  if (chosen.length === 0) return state.lines.length === 0 ? '' : 'No torrents selected';
  const count = chosen.length === 1 ? '1 torrent' : `${chosen.length} torrents`;
  const sizes = chosen.map(wantedBytes).filter((size): size is number => size !== null);
  return sizes.length > 0 ? `Selected ${bytes(sizes.reduce((a, b) => a + b, 0))} in ${count}` : `Selected ${count}`;
}

/** Why Add can't be pressed yet, when the user has something to do; else empty. */
export function problem(state: TorrentLines): string {
  const chosen = chosenLines(state);
  if (state.providerName && chosen.some((line) => line.route === 'unset')) return `Choose This PC or ${state.providerName} for each ticked torrent.`;
  const empty = chosen.find((line) => hasNoFiles(state, line));
  if (empty) return `Choose at least one file of ${lineName(empty)}.`;
  const tight = chosen.find((line) => lineSpaceProblem(line) !== null);
  return tight ? `${lineName(tight)}: ${lineSpaceProblem(tight)}` : '';
}

/** The words under the list, the summary first. */
export function statusLine(state: TorrentLines): string {
  const parts = [summary(state), problem(state), state.capped ? `Only the first ${mostLines} are listed.` : ''].filter((part) => part.length > 0).map((part) => part.replace(/\.$/, ''));
  return parts.length > 0 ? `${parts.join('. ')}.` : '';
}

/** "A magnet asks which files to download once its file list arrives" applies to a listed line. */
export function magnetNoteApplies(state: TorrentLines): boolean {
  return state.chooseFiles && state.lines.some((line) => isMagnetLine(line) && line.route !== 'provider' && (lineState(line) === 'ready' || lineState(line) === 'reading'));
}

/** One torrent to add: `files` is `all`, `chosen` (with indexes) or `askWhenReady` (a magnet whose files come later). */
export interface TorrentPick {
  input: string;
  name: string;
  route: 'thisPc' | 'provider';
  files: 'all' | 'chosen' | 'askWhenReady';
  fileIndexes: number[] | null;
  /** T3: the High and Low files (the rest are Normal, or left out by the choice); null for none. */
  priorities: { index: number; priority: FilePriority }[] | null;
}

/** The chosen lines as the dialog hands them on. Only meaningful while {@link canAddLines}. */
export function picks(state: TorrentLines): TorrentPick[] {
  return chosenLines(state)
    .filter((line) => line.route !== 'unset')
    .map((line): TorrentPick => {
      const base = { input: line.input, name: lineName(line), route: line.route as 'thisPc' | 'provider', fileIndexes: null, priorities: null };
      // The provider's own flow asks about the files.
      if (line.route === 'provider') return { ...base, files: 'all' };
      if (!line.result?.files) return { ...base, files: state.chooseFiles ? 'askWhenReady' : 'all' };
      if (!canChooseFilesFor(state, line)) return { ...base, files: 'all' };
      const priorities = addPriorities(line.priorities);
      if (!isPartial(line)) return { ...base, files: 'all', priorities };
      return { ...base, files: 'chosen', fileIndexes: [...line.chosen!], priorities };
    });
}

/** What the page says once the This PC torrents were sent. */
export function addedWords(added: number, existing: number): string | null {
  if (added === 0 && existing === 0) return null;
  if (existing === 0) return added === 1 ? 'Added 1 torrent.' : `Added ${added} torrents.`;
  if (added === 0) return existing === 1 ? 'That torrent is already in the list.' : `Those ${existing} torrents are already in the list.`;
  return `Added ${added} torrents; ${existing} were already in the list.`;
}

/** The text that sends a This PC line to the provider's own add dialog: its magnets, and its uploads by reference. */
export function providerInputs(list: readonly TorrentPick[]): string[] {
  return list.filter((pick) => pick.route === 'provider').map((pick) => pick.input);
}

// ---- The details' Files tab (DownloadWindow) ----

/** The most files the details list; a bigger torrent says how many more there are. */
export const mostDetailFiles = 2000;

/** The wanted indexes after one file is ticked or cleared; null when that would leave none ("Keep at least one file."). */
export function nextChoice(wanted: ReadonlySet<number>, index: number, want: boolean): number[] | null {
  const next = new Set(wanted);
  if (want) next.add(index);
  else next.delete(index);
  return next.size === 0 ? null : [...next].sort((a, b) => a - b);
}

/** What the Files tab says above the list, or '' when the list speaks for itself. */
export function filesTabNote(job: Pick<Job, 'state' | 'torrent'>, choosing: boolean): string {
  if (!job.torrent?.hasMetadata) return 'The file list is not in yet. It comes from the peers.';
  if (choosing) return 'Choose the files to download, then Start.';
  return '';
}

/** What a screen reader says for a file row: its path, size and how it stands. */
export function fileRowLabel(file: TorrentFile, torrentComplete: boolean): string {
  const progress = fileProgress(file, torrentComplete);
  const size = bytes(file.size) || '0 B';
  return progress ? `${file.path}, ${size}, ${progress}` : `${file.path}, ${size}`;
}
