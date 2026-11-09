import { describe, expect, it } from 'vitest';
import {
  archiveHasOwnRow, archivePasswordText, archivePrimary, archiveRetryText, hideAddresses, providerEntries, canGroup, cancelTargets, categoryForName, categoryOf, countJobs, deleteCompletedText, deletePlan, describe as describeNote,
  graphCeiling, jobView, lastTryText, menuLabel, moveCursor, nextSort, orderBy, packageQueueText, packageView, parseSort, pauseTargets, planFileMenu,
  priorityTargets, remoteEnterAction, resumeTargets, selectAfterClick, sharedQueueId, sortRows, SpeedHistory, speedSample, hoverPoint, agoText, startsAscending, statusBarText,
  statusWords, suggestPackageName, summarizeGroup, timeLeft, emptyText, defaultEmptyText,
} from '../src/lib/list';
import { canRetry, targetOf } from '../src/lib/archives';
import { apply, emptyModel } from '../src/lib/model';
import type { ArchiveSet, Job, RemoteJob } from '../src/lib/types';
import { buildRows, categoryList, counts, parseViewKey, viewHash } from '../src/lib/views';

function job(id: string, state: string, extra: Partial<Job> = {}): Job {
  return {
    id, url: `https://example.com/${id}`, fileName: `${id}.bin`, destinationDirectory: '/d', finalFilePath: `/d/${id}.bin`, receivedBytes: 0,
    state, createdAt: '2026-11-01T10:00:00Z', updatedAt: '2026-11-01T10:00:00Z', queueId: 0, ...extra,
  };
}

describe('package summary (DownloadGroupSummary)', () => {
  it('adds the speed of active files only, and counts retrying files as active', () => {
    const s = summarizeGroup([
      job('a', 'downloading', { bytesPerSecond: 100 }),
      job('b', 'retryWaiting', { bytesPerSecond: 50 }),
      job('c', 'paused', { bytesPerSecond: 999 }),
    ]);
    expect(s.bytesPerSecond).toBe(150);
    expect(s.state).toBe('active');
  });

  it('checks paused before queued', () => {
    expect(summarizeGroup([job('a', 'queued'), job('b', 'paused')]).state).toBe('paused');
    expect(summarizeGroup([job('a', 'queued'), job('b', 'completed')]).state).toBe('queued');
  });

  it('calls a completed and cancelled mix cancelled, not paused', () => {
    expect(summarizeGroup([job('a', 'completed'), job('b', 'cancelled')]).state).toBe('cancelled');
    expect(summarizeGroup([job('a', 'completed'), job('b', 'completed')]).state).toBe('completed');
  });

  it('puts failed above paused and an unknown state counts as queued', () => {
    expect(summarizeGroup([job('a', 'failed'), job('b', 'paused')]).state).toBe('failed');
    expect(summarizeGroup([job('a', 'mystery'), job('b', 'completed')]).state).toBe('queued');
  });

  it('takes progress from bytes, null when a size is unknown, and the time left from the speed', () => {
    const known = summarizeGroup([job('a', 'downloading', { totalBytes: 100, receivedBytes: 25, bytesPerSecond: 10 }), job('b', 'queued', { totalBytes: 100, receivedBytes: 0 })]);
    expect(known.progress).toBe(0.125);
    expect(known.totalBytes).toBe(200);
    expect(known.estimatedRemaining).toBe(17.5);
    const unknown = summarizeGroup([job('a', 'downloading', { totalBytes: 100, receivedBytes: 25 }), job('b', 'queued')]);
    expect(unknown.progress).toBeNull();
    expect(unknown.totalBytes).toBeNull();
    expect(unknown.estimatedRemaining).toBeNull();
  });

  it('does not use done/count as progress', () => {
    const s = summarizeGroup([job('a', 'completed', { totalBytes: 10, receivedBytes: 10 }), job('b', 'queued', { totalBytes: 990, receivedBytes: 0 })]);
    expect(s.completedCount).toBe(1);
    expect(s.progress).toBeCloseTo(0.01);
  });

  it('shows the package row as "<state> · done/count" with its size and time left', () => {
    const files = [job('a', 'downloading', { totalBytes: 2048, receivedBytes: 1024, bytesPerSecond: 1024 }), job('b', 'completed', { totalBytes: 2048, receivedBytes: 2048 })];
    const v = packageView({ id: 'g', name: 'Pack', jobIds: ['a', 'b'] }, files);
    expect(v.status).toBe('Downloading · 1/2');
    expect(v.sizeText).toBe('4.00 KB');
    expect(v.eta).toBe(1);
    expect(v.etaText).toBe('1s');
    expect(v.bar).toBeCloseTo(75);
  });
});

