import { describe, expect, it } from 'vitest';
import {
  ALL_DAYS, canMoveFile, canStartQueue, canStopQueue, checkQueueForm, checkScheduleDraft, defaultMoveTarget, editScheduleDraft, isMember, mbText, moveFile,
  newScheduleDraft, normalizeDays, queueForm, queueMembers, queueStateText, scheduleConflict, scheduleSummary, schedulesOf, sortQueueFiles, toggleDay, waitingForStart, whenText,
} from '../src/lib/queues';
import type { Job, Queue, Schedule } from '../src/lib/types';

const job = (id: string, extra: Partial<Job> = {}) => ({ id, queueId: 0, createdAt: `2026-01-0${id}`, ...extra }) as unknown as Job;
const queue = (id: number, extra: Partial<Queue> = {}) => ({ id, name: `Q${id}`, order: id, jobOrder: [], maxConcurrentDownloads: 2, isRunning: false, quotaEnabled: false, quotaBytes: 0, quotaWindowHours: 0, quotaBytesUsed: 0, ...extra }) as Queue;

describe('queue members (Q13)', () => {
  it('counts a download as a member unless isQueued is false', () => {
    expect(isMember({})).toBe(true);
    expect(isMember({ isQueued: true })).toBe(true);
    expect(isMember({ isQueued: false })).toBe(false);
    expect(queueMembers([job('1'), job('2', { isQueued: false }), job('3', { queueId: 1 })], 0).map((j) => j.id)).toEqual(['1']);
  });
});

describe('files order (Q1)', () => {
  it('sorts by priority, then the saved order, then age', () => {
    const jobs = [job('1'), job('2', { priority: 1 }), job('3'), job('4')];
    expect(sortQueueFiles(jobs, { jobOrder: ['4', '3'] }).map((j) => j.id)).toEqual(['2', '4', '3', '1']);
  });
  it('moves only between equal priorities and not past the ends', () => {
    const files = [job('1', { priority: 1 }), job('2'), job('3')];
    expect(canMoveFile(files, 0, 1)).toBe(false);
    expect(canMoveFile(files, 1, 1)).toBe(true);
    expect(canMoveFile(files, 0, -1)).toBe(false);
    expect(canMoveFile(files, 2, 1)).toBe(false);
    expect(moveFile(files, 1, 1).map((j) => j.id)).toEqual(['1', '3', '2']);
    expect(moveFile(files, 0, 1)).toBe(files);
  });
});

describe('queue form (Q14, Q15, Q16)', () => {
  it('prefills a new queue with the next number and one at a time', () => {
    const form = queueForm(null, 2);
    expect(form.name).toBe('Queue 3');
    expect(form.max).toBe('1');
    expect(form.quotaEnabled).toBe(false);
  });
  it('keeps fractions of a megabyte', () => {
    expect(mbText(512 * 1024)).toBe('0.5');
    const result = checkQueueForm({ ...queueForm(null, 0), quotaEnabled: true, quotaMb: '0.5', quotaHours: '5' });
    expect(result).toEqual({ policy: { name: 'Queue 1', maxConcurrentDownloads: 1, quotaEnabled: true, quotaBytes: 524288, quotaWindowHours: 5 } });
  });
  it('does not read or send the quota numbers while the limit is off', () => {
    const result = checkQueueForm({ name: 'Night', max: '3', quotaEnabled: false, quotaMb: '', quotaHours: 'x' });
    expect(result).toEqual({ policy: { name: 'Night', maxConcurrentDownloads: 3, quotaEnabled: false } });
  });
  it('uses the desktop messages', () => {
    const base = { name: 'A', max: '1', quotaEnabled: true, quotaMb: '10', quotaHours: '5' };
    expect(checkQueueForm({ ...base, name: ' ' })).toEqual({ error: 'Enter a queue name.' });
    expect(checkQueueForm({ ...base, max: '17' })).toEqual({ error: 'Simultaneous downloads must be between 1 and 16.' });
    expect(checkQueueForm({ ...base, quotaMb: '0' })).toEqual({ error: 'The quota must be a positive number of MB, up to 10,485,760.' });
    expect(checkQueueForm({ ...base, quotaMb: '10485761' })).toEqual({ error: 'The quota must be a positive number of MB, up to 10,485,760.' });
    expect(checkQueueForm({ ...base, quotaHours: '8761' })).toEqual({ error: 'The quota window must be between 1 and 8,760 hours.' });
  });
});

