// Queue and schedule rules for the Queues screen, kept out of the component so they can be tested. The wording and
// limits are the desktop Scheduler window's (SchedulerWindow.cs, SchedulerRunOptionsEditor.cs, MainWindow queue editor).
import type { Job, Queue, Schedule } from './types';

export const DAY_NAMES = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'] as const;
export const ALL_DAYS = DAY_NAMES.join(',');
export const MAX_QUOTA_MB = 10_485_760;
export const MAX_QUOTA_HOURS = 8_760;

/** A download belongs to a queue unless the host says isQueued is false (older servers leave the flag out). */
export function isMember(job: Pick<Job, 'isQueued'>): boolean {
  return job.isQueued !== false;
}

export function queueMembers(jobs: Iterable<Job>, queueId: number): Job[] {
  return [...jobs].filter((job) => isMember(job) && job.queueId === queueId);
}

/** The order the host runs a queue in: higher priority first, then the saved order, then the age. */
export function sortQueueFiles(jobs: Job[], queue: Pick<Queue, 'jobOrder'>): Job[] {
  const order = queue.jobOrder ?? [];
  const position = (id: string) => {
    const index = order.indexOf(id);
    return index < 0 ? Number.MAX_SAFE_INTEGER : index;
  };
  return [...jobs].sort((a, b) =>
    (b.priority ?? 0) - (a.priority ?? 0) || position(a.id) - position(b.id) || a.createdAt.localeCompare(b.createdAt));
}

/** Move up/down work only between downloads of the same priority. */
export function canMoveFile(files: Pick<Job, 'priority'>[], index: number, delta: -1 | 1): boolean {
  const other = index + delta;
  if (index < 0 || index >= files.length || other < 0 || other >= files.length) return false;
  return (files[index].priority ?? 0) === (files[other].priority ?? 0);
}

export function moveFile<T extends Pick<Job, 'priority'>>(files: T[], index: number, delta: -1 | 1): T[] {
  if (!canMoveFile(files, index, delta)) return files;
  const copy = [...files];
  [copy[index], copy[index + delta]] = [copy[index + delta], copy[index]];
  return copy;
}

export function priorityText(priority: number | undefined): string {
  const value = priority ?? 0;
  return value > 0 ? 'High' : value < 0 ? 'Low' : 'Normal';
}

/** Queues in the host's order. */
export function orderedQueues(queues: Queue[]): Queue[] {
  return [...queues].sort((a, b) => a.order - b.order || a.id - b.id);
}

// ---- queue editor ----------------------------------------------------------------------------------------------

export interface QueueForm {
  name: string;
  max: string;
  quotaEnabled: boolean;
  quotaMb: string;
  quotaHours: string;
}

export function mbText(bytes: number): string {
  const mb = bytes / 1024 / 1024;
  return String(Math.round(mb * 100) / 100);
}

export function queueForm(queue: Queue | null, queueCount: number): QueueForm {
  return {
    name: queue?.name ?? `Queue ${queueCount + 1}`,
    max: String(queue?.maxConcurrentDownloads ?? 1),
    quotaEnabled: queue?.quotaEnabled ?? false,
    quotaMb: mbText(queue?.quotaBytes || 200 * 1024 * 1024),
    quotaHours: String(queue?.quotaWindowHours || 5),
  };
}

export type QueuePolicy = { name: string; maxConcurrentDownloads: number; quotaEnabled: boolean; quotaBytes?: number; quotaWindowHours?: number };

function digits(text: string): boolean {
  return /^\d+$/.test(text.trim());
}