describe('package rules (PackageRules)', () => {
  const id = '11111111-1111-1111-1111-111111111111';
  it('resume leaves cancelled files cancelled', () => {
    const files = [job('a', 'paused', { canResume: true }), job('b', 'cancelled', { canResume: true, cancelledByPackageId: 'other' })];
    expect(resumeTargets(id, files).map((j) => j.id)).toEqual(['a']);
  });

  it('resume brings every file back after a package cancel that stopped them all', () => {
    const files = [job('a', 'cancelled', { canResume: true, cancelledByPackageId: id.toUpperCase() }), job('b', 'cancelled', { canResume: true, cancelledByPackageId: id })];
    expect(resumeTargets(id, files)).toHaveLength(2);
  });

  it('a finished file in the package means a cancel did not cover it, so nothing is revived', () => {
    const files = [job('a', 'cancelled', { canResume: true, cancelledByPackageId: id }), job('b', 'completed', { isTerminal: true })];
    expect(resumeTargets(id, files)).toHaveLength(0);
  });

  it('pause, cancel and priority targets', () => {
    const files = [job('a', 'downloading', { canPause: true }), job('b', 'completed', { isTerminal: true }), job('c', 'cancelled', { isTerminal: true }), job('d', 'paused')];
    expect(pauseTargets(files).map((j) => j.id)).toEqual(['a']);
    expect(cancelTargets(files).map((j) => j.id)).toEqual(['a', 'd']);
    expect(priorityTargets(files).map((j) => j.id)).toEqual(['a', 'c', 'd']);
  });

  it('shows the shared queue, a marker when they differ, nothing when none is queued', () => {
    expect(packageQueueText([job('a', 'queued', { queueId: 1 }), job('b', 'queued', { queueId: 1 })])).toBe('2');
    expect(sharedQueueId([job('a', 'queued', { queueId: 1 }), job('b', 'queued', { queueId: 1 })])).toBe(1);
    expect(packageQueueText([job('a', 'queued', { queueId: 0 }), job('b', 'queued', { queueId: 1 })])).toBe('*');
    expect(sharedQueueId([job('a', 'queued', { queueId: 0 }), job('b', 'queued', { queueId: 1 })])).toBeNull();
    expect(packageQueueText([job('a', 'queued', { isQueued: false }), job('b', 'queued', { isQueued: false })])).toBe('');
    expect(sharedQueueId([job('a', 'queued', { isQueued: false })])).toBeNull();
  });

  it('suggests a package name without the extension or part number', () => {
    expect(suggestPackageName('movie.part01.rar')).toBe('movie');
    expect(suggestPackageName('movie.r01')).toBe('movie');
    expect(suggestPackageName('movie.001')).toBe('movie');
    expect(suggestPackageName('holiday.photos.zip')).toBe('holiday.photos');
  });

  it('grouping needs two files in two rows', () => {
    expect(canGroup(2, 2)).toBe(true);
    expect(canGroup(5, 1)).toBe(false);
    expect(canGroup(1, 2)).toBe(false);
  });
});

