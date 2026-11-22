// The transfers between remote files and the computer Inbound runs on (SITES.11 R6b), in the desktop's words and
// decisions: Remote/SiteTransferRules.cs. A transfer row looks like a download row of the main list but has its own view
// model and offers only the commands a transfer supports. No UI, so the tests call it directly.
import { bytes, duration, speed } from './format';
import type { SiteTransfer } from './sites';

export type TransferTab = 'active' | 'failed' | 'done';

/** The tab a batch shows under: Running and waiting (running, paused), Failed, or Done (done, canceled). */
export function tabOf(t: Pick<SiteTransfer, 'state'>): TransferTab {
  return t.state === 'running' || t.state === 'paused' ? 'active' : t.state === 'failed' ? 'failed' : 'done';
}

/**
 * A running batch that has started (a file moving now, files done, or bytes moved) and is not waiting to try its server
 * again. A running batch that has not started is waiting.
 */
export function isStarted(t: SiteTransfer): boolean {
  return t.state === 'running' && !t.retryAt && ((t.filesRunning ?? 0) > 0 || t.filesDone + t.filesSkipped + t.filesFailed > 0 || t.bytesDone > 0);
}

const created = (t: SiteTransfer) => Date.parse(t.createdAt) || 0;

/** The tab's batches, newest first; Running and waiting puts what moves first, then what is paused, then what waits. */
export function inTab(transfers: readonly SiteTransfer[], tab: TransferTab): SiteTransfer[] {
  const shown = transfers.filter((t) => tabOf(t) === tab);
  const rank = (t: SiteTransfer) => (isStarted(t) ? 0 : t.state === 'paused' ? 1 : 2);
  return tab === 'active'
    ? shown.sort((a, b) => rank(a) - rank(b) || created(b) - created(a))
    : shown.sort((a, b) => created(b) - created(a));
}

export function tabLabel(transfers: readonly SiteTransfer[], tab: TransferTab): string {
  const count = transfers.filter((t) => tabOf(t) === tab).length;
  const name = tab === 'active' ? 'Running and waiting' : tab === 'failed' ? 'Failed' : 'Done';
  return `${name} (${count})`;
}

export const isDownload = (t: Pick<SiteTransfer, 'direction'>) => t.direction === 'download';
export const isBetween = (t: Pick<SiteTransfer, 'direction'>) => t.direction === 'between';

const number = (value: number) => value.toLocaleString('en-US');

/** "10 files"; "items" when empty folders are among them, since those are counted too. */
export function countText(t: SiteTransfer): string {
  return t.folders > 0 ? (t.files === 1 ? '1 item' : `${number(t.files)} items`) : t.files === 1 ? '1 file' : `${number(t.files)} files`;
}

/** The name column: "Download ubuntu.iso", "Download photos (1,204 files)", "Upload 2 files". */
export function title(t: SiteTransfer): string {
  const verb = isBetween(t) ? 'Copy' : isDownload(t) ? 'Download' : 'Upload';
  const picked = t.picked ?? [];
  const pickedCount = t.pickedCount ?? 0;
  if (pickedCount <= 0 || picked.length === 0) {
    return isBetween(t) ? `Copy from ${t.siteName} to ${t.targetSiteName ?? 'another remote'}` : isDownload(t) ? `Download from ${t.siteName}` : `Upload to ${t.siteName}`;
  }
  if (pickedCount === 1) return t.files > 1 || t.folders > 0 ? `${verb} ${picked[0]} (${countText(t)})` : `${verb} ${picked[0]}`;
  return t.folders === 0 && t.files === pickedCount ? `${verb} ${pickedCount} files` : `${verb} ${pickedCount} items`;
}

/** Where from and to, as the mockup draws it. */
export function route(t: SiteTransfer): string {
  if (isBetween(t)) return `${t.siteName} to ${t.targetSiteName ?? ''}`;
  return isDownload(t) ? `${t.siteName} to ${t.localFolder}` : `${t.localFolder} to ${t.siteName} ${t.remoteFolder}`;
}

export function fullRoute(t: SiteTransfer): string {
  if (isBetween(t)) return `${t.siteName} ${t.remoteFolder} to ${t.targetSiteName ?? ''} ${t.targetFolder ?? ''}`;
  return isDownload(t) ? `${t.siteName} ${t.remoteFolder} to ${t.localFolder}` : `${t.localFolder} to ${t.siteName} ${t.remoteFolder}`;
}