/** The desktop's checks and messages. The quota numbers are only read, and only sent, while the limit is on. */
export function checkQueueForm(form: QueueForm): { error: string } | { policy: QueuePolicy } {
  const name = form.name.trim();
  if (!name) return { error: 'Enter a queue name.' };
  if (name.length > 80) return { error: 'The queue name can have 80 characters at most.' };
  const max = digits(form.max) ? Number(form.max.trim()) : NaN;
  if (!Number.isInteger(max) || max < 1 || max > 16) return { error: 'Simultaneous downloads must be between 1 and 16.' };
  if (!form.quotaEnabled) return { policy: { name, maxConcurrentDownloads: max, quotaEnabled: false } };
  const mb = /^\d+([.,]\d+)?$/.test(form.quotaMb.trim()) ? Number(form.quotaMb.trim().replace(',', '.')) : NaN;
  if (!Number.isFinite(mb) || mb <= 0 || mb > MAX_QUOTA_MB) return { error: 'The quota must be a positive number of MB, up to 10,485,760.' };
  const quotaBytes = Math.round(mb * 1024 * 1024);
  if (quotaBytes < 1) return { error: 'The quota must be large enough to represent at least one byte.' };
  const hours = digits(form.quotaHours) ? Number(form.quotaHours.trim()) : NaN;
  if (!Number.isInteger(hours) || hours < 1 || hours > MAX_QUOTA_HOURS) return { error: 'The quota window must be between 1 and 8,760 hours.' };
  return { policy: { name, maxConcurrentDownloads: max, quotaEnabled: true, quotaBytes, quotaWindowHours: hours } };
}

// ---- delete a queue --------------------------------------------------------------------------------------------

/** The queue the downloads and schedules move to by default: the default queue, else the first other one. */
export function defaultMoveTarget(queues: Queue[], deleting: number, defaultQueueId: number): number | null {
  const others = orderedQueues(queues).filter((q) => q.id !== deleting);
  return (others.find((q) => q.id === defaultQueueId) ?? others[0])?.id ?? null;
}

// ---- schedules -------------------------------------------------------------------------------------------------

/** An empty selection means every day, as in the host. */
export function normalizeDays(days: string | undefined): string {
  const picked = new Set((days ?? '').split(/[,;]/).map((d) => d.trim().slice(0, 3).toLowerCase()).filter(Boolean));
  const list = DAY_NAMES.filter((d) => picked.has(d.toLowerCase()));
  return list.length === 0 ? ALL_DAYS : list.join(',');
}

export function selectedDays(days: string | undefined): Set<string> {
  return new Set(normalizeDays(days).split(','));
}

/** The ticked days as the form shows them: unlike selectedDays, an empty list shows none ticked (the desktop's day buttons). */
export function tickedDays(days: string | undefined): Set<string> {
  const picked = new Set((days ?? '').split(/[,;]/).map((d) => d.trim().slice(0, 3).toLowerCase()).filter(Boolean));
  return new Set(DAY_NAMES.filter((d) => picked.has(d.toLowerCase())));
}

/** Ticks or unticks one day. Unticking the last one gives an empty list (none shown ticked); it is saved as every day. */
export function toggleDay(days: string, day: string): string {
  const set = tickedDays(days);
  if (set.has(day)) set.delete(day);
  else set.add(day);
  return DAY_NAMES.filter((d) => set.has(d)).join(',');
}

export interface RunOptions {
  startOnHostStartup: boolean;
  timedStartEnabled: boolean;
  repeatEnabled: boolean;
  repeatMinutes: string;
  retryEnabled: boolean;
  retryLimit: string;
}

export interface ScheduleDraft {
  schedule: Schedule;
  /** The run options and retry limit, as typed. */
  options: RunOptions;
  /** A schedule that isn't saved yet. */
  isNew: boolean;
  /** The saved copy this draft started from, to notice a change made elsewhere. */
  baseline: Schedule | null;
  /** The queue's retry limit when the form opened (null = unlimited); saved only when the form changes it. */
  retryInitial: number | null;
}