describe('cells (JobViewModel)', () => {
  it('writes the time left as the desktop does', () => {
    expect(timeLeft(null)).toBe('');
    expect(timeLeft(0)).toBe('');
    expect(timeLeft(0.4)).toBe('1s');
    expect(timeLeft(45)).toBe('45s');
    expect(timeLeft(200)).toBe('3m 20s');
    expect(timeLeft(3900)).toBe('1h 5m');
    expect(timeLeft(100000)).toBe('27h 46m');
  });

  it('writes the last try date as the desktop does', () => {
    const date = new Date(2026, 10, 9, 14, 3, 22);
    expect(lastTryText(date)).toBe('Nov 09 14:03:22 2026');
    expect(lastTryText('')).toBe('');
    expect(lastTryText('0001-01-01T00:00:00Z')).toBe('');
  });

  it('shows a size only when one is known, the received bytes when the total is not', () => {
    expect(jobView(job('a', 'queued')).sizeText).toBe('');
    expect(jobView(job('a', 'downloading', { receivedBytes: 2048 })).sizeText).toBe('2.00 KB');
    expect(jobView(job('a', 'downloading', { totalBytes: 4096, receivedBytes: 2048 })).sizeText).toBe('4.00 KB');
  });

  it('shows the rate and the time left while retrying, and the speed of any state that has one', () => {
    const v = jobView(job('a', 'retryWaiting', { bytesPerSecond: 2048, estimatedRemaining: '00:01:30' }));
    expect(v.rateText).toBe('2.00 KB/s');
    expect(v.etaText).toBe('1m 30s');
    expect(v.status).toBe('Retrying');
  });

  it('words the status like the desktop', () => {
    expect(statusWords(job('a', 'downloading', { waitingForHost: 'example.com' }))).toBe('Waiting for a connection to example.com');
    expect(statusWords(job('a', 'downloading', { isRemote: true }))).toBe('Downloading locally');
    expect(statusWords(job('a', 'queued', { queueRetryAt: '2026-11-09T10:00:00Z' }))).toBe('Queue retry scheduled');
    expect(statusWords(job('a', 'completed'))).toBe('Completed');
  });

  it('builds the description as "note · own", and keeps the failure for the status tooltip', () => {
    expect(describeNote('Resumed', 'my file')).toBe('Resumed · my file');
    expect(describeNote(null, 'my file')).toBe('my file');
    expect(describeNote('Resumed', '')).toBe('Resumed');
    const v = jobView(job('a', 'failed', { failure: { kind: 'x', message: 'Server said no' }, statusNote: 'Tried 3 times', description: 'Notes', pageUrl: 'https://page.example/' }));
    expect(v.description).toBe('Tried 3 times · Notes');
    expect(v.statusTip).toBe('Server said no');
    expect(v.detail).toBe('Server said no');
    expect(jobView(job('b', 'completed', { pageUrl: 'https://page.example/' })).description).toBe('https://page.example/');
  });

  it('says the archive was removed after extraction', () => {
    expect(jobView(job('a', 'completed', { fileRemovedAfterExtraction: true })).description).toBe('Archive removed after extraction');
  });

  it('numbers the queue from 1 and leaves it empty when the file is not queued', () => {
    expect(jobView(job('a', 'queued', { queueId: 2, queueName: 'Night' }))).toMatchObject({ queue: '3', queueTip: 'Night' });
    expect(jobView(job('a', 'queued', { isQueued: false })).queue).toBe('');
  });

  it('takes the last try from the completion time, else the last update', () => {
    const v = jobView(job('a', 'completed', { completedAt: '2026-11-05T08:00:00Z', updatedAt: '2026-11-09T08:00:00Z' }));
    expect(v.lastTry).toBe(Date.parse('2026-11-05T08:00:00Z'));
  });

  it('shows the bar while active or paused with progress above 0', () => {
    expect(jobView(job('a', 'downloading', { totalBytes: 100, receivedBytes: 50 })).bar).toBe(50);
    expect(jobView(job('a', 'paused', { totalBytes: 100, receivedBytes: 10 })).bar).toBe(10);
    expect(jobView(job('a', 'failed', { totalBytes: 100, receivedBytes: 10 })).bar).toBeNull();
    expect(jobView(job('a', 'downloading')).bar).toBeNull();
  });
});

describe('deleting', () => {
  it('words the question and offers the file box only for a completed file', () => {
    const one = deletePlan([job('a', 'completed')]);
    expect(one.title).toBe('Delete download?');
    expect(one.text).toBe("Remove 'a.bin' from the list?");
    expect(one.offerFiles).toBe(true);

    const many = deletePlan([job('a', 'downloading'), job('b', 'paused')]);
    expect(many.title).toBe('Delete 2 downloads?');
    expect(many.text).toBe('Remove 2 downloads from the list? Partial data is always removed.');
    expect(many.offerFiles).toBe(false);
    expect(many.deleteFile).toBe(true);
  });

  it('says there is nothing to delete when the archives went on purpose', () => {
    const plan = deletePlan([job('a', 'completed', { fileRemovedAfterExtraction: true })]);
    expect(plan.text).toContain('Its archive was removed after extraction, so no file is deleted.');
    expect(plan.offerFiles).toBe(false);
    expect(plan.deleteFile).toBe(false);
    expect(deletePlan([job('a', 'completed', { fileRemovedAfterExtraction: true }), job('b', 'completed', { fileRemovedAfterExtraction: true })]).text).toContain('Their archives were removed');
  });

  it('keeps Delete Completed to the list', () => {
    expect(deleteCompletedText(3)).toBe('Remove 3 completed item(s) from the list? The downloaded files will stay on disk.');
  });
});