/** The phone card's "A to B" line: the remote and the computer Inbound runs on, in the batch's direction. */
export const routeLine = (t: SiteTransfer, hostName: string) => (isBetween(t) ? `${t.siteName} to ${t.targetSiteName ?? ''}` : isDownload(t) ? `${t.siteName} to ${hostName}` : `${hostName} to ${t.siteName}`);

/** How far a batch is, 0 to 100: the bytes done of those still to move, or the files done when no size is known. */
export function progress(t: SiteTransfer): number {
  const aside = t.bytesSetAside ?? 0;
  const whole = aside > 0 && aside < t.bytes ? t.bytes - aside : t.bytes;
  if (whole > 0) return Math.min(100, Math.max(0, (100 * t.bytesDone) / whole));
  return t.files > 0 ? Math.min(100, Math.max(0, (100 * (t.filesDone + t.filesSkipped)) / t.files)) : 0;
}

/** A batch just accepted whose files are not counted yet (a big folder): it says so rather than look idle. */
export const isPreparing = (t: SiteTransfer): boolean => t.state === 'running' && !t.retryAt && t.files === 0 && t.bytesDone === 0 && (t.filesRunning ?? 0) === 0;

export type StatusKind = 'active' | 'done' | 'failed' | 'waiting' | 'neutral';
export interface TransferStatus { text: string; kind: StatusKind; percent: number | null }

export function status(t: SiteTransfer): TransferStatus {
  switch (t.state) {
    case 'running': {
      if (isPreparing(t)) return { text: 'Preparing… (counting files)', kind: 'waiting', percent: null };
      if ((t.filesRunning ?? 0) === 0) return { text: 'Waiting', kind: 'waiting', percent: null };
      const percent = progress(t);
      return { text: `${isBetween(t) ? 'Copying' : isDownload(t) ? 'Downloading' : 'Uploading'} ${percent.toFixed(1)}%`, kind: 'active', percent };
    }
    case 'paused': return { text: t.waitsForSchedule ? 'Waits for a schedule' : 'Paused', kind: 'waiting', percent: null };
    case 'failed': return { text: 'Failed', kind: 'failed', percent: null };
    case 'canceled': return { text: 'Canceled', kind: 'neutral', percent: null };
    default:
      return (t.filesStopped ?? 0) > 0 && t.filesDone === 0 && t.filesSkipped === 0
        ? { text: 'Stopped', kind: 'neutral', percent: null }
        : { text: 'Completed', kind: 'done', percent: null };
  }
}

export const sizeText = (t: SiteTransfer) => (t.bytes > 0 ? bytes(t.bytes) : '');
export const timeLeft = (t: SiteTransfer) => (t.state === 'running' && t.secondsLeft != null ? duration(Math.max(1, t.secondsLeft)) : '');
export const rateText = (t: SiteTransfer) => (t.state === 'running' && (t.filesRunning ?? 0) > 0 ? speed(t.speed) : '');

const clock = (iso: string) => {
  const d = new Date(iso);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
};

/**
 * The description column. A failed batch says why; one waiting out a lost connection says for whom and until when;
 * others say where from and to, with what was skipped, stopped or failed after it.
 */
export function description(t: SiteTransfer): string {
  if (t.state === 'running' && t.retryAt) {
    const because = t.retryReason?.trim() ? ` ${t.retryReason.trim()}` : '';
    return `Waiting for ${t.siteName}, trying again at ${clock(t.retryAt)}.${because}`;
  }
  if (t.state === 'failed') {
    if (t.message) return t.message;
    if (t.firstFailedError) return t.files > 1 && t.firstFailedPath ? `${t.firstFailedPath}: ${t.firstFailedError}` : t.firstFailedError;
    return `${t.filesFailed} of ${countText(t)} failed`;
  }
  if (t.state === 'paused' && t.message) return t.message;
  const parts = [route(t) + (t.conflict === 'skip' ? ' · only new files' : '')];
  if (t.filesSkipped > 0) parts.push(`${t.filesSkipped} skipped (already there)`);
  if ((t.filesStopped ?? 0) > 0) parts.push(`${t.filesStopped} stopped`);
  if (t.filesFailed > 0) parts.push(`${t.filesFailed} failed`);
  return parts.join(' · ');
}

