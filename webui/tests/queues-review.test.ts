import { describe, expect, it } from 'vitest';
import { editScheduleDraft, newScheduleDraft, scheduleConflict, checkScheduleDraft, type ScheduleDraft } from '../src/lib/queues';
import type { Schedule } from '../src/lib/types';

const base: Schedule = {
  id: 's1', name: 'Night', queueId: 1, enabled: true, kind: 'daily', days: 'Mon,Tue', startTime: '02:00',
  timedStartEnabled: true, startOnHostStartup: false, openFileWhenDone: false, exitWhenDone: false,
  shutdownWhenDone: false, shutdownMode: 'shutdown', forceShutdown: false,
} as Schedule;

describe('retry limit is remembered when the form opens', () => {
  it('records the queue limit for a new schedule and an edited one', () => {
    expect(newScheduleDraft(1, 'x', undefined).retryInitial).toBeNull();
    expect(newScheduleDraft(1, 'x', 5).retryInitial).toBe(5);
    expect(editScheduleDraft(base, 0).retryInitial).toBe(0);
    expect(editScheduleDraft(base, undefined).retryInitial).toBeNull();
  });
});

describe('removing a schedule checks against the copy taken when the dialog opened', () => {
  it('refuses when the schedule changed after the dialog opened', () => {
    const snapshot = JSON.parse(JSON.stringify(base)) as Schedule;
    const changed = { ...base, startTime: '03:00' };
    const conflict = scheduleConflict([changed], { isNew: false, schedule: snapshot, baseline: snapshot }, true);
    expect(conflict?.error).toBe("Schedule 'Night' changed elsewhere; it was not removed.");
  });

  it('allows the removal when nothing changed', () => {
    const snapshot = JSON.parse(JSON.stringify(base)) as Schedule;
    expect(scheduleConflict([base], { isNew: false, schedule: snapshot, baseline: snapshot }, true)).toBeNull();
  });
});

describe('a draft with no changed retry limit leaves the queue alone', () => {
  it('reports the retry result so the screen can compare it with retryInitial', () => {
    const draft: ScheduleDraft = editScheduleDraft(base, 3);
    draft.options.retryEnabled = true;
    draft.options.retryLimit = '3';
    const result = checkScheduleDraft(draft, [1]);
    expect(result).toMatchObject({ retryLimit: 3 });
    expect(draft.retryInitial).toBe(3);
  });
});

import { checkQueueForm as cqf, queueForm as qf, retryConflict, tickedDays, toggleDay as td } from '../src/lib/queues';

describe('review round 2', () => {
  it('shows no day ticked after unticking the last one', () => {
    expect([...tickedDays(td('Mon', 'Mon'))]).toEqual([]);
    expect(td('Mon', 'Tue')).toBe('Mon,Tue');
  });
  it('refuses number forms the desktop refuses', () => {
    const f = qf(null, 1);
    expect('error' in cqf({ ...f, max: '1e1' })).toBe(true);
    expect('error' in cqf({ ...f, max: '+5' })).toBe(true);
    expect('error' in cqf({ ...f, quotaEnabled: true, quotaMb: '0x10' })).toBe(true);
    expect('error' in cqf({ ...f, quotaEnabled: true, quotaMb: '1,5' })).toBe(false);
  });
  it('detects a retry limit changed elsewhere', () => {
    expect(retryConflict({ name: 'Main', retryLimit: 5 }, { retryInitial: 3 })).toMatchObject({ error: expect.stringContaining('retry policy changed elsewhere') });
    expect(retryConflict({ name: 'Main', retryLimit: undefined }, { retryInitial: null })).toBeNull();
  });
});
