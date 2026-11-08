// Numbers and words as the desktop app shows them (docs/design-guidelines.md): 1024-based KB/MB/GB, unknown values
// left blank, status text in the state's color.

import type { ArchiveSet, Job } from './types';

const units = ['B', 'KB', 'MB', 'GB', 'TB'];

export function bytes(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value) || value < 0) return '';
  let size = value;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit++;
  }
  if (unit === 0) return `${Math.round(size)} B`;
  return `${size >= 100 ? size.toFixed(0) : size >= 10 ? size.toFixed(1) : size.toFixed(2)} ${units[unit]}`;
}

export function speed(value: number | null | undefined): string {
  if (!value || value <= 0) return '';
  return `${bytes(value)}/s`;
}

/** A .NET TimeSpan as JSON ("hh:mm:ss", "d.hh:mm:ss.fffffff") in seconds; null when it isn't one. */
export function timeSpanSeconds(value: string | null | undefined): number | null {
  if (!value) return null;
  const match = /^(-)?(?:(\d+)\.)?(\d+):(\d+):(\d+)(?:\.(\d+))?$/.exec(value);
  if (!match) return null;
  const [, negative, days, hours, minutes, seconds, fraction] = match;
  const total = Number(days ?? 0) * 86400 + Number(hours) * 3600 + Number(minutes) * 60 + Number(seconds) +
    (fraction ? Number(`0.${fraction}`) : 0);
  return negative ? -total : total;
}

export function duration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds) || seconds < 0) return '';
  const s = Math.round(seconds);
  if (s < 60) return `${s} s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} min ${String(s % 60).padStart(2, '0')} s`;
  const h = Math.floor(m / 60);
  if (h < 48) return `${h} h ${String(m % 60).padStart(2, '0')} min`;
  return `${Math.floor(h / 24)} days ${h % 24} h`;
}

export function dateTime(value: string | null | undefined): string {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime()) || date.getFullYear() < 2000) return '';
  return date.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

export function stateWord(state: string): string {
  switch (state) {
    case 'queued': return 'Queued';
    case 'connecting': return 'Connecting';
    case 'downloading': return 'Downloading';
    case 'retryWaiting': return 'Retrying';
    case 'verifying': return 'Verifying';
    case 'paused': return 'Paused';
    case 'completed': return 'Completed';
    case 'failed': return 'Failed';
    case 'cancelled': return 'Cancelled';
    default: return state;
  }
}

export type Tone = 'active' | 'done' | 'failed' | 'waiting' | 'neutral';

export function tone(state: string): Tone {
  switch (state) {
    case 'downloading':
    case 'connecting':
    case 'verifying':
      return 'active';
    case 'completed':
      return 'done';
    case 'failed':
      return 'failed';
    case 'paused':
    case 'queued':
    case 'retryWaiting':
      return 'waiting';
    default:
      return 'neutral';
  }
}

export function percent(job: Pick<Job, 'progress' | 'totalBytes' | 'receivedBytes'>): number | null {
  if (job.progress !== undefined && job.progress !== null && Number.isFinite(job.progress)) {
    // The Host sends a fraction, 0 to 1.
    return Math.max(0, Math.min(100, job.progress * 100));
  }
  if (job.totalBytes && job.totalBytes > 0) return Math.max(0, Math.min(100, (job.receivedBytes / job.totalBytes) * 100));
  return null;
}

/** The status column's words: the state, with what it waits for. */
export function statusText(job: Job): string {
  const word = stateWord(job.state);
  const p = percent(job);
  if ((job.state === 'downloading' || job.state === 'paused') && p !== null && p > 0) return `${word} ${p.toFixed(p >= 10 ? 0 : 1)}%`;
  if (job.state === 'downloading' && job.waitingForHost && !job.activeConnections) return `Waiting for ${job.waitingForHost}`;
  return word;
}

/** Shows the bar under the status, as in the desktop list: while active or paused with progress above 0. */
export function showsBar(job: Job): boolean {
  const p = percent(job);
  return (tone(job.state) === 'active' || job.state === 'paused') && p !== null && p > 0;
}

export function siteOf(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return '';
  }
}

export function archiveTone(set: ArchiveSet): Tone {
  switch (set.state) {
    case 'extracting':
      return 'active';
    case 'completed':
      return 'done';
    case 'failed':
      return set.failureKind === 'cancelled' ? 'neutral' : 'failed';
    case 'waiting':
      return 'waiting';
    default:
      return 'neutral';
  }
}

export function archiveShort(set: ArchiveSet): string {
  switch (set.state) {
    case 'downloading': return 'Downloading parts';
    case 'downloaded': return 'Not extracted';
    case 'waiting': return set.needsPassword && !set.hasPassword ? 'Needs password' : 'Waiting';
    case 'extracting': return set.percent !== undefined && set.percent !== null ? `Extracting ${set.percent}%` : 'Extracting';
    case 'completed': return 'Extracted';
    case 'failed':
      if (set.needsPassword) return set.failureKind === 'wrongPassword' ? 'Wrong password' : 'Needs password';
      return set.failureKind === 'cancelled' ? 'Cancelled' : 'Extraction failed';
    default: return set.stateText;
  }
}