export function emptyMessage(tab: TransferTab): string {
  return tab === 'failed' ? 'No failed transfers.' : tab === 'done' ? 'No finished transfers.' : 'Nothing is moving. Select files in a remote and choose Download, or choose Upload.';
}

// ---- Commands a row offers: only what transfers support -----------------------------------------------------------------

/** Files that have not run yet, or wait for another try; a file the user stopped does not wait. */
export function waiting(t: SiteTransfer): number {
  return Math.max(0, t.files - t.filesDone - t.filesSkipped - t.filesFailed - (t.filesStopped ?? 0) - (t.filesRunning ?? 0));
}

export const canPause = (t: SiteTransfer) => t.state === 'running';
/** Paused, or stopped by a problem with files still waiting. */
export const canResume = (t: SiteTransfer) => t.state === 'paused' || (t.state === 'failed' && (waiting(t) > 0 || !!t.problem));
export const canRetry = (t: SiteTransfer) => t.state === 'failed' && t.filesFailed > 0;
export const canCancel = (t: SiteTransfer) => t.state === 'running' || t.state === 'paused' || canResume(t);
export const canClear = (t: SiteTransfer) => t.state === 'done' || t.state === 'failed' || t.state === 'canceled';

/** The commands the web has for a transfer: the desktop's, minus Open and Open folder (they need this PC's shell). */
export type TransferCommand = 'stop' | 'resume' | 'wait' | 'retry' | 'showOnRemote' | 'cancel' | 'remove';

export const commandLabel = (command: TransferCommand): string => ({
  stop: 'Stop', resume: 'Resume', wait: 'Wait for a schedule', retry: 'Retry', showOnRemote: 'Show on the remote', cancel: 'Cancel transfer', remove: 'Remove from list',
})[command];

/** The Host message a command sends. */
export const commandType = (command: TransferCommand): string | null => ({
  stop: 'sites.transfer.pause', resume: 'sites.transfer.resume', wait: 'sites.transfer.schedule', retry: 'sites.transfer.retry', showOnRemote: null,
  cancel: 'sites.transfer.cancel', remove: 'sites.transfer.clear',
})[command];

export function valid(command: TransferCommand, t: SiteTransfer): boolean {
  switch (command) {
    case 'stop': return canPause(t);
    case 'resume': return canResume(t);
    case 'wait': return t.state === 'paused' && !t.waitsForSchedule;
    case 'retry': return canRetry(t);
    case 'showOnRemote': return true;
    case 'cancel': return canCancel(t);
    case 'remove': return canClear(t);
  }
}

/** The menu of the selected rows, in groups: a command shows when it is valid for any selected row; "Show on the remote" for one. */
export function menu(selection: readonly SiteTransfer[]): TransferCommand[][] {
  if (!selection.length) return [];
  const layout: TransferCommand[][] = [['stop', 'resume', 'wait', 'retry'], ['showOnRemote'], ['cancel', 'remove']];
  return layout
    .map((group) => group.filter((command) => (command === 'showOnRemote' ? selection.length === 1 : selection.some((t) => valid(command, t)))))
    .filter((group) => group.length > 0);
}

/** Space: Stop on a running batch, Resume on a paused one, nothing otherwise. */
export function spaceCommand(selection: readonly SiteTransfer[]): TransferCommand | null {
  return selection.some(canPause) ? 'stop' : selection.some((t) => t.state === 'paused' || canResume(t)) ? 'resume' : null;
}

/** The finished and canceled batches "Clear done" removes, one id at a time (a failed one stays until retried or removed). */
export const clearDoneTargets = (transfers: readonly SiteTransfer[]) => transfers.filter((t) => t.state === 'done' || t.state === 'canceled').map((t) => t.id);

/** The bar's header button: "Pause transfers" when something runs, else "Resume transfers" when something is paused. */
export function pauseAllAction(transfers: readonly SiteTransfer[]): { label: string; type: string; ids: string[] } | null {
  const running = transfers.filter(canPause).map((t) => t.id);
  if (running.length) return { label: 'Pause transfers', type: 'sites.transfer.pause', ids: running };
  const paused = transfers.filter((t) => t.state === 'paused').map((t) => t.id);
  return paused.length ? { label: 'Resume transfers', type: 'sites.transfer.resume', ids: paused } : null;
}

export const retryFailedTargets = (transfers: readonly SiteTransfer[]) => transfers.filter(canRetry).map((t) => t.id);

// ---- The Transfers bar ---------------------------------------------------------------------------------------------------

