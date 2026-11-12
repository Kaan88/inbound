// What the download list shows and does, as plain functions of the Host's data. Each one follows the desktop window's
// rule of the same name (JobViewModel, DownloadGroupSummary, PackageRules, SelectionCommands, ListSort, DurationText),
// so the two lists read alike. Nothing here touches the page, so the tests call it directly.

import { archiveRowView, canRetry, fullText, packageArchiveView } from './archives';
import { failureText } from './details';
import { bytes, dateTime, duration, jobStateWord, percent, speed as speedText, stateWord, timeSpanSeconds, tone as toneOf, type Tone } from './format';
import { actionsOf, canBrowse, isRow, jobRow, providerNameOf, sameProvider, showsWaiting } from './remote';
import { fileListWaitNote, filesSummary, shortMagnet, torrentReason, torrentStatusText } from './torrents';
import { seedGoalOf, swarmCells, type SeedGoal, type SwarmCells } from './torrentList';
import type { ArchiveSet, CategoryDefinition, DownloadGroup, Job, RemoteAccount, RemoteJob } from './types';

// ---- Words for a download's cells ----

const removedWords = 'Archive removed after extraction';

/** A local file a provider supplies. Its address is a reference, not something to edit or send again. */
export function isProviderFile(job: Job): boolean {
  return job.isRemote === true || job.url.toLowerCase().startsWith('remote://');
}

/** What a running download waiting for a free connection to its host says. The host name only. */
export function waitingText(job: Job): string | null {
  return ['connecting', 'downloading', 'retryWaiting'].includes(job.state) && job.waitingForHost?.trim()
    ? `Waiting for a connection to ${job.waitingForHost}`
    : null;
}

/** The Host's words about a download beyond its state; null when it has nothing to say. */
export function jobNote(job: Job): string | null {
  if (waitingText(job) !== null) return job.fileRemovedAfterExtraction ? removedWords : null;
  if (job.statusNote?.trim()) return hideAddresses(job.statusNote, job);
  return job.fileRemovedAfterExtraction ? removedWords : null;
}

/** The note goes first, because the Description column is often cut short. */
export function describe(note: string | null | undefined, own: string | null | undefined): string {
  if (!note || !note.trim()) return own ?? '';
  return !own || !own.trim() ? note : `${note} · ${own}`;
}

/** The status column's words. */
export function statusWords(job: Job): string {
  const torrent = torrentStatusText(job);
  if (torrent) return torrent;
  if (job.queueRetryAt) return 'Queue retry scheduled';
  const waiting = waitingText(job);
  if (waiting) return waiting;
  if (isProviderFile(job) && job.state === 'downloading') return 'Downloading locally';
  return jobStateWord(job);
}

/** The Time left column: "1h 5m", "3m 20s", "45s"; blank when there is no estimate (format.duration, the desktop's words). */
export function timeLeft(seconds: number | null | undefined): string {
  return duration(seconds);
}


/** The Last Try Date column, "Nov 09 14:03:22 2026" in the viewer's time zone. */
export function lastTryText(value: string | number | Date | null | undefined): string {
  return dateTime(value);
}

/** The queue's number as the Q column shows it; empty when the download is not in a queue. */
export function queueText(job: Pick<Job, 'isQueued' | 'queueId'>): string {
  return job.isQueued === false ? '' : String(job.queueId + 1);
}

export function queueName(job: Pick<Job, 'queueId' | 'queueName'>): string {
  return job.queueName ?? (job.queueId === 0 ? 'Main' : `Queue ${job.queueId}`);
}

// ---- Packages: DownloadGroupSummary and PackageRules ----

export const mixedQueueMarker = '*';

export type GroupState = 'empty' | 'active' | 'failed' | 'paused' | 'queued' | 'cancelled' | 'completed';

export interface GroupSummary {
  memberCount: number;
  completedCount: number;
  receivedBytes: number;
  totalBytes: number | null;
  bytesPerSecond: number;
  /** A fraction from 0 to 1; null when any file has an unknown size. */
  progress: number | null;
  state: GroupState;
  /** Seconds. */
  estimatedRemaining: number | null;
}

const activeStates = ['downloading', 'connecting', 'verifying', 'retryWaiting'];

/** The same arithmetic as the desktop's DownloadGroupSummary.Create. */
export function summarizeGroup(jobs: readonly Job[]): GroupSummary {
  if (jobs.length === 0) {
    return { memberCount: 0, completedCount: 0, receivedBytes: 0, totalBytes: null, bytesPerSecond: 0, progress: null, state: 'empty', estimatedRemaining: null };
  }

  let received = 0;
  let total = 0;
  let allKnown = true;
  let rate = 0;
  let completed = 0;
  let hasActive = false;
  let hasFailed = false;
  let hasPaused = false;
  let hasQueued = false;
  let hasCancelled = false;

  for (const job of jobs) {
    received += Math.max(0, job.receivedBytes ?? 0);
    if (typeof job.totalBytes === 'number' && job.totalBytes >= 0) total += job.totalBytes;
    else allKnown = false;

    const state = (job.state ?? '').trim().toLowerCase();
    if (activeStates.some((s) => s.toLowerCase() === state)) {
      hasActive = true;
      const r = job.bytesPerSecond;
      if (typeof r === 'number' && Number.isFinite(r) && r > 0) rate += r;
    } else if (state === 'failed') hasFailed = true;
    else if (state === 'paused') hasPaused = true;
    else if (state === 'queued') hasQueued = true;
    else if (state === 'cancelled') hasCancelled = true;
    else if (state === 'completed') completed++;
    else hasQueued = true;
  }

  const allCompleted = completed === jobs.length;
  const state: GroupState = hasActive ? 'active' : hasFailed ? 'failed' : hasPaused ? 'paused' : hasQueued ? 'queued' : hasCancelled ? 'cancelled' : allCompleted ? 'completed' : 'cancelled';
  const knownTotal = allKnown ? total : null;
  const progress = knownTotal === null ? null : knownTotal === 0 ? (allCompleted ? 1 : 0) : Math.min(1, received / knownTotal);

  let remaining: number | null = null;
  if (knownTotal !== null && rate > 0) remaining = Math.max(0, knownTotal - Math.min(received, knownTotal)) / rate;

  return { memberCount: jobs.length, completedCount: completed, receivedBytes: received, totalBytes: knownTotal, bytesPerSecond: rate, progress, state, estimatedRemaining: remaining };
}

