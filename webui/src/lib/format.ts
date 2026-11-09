// Numbers and words as the desktop app shows them (docs/design-guidelines.md): 1024-based KB/MB/GB, unknown values
// left blank, status text in the state's color.

import type { Job } from './types';

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

/**
 * Time left, in the desktop list's words everywhere (DurationText.TimeLeft): "1h 5m", "1m 45s", "45s"; blank when there
 * is no estimate. The figures are whole units, cut and not rounded, as a .NET TimeSpan's are.
 */
export function duration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds) || seconds <= 0) return '';
  const total = Math.floor(seconds);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const rest = total % 60;
  if (hours >= 1) return `${hours}h ${minutes}m`;
  return minutes >= 1 ? `${minutes}m ${rest}s` : `${Math.max(1, rest)}s`;
}

const monthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

function validDate(value: string | number | Date | null | undefined): Date | null {
  if (value === null || value === undefined || value === '') return null;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) || date.getFullYear() < 2000 ? null : date;
}

const two = (n: number) => String(n).padStart(2, '0');

/**
 * The one way a moment is written, everywhere: the list's Last Try Date, details, Statistics, schedules (U-WU-21). It is
 * the desktop list's own format, "Nov 09 05:19:47 2026", in this browser's time zone.
 */
export function dateTime(value: string | number | Date | null | undefined): string {
  const date = validDate(value);
  if (!date) return '';
  return `${monthNames[date.getMonth()]} ${two(date.getDate())} ${two(date.getHours())}:${two(date.getMinutes())}:${two(date.getSeconds())} ${date.getFullYear()}`;
}

/** A day alone, in the same words: "Nov 09 2026". */
export function dateOnly(value: string | number | Date | null | undefined): string {
  const date = validDate(value);
  return date ? `${monthNames[date.getMonth()]} ${two(date.getDate())} ${date.getFullYear()}` : '';
}

/** A time of day alone: "05:19". */
export function clockTime(value: number | Date): string {
  const date = new Date(value);
  return `${two(date.getHours())}:${two(date.getMinutes())}`;
}

export function stateWord(state: string): string {
  switch (state) {
    case 'queued': return 'Queued';
    case 'connecting': return 'Connecting';
    case 'downloading': return 'Downloading';
    case 'retryWaiting':
    case 'retrying': return 'Retrying';
    case 'verifying': return 'Verifying';
    case 'paused': return 'Paused';
    case 'completed': return 'Completed';
    case 'failed': return 'Failed';
    case 'cancelled': return 'Cancelled';
    case 'muxing': return 'Merging';
    case 'scheduled': return 'Scheduled';
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
    case 'retrying':
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