/** "2 running · 2 waiting", or "Nothing is moving"; the bar's words. */
export function barSummary(transfers: readonly SiteTransfer[]): string {
  const active = transfers.filter((t) => t.state === 'running');
  const moving = active.filter(isStarted).length;
  const preparing = active.filter(isPreparing).length;
  const waits = active.length - moving - preparing;
  const paused = transfers.filter((t) => t.state === 'paused').length;
  const parts = [moving ? `${moving} running` : '', preparing ? `${preparing} preparing` : '', waits ? `${waits} waiting` : '', paused ? `${paused} paused` : ''].filter(Boolean);
  return parts.length ? parts.join(' · ') : 'Nothing is moving';
}

/** The speeds of the files moving, by direction, for the bar's arrows. */
export function barSpeeds(transfers: readonly SiteTransfer[]): { down: number; up: number } {
  let down = 0;
  let up = 0;
  for (const t of transfers) {
    if (t.state !== 'running' || (t.filesRunning ?? 0) === 0) continue;
    if (isDownload(t)) down += t.speed;
    else up += t.speed;
  }
  return { down, up };
}

/** The rate the speed graph and the status bar add to the downloads' and torrents': what the remote transfers move now. */
export function graphRates(transfers: readonly SiteTransfer[]): { down: number; up: number } {
  const { down, up } = barSpeeds(transfers);
  return { down: Number.isFinite(down) ? down : 0, up: Number.isFinite(up) ? up : 0 };
}

/** A sample of the graph plus the transfers' share; a gap (null, no connection) stays a gap. */
export const withRate = (sample: number | null, extra: number): number | null => (sample === null ? null : sample + extra);

export interface FinishNotice { id: string; text: string }

/** The batches that were running or paused in `before` and are done in `after` (not those first seen done), for a toast with Show. */
export function finishedNotices(before: readonly SiteTransfer[], after: readonly SiteTransfer[]): FinishNotice[] {
  const was = new Map(before.map((t) => [t.id, t.state]));
  return after
    .filter((t) => t.state === 'done' && ['running', 'paused'].includes(was.get(t.id) ?? ''))
    .filter((t) => (t.filesStopped ?? 0) === 0 || t.filesDone > 0 || t.filesSkipped > 0)
    .map((t) => ({ id: t.id, text: `${title(t)} finished.` }));
}

/** The toast when a download of remote files to the server is accepted. */
export const startedText = (what: string): string => `Downloading ${what} to the server`;

export const failedCount = (transfers: readonly SiteTransfer[]) => transfers.filter((t) => t.state === 'failed').length;

// ---- A phone (R7) --------------------------------------------------------------------------------------------------------

/** A phone shows one list, no tabs: what runs and waits, then what failed, then what is done (each with × to remove). */
export const phoneRows = (transfers: readonly SiteTransfer[]): SiteTransfer[] => [...inTab(transfers, 'active'), ...inTab(transfers, 'failed'), ...inTab(transfers, 'done')];

/** A phone's header in place of the tabs: "2 running · 1 waiting · 1 failed" (paused batches count as waiting). */
export function phoneHeader(transfers: readonly SiteTransfer[]): string {
  const moving = transfers.filter(isStarted).length;
  const waits = transfers.filter((t) => (t.state === 'running' && !isStarted(t)) || t.state === 'paused').length;
  const failed = failedCount(transfers);
  const parts = [moving ? `${moving} running` : '', waits ? `${waits} waiting` : '', failed ? `${failed} failed` : ''].filter(Boolean);
  return parts.length ? parts.join(' · ') : 'Nothing is moving';
}

/** A phone card's one button: Pause on a batch that moves, × on one that waits, failed or is done (cancel it, or take it off the list). */
export function phoneButton(t: SiteTransfer): TransferCommand | null {
  if (isStarted(t)) return 'stop';
  return canCancel(t) ? 'cancel' : canClear(t) ? 'remove' : null;
}

/** A phone card's long press: its actions as a bottom sheet, in this order, only those the batch allows. */
export function phoneSheet(t: SiteTransfer): TransferCommand[] {
  return (['resume', 'retry', 'stop', 'cancel', 'remove'] as const).filter((command) => valid(command, t));
}

/** A phone sheet's button words: the menu's, with a shorter Cancel. */
export const phoneSheetLabel = (command: TransferCommand): string => (command === 'cancel' ? 'Cancel' : commandLabel(command));