/** Pause touches the files that can pause. */
export function pauseTargets(children: readonly Job[]): Job[] {
  return children.filter((child) => child.canPause);
}

/**
 * Resume restarts paused and failed files but leaves cancelled ones cancelled, because cancelling a file was a decision
 * about that file. The one exception undoes a package Cancel: when that Cancel stopped every file in the package, Resume
 * brings them all back.
 */
export function resumeTargets(packageId: string, children: readonly Job[]): Job[] {
  const same = (a?: string, b?: string) => !!a && !!b && a.toLowerCase() === b.toLowerCase();
  const undoCancel = children.length > 0 && children.every((child) => child.state === 'cancelled' && same(child.cancelledByPackageId, packageId));
  return children.filter((child) => child.canResume && (undoCancel || child.state !== 'cancelled'));
}

/** Finished and already cancelled files are left as they are. */
export function cancelTargets(children: readonly Job[]): Job[] {
  return children.filter((child) => !child.isTerminal && child.state !== 'cancelled');
}

/** A finished file has nothing left to schedule. */
export function priorityTargets(children: readonly Job[]): Job[] {
  return children.filter((child) => child.state !== 'completed');
}

function queueKey(job: Job): number {
  return job.isQueued === false ? -1 : job.queueId;
}

/** The queue every file is in; null when they differ or none is in a queue. */
export function sharedQueueId(children: readonly Job[]): number | null {
  const queues = new Set(children.map(queueKey));
  if (queues.size !== 1) return null;
  const only = [...queues][0];
  return only >= 0 ? only : null;
}

/** The Q column of a package: the shared queue's number, a marker when mixed, blank when none. */
export function packageQueueText(children: readonly Job[]): string {
  const queues = new Set(children.map(queueKey));
  if (queues.size === 0) return '';
  if (queues.size === 1) {
    const only = [...queues][0];
    return only >= 0 ? String(only + 1) : '';
  }
  return mixedQueueMarker;
}

/** The suggestion for a new package's name: the first file's name without its extension or part number. */
export function suggestPackageName(fileName: string): string {
  return fileName.replace(/(\.part\d+\.rar|\.r\d{2,3}|\.z\d{2}|\.\d{3})$|\.[^.]+$/i, '');
}

// ---- What each file menu command has to say for itself (FileMenuRules) ----

export interface FileMenuPlan {
  fileReason: string | null;
  localReason: string | null;
  removeReason: string | null;
  openReason: string | null;
}

export const fileRemovedReason = 'archive removed after extraction';

export function planFileMenu(singleFile: boolean, packageRow: boolean, sideRows: number, removableSideRows: number, fileRemoved = false): FileMenuPlan {
  const local = singleFile || packageRow;
  const noLocalRow = local ? null : sideRows > 0 ? 'no local file selected' : 'select a download first';
  const fileReason = singleFile ? null : packageRow ? 'select a file, not a package' : noLocalRow;
  return {
    fileReason,
    localReason: noLocalRow,
    removeReason: local || removableSideRows > 0 ? null : sideRows > 0 ? 'cancel the extraction first' : 'select a download first',
    openReason: fileReason ?? (fileRemoved ? fileRemovedReason : null),
  };
}

export function menuLabel(text: string, reason: string | null): string {
  // With nothing selected the greyed item says nothing more (owner, 2026-11-09).
  return reason === null || reason === 'select a download first' ? text : `${text} (${reason})`;
}

export interface FileCommand { label: string; enabled: boolean; hidden: boolean }

/**
 * The desktop row menu's file commands the web menu lacked (U-WU-10): Move/Rename… for one finished file, Redownload and
 * Refresh Download Address… for one file (not a provider's), Change Category… for any downloads. The reasons are the
 * same words as the other entries.
 */
export function fileCommands(
  single: Pick<Job, 'state' | 'url' | 'isRemote' | 'torrent'> | null,
  plan: FileMenuPlan,
  files: number,
  torrentSelected = !!single?.torrent,
): { move: FileCommand; redownload: FileCommand; refresh: FileCommand; category: FileCommand } {
  const providers = !!single && (single.isRemote === true || single.url.toLowerCase().startsWith('remote://'));
  // T1: a torrent has no single address or file for these, so they are hidden for it (owner, 2026-11-11).
  const torrent = torrentSelected;
  const reasonFor = (reason: string | null) => reason ?? (torrent ? torrentReason : null);
  return {
    move: { label: menuLabel('Move/Rename…', reasonFor(plan.openReason)), enabled: !torrent && plan.openReason === null && single?.state === 'completed', hidden: torrent },
    redownload: { label: menuLabel('Redownload', reasonFor(plan.fileReason)), enabled: !torrent && plan.fileReason === null, hidden: torrent },
    refresh: { label: menuLabel('Refresh Download Address…', reasonFor(plan.fileReason)), enabled: !torrent && plan.fileReason === null && !providers, hidden: providers || torrent },
    category: { label: 'Change Category…', enabled: files > 0, hidden: false },
  };
}

/** Grouping takes two files or more, in two rows or more. */
export function canGroup(files: number, localRows: number): boolean {
  return files >= 2 && localRows >= 2;
}

// ---- Deleting ----

export interface DeletePlan {
  title: string;
  /** The question and anything the Host will do on its own. */
  text: string;
  /** Whether to offer "Delete from disk". */
  offerFiles: boolean;
  /** What to send as deleteFile when the box isn't offered. */
  deleteFile: boolean;
}

export function nothingToDelete(count: number): string {
  return count === 1
    ? 'Its archive was removed after extraction, so no file is deleted.'
    : 'Their archives were removed after extraction, so no files are deleted.';
}