describe('categories', () => {
  it('uses the built-in category of a file name', () => {
    expect(categoryForName('a.ZIP')).toBe('compressed');
    expect(categoryForName('a.mkv')).toBe('video');
    expect(categoryForName('a.tar.gz')).toBe('compressed');
    expect(categoryForName('README')).toBe('other');
  });

  it('prefers the Host\'s category, then a custom one by extension, then the built-in', () => {
    const custom = [{ id: 'books', name: 'Books', extensions: ['mobi', '.azw'] }];
    expect(categoryOf({ categoryId: 'music', fileName: 'a.zip' }, custom)).toBe('music');
    expect(categoryOf({ fileName: 'a.mobi' }, custom)).toBe('books');
    expect(categoryOf({ fileName: 'a.azw' }, custom)).toBe('books');
    expect(categoryOf({ fileName: 'a.zip' }, custom)).toBe('compressed');
  });

  it('lists categories with the custom ones before Other', () => {
    expect(categoryList([{ id: 'books', name: 'Books', extensions: [] }]).map((c) => c.label)).toEqual(['Compressed', 'Documents', 'Music', 'Programs', 'Video', 'Books', 'Other']);
  });

  it('filters and counts by category', () => {
    const model = emptyModel();
    apply(model, { v: 1, id: 'x', type: 'jobs.snapshot', payload: { jobs: [job('a', 'queued', { fileName: 'a.zip' }), job('b', 'queued', { fileName: 'b.mp3' }), job('c', 'queued', { fileName: 'c', categoryId: 'video' })], settings: { downloadGroups: [] }, status: {} } });
    expect(buildRows(model, 'cat:compressed', '', new Set()).map((r) => r.id)).toEqual(['a']);
    expect(buildRows(model, 'cat:video', '', new Set()).map((r) => r.id)).toEqual(['c']);
    expect(counts(model)).toMatchObject({ all: 3, 'cat:compressed': 1, 'cat:music': 1, 'cat:video': 1, 'cat:other': 0 });
  });

  it('names views in the hash', () => {
    expect(parseViewKey('cat/music')).toBe('cat:music');
    expect(parseViewKey('active')).toBe('active');
    expect(parseViewKey('queues')).toBeNull();
    expect(viewHash('cat:my cat')).toBe('#/cat/my%20cat');
    expect(parseViewKey('cat/my%20cat')).toBe('cat:my cat');
  });
});

describe('sorting (ListSort)', () => {
  it('starts words ascending and sizes, speeds and dates descending', () => {
    expect(startsAscending('name')).toBe(true);
    expect(startsAscending('status')).toBe(true);
    expect(startsAscending('queue')).toBe(true);
    expect(startsAscending('eta')).toBe(true);
    expect(startsAscending('size')).toBe(false);
    expect(startsAscending('rate')).toBe(false);
    expect(startsAscending('lastTry')).toBe(false);
    expect(startsAscending('added')).toBe(false);
  });

  it('reverses on a second click and starts in the usual direction on another', () => {
    const first = nextSort({ key: 'added', ascending: false }, 'name');
    expect(first).toEqual({ key: 'name', ascending: true });
    expect(nextSort(first, 'name')).toEqual({ key: 'name', ascending: false });
    expect(nextSort(first, 'size')).toEqual({ key: 'size', ascending: false });
  });

  it('keeps blank cells at the end in either direction, and equal rows in order', () => {
    const rows = [{ n: 'a', v: 0 }, { n: 'b', v: 5 }, { n: 'c', v: 1 }, { n: 'd', v: 5 }];
    const blank = (r: { v: number }) => r.v === 0;
    expect(orderBy(rows, (r) => r.v, true, blank).map((r) => r.n)).toEqual(['c', 'b', 'd', 'a']);
    expect(orderBy(rows, (r) => r.v, false, blank).map((r) => r.n)).toEqual(['b', 'd', 'c', 'a']);
  });

  it('sorts names the natural way', () => {
    expect(orderBy(['file10', 'file2', 'File1'], (s) => s, true)).toEqual(['File1', 'file2', 'file10']);
  });

  it('sorts the status by the words shown, not the state key', () => {
    const rows = [job('a', 'retryWaiting'), job('b', 'queued'), job('c', 'downloading'), job('d', 'failed')];
    const sorted = sortRows(rows, (j) => jobView(j), { key: 'status', ascending: true }).map((j) => jobView(j).status);
    expect(sorted).toEqual(['Downloading', 'Failed', 'Queued', 'Retrying']);
  });

  it('sorts every column', () => {
    const rows = [
      job('a', 'queued', { totalBytes: 10, queueId: 2, bytesPerSecond: 5, estimatedRemaining: '00:00:30' }),
      job('b', 'queued', { totalBytes: 30, queueId: 0, bytesPerSecond: 1, estimatedRemaining: '00:00:10' }),
      job('c', 'queued', { queueId: 1, isQueued: false }),
    ];
    const ids = (key: Parameters<typeof sortRows>[2]['key'], ascending: boolean) => sortRows(rows, (j) => jobView(j), { key, ascending }).map((j) => j.id);
    expect(ids('size', false)).toEqual(['b', 'a', 'c']);
    expect(ids('queue', true)).toEqual(['b', 'a', 'c']);
    expect(ids('queue', false)).toEqual(['a', 'b', 'c']);
    expect(ids('rate', false)).toEqual(['a', 'b', 'c']);
    expect(ids('eta', true)).toEqual(['b', 'a', 'c']);
    expect(ids('name', false)).toEqual(['c', 'b', 'a']);
  });

  it('sorts a package row by its combined cells, and its files by name under the default order', () => {
    const model = emptyModel();
    apply(model, {
      v: 1, id: 'x', type: 'jobs.snapshot',
      payload: {
        jobs: [
          job('z', 'queued', { fileName: 'z.bin', totalBytes: 100 }),
          job('p2', 'queued', { fileName: 'part2.bin', totalBytes: 600, createdAt: '2026-11-01T10:00:02Z' }),
          job('p1', 'queued', { fileName: 'part10.bin', totalBytes: 600, createdAt: '2026-11-01T10:00:01Z' }),
        ],
        settings: { downloadGroups: [{ id: 'g', name: 'Pack', jobIds: ['p2', 'p1'] }] },
        status: {},
      },
    });
    expect(buildRows(model, 'all', '', new Set(), { key: 'size', ascending: false }).map((r) => r.id)).toEqual(['package:g', 'z']);
    expect(buildRows(model, 'all', '', new Set(), { key: 'size', ascending: true }).map((r) => r.id)).toEqual(['z', 'package:g']);
    expect(buildRows(model, 'all', '', new Set(['g'])).map((r) => r.id)).toEqual(['package:g', 'p2', 'p1', 'z']);
  });

  it('reads a saved sort and ignores nonsense', () => {
    expect(parseSort('{"key":"size","ascending":true}')).toEqual({ key: 'size', ascending: true });
    expect(parseSort('{"key":"bogus","ascending":true}')).toEqual({ key: 'added', ascending: false });
    expect(parseSort('not json')).toEqual({ key: 'added', ascending: false });
    expect(parseSort(null)).toEqual({ key: 'added', ascending: false });
  });
});