describe('delete a queue (Q2)', () => {
  it('preselects the default queue, else the first other one', () => {
    const queues = [queue(0), queue(1), queue(2)];
    expect(defaultMoveTarget(queues, 1, 0)).toBe(0);
    expect(defaultMoveTarget(queues, 0, 0)).toBe(1);
    expect(defaultMoveTarget([queue(0)], 0, 0)).toBeNull();
  });
});

describe('days (Q12)', () => {
  it('reads an empty selection as every day', () => {
    expect(normalizeDays('')).toBe(ALL_DAYS);
    expect(normalizeDays(undefined)).toBe(ALL_DAYS);
    expect(normalizeDays('Sat,mon')).toBe('Mon,Sat');
  });
  it('unticking the last day shows every day, not an empty summary', () => {
    const left = toggleDay('Mon,Tue', 'Tue');
    expect(left).toBe('Mon');
    const none = toggleDay(left, 'Mon');
    expect(none).toBe('');
    expect(whenText({ kind: 'daily', days: none } as Schedule)).toBe('every day');
  });
});

describe('schedule drafts (Q3 to Q6, Q9)', () => {
  const ids = [0, 1];
  const fresh = () => newScheduleDraft(1, 'id-1', undefined);

  it('starts as the desktop does', () => {
    const { schedule, options } = fresh();
    expect(schedule).toMatchObject({ name: 'New schedule', startTime: '09:00', days: ALL_DAYS, queueId: 1, enabled: true, timedStartEnabled: true });
    expect(schedule.stopTime).toBeUndefined();
    expect(options).toMatchObject({ repeatEnabled: false, repeatMinutes: '60', retryEnabled: false, retryLimit: '3' });
  });
  it('accepts the defaults', () => {
    const result = checkScheduleDraft(fresh(), ids);
    expect(result).toMatchObject({ schedule: { name: 'New schedule', kind: 'daily', startTime: '09:00' }, retryLimit: null });
  });
  it('refuses a schedule that could never fire', () => {
    const d = fresh();
    d.schedule.name = '  ';
    expect(checkScheduleDraft(d, ids)).toEqual({ error: 'Enter a schedule name of 1–80 characters.' });
    d.schedule.name = 'x';
    d.schedule.startTime = '';
    expect(checkScheduleDraft(d, ids)).toEqual({ error: 'Start time must use HH:mm.' });
    d.schedule.startTime = '09:00';
    d.schedule.stopTime = '25:00';
    expect(checkScheduleDraft(d, ids)).toEqual({ error: 'Stop time must use HH:mm.' });
    d.schedule.stopTime = '';
    d.schedule.startDate = '2026-02-31';
    expect(checkScheduleDraft(d, ids)).toEqual({ error: 'One-time date must use yyyy-MM-dd.' });
    d.schedule.startDate = '';
    d.schedule.queueId = 9;
    expect(checkScheduleDraft(d, ids)).toEqual({ error: 'Choose an existing target queue.' });
  });
  it('turns an optional date into a one-time schedule, and none into a daily one', () => {
    const d = fresh();
    d.schedule.startDate = '2026-12-24';
    expect(checkScheduleDraft(d, ids)).toMatchObject({ schedule: { kind: 'oneTime', startDate: '2026-12-24' } });
    d.schedule.startDate = '';
    const again = checkScheduleDraft(d, ids);
    expect('schedule' in again && again.schedule.kind).toBe('daily');
    expect('schedule' in again && 'startDate' in again.schedule).toBe(false);
  });
  it('needs a trigger and ties the periodic check to timed start', () => {
    const d = fresh();
    d.options.timedStartEnabled = false;
    expect(checkScheduleDraft(d, ids)).toEqual({ error: 'Enable startup or timed start so this queue has a way to run.' });
    d.options.startOnHostStartup = true;
    d.options.repeatEnabled = true;
    expect(checkScheduleDraft(d, ids)).toEqual({ error: 'Periodic synchronization requires timed start.' });
  });
  it('checks the synchronization interval and the retry limit', () => {
    const d = fresh();
    d.options.repeatEnabled = true;
    d.options.repeatMinutes = '1441';
    expect(checkScheduleDraft(d, ids)).toEqual({ error: 'Set the synchronization interval to a whole number from 1 to 1440 minutes.' });
    d.options.repeatMinutes = '30';
    d.options.retryEnabled = true;
    d.options.retryLimit = '101';
    expect(checkScheduleDraft(d, ids)).toEqual({ error: 'Set the retry limit to a whole number from 0 to 100.' });
    d.options.retryLimit = '0';
    expect(checkScheduleDraft(d, ids)).toMatchObject({ schedule: { repeatEveryMinutes: 30 }, retryLimit: 0 });
  });
  it('writes an emptied day list as every day', () => {
    const d = fresh();
    d.schedule.days = '';
    expect(checkScheduleDraft(d, ids)).toMatchObject({ schedule: { days: ALL_DAYS } });
  });
  it('starts an edit from the retry limit of the queue', () => {
    const saved = { ...fresh().schedule, repeatEveryMinutes: 15 };
    const d = editScheduleDraft(saved, 5);
    expect(d.options).toMatchObject({ repeatEnabled: true, repeatMinutes: '15', retryEnabled: true, retryLimit: '5' });
    expect(d.isNew).toBe(false);
  });
});