export const mixedDeleteNote = 'Unfinished downloads always lose their partial data; the box decides only the finished files.';

/**
 * UI.B19: what Delete sends as deleteFile for one download. An unfinished one has only partial data, which nothing can
 * use once it is off the list, so it always goes, whatever the box says, as when it is deleted alone. The box
 * ("Delete from disk") decides only a finished file.
 */
export function deleteFileFor(job: Job, deleteFinishedFiles: boolean): boolean {
  return job.state !== 'completed' || deleteFinishedFiles;
}

/** The selection split by what {@link deleteFileFor} sends, as one remove each: data going first, then files kept. */
export function deleteBatches(jobs: readonly Job[], deleteFinishedFiles: boolean): { ids: string[]; deleteFile: boolean }[] {
  const withData = jobs.filter((job) => deleteFileFor(job, deleteFinishedFiles)).map((job) => job.id);
  const keepFile = jobs.filter((job) => !deleteFileFor(job, deleteFinishedFiles)).map((job) => job.id);
  return [{ ids: withData, deleteFile: true }, { ids: keepFile, deleteFile: false }].filter((batch) => batch.ids.length > 0);
}

/** The Delete dialog: its title and words, and whether a finished file is there to delete. */
export function deletePlan(jobs: readonly Job[]): DeletePlan {
  const anyCompleted = jobs.some((job) => job.state === 'completed' && !job.fileRemovedAfterExtraction);
  const anyUnfinished = jobs.some((job) => job.state !== 'completed');
  const noFilesLeft = jobs.length > 0 && jobs.every((job) => job.fileRemovedAfterExtraction);
  const question = jobs.length === 1 ? `Delete '${jobs[0].fileName}' from the list?` : `Delete ${jobs.length} downloads from the list?`;
  return {
    title: jobs.length === 1 ? 'Delete download?' : `Delete ${jobs.length} downloads?`,
    text: noFilesLeft ? `${question} ${nothingToDelete(jobs.length)}`
      : anyCompleted ? (anyUnfinished ? `${question} ${mixedDeleteNote}` : question)
      : `${question} Partial data is always removed.`,
    offerFiles: anyCompleted,
    deleteFile: !anyCompleted && !noFilesLeft,
  };
}

/**
 * Delete Completed's question. A seeding torrent is completed too, so it goes with them; the question says that stops it
 * seeding (review 4:L9), as TorrentSeedRules.DeleteCompletedText does on the desktop.
 */
export function deleteCompletedText(count: number, seeding = 0): string {
  const text = `Remove ${count} completed item(s) from the list? The downloaded files will stay on disk.`;
  if (seeding <= 0) return text;
  return seeding === 1
    ? `${text} 1 of them is a torrent that is seeding; removing it stops seeding.`
    : `${text} ${seeding} of them are torrents that are seeding; removing them stops seeding.`;
}

// ---- Categories ----

const builtIn: Record<string, string> = {};
for (const [category, extensions] of Object.entries({
  compressed: 'zip 7z rar gz gzip bz2 tar xz zst iso cab ace arj lzh',
  documents: 'pdf doc docx xls xlsx ppt pptx txt rtf epub odt ods odp csv',
  music: 'mp3 flac wav m4a aac ogg wma opus aif ra',
  programs: 'exe msi apk msix appx bat cmd deb rpm dmg pkg',
  video: 'mp4 mkv avi mov wmv webm m4v mpg mpeg flv ts 3gp rm rmvb',
})) for (const extension of extensions.split(' ')) builtIn[extension] = category;

/** FileCategories.For: the built-in category a file name belongs to. */
export function categoryForName(fileName: string | null | undefined): string {
  const dot = (fileName ?? '').lastIndexOf('.');
  if (dot < 0) return 'other';
  return builtIn[fileName!.slice(dot + 1).toLowerCase()] ?? 'other';
}

/** The category a download is listed under: the Host's choice, else by its file name and the custom categories. */
export function categoryOf(job: Pick<Job, 'categoryId' | 'fileName'>, custom: readonly CategoryDefinition[] = []): string {
  if (job.categoryId) return job.categoryId;
  const dot = job.fileName.lastIndexOf('.');
  const extension = dot >= 0 ? job.fileName.slice(dot + 1).toLowerCase() : '';
  const own = extension ? custom.find((category) => category.extensions.some((e) => e.replace(/^\./, '').toLowerCase() === extension)) : undefined;
  return own?.id ?? categoryForName(job.fileName);
}

export const builtInCategories: { id: string; label: string }[] = [
  { id: 'compressed', label: 'Compressed' },
  { id: 'documents', label: 'Documents' },
  { id: 'music', label: 'Music' },
  { id: 'programs', label: 'Programs' },
  { id: 'video', label: 'Video' },
];

// ---- Sorting: ListSort ----

export type SortKey = 'name' | 'queue' | 'size' | 'status' | 'extraction' | 'eta' | 'rate' | 'lastTry' | 'description' | 'added' | 'seeds' | 'peers' | 'up' | 'ratio';

export interface SortState {
  key: SortKey;
  ascending: boolean;
}

export const defaultSort: SortState = { key: 'added', ascending: false };

export const sortKeys: SortKey[] = ['name', 'queue', 'size', 'status', 'extraction', 'eta', 'rate', 'lastTry', 'description', 'added', 'seeds', 'peers', 'up', 'ratio'];

/** Words and times read from the first down. Sizes, speeds and dates read from the biggest or newest. */
export function startsAscending(key: SortKey): boolean {
  return key === 'name' || key === 'queue' || key === 'status' || key === 'extraction' || key === 'eta' || key === 'description';
}

/** A click on another column starts in its usual direction, and a second click reverses it. */
export function nextSort(current: SortState, clicked: SortKey): SortState {
  return current.key === clicked ? { key: clicked, ascending: !current.ascending } : { key: clicked, ascending: startsAscending(clicked) };
}