describe('keys', () => {
  it('moves the cursor and stays inside the list', () => {
    expect(moveCursor('ArrowDown', -1, 5)).toBe(0);
    expect(moveCursor('ArrowDown', 4, 5)).toBe(4);
    expect(moveCursor('ArrowUp', 0, 5)).toBe(0);
    expect(moveCursor('ArrowUp', 3, 5)).toBe(2);
    expect(moveCursor('Home', 3, 5)).toBe(0);
    expect(moveCursor('End', 3, 5)).toBe(4);
    expect(moveCursor('PageDown', 1, 30)).toBe(11);
    expect(moveCursor('PageUp', 3, 30)).toBe(0);
    expect(moveCursor('x', 3, 5)).toBeNull();
    expect(moveCursor('ArrowDown', 0, 0)).toBeNull();
  });

  it('leaves keys typed in a field to the field', async () => {
    const { isTypingTarget } = await import('../src/lib/list');
    expect(isTypingTarget({ tagName: 'INPUT' } as unknown as EventTarget)).toBe(true);
    expect(isTypingTarget({ tagName: 'TEXTAREA' } as unknown as EventTarget)).toBe(true);
    expect(isTypingTarget({ tagName: 'DIV', isContentEditable: true } as unknown as EventTarget)).toBe(true);
    expect(isTypingTarget({ tagName: 'DIV' } as unknown as EventTarget)).toBe(false);
    expect(isTypingTarget(null)).toBe(false);
  });

  const ids = ['a', 'b', 'c', 'd', 'e'];
  it('a plain click replaces the selection', () => {
    expect([...selectAfterClick(new Set(['a', 'b']), ids, 'c', 'a', { shift: false, ctrl: false })]).toEqual(['c']);
  });
  it('Ctrl toggles a row', () => {
    expect([...selectAfterClick(new Set(['a']), ids, 'c', 'a', { shift: false, ctrl: true })].sort()).toEqual(['a', 'c']);
    expect([...selectAfterClick(new Set(['a', 'c']), ids, 'c', 'a', { shift: false, ctrl: true })]).toEqual(['a']);
  });
  it('Shift replaces the selection with the range, and Ctrl+Shift adds it', () => {
    expect([...selectAfterClick(new Set(['a']), ids, 'd', 'b', { shift: true, ctrl: false })].sort()).toEqual(['b', 'c', 'd']);
    expect([...selectAfterClick(new Set(['a']), ids, 'd', 'c', { shift: true, ctrl: true })].sort()).toEqual(['a', 'c', 'd']);
    expect([...selectAfterClick(new Set(['a']), ids, 'b', 'e', { shift: true, ctrl: false })].sort()).toEqual(['b', 'c', 'd', 'e']);
  });
  it('Shift with no anchor is a plain click', () => {
    expect([...selectAfterClick(new Set(), ids, 'c', null, { shift: true, ctrl: false })]).toEqual(['c']);
  });
});

describe('status bar', () => {
  it('counts the desktop way: retrying files are not active', () => {
    const counts = countJobs([job('a', 'downloading'), job('b', 'connecting'), job('c', 'verifying'), job('d', 'retryWaiting'), job('e', 'queued'), job('f', 'paused'), job('g', 'completed')]);
    expect(counts).toEqual({ total: 7, active: 3, queued: 1, retrying: 1 });
    expect(statusBarText(counts, 0)).toBe('7 downloads · 3 active · 1 queued · 1 retrying');
    expect(statusBarText({ total: 2, active: 0, queued: 2, retrying: 0 }, 0)).toBe('2 downloads · 0 active · 2 queued');
    expect(statusBarText({ total: 2, active: 0, queued: 2, retrying: 0 }, 2)).toBe('2 downloads · 0 active · 2 queued  (2 selected)');
    expect(statusBarText({ total: 2, active: 0, queued: 2, retrying: 0 }, 1)).toBe('2 downloads · 0 active · 2 queued');
  });
});