describe('changed elsewhere (Q7)', () => {
  const saved = (): Schedule => newScheduleDraft(0, 's1', undefined).schedule;
  it('lets an unchanged schedule be saved or removed', () => {
    const d = editScheduleDraft(saved(), undefined);
    expect(scheduleConflict([saved()], d)).toBeNull();
    expect(scheduleConflict([saved()], d, true)).toBeNull();
    expect(scheduleConflict([{ ...saved(), lastRunAt: '2026-01-01T00:00:00Z' }], d)).toBeNull();
  });
  it('refuses one that was changed or removed meanwhile, in the desktop words', () => {
    const d = editScheduleDraft(saved(), undefined);
    const changed = { ...saved(), startTime: '10:00' };
    expect(scheduleConflict([changed], d)).toEqual({ error: "Schedule 'New schedule' changed elsewhere; its unsaved edit was not applied." });
    expect(scheduleConflict([changed], d, true)).toEqual({ error: "Schedule 'New schedule' changed elsewhere; it was not removed." });
    expect(scheduleConflict([], d)).toEqual({ error: "Schedule 'New schedule' was removed elsewhere; its unsaved edit was not applied." });
    expect(scheduleConflict([], d, true)).toEqual({ error: "Schedule 'New schedule' was already removed elsewhere." });
  });
});

describe('schedule text (Q3, Q11)', () => {
  it('says synchronize, not start again', () => {
    const s = { ...newScheduleDraft(0, 'a', undefined).schedule, repeatEveryMinutes: 30, stopTime: '17:00', startOnHostStartup: true };
    expect(scheduleSummary(s)).toBe('start at 09:00, stop at 17:00, every day, synchronize every 30 min, and when the server starts');
  });
  it('groups by queue and sorts by name', () => {
    const a = { ...newScheduleDraft(0, 'a', undefined).schedule, name: 'beta' };
    const b = { ...a, id: 'b', name: 'Alpha' };
    const c = { ...a, id: 'c', queueId: 1 };
    expect(schedulesOf([a, b, c], 0).map((s) => s.name)).toEqual(['Alpha', 'beta']);
  });
});

describe('queue Start and Stop follow the host (U-WL-1, U-WL-2)', () => {
  const running = queue(0, { isRunning: true });

  it('offers Start on a running queue while a later download waits for it', () => {
    const jobs = [job('1', { state: 'queued', startMode: 'later' }), job('2', { state: 'downloading' })];
    expect(waitingForStart(running, jobs)).toBe(1);
    expect(canStartQueue(running, jobs)).toBe(true);
    expect(canStopQueue(running)).toBe(true);
    expect(queueStateText(running, jobs)).toBe('Running, 1 waiting for Start');
  });

  it('does not count a download the user paused, one in another queue, or one out of its queue', () => {
    const jobs = [
      job('1', { state: 'paused', pausedByUser: true }),
      job('2', { state: 'queued', startMode: 'later', queueId: 1 }),
      job('3', { state: 'queued', startMode: 'later', isQueued: false }),
      job('4', { state: 'queued', startMode: 'startNow' }),
    ];
    expect(canStartQueue(running, jobs)).toBe(false);
    expect(queueStateText(running, jobs)).toBe('Running');
  });

  it('counts a download the queue itself paused', () => {
    expect(canStartQueue(running, [job('1', { state: 'paused', pausedByUser: false })])).toBe(true);
  });

  it('always offers Start, and never Stop, on a stopped queue', () => {
    const stopped = queue(0, { isRunning: false });
    expect(canStartQueue(stopped, [])).toBe(true);
    expect(canStopQueue(stopped)).toBe(false);
    expect(queueStateText(stopped, [job('1', { state: 'queued', startMode: 'later' })])).toBe('Stopped');
  });
});