export function parseSort(text: string | null | undefined): SortState {
  try {
    const value = JSON.parse(text ?? '') as Partial<SortState> | null;
    if (value && sortKeys.includes(value.key as SortKey) && typeof value.ascending === 'boolean') return { key: value.key as SortKey, ascending: value.ascending };
  } catch {
    // Nothing saved, or something else wrote there: the default.
  }
  return defaultSort;
}

export function naturalCompare(a: string, b: string): number {
  return a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });
}

/** Rows with nothing to compare stay at the end in either direction, and equal rows keep their order. */
export function orderBy<T>(rows: readonly T[], key: (row: T) => number | string, ascending: boolean, isBlank?: (row: T) => boolean): T[] {
  const sign = ascending ? 1 : -1;
  return rows
    .map((row, index) => ({ row, index, blank: isBlank?.(row) === true }))
    .sort((a, b) => {
      if (a.blank !== b.blank) return a.blank ? 1 : -1;
      const x = key(a.row);
      const y = key(b.row);
      const c = typeof x === 'string' && typeof y === 'string' ? naturalCompare(x, y) : (x as number) - (y as number);
      return c !== 0 ? c * sign : a.index - b.index;
    })
    .map((entry) => entry.row);
}

/** The values a row is sorted by, and what it shows. */
export interface RowView {
  name: string;
  /** The Q column. */
  queue: string;
  queueTip: string;
  /** Bytes; 0 when unknown. */
  size: number;
  sizeText: string;
  /** The words in the Status column. */
  status: string;
  statusTone: Tone;
  statusTip: string;
  /** 0 to 100 when the bar shows under the status. */
  bar: number | null;
  /** Seconds; null when there is no estimate. */
  eta: number | null;
  etaText: string;
  rate: number;
  rateText: string;
  lastTry: number;
  lastTryText: string;
  description: string;
  extraction: string;
  extractionTone: Tone;
  extractionTip: string;
  created: number;
  /** What the row says beyond its name: the failure or the address. Searched, and shown as a tooltip. */
  detail: string;
  /** A torrent's Seeds, Peers, Up and Ratio cells, shown in the torrent views. */
  swarm?: SwarmCells;
}

/** Review 5:E1: the Status of a row that moves while the server can't be reached (JobViewModel.NotCurrentStatus). */
export const notCurrentStatus = 'Status unknown';

/**
 * Review 5:E1: while the server can't be reached the list is from before, so a row that moves (a download under way, a
 * seeding torrent; SpeedSample.Moves on the desktop) shows no speed, no time left and no state that may have ended.
 */
export function notCurrentView(v: RowView, jobs: readonly Pick<Job, 'state' | 'torrent'>[]): RowView {
  const moves = jobs.some((job) => activeStates.includes(job.state) || (job.state === 'completed' && job.torrent?.seeding === true));
  if (!moves) return v;
  return { ...v, status: notCurrentStatus, statusTone: 'neutral', rate: 0, rateText: '', eta: null, etaText: '' };
}

export function sortRows<T>(rows: readonly T[], view: (row: T) => RowView, sort: SortState): T[] {
  const a = sort.ascending;
  switch (sort.key) {
    case 'name': return orderBy(rows, (r) => view(r).name, a);
    case 'queue': return orderBy(rows, (r) => view(r).queue, a, (r) => view(r).queue.length === 0);
    case 'size': return orderBy(rows, (r) => view(r).size, a, (r) => view(r).size <= 0);
    case 'status': return orderBy(rows, (r) => view(r).status, a);
    case 'extraction': return orderBy(rows, (r) => view(r).extraction, a, (r) => view(r).extraction.length === 0);
    case 'eta': return orderBy(rows, (r) => view(r).eta ?? 0, a, (r) => view(r).eta === null);
    case 'rate': return orderBy(rows, (r) => view(r).rate, a, (r) => view(r).rate <= 0);
    case 'lastTry': return orderBy(rows, (r) => view(r).lastTry, a);
    case 'description': return orderBy(rows, (r) => view(r).description, a, (r) => view(r).description.length === 0);
    case 'seeds': return orderBy(rows, (r) => view(r).swarm?.seeds ?? -1, a, (r) => (view(r).swarm?.seeds ?? -1) < 0);
    case 'peers': return orderBy(rows, (r) => view(r).swarm?.peers ?? -1, a, (r) => (view(r).swarm?.peers ?? -1) < 0);
    case 'up': return orderBy(rows, (r) => view(r).swarm?.up ?? 0, a, (r) => (view(r).swarm?.up ?? 0) <= 0);
    case 'ratio': return orderBy(rows, (r) => view(r).swarm?.ratio ?? 0, a, (r) => !view(r).swarm);
    default: return orderBy(rows, (r) => view(r).created, a);
  }
}

// ---- The cells of each kind of row ----

const blankTone: Tone = 'neutral';

function ms(value: string | undefined): number {
  const t = value ? Date.parse(value) : NaN;
  return Number.isNaN(t) ? 0 : t;
}