function runOptions(schedule: Schedule, retryLimit: number | undefined): RunOptions {
  return {
    startOnHostStartup: schedule.startOnHostStartup,
    timedStartEnabled: schedule.timedStartEnabled,
    repeatEnabled: schedule.repeatEveryMinutes != null,
    repeatMinutes: String(schedule.repeatEveryMinutes ?? 60),
    retryEnabled: retryLimit != null,
    retryLimit: String(retryLimit ?? 3),
  };
}

export function newScheduleDraft(queueId: number, id: string, retryLimit: number | undefined): ScheduleDraft {
  const schedule: Schedule = {
    id, name: 'New schedule', queueId, enabled: true, kind: 'daily', days: ALL_DAYS, startTime: '09:00',
    timedStartEnabled: true, startOnHostStartup: false,
    openFileWhenDone: false, exitWhenDone: false, shutdownWhenDone: false, shutdownMode: 'shutdown', forceShutdown: false,
  };
  return { schedule, options: runOptions(schedule, retryLimit), isNew: true, baseline: null, retryInitial: retryLimit ?? null };
}

export function editScheduleDraft(schedule: Schedule, retryLimit: number | undefined): ScheduleDraft {
  return {
    schedule: { ...(JSON.parse(JSON.stringify(schedule)) as Schedule), days: normalizeDays(schedule.days) },
    options: runOptions(schedule, retryLimit),
    isNew: false,
    baseline: JSON.parse(JSON.stringify(schedule)) as Schedule,
    retryInitial: retryLimit ?? null,
  };
}

const TIME = /^([01]\d|2[0-3]):[0-5]\d$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

function validDate(text: string): boolean {
  if (!DATE.test(text)) return false;
  const [y, m, d] = text.split('-').map(Number);
  const date = new Date(Date.UTC(y, m - 1, d));
  return date.getUTCFullYear() === y && date.getUTCMonth() === m - 1 && date.getUTCDate() === d;
}

function wholeNumber(text: string, min: number, max: number): number | null {
  if (!digits(text)) return null;
  const value = Number(text.trim());
  return Number.isInteger(value) && value >= min && value <= max ? value : null;
}

/** The desktop's checks, in its order, with its messages. Returns the schedule to save and the queue's retry limit (null = unlimited). */
export function checkScheduleDraft(
  draft: ScheduleDraft,
  queueIds: number[],
): { error: string } | { schedule: Schedule; retryLimit: number | null } {
  const s = draft.schedule;
  const o = draft.options;
  if (!queueIds.includes(s.queueId)) return { error: 'Choose an existing target queue.' };
  const name = (s.name ?? '').trim();
  if (name.length < 1 || name.length > 80) return { error: 'Enter a schedule name of 1–80 characters.' };
  const startTime = (s.startTime ?? '').trim();
  if (!TIME.test(startTime)) return { error: 'Start time must use HH:mm.' };
  const stopTime = (s.stopTime ?? '').trim();
  if (stopTime && !TIME.test(stopTime)) return { error: 'Stop time must use HH:mm.' };
  const date = (s.startDate ?? '').trim();
  if (date && !validDate(date)) return { error: 'One-time date must use yyyy-MM-dd.' };
  if (!o.startOnHostStartup && !o.timedStartEnabled) return { error: 'Enable startup or timed start so this queue has a way to run.' };
  if (o.repeatEnabled && !o.timedStartEnabled) return { error: 'Periodic synchronization requires timed start.' };
  const repeat = o.repeatEnabled ? wholeNumber(o.repeatMinutes, 1, 1440) : null;
  if (o.repeatEnabled && repeat === null) return { error: 'Set the synchronization interval to a whole number from 1 to 1440 minutes.' };
  const retry = o.retryEnabled ? wholeNumber(o.retryLimit, 0, 100) : null;
  if (o.retryEnabled && retry === null) return { error: 'Set the retry limit to a whole number from 0 to 100.' };
  const saved: Schedule = {
    ...s,
    name,
    kind: date ? 'oneTime' : 'daily',
    days: normalizeDays(s.days),
    startTime,
    startOnHostStartup: o.startOnHostStartup,
    timedStartEnabled: o.timedStartEnabled,
  };
  if (date) saved.startDate = date; else delete saved.startDate;
  if (stopTime) saved.stopTime = stopTime; else delete saved.stopTime;
  if (repeat !== null) saved.repeatEveryMinutes = repeat; else delete saved.repeatEveryMinutes;
  return { schedule: saved, retryLimit: retry };
}