describe('file menu rules', () => {
  it('gives each reason and dims accordingly', () => {
    expect(planFileMenu(true, false, 0, 0)).toMatchObject({ fileReason: null, openReason: null, removeReason: null });
    expect(planFileMenu(false, true, 0, 0).fileReason).toBe('select a file, not a package');
    expect(planFileMenu(false, false, 2, 0)).toMatchObject({ fileReason: 'no local file selected', removeReason: 'cancel the extraction first' });
    expect(planFileMenu(false, false, 2, 1).removeReason).toBeNull();
    expect(planFileMenu(false, false, 0, 0).fileReason).toBe('select a download first');
    expect(planFileMenu(true, false, 0, 0, true).openReason).toBe('archive removed after extraction');
    expect(menuLabel('Properties', 'select a download first')).toBe('Properties (select a download first)');
    expect(menuLabel('Properties', null)).toBe('Properties');
  });
});

describe('empty list', () => {
  it('has the desktop words per view', () => {
    expect(emptyText('all', 'TorBox')).toBe(defaultEmptyText);
    expect(emptyText('remote', 'TorBox')).toBe('Nothing is waiting for TorBox.\nItems show here until their files start downloading.');
    expect(emptyText('extractions', 'TorBox')).toBe('No archives yet.');
  });
});

describe('provider and archive rows', () => {
  const remote = (extra: Partial<RemoteJob>): RemoteJob => ({
    id: 'r', provider: 'torbox', providerName: 'TorBox', kind: 'torrent', state: 'waiting', status: 'Waiting', progress: 0, speedBytesPerSecond: 0, cached: false,
    approvalNeeded: false, heldForApproval: false, hasLinkPassword: false, hasArchivePassword: false, queueId: 0, selectionMode: 'all', notes: [],
    createdAt: '2026-11-01T10:00:00Z', updatedAt: '2026-11-01T10:00:00Z', ...extra,
  });
  const set = (extra: Partial<ArchiveSet>): ArchiveSet => ({
    id: 's', name: 'Set', directory: '/d', firstVolume: 'a.rar', state: 'completed', stateText: 'Extracted', needsPassword: false, hasPassword: false, volumes: [],
    missingVolumes: [], downloadingVolumes: [], failedVolumes: [], jobIds: [], updatedAt: '2026-11-01T10:00:00Z', ...extra,
  });

  it('Enter does a single row\'s next step, and for several the one thing they all wait for', () => {
    expect(remoteEnterAction([])).toBe('none');
    expect(remoteEnterAction([remote({ state: 'awaitingSelection' })])).toBe('selectFiles');
    expect(remoteEnterAction([remote({ state: 'intended', approvalNeeded: true })])).toBe('approve');
    expect(remoteEnterAction([remote({ state: 'failed', failureKind: 'linkPasswordRequired' })])).toBe('enterLinkPassword');
    expect(remoteEnterAction([remote({ state: 'failed' })])).toBe('retry');
    expect(remoteEnterAction([remote({ state: 'waiting' })])).toBe('checkNow');
    expect(remoteEnterAction([remote({ state: 'localStarted' })])).toBe('none');
    expect(remoteEnterAction([remote({ state: 'intended', approvalNeeded: true }), remote({ state: 'intended', approvalNeeded: true })])).toBe('approve');
    expect(remoteEnterAction([remote({ state: 'awaitingSelection' }), remote({ state: 'awaitingSelection' })])).toBe('selectFiles');
    expect(remoteEnterAction([remote({ state: 'awaitingSelection' }), remote({ state: 'failed' })])).toBe('none');
  });

  it('runs an archive row\'s main action', () => {
    expect(archivePrimary(set({ needsPassword: true, state: 'failed' }))).toBe('password');
    expect(archivePrimary(set({ state: 'failed' }))).toBe('retry');
    expect(archivePrimary(set({ state: 'completed', archivesDeleted: true }))).toBe('none');
    expect(archivePrimary(set({ state: 'completed', destination: 'D:/x' }))).toBe('copyFolder');
  });

  it('puts a record on the list as a row of its own only when none of its files are there', () => {
    expect(archiveHasOwnRow(set({ jobIds: ['x'] }), { x: job('x', 'completed') }, true)).toBe(false);
    expect(archiveHasOwnRow(set({ jobIds: ['y'] }), { x: job('x', 'completed') }, true)).toBe(true);
    // Until the list of downloads is here, every record would look like one.
    expect(archiveHasOwnRow(set({ jobIds: ['y'] }), {}, false)).toBe(false);
  });

  it('shows such records above the files in All Downloads, and all records in Extractions, newest first', () => {
    const model = emptyModel();
    apply(model, { v: 1, id: 'x', type: 'jobs.snapshot', payload: { jobs: [job('x', 'completed')], settings: { downloadGroups: [] }, status: {} } });
    apply(model, { v: 1, id: 'y', type: 'archive.listResult', payload: { sets: [
      set({ id: 'old', jobIds: ['gone'], createdAt: '2026-11-01T00:00:00Z', updatedAt: '2026-11-08T00:00:00Z' }),
      set({ id: 'new', jobIds: ['gone2'], createdAt: '2026-11-05T00:00:00Z', updatedAt: '2026-11-05T00:00:00Z' }),
      set({ id: 'attached', jobIds: ['x'] }),
    ] } });
    expect(model.archivesAnswered).toBe(true);
    expect(buildRows(model, 'all', '', new Set()).map((r) => r.id)).toEqual(['archive:new', 'archive:old', 'x']);
    expect(buildRows(model, 'extractions', '', new Set()).map((r) => r.id)).toEqual(['archive:new', 'archive:attached', 'archive:old']);
    // The state text is searched, not just the name.
    apply(model, { v: 1, id: 'z', type: 'archive.changed', payload: { set: set({ id: 'new', jobIds: ['gone2'], stateText: 'Wrong password given' }) } });
    expect(buildRows(model, 'extractions', 'wrong password', new Set()).map((r) => r.id)).toEqual(['archive:new']);
  });

  it('searches a provider row\'s status and description', () => {
    const model = emptyModel();
    apply(model, { v: 1, id: 'x', type: 'remote.listResult', payload: { ok: true, jobs: [remote({ id: 'a', name: 'Show', status: 'Caching at the provider', stage: 'downloading' })] } });
    expect(buildRows(model, 'all', 'caching', new Set()).map((r) => r.id)).toEqual(['remote:a']);
    expect(buildRows(model, 'all', 'nope', new Set()).map((r) => r.id)).toEqual([]);
  });

  it('leaves out provider rows that were removed', () => {
    const model = emptyModel();
    apply(model, { v: 1, id: 'x', type: 'remote.listResult', payload: { ok: true, jobs: [remote({ id: 'a', state: 'removed' }), remote({ id: 'b', state: 'localStarted' })] } });
    expect(buildRows(model, 'all', '', new Set())).toHaveLength(0);
    expect(counts(model).remote).toBe(0);
  });
});

