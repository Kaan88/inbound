// The download details view and the package dialog, as the desktop window words and orders them
// (DownloadWindow, JobDetailsPanel, PackageWindow). Pure functions only, so the tests run them without a page.

import { bytes, duration, percent, siteOf, speed, stateWord, timeSpanSeconds } from './format';
import type { ConnectionInfo, Job, JobCompletionOptions, Segment } from './types';

// ---- Address -------------------------------------------------------------------------------------------------

/** A provider's file has no address of its own, only a reference to the account's item (JobAddress.HasAddress). */
export function hasOwnAddress(job: Pick<Job, 'isRemote' | 'url'>): boolean {
  return !job.isRemote && !job.url.toLowerCase().startsWith('remote://');
}

/** "Site" for an address that holds a key and stays on the server, otherwise "Address" (ShownAddress.Label). */
export function addressLabel(job: Pick<Job, 'addressHidden'>): 'Site' | 'Address' {
  return job.addressHidden ? 'Site' : 'Address';
}

/** The address as it may be shown or copied; empty when the job has none. */
export function shownAddress(job: Pick<Job, 'isRemote' | 'url' | 'addressHidden'>): string {
  if (!hasOwnAddress(job)) return '';
  if (!job.addressHidden) return job.url;
  return siteOf(job.url) || 'A direct link';
}

/** The refresh box starts empty for a hidden address, since what the list holds can't download anything. */
export function refreshStart(job: Pick<Job, 'url' | 'addressHidden'>): string {
  return job.addressHidden ? '' : job.url;
}

// ---- Rows ----------------------------------------------------------------------------------------------------

export function isMoving(state: string, totalBytes: number | null | undefined): boolean {
  return !(totalBytes && totalBytes > 0) && (state === 'connecting' || state === 'downloading' || state === 'verifying');
}

/** The main bar: null (moving) only while bytes are being worked on and the total is unknown, otherwise a number (D19). */
export function barValue(job: Pick<Job, 'state' | 'totalBytes' | 'progress' | 'receivedBytes'>): number | null {
  if (job.state === 'completed') return 100;
  if (isMoving(job.state, job.totalBytes)) return null;
  return percent(job) ?? 0;
}