/** Before a schedule save writes a queue's retry limit: has another client changed that limit since the form opened? */
export function retryConflict(queue: Pick<Queue, 'name' | 'retryLimit'> | undefined, draft: Pick<ScheduleDraft, 'retryInitial'>): { error: string } | null {
  if (!queue) return null;
  if ((queue.retryLimit ?? null) === draft.retryInitial) return null;
  return { error: `Queue '${queue.name}' retry policy changed elsewhere; its unsaved queue edits were not applied.` };
}

/** Everything the user can edit on a schedule; the run history is the host's. */
export function sameScheduleConfig(a: Schedule, b: Schedule): boolean {
  const keys: (keyof Schedule)[] = ['name', 'queueId', 'enabled', 'kind', 'startDate', 'days', 'startTime', 'stopTime', 'startOnHostStartup',
    'timedStartEnabled', 'repeatEveryMinutes', 'openFileWhenDone', 'exitWhenDone', 'shutdownWhenDone', 'shutdownMode', 'forceShutdown'];
  return keys.every((key) => (a[key] ?? null) === (b[key] ?? null));
}

export type ScheduleConflict = { error: string } | null;

/** Before saving an edit or removing a schedule: has someone else changed or removed it since this draft began? */
export function scheduleConflict(current: Schedule[], draft: Pick<ScheduleDraft, 'isNew' | 'baseline' | 'schedule'>, removing = false): ScheduleConflict {
  if (draft.isNew) return current.some((s) => s.id === draft.schedule.id) ? { error: 'This schedule already exists.' } : null;
  const name = draft.baseline?.name ?? draft.schedule.name;
  const found = current.find((s) => s.id === draft.schedule.id);
  if (!found) return { error: removing ? `Schedule '${name}' was already removed elsewhere.` : `Schedule '${name}' was removed elsewhere; its unsaved edit was not applied.` };
  if (draft.baseline && !sameScheduleConfig(found, draft.baseline)) {
    return { error: removing ? `Schedule '${name}' changed elsewhere; it was not removed.` : `Schedule '${name}' changed elsewhere; its unsaved edit was not applied.` };
  }
  return null;
}

/** "every day", the picked days, or the one-time date. */
export function whenText(schedule: Schedule): string {
  if (schedule.kind === 'oneTime' && schedule.startDate) return `once on ${schedule.startDate}`;
  const days = normalizeDays(schedule.days);
  return days === ALL_DAYS ? 'every day' : days.replaceAll(',', ', ');
}

export function scheduleSummary(schedule: Schedule): string {
  const parts: string[] = [];
  parts.push(schedule.timedStartEnabled ? `start at ${schedule.startTime}` : 'no timed start');
  if (schedule.stopTime) parts.push(`stop at ${schedule.stopTime}`);
  parts.push(whenText(schedule));
  if (schedule.repeatEveryMinutes) parts.push(`synchronize every ${schedule.repeatEveryMinutes} min`);
  if (schedule.startOnHostStartup) parts.push('and when the server starts');
  return parts.join(', ');
}

/** Schedules of one queue, by name. */
export function schedulesOf(schedules: Schedule[], queueId: number): Schedule[] {
  return schedules.filter((s) => s.queueId === queueId).sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
}

export function queueStartedText(name: string, started: boolean): string {
  return started ? `${name} started now.` : `${name} stopped.`;
}

/** "3 downloads" / "1 download", for the queue card. */
export function downloadsText(count: number): string {
  return `${count} ${count === 1 ? 'download' : 'downloads'}`;
}