describe('speed graph', () => {
  it('samples the active downloads, retrying ones included, and counts quiet ones as zero', () => {
    const now = Date.parse('2026-11-09T12:00:10Z');
    const fresh = '2026-11-09T12:00:05Z';
    const stale = '2026-11-09T11:59:00Z';
    const jobs = [
      job('a', 'downloading', { bytesPerSecond: 100, updatedAt: fresh }),
      job('b', 'retryWaiting', { bytesPerSecond: 40, updatedAt: fresh }),
      job('c', 'downloading', { bytesPerSecond: 999, updatedAt: stale }),
      job('d', 'paused', { bytesPerSecond: 500, updatedAt: fresh }),
    ];
    expect(speedSample(jobs, true, true, now)).toBe(140);
    expect(speedSample([], true, true, now)).toBe(0);
  });

  it('leaves a gap while the server is not reachable', () => {
    expect(speedSample([], false, true, 0)).toBeNull();
    expect(speedSample([], true, false, 0)).toBeNull();
  });

  it('keeps a limited history with its gaps', () => {
    const history = new SpeedHistory(3);
    for (const v of [1, null, 5, 2]) history.add(v);
    expect(history.samples).toEqual([null, 5, 2]);
    expect(history.peak()).toBe(5);
  });

  it("replaces its samples with the server's seconds without showing one twice", () => {
    const empty = new SpeedHistory(4);
    // The last figure is the second still under way, so it is left for this page's own sample.
    empty.replace([1, 2, 3, 4, 9]);
    expect(empty.samples).toEqual([1, 2, 3, 4]);

    const sampled = new SpeedHistory(5);
    sampled.add(7);
    sampled.add(8);
    sampled.replace([1, 2, 3, 4, 5, 6]);
    expect(sampled.samples).toEqual([2, 3, 4, 5, 8]);

    const odd = new SpeedHistory(5);
    odd.replace([-5, Number.NaN, 3, 0]);
    expect(odd.samples).toEqual([0, 0, 3]);
  });

  it('fills the time a hidden tab left as a gap, and ignores a trailing gap', () => {
    const history = new SpeedHistory();
    history.add(5);
    history.add(null);
    history.add(null);
    history.replace([1, 2, 3, 4]);
    expect(history.samples).toEqual([1, 2, 3, 5]);
    history.replace([9, 9]);
    expect(history.samples).toEqual([9, 5]);
  });

  it("keeps the server's whole hour and draws the newest ten minutes", () => {
    const history = new SpeedHistory();
    expect(history.limit).toBe(3600);
    history.replace(Array.from({ length: 3601 }, (_, i) => i));
    expect(history.samples).toHaveLength(3600);
    expect(history.recent()).toHaveLength(600);
    expect(history.recent()[599]).toBe(3599);
    expect(history.peak()).toBe(3599);
    history.add(1);
    expect(history.samples).toHaveLength(3600);
    expect(history.samples[0]).toBe(1);
    const short = new SpeedHistory();
    short.add(2);
    expect(short.recent()).toEqual([2]);
  });

  it('finds the sample under the pointer, newest at the right edge', () => {
    const step = 600 / 599;
    const samples = [1, null, 3, 4];
    // The line starts at 600 - 3 steps.
    expect(hoverPoint(samples, 600)).toEqual({ index: 3, rate: 4, age: 0 });
    expect(hoverPoint(samples, 600 - 2 * step)).toEqual({ index: 1, rate: null, age: 2 });
    expect(hoverPoint(samples, 600 - 3 * step - 0.2)).toEqual({ index: 0, rate: 1, age: 3 });
    expect(hoverPoint(samples, 100)).toBeNull();
    expect(hoverPoint([], 300)).toBeNull();
    expect(hoverPoint(samples, Number.NaN)).toBeNull();
  });

  it('words the age as the desktop graph does', () => {
    expect(agoText(0)).toBe('now');
    expect(agoText(30)).toBe('30 s ago');
    expect(agoText(125)).toBe('2 min 05 s ago');
    expect(agoText(3725)).toBe('1 h 02 min ago');
  });

  it('scales the top of the graph above the peak', () => {
    expect(graphCeiling(0)).toBe(64 * 1024);
    expect(graphCeiling(100 * 1024)).toBe(128 * 1024);
    expect(graphCeiling(5 * 1024 * 1024)).toBeGreaterThanOrEqual(5 * 1024 * 1024);
  });
});