/** Web addresses in a text, for a download whose own address stays hidden: the site only, as the desktop's UrlRedactor does. */
export function hideAddresses(text: string, job: Job): string {
  const hidden = job.addressHidden === true || isProviderFile(job);
  if (!hidden) return text;
  return text.replace(/[a-z][a-z0-9+.-]*:\/\/[^\s"'<>)]+/gi, (url) => (url.toLowerCase().startsWith('remote:') ? 'a provider link' : siteOnly(url) || 'a direct link'));
}

function shownAddress(job: Job): string {
  // A torrent's url is its magnet, trackers and all: only its info hash is shown (TorrentRules.ShortMagnet).
  if (job.torrent) return job.torrent.infoHash ? shortMagnet(job.torrent) : '';
  if (isProviderFile(job)) return '';
  return job.addressHidden ? (siteOnly(job.url) || 'A direct link') : job.url;
}

function siteOnly(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return '';
  }
}

/**
 * A download's cells, as JobViewModel.Update shows them. A torrent's swarm cells measure a seeding one against the
 * seeding goal of Options › Torrents (the Host's defaults when none is given).
 */
export function jobView(job: Job, set?: ArchiveSet, goal: SeedGoal = seedGoalOf(null), now = Date.now()): RowView {
  const total = job.totalBytes && job.totalBytes > 0 ? job.totalBytes : job.receivedBytes;
  // T2: a seeding torrent's rate is what it sends.
  const seeding = job.torrent?.seeding === true;
  const rate = seeding ? job.torrent?.uploadRate ?? 0 : job.bytesPerSecond ?? 0;
  const eta = timeSpanSeconds(job.estimatedRemaining);
  const p = percent(job) ?? 0;
  const note = jobNote(job);
  // A torrent says how many of its files it downloads, unless the user gave it words of their own.
  const own = job.torrent && !job.description?.trim() ? fileListWaitNote(job, now) ?? filesSummary(job.torrent)
    : job.description ?? (job.pageUrl && (job.addressHidden || isProviderFile(job)) ? siteOnly(job.pageUrl) : job.pageUrl);
  const barStates = ['downloading', 'connecting', 'retryWaiting', 'verifying', 'paused'];
  const updated = job.completedAt ?? job.updatedAt;
  const archive = set ? archiveRowView(set) : null;
  const swarm = swarmCells(job, goal, now);
  return {
    name: job.fileName || job.torrent?.name || (job.addressHidden || isProviderFile(job) || job.torrent ? '' : job.url),
    queue: queueText(job),
    queueTip: job.isQueued === false ? 'Not in a queue' : queueName(job),
    size: total > 0 ? total : 0,
    sizeText: total > 0 ? bytes(total) : '',
    status: statusWords(job),
    statusTone: seeding ? 'active' : toneOf(job.state),
    statusTip: failureText(job) || (note ?? ''),
    // A seeding torrent's bar is its way to the seeding goal.
    bar: swarm?.goal !== null && swarm?.goal !== undefined ? swarm.goal : barStates.includes(job.state) && p > 0 ? p : null,
    eta,
    etaText: timeLeft(eta),
    rate,
    rateText: seeding ? (rate > 0 ? `↑ ${speedText(rate)}` : '') : speedText(rate),
    lastTry: ms(updated),
    lastTryText: lastTryText(updated),
    description: describe(note, own),
    extraction: archive?.text ?? '',
    extractionTone: archive?.tone ?? blankTone,
    extractionTip: archive?.tooltip ?? '',
    created: ms(job.createdAt),
    detail: failureText(job) || shownAddress(job),
    swarm,
  };
}

/** A package's cells, as JobViewModel.UpdatePackage shows them. */
export function packageView(group: DownloadGroup, members: readonly Job[], sets: ArchiveSet[] = []): RowView {
  const summary = summarizeGroup(members);
  const archive = packageArchiveView(sets);
  const state = summary.state === 'active' ? 'downloading' : summary.state;
  const word = state === 'downloading' && members.some(isProviderFile) ? 'Downloading locally' : stateWord(state);
  const total = summary.totalBytes ?? summary.receivedBytes;
  const queue = packageQueueText(members);
  const latest = members.length === 0 ? 0 : Math.max(...members.map((m) => ms(m.updatedAt)));
  const first = members.length === 0 ? 0 : Math.min(...members.map((m) => ms(m.createdAt)));
  const progress = summary.progress === null ? 0 : Math.max(0, Math.min(100, summary.progress * 100));
  return {
    name: group.name,
    queue,
    queueTip: queue.length === 0 ? 'None' : queue === mixedQueueMarker ? 'Files are in different queues' : (members[0]?.queueName ?? `Queue ${members[0]?.queueId}`),
    size: total > 0 ? total : 0,
    sizeText: total > 0 ? bytes(total) : '',
    status: `${word} · ${summary.completedCount}/${summary.memberCount}`,
    statusTone: state === 'downloading' ? 'active' : state === 'completed' ? 'done' : state === 'failed' ? 'failed' : state === 'paused' || state === 'queued' ? 'waiting' : 'neutral',
    statusTip: '',
    bar: (summary.state === 'active' || summary.state === 'paused') && progress > 0 ? progress : null,
    eta: summary.estimatedRemaining,
    etaText: timeLeft(summary.estimatedRemaining),
    rate: summary.bytesPerSecond,
    rateText: speedText(summary.bytesPerSecond),
    lastTry: latest,
    lastTryText: members.length === 0 ? '' : lastTryText(latest),
    description: `${summary.memberCount} files`,
    extraction: archive?.text ?? '',
    extractionTone: archive?.tone ?? blankTone,
    extractionTip: archive?.tooltip ?? '',
    created: first,
    detail: `${summary.memberCount} files`,
  };
}

/** A provider's job on the list (JobViewModel.UpdateRemote): its stage in words, never a local figure. */
export function remoteView(remote: RemoteJob, now: Date = new Date()): RowView {
  const row = jobRow(remote, now);
  return {
    name: row.name,
    queue: '', queueTip: '', size: 0, sizeText: '',
    status: row.status,
    statusTone: row.tone,
    statusTip: row.tooltip,
    bar: null, eta: null, etaText: '', rate: 0, rateText: '',
    lastTry: ms(remote.updatedAt),
    lastTryText: lastTryText(remote.updatedAt),
    description: row.detail,
    extraction: '', extractionTone: blankTone, extractionTip: '',
    created: ms(remote.createdAt),
    detail: row.detail,
  };
}

/** An extraction record whose files are not on the list (JobViewModel.UpdateArchiveRow). The status stays blank: the Extraction column says the rest. */
export function archiveView(set: ArchiveSet): RowView {
  const archive = archiveRowView(set);
  return {
    name: set.name,
    queue: '', queueTip: '', size: 0, sizeText: '',
    status: '', statusTone: blankTone, statusTip: '',
    bar: null, eta: null, etaText: '', rate: 0, rateText: '',
    lastTry: ms(set.updatedAt),
    lastTryText: lastTryText(set.updatedAt),
    description: fullText(set),
    extraction: archive.text,
    extractionTone: archive.tone,
    extractionTip: archive.tooltip,
    created: ms(set.createdAt ?? set.updatedAt),
    detail: archive.tooltip,
  };
}

/**
 * The words searched for a row: the desktop's MatchesSearch plus the address and details, and the status tooltip, which
 * for a provider's row holds the Host's own longer words for its state.
 */
export function searchText(view: RowView, job?: Job): string {
  const parts = [view.name, view.status, view.extraction, view.description, view.detail, view.statusTip];
  if (job && !job.addressHidden && !isProviderFile(job) && !job.torrent) parts.push(job.url);
  return parts.join('\n').toLocaleLowerCase();
}

// ---- The status bar ----

export interface ListCounts {
  total: number;
  active: number;
  queued: number;
  retrying: number;
}

export function countJobs(jobs: readonly Job[]): ListCounts {
  let active = 0;
  let queued = 0;
  let retrying = 0;
  for (const job of jobs) {
    if (job.state === 'connecting' || job.state === 'downloading' || job.state === 'verifying') active++;
    else if (job.state === 'retryWaiting') retrying++;
    else if (job.state === 'queued') queued++;
  }
  return { total: jobs.length, active, queued, retrying };
}

export function statusBarText(counts: ListCounts, selected: number): string {
  let text = `${counts.total} download${counts.total === 1 ? '' : 's'} · ${counts.active} active · ${counts.queued} queued`;
  if (counts.retrying > 0) text += ` · ${counts.retrying} retrying`;
  if (selected > 1) text += `  (${selected} selected)`;
  return text;
}

// ---- Empty list ----

export const defaultEmptyText = 'No downloads in this view.\nUse Add URL or the browser extension to start one.';

export function emptyText(view: string, providerName: string): string {
  if (view === 'remote') return `Nothing is waiting for ${providerName}.\nItems show here until their files start downloading.`;
  if (view === 'extractions') return 'No archives yet.';
  if (view === 'torrents' || view.startsWith('torrents-')) return 'No torrents in this view.';
  return defaultEmptyText;
}

// ---- Keys: which row the arrows land on ----

/** Where the cursor goes for a navigation key; null when the key is not one. */
export function moveCursor(key: string, from: number, count: number, page = 10): number | null {
  if (count === 0) return null;
  const last = count - 1;
  const at = from < 0 ? -1 : from;
  switch (key) {
    case 'ArrowDown': return Math.min(last, at + 1);
    case 'ArrowUp': return Math.max(0, at < 0 ? 0 : at - 1);
    case 'Home': return 0;
    case 'End': return last;
    case 'PageDown': return Math.min(last, Math.max(0, at) + page);
    case 'PageUp': return Math.max(0, Math.max(0, at) - page);
    default: return null;
  }
}

/** A key typed in a field or a button belongs to it, not to the list. */
export function isTypingTarget(target: EventTarget | null): boolean {
  const element = target as { tagName?: string; isContentEditable?: boolean } | null;
  const tag = element?.tagName?.toLowerCase();
  return tag === 'input' || tag === 'textarea' || tag === 'select' || element?.isContentEditable === true;
}

// ---- Selecting ----

export interface ClickKeys {
  shift: boolean;
  ctrl: boolean;
}

/**
 * The selection after a click: a plain click replaces it, Ctrl toggles the row, Shift replaces it with the range from the
 * anchor, and Ctrl+Shift adds that range.
 */
export function selectAfterClick(selected: ReadonlySet<string>, ids: readonly string[], id: string, anchor: string | null, keys: ClickKeys): Set<string> {
  if (keys.shift && anchor) {
    const from = ids.indexOf(anchor);
    const to = ids.indexOf(id);
    if (from >= 0 && to >= 0) {
      const next = keys.ctrl ? new Set(selected) : new Set<string>();
      for (let i = Math.min(from, to); i <= Math.max(from, to); i++) next.add(ids[i]);
      return next;
    }
  }
  if (keys.ctrl) {
    const next = new Set(selected);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    return next;
  }
  return new Set([id]);
}

// ---- Provider rows ----

export type RemoteAction = 'none' | 'selectFiles' | 'approve' | 'enterLinkPassword' | 'retry' | 'checkNow';

/** RemoteRowActions.Primary: one row's next step. */
function primary(job: RemoteJob): RemoteAction {
  const actions = actionsOf(job);
  return actions.selectFiles ? 'selectFiles' : actions.approve ? 'approve' : actions.enterLinkPassword ? 'enterLinkPassword' : actions.retry ? 'retry' : actions.checkNow ? 'checkNow' : 'none';
}

/** What Enter does with these rows (RemoteRules.EnterFor): one row's next step, or for several the one thing they all wait for. */
export function remoteEnterAction(jobs: readonly RemoteJob[]): RemoteAction {
  if (jobs.length === 0) return 'none';
  if (jobs.length === 1) return primary(jobs[0]);
  if (jobs.every((job) => actionsOf(job).approve)) return 'approve';
  if (jobs.every((job) => actionsOf(job).selectFiles)) return 'selectFiles';
  return 'none';
}

/** "Download All Files" is for several rows at once; one row's own file dialog does the same for it. */
export function offersDownloadAll(jobs: readonly RemoteJob[]): boolean {
  return jobs.filter((job) => job.state === 'awaitingSelection').length >= 2;
}

/** An extraction can be taken off the list unless it is still running. */
export function archiveRemovable(set: ArchiveSet): boolean {
  return set.state !== 'extracting';
}

export function archiveRetryText(sets: readonly ArchiveSet[]): string {
  const retry = sets.filter(canRetry);
  return retry.length > 0 && retry.every((set) => set.state === 'completed') ? 'Extract Again…' : 'Retry Extraction';
}

/** The password item says what the plan's target is waiting for. */
export function archivePasswordText(needed: boolean): string {
  return needed ? 'Enter Archive Password…' : 'Archive Password…';
}

/** What a double-click or Enter does on a record's own row (ArchiveRules.Primary). The web can't open a folder, so it copies its path. */
export function archivePrimary(set: ArchiveSet): 'password' | 'copyFolder' | 'retry' | 'none' {
  if (set.needsPassword) return 'password';
  if (set.state === 'completed' && set.destination?.trim()) return 'copyFolder';
  return canRetry(set) ? 'retry' : 'none';
}

/** A set is on the list as a row of its own when none of its files are there. */
export function archiveHasOwnRow(set: ArchiveSet, jobs: Record<string, Job>, snapshotsSeen: boolean): boolean {
  return snapshotsSeen && !set.jobIds.some((id) => id in jobs);
}

// ---- Limits for the quick menus ----

export const downloadChoices = [1, 2, 3, 4, 5, 6, 8, 10, 12, 16];
export const connectionChoices = [1, 2, 4, 6, 8, 12, 16, 24, 32];
export const speedChoices = [1, 2, 5, 10, 20, 30, 50, 70, 100, 200, 500];
export const megabyte = 1024 * 1024;

export function speedIsPreset(bytesPerSecond: number): boolean {
  return bytesPerSecond === 0 || speedChoices.some((mb) => mb * megabyte === bytesPerSecond);
}

export function speedLimitLabel(bytesPerSecond: number): string {
  if (bytesPerSecond <= 0) return 'None';
  const mb = bytesPerSecond / megabyte;
  return `${Number.isInteger(mb) ? mb : mb.toFixed(1)} MB/s`;
}

// ---- The speed graph ----

/** What the graph draws: the newest ten minutes of the history, one sample a second. */
export const speedGraphWindow = 600;

/** The speed history: one sample a second, with gaps (null) while the server is not reachable. Keeps the Host's full hour. */
export class SpeedHistory {
  samples: (number | null)[] = [];
  /** The Unix second the newest sample was taken in. */
  private newestSecond = 0;
  constructor(readonly limit = 3600) {}

  add(rate: number | null, second = Math.floor(Date.now() / 1000)): void {
    this.samples.push(rate === null ? null : Math.max(0, rate));
    this.newestSecond = second;
    if (this.samples.length > this.limit) this.samples.splice(0, this.samples.length - this.limit);
  }

  /**
   * Takes the server's own figures (speed.history, one a second, the last the second under way) in place of what this page
   * sampled, which may hold gaps from a hidden tab or a lost connection. `start` is the time of the first figure. The
   * newest second this page sampled stays at the end for the second under way, but only when it is that second or later,
   * so no second shows twice; otherwise, or when this page has a gap there, the server's own last figure ends the line.
   */
  replace(serverSamples: readonly number[], start?: string): void {
    const clean = serverSamples.map((rate) => (Number.isFinite(rate) ? Math.max(0, rate) : 0));
    if (clean.length === 0) return;
    const finished = clean.slice(0, -1);
    const tail = this.samples[this.samples.length - 1] ?? null;
    const first = start === undefined ? Number.NaN : Date.parse(start) / 1000;
    const underWay = Math.floor(first) + clean.length - 1;
    const keepLocal = tail !== null && Number.isFinite(underWay) && this.newestSecond >= underWay;
    this.samples = [...finished, keepLocal ? tail : clean[clean.length - 1]];
    if (this.samples.length > this.limit) this.samples.splice(0, this.samples.length - this.limit);
  }

  /**
   * T8: a line of gaps as long as `length` (the download line after the server's hour replaced it), for when the server
   * sends no upload hour that lines up with it (an older server). Upload samples taken before the gap would otherwise sit
   * at the wrong seconds, since the two lines are laid out from the newest sample by index.
   */
  blank(length: number, second = Math.floor(Date.now() / 1000)): void {
    this.samples = new Array<number | null>(Math.max(0, Math.min(this.limit, length))).fill(null);
    this.newestSecond = second;
  }

  /** The newest `count` samples, which is all the graph has room for. */
  recent(count = speedGraphWindow): (number | null)[] {
    return this.samples.length > count ? this.samples.slice(-count) : this.samples;
  }

  /** The highest rate among the newest `count` samples. */
  peak(count = speedGraphWindow): number {
    let peak = 0;
    for (const sample of this.recent(count)) if (sample !== null && sample > peak) peak = sample;
    return peak;
  }
}

/** What is under the pointer on the speed graph; rate is null for a gap. */
export interface GraphHover {
  index: number;
  rate: number | null;
  /** Seconds before the newest sample. */
  age: number;
}

/**
 * The sample nearest the pointer. x is in the graph's own units (0 to width); the newest sample is at the right edge and
 * the line starts where the history does, as it is drawn. Null when the pointer is left of the first sample.
 */
export function hoverPoint(samples: readonly (number | null)[], x: number, width = 600, windowSize = speedGraphWindow): GraphHover | null {
  if (samples.length === 0 || !Number.isFinite(x)) return null;
  const step = width / (windowSize - 1);
  const offset = width - (samples.length - 1) * step;
  const index = Math.round((x - offset) / step);
  if (index < 0 && (x - offset) / step < -0.5) return null;
  const at = Math.min(samples.length - 1, Math.max(0, index));
  return { index: at, rate: samples[at], age: samples.length - 1 - at };
}

/** How long before the newest sample, as the desktop graph says it. */
export function agoText(seconds: number): string {
  if (seconds < 1.5) return 'now';
  if (seconds < 60) return `${Math.round(seconds)} s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ${String(Math.floor(seconds % 60)).padStart(2, '0')} s ago`;
  return `${Math.floor(seconds / 3600)} h ${String(Math.floor((seconds % 3600) / 60)).padStart(2, '0')} min ago`;
}

/**
 * The rate of one tick: the active downloads (retrying ones included) updated in the last ten seconds. A download that has
 * gone quiet counts as 0, not unknown; a gap (null) means there is no data.
 */
export function speedSample(jobs: readonly Job[], connected: boolean, ready: boolean, now: number, serverOffsetMs = 0): number | null {
  if (!connected || !ready) return null;
  let sum = 0;
  // updatedAt is the server's clock; `now` is this browser's, which can be ahead of it (audit 4 L22).
  const serverNow = now - serverOffsetMs;
  for (const job of jobs) {
    if (!activeStates.includes(job.state)) continue;
    if (serverNow - ms(job.updatedAt) > 10_000) continue;
    const r = job.bytesPerSecond ?? 0;
    if (Number.isFinite(r) && r > 0) sum += r;
  }
  return sum;
}

/**
 * How far this browser's clock is ahead of the server's (negative: behind), for speedSample: the smallest gap seen
 * between the time here and the newest updatedAt of any download. A download's update reaches the page within a moment of
 * being made, so the smallest gap is the clocks' difference plus that moment. 0 until anything was seen.
 */
export class ServerClock {
  private smallest: number | null = null;

  observe(jobs: readonly Job[], now: number): void {
    let newest = Number.NEGATIVE_INFINITY;
    for (const job of jobs) {
      const at = ms(job.updatedAt);
      if (Number.isFinite(at) && at > newest) newest = at;
    }
    if (!Number.isFinite(newest)) return;
    const gap = now - newest;
    if (this.smallest === null || gap < this.smallest) this.smallest = gap;
  }

  get offsetMs(): number {
    return this.smallest ?? 0;
  }
}

/** Whether any download transfers, or is about to: the speed graph samples only then (audit 4 M11). */
export function anyActive(jobs: readonly Job[]): boolean {
  return jobs.some((job) => activeStates.includes(job.state));
}

/**
 * The total the status bar and the speed graph's label show (U-WU-14): the rates the rows show, added up, so the three
 * places never disagree. Null while the server can't be reached.
 */
export function liveSpeed(jobs: readonly Job[], connected: boolean, ready: boolean): number | null {
  if (!connected || !ready) return null;
  let sum = 0;
  for (const job of jobs) {
    if (!activeStates.includes(job.state)) continue;
    const r = job.bytesPerSecond ?? 0;
    if (Number.isFinite(r) && r > 0) sum += r;
  }
  return sum;
}

// ---- T8: the torrents' upload on the speed graph (SpeedSample's rules) ----

function seedingNow(job: Pick<Job, 'state' | 'torrent'>): boolean {
  return job.torrent?.seeding === true && job.state === 'completed';
}

function cleanUpload(job: Pick<Job, 'torrent'>): number {
  const rate = job.torrent?.uploadRate ?? 0;
  return Number.isFinite(rate) && rate > 0 ? rate : 0;
}

/**
 * The torrents' upload in one tick. A downloading torrent's counts while its update is recent, as speedSample's; a seeding
 * one's as the server last said (seeding updates don't move updatedAt, and the server sets it to 0 when seeding stops).
 */
export function uploadSample(jobs: readonly Job[], connected: boolean, ready: boolean, now: number, serverOffsetMs = 0): number | null {
  if (!connected || !ready) return null;
  let sum = 0;
  const serverNow = now - serverOffsetMs;
  for (const job of jobs) {
    if (!job.torrent) continue;
    if (seedingNow(job)) sum += cleanUpload(job);
    else if (activeStates.includes(job.state) && serverNow - ms(job.updatedAt) <= 10_000) sum += cleanUpload(job);
  }
  return sum;
}

/** The upload line, its legend and its figure show while a torrent downloads or seeds (owner: hidden while none is active). */
export function torrentsRun(jobs: readonly Job[]): boolean {
  return jobs.some((job) => !!job.torrent && (activeStates.includes(job.state) || seedingNow(job)));
}

/** A seeding torrent sends something: the graph samples then too. One that seeds and sends nothing wakes nothing. */
export function anySending(jobs: readonly Job[]): boolean {
  return jobs.some((job) => seedingNow(job) && cleanUpload(job) >= 1);
}

/**
 * T8: the server's upload hour, when it lines up second for second with its download hour; null otherwise (an older
 * server leaves it out, and one of another length can't be placed). The graph then blanks its upload line instead.
 */
export function uploadHourOf(result: { bytesPerSecond: readonly number[]; uploadBytesPerSecond?: readonly number[] | null }): number[] | null {
  const upload = result.uploadBytesPerSecond;
  return Array.isArray(upload) && Array.isArray(result.bytesPerSecond) && upload.length === result.bytesPerSecond.length ? [...upload] : null;
}

/**
 * Whether the speed graph rests (audit 4 M11): connected, and nothing transfers (no download moves, no seeding torrent
 * sends). Then no timer runs. Not idle while the server can't be reached, so a gap is drawn.
 */
export function graphIdle(jobs: readonly Job[], connected: boolean, ready: boolean): boolean {
  return connected && ready && !anyActive(jobs) && !anySending(jobs);
}

/** The upload figure beside the graph's title: what the torrents send now, as the rows say. Null while the server can't be reached. */
export function liveUpload(jobs: readonly Job[], connected: boolean, ready: boolean): number | null {
  if (!connected || !ready) return null;
  let sum = 0;
  for (const job of jobs) if (job.torrent && (activeStates.includes(job.state) || seedingNow(job))) sum += cleanUpload(job);
  return sum;
}

/** The scale the graph's top line is drawn at: 64 KB/s doubled until it clears the peak. */
export function graphCeiling(peak: number): number {
  let ceiling = 64 * 1024;
  while (ceiling < peak * 1.1) ceiling *= 2;
  return ceiling;
}

// ---- The sidebar's provider entries (SyncRemoteEntryPoints) ----

export interface ProviderEntry {
  provider: string;
  name: string;
  waiting: number;
  showWaiting: boolean;
  showBrowse: boolean;
}

/** "Waiting for X" shows with a usable key or while rows are left; "My X" only when the account can browse its catalog. */
export function providerEntries(accounts: readonly RemoteAccount[], remoteJobs: readonly RemoteJob[]): ProviderEntry[] {
  return accounts.map((account) => {
    const waiting = remoteJobs.filter((job) => isRow(job) && sameProvider(job.provider, account.provider)).length;
    return {
      provider: account.provider,
      name: providerNameOf(account),
      waiting,
      showWaiting: showsWaiting(account, remoteJobs),
      showBrowse: canBrowse(account),
    };
  });
}