/** The Host's note without any web address in it (UrlRedactor.HideAddresses). */
export function hideAddresses(text: string | null | undefined): string {
  return (text ?? '').replace(/\b[a-z][a-z0-9+.-]*:\/\/[^\s"'<>]+/gi, 'a web address').trim();
}

/** The source in the desktop's words (ToDisplaySource). */
export function displaySource(source: string | null | undefined): string {
  switch ((source ?? '').toLowerCase()) {
    case 'chromecontextmenu':
    case 'browsermenu':
      return 'Browser menu';
    case 'chromeauto':
    case 'chrome':
    case 'browser':
      return 'Browser';
    case 'clipboard':
      return 'Clipboard';
    case 'cli':
      return 'CLI';
    case 'batch':
      return 'Batch';
    default:
      return 'App';
  }
}

export type DetailRow = { label: string; value: string };

/** Address first, then the figures in the desktop's order; always shown, with "—" for no value (D3). */
export function detailRows(job: Job): DetailRow[] {
  const complete = job.state === 'completed';
  const p = job.totalBytes && job.totalBytes > 0 ? Math.max(0, Math.min(100, (job.receivedBytes * 100) / job.totalBytes)) : 0;
  const eta = timeSpanSeconds(job.estimatedRemaining);
  const rows: DetailRow[] = [];
  rows.push({ label: 'Status', value: stateWord(job.state) });
  rows.push({ label: 'File size', value: job.totalBytes !== undefined && job.totalBytes !== null && job.totalBytes >= 0 ? bytes(job.totalBytes) : 'Unknown' });
  rows.push({
    label: 'Downloaded',
    value: job.totalBytes && job.totalBytes > 0 ? `${bytes(job.receivedBytes)} (${(complete ? 100 : p).toFixed(2)}%)` : bytes(job.receivedBytes) || '0 B',
  });
  rows.push({ label: 'Transfer rate', value: job.bytesPerSecond && job.bytesPerSecond > 0 ? speed(job.bytesPerSecond) : '—' });
  rows.push({ label: 'Time left', value: complete ? 'Complete' : duration(eta) || '—' });
  rows.push({ label: 'Resume capability', value: job.resumeSupported === true ? 'Yes' : job.resumeSupported === false ? 'No' : 'Unknown' });
  return rows;
}

/** The completed summary: "Downloaded 1.2 MB (1,234,567 bytes)" (DownloadWindow.CompletedSize). */
export function completedSize(job: Pick<Job, 'receivedBytes'>): string {
  return `Downloaded ${bytes(job.receivedBytes) || '0 B'} (${job.receivedBytes.toLocaleString('en-US')} bytes)`;
}

/** The window's heading: "Download complete", or the state. */
export function heading(job: Pick<Job, 'state'>): string {
  return job.state === 'completed' ? 'Download complete' : stateWord(job.state);
}

const removedWords = 'Archive removed after extraction';

/** Says why a command that wants the file has nothing to act on (JobNotes.NoFile). */
export function noFileText(note: string | null | undefined): string {
  return `${note && note.trim() ? note.trim().replace(/\.+$/, '') : removedWords}, so there is no file to open.`;
}

/** The line under the figures: the failure when there is one, otherwise a hint about the state (D18). */
export function noticeText(job: Pick<Job, 'state' | 'failure' | 'fileRemovedAfterExtraction' | 'statusNote'>): string {
  if (job.failure?.message) return job.failure.message;
  if (job.state === 'completed') return job.fileRemovedAfterExtraction ? noFileText(job.statusNote) : 'The file has been saved. Choose an action below.';
  if (job.state === 'paused') return 'The download is paused. Resume it or close this window.';
  return 'Closing this window will not stop the download.';
}

/** "Why it failed" while failed, "Last error" for a failure kept while it retries or waits (D18). */
export function failureLabel(job: Pick<Job, 'state' | 'failure'>): string | null {
  if (!job.failure?.message) return null;
  return job.state === 'failed' ? 'Why it failed' : 'Last error';
}

// ---- Speed limiter (D1) --------------------------------------------------------------------------------------

export const speedLimitHelp = 'Applies to ordinary HTTP/HTTPS and FTP file downloads. HLS and DASH transfers do not support this control.';
export const speedLimitUnavailable = 'Speed limiting is available for unfinished HTTP/HTTPS and FTP file downloads. It is unavailable for HLS, DASH, and finished transfers.';

/** The box's text for a limit in bytes per second: KB/s with up to two decimals, empty when off. */
export function speedLimitText(limit: number | null | undefined): string {
  if (!limit || limit <= 0) return '';
  return String(Math.round((limit / 1024) * 100) / 100);
}

export type LimitResult = { ok: true; bytesPerSecond: number } | { ok: false; error: string };

/** What Apply sends: 0 when the limiter is off, else the KB/s typed as bytes per second (SpeedLimitInput checks). */
export function speedLimitFrom(enabled: boolean, text: string): LimitResult {
  if (!enabled) return { ok: true, bytesPerSecond: 0 };
  const cleaned = text.trim().replace(',', '.');
  const kilobytes = /^(\d+(\.\d*)?|\.\d+)$/.test(cleaned) ? Number(cleaned) : Number.NaN;
  if (!Number.isFinite(kilobytes) || kilobytes <= 0 || kilobytes > Number.MAX_SAFE_INTEGER / 1024) {
    return { ok: false, error: 'Enter a positive speed in KB/s, or turn the limiter off.' };
  }
  const limit = Math.round(kilobytes * 1024);
  if (limit < 1) return { ok: false, error: 'The speed limit must be at least one byte per second.' };
  return { ok: true, bytesPerSecond: limit };
}

// ---- Segments (D2) -------------------------------------------------------------------------------------------

export type SegmentRowView = { number: number; percent: number; downloaded: string; speed: string; info: string; segment: Segment };

/**
 * The saved segments with what the live connections know on top. A single-stream download has no saved ranges and
 * shows as one segment over the whole file. Checkpoints lag the live streams, so each segment takes the furthest
 * known point, and never moves backwards between refreshes (highWater keeps the furthest shown per segment start).
 */
export function mergeSegments(
  saved: Segment[],
  connections: ConnectionInfo[],
  job: Pick<Job, 'totalBytes' | 'receivedBytes'>,
  highWater: Map<number, number> = new Map(),
): Segment[] {
  const total = job.totalBytes ?? 0;
  let segments = saved.map((segment) => ({ ...segment }));
  if (segments.length === 0 && total > 0) {
    segments = [{ start: 0, end: total - 1, completed: job.receivedBytes, isComplete: job.receivedBytes >= total, fraction: job.receivedBytes / total }];
  }

  for (const segment of segments) {
    let live = 0;
    for (const connection of connections) {
      const from = connection.rangeStart;
      if (from === undefined || from === null || from < segment.start || from > segment.end) continue;
      live = Math.max(live, from - segment.start + connection.receivedBytes);
    }
    const length = Math.max(1, segment.end - segment.start + 1);
    const shown = highWater.get(segment.start) ?? 0;
    segment.completed = Math.min(length, Math.max(segment.completed, live, shown));
    highWater.set(segment.start, segment.completed);
    segment.isComplete = segment.isComplete || segment.completed >= length;
    segment.fraction = segment.completed / length;
  }

  return segments;
}

/** Where a segment's live connections are, matched by range start. */
function liveFor(segment: Segment, segments: Segment[], connections: ConnectionInfo[]): ConnectionInfo[] {
  return connections.filter((c) => (c.rangeStart !== undefined && c.rangeStart !== null ? c.rangeStart >= segment.start && c.rangeStart <= segment.end : segments.length === 1));
}

/** One sample a second: moves each segment's smoothed speed (0.75 old, 0.25 new), as the desktop does. Call from the poll only. */
export function updateRates(segments: Segment[], connections: ConnectionInfo[], rates: Map<number, number>): void {
  for (const segment of segments) {
    const raw = liveFor(segment, segments, connections).reduce((sum, c) => sum + Math.max(0, c.bytesPerSecond), 0);
    const previous = rates.get(segment.start);
    rates.set(segment.start, previous !== undefined && raw > 0 ? previous * 0.75 + raw * 0.25 : raw);
  }
}

/** The table's rows. Reads the smoothed speeds in `rates` and never changes them. */
export function segmentRows(segments: Segment[], connections: ConnectionInfo[], rates: Map<number, number> = new Map()): SegmentRowView[] {
  return segments.map((segment, index) => {
    const length = Math.max(1, segment.end - segment.start + 1);
    const live = liveFor(segment, segments, connections);
    const rate = rates.get(segment.start) ?? 0;
    return {
      number: index + 1,
      percent: Math.max(0, Math.min(100, (segment.completed * 100) / length)),
      downloaded: `${bytes(segment.completed) || '0 B'} / ${bytes(length)}`,
      speed: rate > 0 ? speed(rate) : '—',
      info: segment.isComplete ? 'Complete' : live.length > 0 ? 'Receiving data…' : 'Waiting',
      segment,
    };
  });
}

/** Segment details are read while the dialog is open and the download is not in a terminal state. */
export function pollsSegments(job: Pick<Job, 'isTerminal' | 'state'>): boolean {
  return !job.isTerminal && job.state !== 'completed' && job.state !== 'failed' && job.state !== 'cancelled';
}

// ---- Redownload (D12) ----------------------------------------------------------------------------------------

/** The desktop's refusals, in its words; null when the download can be sent again. */
export function redownloadRefusal(job: Pick<Job, 'state' | 'url' | 'isRemote' | 'addressHidden'>): string | null {
  if (['connecting', 'downloading', 'queued', 'paused', 'retryWaiting'].includes(job.state)) return 'Stop the current download before redownloading.';
  if (job.isRemote || job.url.toLowerCase().startsWith('remote://')) {
    return "This file comes from a provider account, so it can't be redownloaded from here. Add it again from the account.";
  }
  if (job.addressHidden || /[?&]token=\*+/i.test(job.url)) {
    return 'This address has its key hidden, so it can\'t be sent again. Paste the original link to download the file again.';
  }
  return null;
}

// ---- Move / Rename (D6) --------------------------------------------------------------------------------------

export function splitPath(path: string): { directory: string; name: string } {
  const cut = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
  return cut < 0 ? { directory: '', name: path } : { directory: path.slice(0, cut), name: path.slice(cut + 1) };
}

/** The length of the name without its extension, for the preselection like Explorer's rename. */
export function stemLength(name: string): number {
  const dot = name.lastIndexOf('.');
  return dot > 0 ? dot : name.length;
}

/** The new full path from the folder and name boxes, using the separator the old path used. */
export function joinPath(directory: string, name: string, like: string): string {
  const separator = like.includes('\\') && !like.includes('/') ? '\\' : like.includes('/') && !like.includes('\\') ? '/' : directory.includes('\\') ? '\\' : '/';
  const folder = directory.trim().replace(/[\\/]+$/, '');
  return `${folder || ''}${separator}${name.trim()}`;
}

/** null when nothing changed (the desktop sends nothing then). */
export function moveTarget(current: string, directory: string, name: string): string | null {
  if (!directory.trim() || !name.trim()) return null;
  const target = joinPath(directory, name, current);
  return target === current ? null : target;
}

// ---- Categories and Properties (D7) --------------------------------------------------------------------------

export const builtInCategories = ['compressed', 'documents', 'music', 'programs', 'video', 'other'] as const;

export type CategoryChoice = { id: string; label: string };

/** "Automatic (file extension)", the six built in, then the custom ones (id '' is automatic). */
export function categoryChoices(custom: { id: string; name: string }[] | undefined): CategoryChoice[] {
  return [
    { id: '', label: 'Automatic (file extension)' },
    ...builtInCategories.map((id) => ({ id, label: id[0].toUpperCase() + id.slice(1) })),
    ...(custom ?? []).map((c) => ({ id: c.id, label: c.name })),
  ];
}

/** The choice a job starts on. */
export function categoryOf(job: Pick<Job, 'categoryId' | 'categoryIsAutomatic'>): string {
  return job.categoryIsAutomatic === false ? job.categoryId ?? '' : '';
}

// ---- Completion options (D8, D9) -----------------------------------------------------------------------------

export type ProgramMode = 'global' | 'none' | 'run';

export const systemActions: { id: string; label: string }[] = [
  { id: 'none', label: 'No system action' },
  { id: 'exit', label: 'Exit application' },
  { id: 'shutdown', label: 'Shut down' },
  { id: 'restart', label: 'Restart' },
  { id: 'logoff', label: 'Log off' },
];

export function programModeOf(options: Pick<JobCompletionOptions, 'programPath'> | undefined): ProgramMode {
  const path = options?.programPath;
  return path === undefined || path === null ? 'global' : path === '' ? 'none' : 'run';
}

export function canForce(action: string): boolean {
  return action === 'shutdown' || action === 'restart' || action === 'logoff';
}

/** Builds the options to send, or the desktop's words for what is wrong. The Host checks that the program exists. */
export function completionFrom(
  mode: ProgramMode,
  programPath: string,
  openFile: boolean,
  systemAction: string,
  force: boolean,
  systemActionsAllowed: boolean,
): { ok: true; options: JobCompletionOptions } | { ok: false; error: string } {
  const path = mode === 'global' ? null : mode === 'none' ? '' : programPath.trim();
  if (mode === 'run' && !/^([a-zA-Z]:[\\/]|\\\\|\/).+\.exe$/i.test(path ?? '')) return { ok: false, error: 'Choose an absolute path to an existing .exe file.' };
  if (!systemActions.some((a) => a.id === systemAction)) return { ok: false, error: 'Choose a valid system action.' };
  if (systemAction !== 'none' && !systemActionsAllowed) return { ok: false, error: 'Enable system completion actions in Scheduler settings before choosing a system action.' };
  return { ok: true, options: { programPath: path, openFile, systemAction, forceSystemAction: canForce(systemAction) && force } };
}

/**
 * Keeps only the completion fields this session may set. A field it may not set is left out, so the Host keeps the
 * download's own value instead of refusing a save the user cannot fix.
 */
export function completionForSession(options: JobCompletionOptions, programAllowed: boolean, actionsAllowed: boolean): Partial<JobCompletionOptions> {
  const out: Partial<JobCompletionOptions> = {};
  if (programAllowed) out.programPath = options.programPath;
  if (actionsAllowed) {
    out.openFile = options.openFile;
    out.systemAction = options.systemAction;
    out.forceSystemAction = options.forceSystemAction;
  }
  return out;
}

// ---- Packages (D15, D16) -------------------------------------------------------------------------------------

export function isCancelled(job: Pick<Job, 'state'>): boolean {
  return job.state === 'cancelled';
}

export function pauseTargets(children: Job[]): Job[] {
  return children.filter((c) => c.canPause);
}

/**
 * Resume restarts paused and failed files but leaves cancelled ones cancelled, except to undo a package Cancel:
 * when that Cancel stopped every file in the package, Resume brings them all back (PackageRules.ResumeTargets).
 */
export function resumeTargets(packageId: string, children: Job[]): Job[] {
  const undoCancel = children.length > 0 && children.every((c) => isCancelled(c) && c.cancelledByPackageId === packageId);
  return children.filter((c) => c.canResume && (undoCancel || !isCancelled(c)));
}

export function cancelTargets(children: Job[]): Job[] {
  return children.filter((c) => !c.isTerminal && !isCancelled(c));
}

/** The file whose details the package dialog shows: the picked one while it is there, else the first working one. */
export function fileToShow(members: Job[], picked: string | null): string | null {
  if (picked && members.some((m) => m.id === picked)) return picked;
  return (members.find((m) => ['connecting', 'downloading', 'verifying'].includes(m.state)) ?? members.find((m) => !m.isTerminal) ?? members[0])?.id ?? null;
}

/** Files by name, plainly, so file10 sorts before file2 (PackageWindow, OrderBy FileName ignoring case). */
export function sortedByName(members: Job[]): Job[] {
  return [...members].sort((a, b) => (a.fileName || '').localeCompare(b.fileName || '', undefined, { sensitivity: 'base' }));
}

export interface PackageFigures {
  count: number;
  completed: number;
  summary: string;
  detail: string;
  /** 0 to 100, or null for the moving bar (sizes not all known while bytes flow). */
  bar: number | null;
}

/** The summary line, the figures line and the overall bar (PackageWindow.Render). */
export function packageFigures(members: Job[]): PackageFigures {
  const count = members.length;
  const total = members.reduce((sum, j) => sum + (j.totalBytes ?? 0), 0);
  const received = members.reduce((sum, j) => sum + (j.state === 'completed' && j.totalBytes && j.totalBytes > 0 ? j.totalBytes : j.receivedBytes), 0);
  const completed = members.filter((j) => j.state === 'completed').length;
  const rate = members.reduce((sum, j) => sum + (j.state === 'downloading' ? j.bytesPerSecond ?? 0 : 0), 0);
  const allKnown = count > 0 && members.every((j) => j.totalBytes && j.totalBytes > 0);
  const left = allKnown && rate > 0 ? duration((total - received) / rate) : '';
  const parts = [allKnown ? `${bytes(received)} of ${bytes(total)}` : bytes(received) || '0 B', rate > 0 ? speed(rate) : '', left ? `${left} left` : ''].filter(Boolean);
  const bar = count > 0 && completed === count ? 100 : allKnown ? Math.max(0, Math.min(100, (received * 100) / Math.max(1, total))) : !allKnown && completed < count && rate > 0 ? null : 0;
  return {
    count,
    completed,
    summary: count === 0 ? '' : `${completed} of ${count} files complete`,
    detail: count === 0 ? '' : parts.join('   ·   '),
    bar,
  };
}

const archiveSuffixes = ['.rar', '.7z', '.zip', '.tar', '.tar.gz', '.tgz', '.tar.bz2', '.tbz2', '.tbz', '.tar.xz', '.txz', '.tar.zst', '.tzst'];

export function looksLikeArchive(name: string | null | undefined): boolean {
  if (!name) return false;
  const lower = name.toLowerCase();
  return archiveSuffixes.some((suffix) => lower.length > suffix.length && lower.endsWith(suffix)) || /(\.r\d{2}|\.(?:7z|zip|rar|tar)\.\d{3}|\.z\d{2,3})$/i.test(name);
}

/** PackageArchiveSection.Show: an archive set, a password on record, no files yet, an unnamed file or one named like an archive. */
export function showsArchiveSection(memberNames: (string | null | undefined)[], archiveSets: number, hasPassword: boolean): boolean {
  return archiveSets > 0 || hasPassword || memberNames.length === 0 || memberNames.some((n) => !n || !n.trim() || looksLikeArchive(n));
}

export const packagePasswordHint =
  "For extracting this package's archives. It isn't for the download or the site it came from. You can set it now, before the files have downloaded.";

export const cancelPackageText =
  'Cancel the unfinished files in this package? Finished files and partial data are kept. Resume can undo this only if the cancel stopped every file in the package.';

/** The notice after a package action (PackageWindow.RunPackageActionAsync). */
export function packageActionNotice(result: { ok: boolean; error?: string; failedJobs?: Record<string, string>; leftCancelled?: number }): string {
  const failed = Object.keys(result.failedJobs ?? {}).length;
  if (!result.ok) return failed > 0 ? `${failed} file(s) could not be changed.` : result.error ?? 'The package could not be changed.';
  if ((result.leftCancelled ?? 0) > 0) return `${result.leftCancelled} cancelled file(s) stayed cancelled. Resume them one by one to bring them back.`;
  return '';
}

// ---- Notices (D10) -------------------------------------------------------------------------------------------

export type JobNotice = { text: string; tone: 'info' | 'error'; jobId: string };

/** A toast for a download that just finished or failed; null for every other change. */
export function noticeFor(previousState: string | undefined, job: Job, notifyOnCompletion: boolean): JobNotice | null {
  if (!previousState || previousState === job.state) return null;
  const name = job.fileName || 'A download';
  if (job.state === 'completed' && previousState !== 'completed' && notifyOnCompletion) {
    return { text: `Download complete: ${name}`, tone: 'info', jobId: job.id };
  }
  if (job.state === 'failed') {
    return { text: `Download failed: ${name}${job.failure?.message ? `. ${job.failure.message}` : ''}`, tone: 'error', jobId: job.id };
  }
  return null;
}

// ---- Statistics (D20) ----------------------------------------------------------------------------------------

export function exactBytes(value: number): string {
  return `${Math.round(value).toLocaleString('en-US')} bytes`;
}