describe('review fixes', () => {
  const set = (extra: Partial<ArchiveSet>): ArchiveSet => ({
    id: 's', name: 'Set', directory: '/d', firstVolume: 'a.rar', state: 'completed', stateText: 'Extracted', needsPassword: false, hasPassword: false, volumes: [],
    missingVolumes: [], downloadingVolumes: [], failedVolumes: [], jobIds: [], updatedAt: '2026-11-01T10:00:00Z', ...extra,
  });

  it('hides addresses in notes and description for a hidden or provider download', () => {
    const hidden = job('h', 'failed', { addressHidden: true, statusNote: 'Failed at https://cdn.example.org/a/b?token=1 today', pageUrl: 'https://page.example.net/x?k=2' });
    expect(hideAddresses('see https://cdn.example.org/a?t=1', hidden)).toBe('see cdn.example.org');
    const view = jobView(hidden);
    expect(view.description).not.toContain('token');
    expect(view.description).not.toContain('?k=2');
    expect(view.description).toContain('page.example.net');
    const plain = job('p', 'failed', { statusNote: 'see https://cdn.example.org/a?t=1' });
    expect(jobView(plain).description).toContain('?t=1');
  });

  it('never names a provider file by its remote address', () => {
    expect(jobView(job('r', 'queued', { url: 'remote://torbox/9', fileName: '' })).name).toBe('');
  });

  it('offers Extract Again for a completed set that still has its archives', () => {
    const done = set({ state: 'completed' });
    expect(canRetry(done)).toBe(true);
    expect(canRetry({ ...done, archivesDeleted: true })).toBe(false);
    expect(archiveRetryText([done])).toBe('Extract Again…');
    expect(archiveRetryText([set({ state: 'failed' })])).toBe('Retry Extraction');
    expect(archivePasswordText(targetOf(set({ needsPassword: true })).needed)).toBe('Enter Archive Password…');
    expect(archivePasswordText(targetOf(set({})).needed)).toBe('Archive Password…');
  });

  it('shows provider entries by the desktop gating', () => {
    const account = (hasKey: boolean, catalog: boolean) => ({ provider: 'torbox', providerName: 'TorBox', capabilities: catalog ? ['catalog'] : [], itemKinds: catalog ? ['torrent'] : [], hasKey, keyUnreadable: false, premium: false });
    expect(providerEntries([account(false, false)], [])).toEqual([{ provider: 'torbox', name: 'TorBox', waiting: 0, showWaiting: false, showBrowse: false }]);
    const left = { provider: 'torbox', state: 'waiting' } as RemoteJob;
    expect(providerEntries([account(false, false)], [left])[0]).toMatchObject({ waiting: 1, showWaiting: true, showBrowse: false });
    expect(providerEntries([account(true, true)], [])[0]).toMatchObject({ showWaiting: true, showBrowse: true });
  });
});
