// Audit 4, second pass: a schedule edit or removal is checked again on the server's current settings (H1, the case a
// stale page copy would miss), a refused change sends nothing, and the statistics page reads on a timer only while
// something downloads (M11).
import { afterEach, describe, expect, it, vi } from 'vitest';
import statisticsSource from '../src/screens/StatisticsScreen.svelte?raw';
import { applyScheduleEdit, applyScheduleRemoval, editScheduleDraft, newScheduleDraft } from '../src/lib/queues';
import { Store } from '../src/lib/store.svelte';
import type { Envelope } from '../src/lib/protocol';
import type { Queue, Schedule, Settings } from '../src/lib/types';

const base: Schedule = {
  id: 's1', name: 'Night', queueId: 1, enabled: true, kind: 'daily', days: 'Mon,Tue', startTime: '02:00',
  timedStartEnabled: true, startOnHostStartup: false, openFileWhenDone: false, exitWhenDone: false,
  shutdownWhenDone: false, shutdownMode: 'shutdown', forceShutdown: false,
} as Schedule;

const copy = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T;

function current(schedules: Schedule[], retryLimit?: number) {
  return { schedules: copy(schedules), queues: [{ id: 1, name: 'Main', retryLimit } as Queue] };
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('applyScheduleEdit', () => {
  it('does not put back a schedule removed elsewhere after this page read it', () => {
    const draft = editScheduleDraft(base, undefined);
    const settings = current([]);
    const outcome = applyScheduleEdit(settings, draft, { ...base, startTime: '03:00' });
    expect(outcome?.refused).toBe("Schedule 'Night' was removed elsewhere; its unsaved edit was not applied.");
    expect(settings.schedules).toEqual([]);
  });

  it('does not overwrite a schedule changed elsewhere', () => {
    const draft = editScheduleDraft(base, undefined);
    const settings = current([{ ...base, startTime: '05:00' }]);
    expect(applyScheduleEdit(settings, draft, { ...base, startTime: '03:00' })?.refused).toMatch(/changed elsewhere/);
    expect(settings.schedules[0].startTime).toBe('05:00');
  });

  it('changes only this schedule, and the retry limit only when the form changed it', () => {
    const other = { ...base, id: 's2', name: 'Day' };
    const settings = current([base, other], 3);
    expect(applyScheduleEdit(settings, editScheduleDraft(base, 3), { ...base, startTime: '03:00' })).toBeUndefined();
    expect(settings.schedules.map((s) => [s.id, s.startTime])).toEqual([['s1', '03:00'], ['s2', '02:00']]);
    expect(settings.queues[0].retryLimit).toBe(3);

    expect(applyScheduleEdit(settings, editScheduleDraft(settings.schedules[0], 3), settings.schedules[0], null)).toBeUndefined();
    expect(settings.queues[0].retryLimit).toBeUndefined();
  });

  it('refuses a retry limit the queue changed elsewhere, and adds a new schedule', () => {
    const settings = current([], 7);
    const fresh = { ...base, id: 'new' };
    expect(applyScheduleEdit(settings, newScheduleDraft(1, 'new', 3), fresh, 5)?.refused).toMatch(/retry policy changed elsewhere/);
    expect(settings.schedules).toEqual([]);
    expect(applyScheduleEdit(settings, newScheduleDraft(1, 'new', 7), fresh)).toBeUndefined();
    expect(settings.schedules.map((s) => s.id)).toEqual(['new']);
  });
});

describe('applyScheduleRemoval', () => {
  it('removes the schedule only when it is as it was read', () => {
    const settings = current([base]);
    expect(applyScheduleRemoval(settings, copy(base))).toBeUndefined();
    expect(settings.schedules).toEqual([]);

    const changed = current([{ ...base, startTime: '04:00' }]);
    expect(applyScheduleRemoval(changed, copy(base))?.refused).toBe("Schedule 'Night' changed elsewhere; it was not removed.");
    expect(changed.schedules).toHaveLength(1);
  });
});

describe('saveSettings with a refused change', () => {
  it('sends nothing and shows the reason', async () => {
    vi.useFakeTimers();
    const requests: Envelope[] = [];
    vi.stubGlobal('fetch', async (_input: string, init: { body: string }) => {
      const request = JSON.parse(init.body) as Envelope;
      requests.push(request);
      const body = { v: 1, type: 'settings.result', id: 'r', replyTo: request.id, payload: { ok: true, settings: { schedules: [], queues: [] } } };
      return { status: 200, ok: true, json: async () => body };
    });
    vi.stubGlobal('location', { assign: vi.fn(), protocol: 'http:', host: 'server' });
    const store = new Store();
    store.model.settings = { schedules: [base], queues: [] } as unknown as Settings;

    const ok = await store.saveSettings((settings) => applyScheduleEdit(settings, editScheduleDraft(base, undefined), base));

    expect(ok).toBe(false);
    expect(requests.map((r) => r.type)).toEqual(['settings.get']);
    expect(store.toasts.map((t) => [t.text, t.tone])).toEqual([["Schedule 'Night' was removed elsewhere; its unsaved edit was not applied.", 'error']]);
  });
});

describe('statistics page (M11)', () => {
  it('reads on a timer only while something downloads', () => {
    // The timer is made inside an effect that returns early when nothing is active; there is no other timer.
    expect(statisticsSource).toMatch(/const active = \$derived\(anyActive\(/);
    expect(statisticsSource).toMatch(/if \(!active\) return;\s*return everyWhileVisible\(2000/);
    expect(statisticsSource.match(/everyWhileVisible\(/g)).toHaveLength(1);
    expect(statisticsSource).not.toMatch(/setInterval|onMount/);
  });
});
