import { describe, expect, it } from 'vitest';
import { cantTell, kindWord, linesOf, routeOf, showsAddress, type AddLine } from '../src/lib/addlines';
import { failureNextStep, failureText } from '../src/lib/details';
import { bytes, duration, jobStateWord, percent, stateWord, statusText, timeSpanSeconds, tone } from '../src/lib/format';
import { fileKindIcon, iconPaths } from '../src/lib/icons';
import { apply, emptyModel, hasCapability } from '../src/lib/model';
import type { Envelope } from '../src/lib/protocol';
import type { ClassifyResult, Job } from '../src/lib/types';
import { buildRows, counts, selectedJobIds } from '../src/lib/views';

function message(type: string, payload: unknown): Envelope {
  return { v: 1, type, id: 'x', payload };
}

let order = 0;
function job(id: string, state: string, extra: Partial<Job> = {}): Job {
  order++;
  return {
    id, url: `https://example.com/${id}`, fileName: `${id}.bin`, destinationDirectory: '/data', finalFilePath: `/data/${id}.bin`,
    receivedBytes: 0, state, createdAt: `2026-11-08T00:00:${String(order).padStart(2, '0')}Z`, updatedAt: '2026-11-08T00:00:00Z', queueId: 0, ...extra,
  };
}

function snapshot(jobs: Job[], groups: { id: string; name: string; jobIds: string[] }[] = []) {
  const model = emptyModel();
  apply(model, message('server.hello', { hostVersion: '1', protocolVersion: 1, startedAt: '', capabilities: ['remote', 'archives'] }));
  apply(model, message('jobs.snapshot', { jobs, settings: { downloadGroups: groups, queues: [], schedules: [] }, status: { totalJobs: jobs.length } }));
  return model;
}

describe('model', () => {
  it('takes the hello, the snapshot and job events', () => {
    const model = snapshot([job('a', 'queued')]);
    expect(hasCapability(model, 'remote')).toBe(true);
    expect(hasCapability(model, 'nothing')).toBe(false);
    expect(model.snapshots).toBe(1);
    expect(Object.keys(model.jobs)).toEqual(['a']);

    apply(model, message('job.event', { eventType: 'progress', job: job('a', 'downloading', { receivedBytes: 10 }) }));
    expect(model.jobs.a.state).toBe('downloading');
    apply(model, message('job.event', { eventType: 'added', job: job('b', 'queued') }));
    expect(Object.keys(model.jobs).sort()).toEqual(['a', 'b']);
    apply(model, message('job.event', { eventType: 'removed', job: job('a', 'cancelled') }));
    expect(Object.keys(model.jobs)).toEqual(['b']);
  });

  it('replaces the list on a new snapshot', () => {
    const model = snapshot([job('a', 'queued')]);
    apply(model, message('jobs.snapshot', { jobs: [job('c', 'completed')], settings: { downloadGroups: [] } }));
    expect(Object.keys(model.jobs)).toEqual(['c']);
    expect(model.snapshots).toBe(2);
  });

  it('keeps settings, groups, provider work, the account and archive sets', () => {
    const model = snapshot([]);
    apply(model, message('settings.result', { ok: true, settings: { defaultDownloadDirectory: '/x', downloadGroups: [{ id: 'g', name: 'G', jobIds: [] }] } }));
    expect(model.settings?.defaultDownloadDirectory).toBe('/x');
    expect(model.groups).toHaveLength(1);
    apply(model, message('settings.result', { ok: false, error: 'no' }));
    expect(model.settings?.defaultDownloadDirectory).toBe('/x');

    apply(model, message('remote.listResult', { ok: true, jobs: [{ id: 'r1', state: 'waiting' }] }));
    apply(model, message('remote.changed', { change: 'job', job: { id: 'r2', state: 'waiting' } }));
    apply(model, message('remote.changed', { change: 'removed', jobId: 'r1' }));
    expect(Object.keys(model.remoteJobs)).toEqual(['r2']);

    apply(model, message('remote.account.result', { ok: true, accounts: [{ provider: 'torbox', hasKey: false }] }));
    apply(model, message('remote.changed', { change: 'account', account: { provider: 'torbox', hasKey: true } }));
    expect(model.accounts).toHaveLength(1);
    expect(model.accounts[0].hasKey).toBe(true);

    apply(model, message('archive.listResult', { sets: [{ id: 's1', jobIds: [] }] }));
    apply(model, message('archive.changed', { set: { id: 's2', jobIds: [] } }));
    apply(model, message('archive.changed', { removedId: 's1' }));
    expect(Object.keys(model.archives)).toEqual(['s2']);
  });

  it('ignores types it does not know', () => {
    const model = snapshot([]);
    expect(apply(model, message('something.new', { anything: 1 }))).toBe(false);
  });
});

describe('views', () => {
  it('counts each view with the desktop rules', () => {
    const model = snapshot([job('q', 'queued'), job('p', 'paused'), job('d', 'downloading'), job('f', 'failed'), job('c', 'completed'), job('x', 'cancelled')]);
    apply(model, message('remote.listResult', { ok: true, jobs: [{ id: 'r', state: 'waiting', createdAt: '' }, { id: 'l', state: 'localStarted', createdAt: '' }] }));
    const n = counts(model);
    expect(n).toMatchObject({ all: 6, unfinished: 4, active: 1, queued: 2, failed: 1, finished: 1, remote: 1 });
  });

  it('shows a package of two or more as one row, and its files when expanded', () => {
    const model = snapshot([job('a', 'downloading', { totalBytes: 100, receivedBytes: 50 }), job('b', 'completed', { totalBytes: 100, receivedBytes: 100 }), job('solo', 'queued')],
      [{ id: 'g', name: 'Pack', jobIds: ['a', 'b'] }, { id: 'one', name: 'One', jobIds: ['solo', 'gone'] }]);
    const rows = buildRows(model, 'all', '', new Set());
    expect(rows.map((r) => r.kind)).toEqual(['job', 'package']);
    const pack = rows.find((r) => r.kind === 'package');
    expect(pack && pack.kind === 'package' && pack.summary).toMatchObject({ state: 'active', completedCount: 1, memberCount: 2, progress: 0.75 });

    const open = buildRows(model, 'all', '', new Set(['g']));
    expect(open.map((r) => r.id)).toEqual(['solo', 'package:g', 'a', 'b']);
    // Selecting the package selects its files.
    expect(selectedJobIds(open, new Set(['package:g'])).sort()).toEqual(['a', 'b']);

    // In Finished only its finished file is listed under it.
    const finished = buildRows(model, 'finished', '', new Set(['g']));
    expect(finished.map((r) => r.id)).toEqual(['package:g', 'b']);
    // Selecting it there stands for that file only, not the one still downloading.
    expect(selectedJobIds(finished, new Set(['package:g']))).toEqual(['b']);
  });

  it('searches names but not hidden addresses', () => {
    const model = snapshot([job('alpha', 'queued'), job('beta', 'queued', { url: 'https://secret.example/alpha', addressHidden: true })]);
    expect(buildRows(model, 'all', 'alpha', new Set()).map((r) => r.id)).toEqual(['alpha']);
  });

  it('puts provider work first in All and alone in its own view', () => {
    const model = snapshot([job('a', 'queued')]);
    apply(model, message('remote.listResult', { ok: true, jobs: [{ id: 'r', state: 'waiting', name: 'Show', createdAt: '2026' }] }));
    expect(buildRows(model, 'all', '', new Set()).map((r) => r.kind)).toEqual(['remote', 'job']);
    expect(buildRows(model, 'remote', '', new Set()).map((r) => r.kind)).toEqual(['remote']);
  });
});

describe('add lines', () => {
  function line(input: string, result: Partial<ClassifyResult> | null, direct = false): AddLine {
    return { input, position: 1, result: result ? ({ ok: true, checkIncomplete: false, kind: '', ...result } as ClassifyResult) : null, error: null, direct };
  }

  it('splits, trims and drops repeated lines', () => {
    expect(linesOf(' a \r\n\nb\na\n  \nc')).toEqual(['a', 'b', 'c']);
    expect(linesOf(Array.from({ length: 1500 }, (_, i) => `l${i}`).join('\n'))).toHaveLength(1000);
  });

  it('never sends a provider link directly', () => {
    for (const kind of ['magnet', 'torrentFile', 'hosterLink']) expect(routeOf(line('x', { kind }))).toBe('provider');
    expect(routeOf(line('x', { kind: 'folderLink', itemId: '1', itemKind: 'torrent', provider: 'p' }))).toBe('provider');
    expect(routeOf(line('x', { kind: 'folderLink' }))).toBe('none');
    expect(routeOf(line('https://a/b', { kind: 'ordinaryLink' }))).toBe('direct');
    expect(routeOf(line('https://a/b', { kind: 'providerDirectLink' }))).toBe('direct');
    expect(routeOf(line('https://a/b', { kind: 'brandNewKind' }))).toBe('none');
    expect(routeOf(line('https://a/b', null))).toBe('checking');
  });

  it('sends a line nobody could classify directly only when ticked, and only a web link', () => {
    const unsure = line('https://a/b', { kind: 'undetermined' });
    expect(cantTell(unsure)).toBe(true);
    expect(routeOf(unsure)).toBe('none');
    expect(routeOf({ ...unsure, direct: true })).toBe('direct');
    expect(routeOf(line('ftp://a/b', { kind: 'undetermined' }, true))).toBe('none');
    expect(routeOf(line('https://a/b', { ok: false, kind: '' }, true))).toBe('direct');
    expect(kindWord(unsure, 'TorBox')).toBe("Can't tell");
    expect(kindWord(line('x', { kind: 'hosterLink' }), 'TorBox')).toBe('Hoster through TorBox');
  });

  it('shows the address of an ordinary link only', () => {
    expect(showsAddress(line('https://a/b', { kind: 'ordinaryLink' }))).toBe(true);
    expect(showsAddress(line('https://h/f?key=1', { kind: 'hosterLink' }))).toBe(false);
    expect(showsAddress(line('magnet:?x', { kind: 'magnet' }))).toBe(false);
  });
});

describe('format', () => {
  it('writes sizes 1024-based', () => {
    expect(bytes(0)).toBe('0 B');
    expect(bytes(1536)).toBe('1.50 KB');
    expect(bytes(10 * 1024 * 1024)).toBe('10.0 MB');
    expect(bytes(undefined)).toBe('');
  });

  it('reads .NET time spans', () => {
    expect(timeSpanSeconds('00:01:05')).toBe(65);
    expect(timeSpanSeconds('1.02:00:00')).toBe(93600);
    expect(timeSpanSeconds('00:00:01.5000000')).toBe(1.5);
    expect(timeSpanSeconds('soon')).toBeNull();
    // The desktop list's words (DurationText.TimeLeft), the same in the web UI and the extension (EXT.R8 Q2).
    expect(duration(3725)).toBe('1h 2m');
    expect(duration(105)).toBe('1m 45s');
    expect(duration(45.9)).toBe('45s');
    expect(duration(0.4)).toBe('1s');
    expect(duration(0)).toBe('');
    expect(duration(100000)).toBe('27h 46m');
  });

  it('takes progress as a fraction', () => {
    expect(percent({ progress: 0.25, receivedBytes: 0 })).toBe(25);
    expect(percent({ totalBytes: 200, receivedBytes: 50 })).toBe(25);
    expect(percent({ receivedBytes: 50 })).toBeNull();
    expect(statusText(job('a', 'downloading', { progress: 0.5 }))).toBe('Downloading 50%');
  });
});

describe('words and glyphs shared with the browser extension (EXT.R8)', () => {
  it('names every state the extension shows', () => {
    expect(stateWord('muxing')).toBe('Merging');
    expect(stateWord('scheduled')).toBe('Scheduled');
    expect(stateWord('retryWaiting')).toBe('Retrying');
    expect(tone('retryWaiting')).toBe('waiting');
  });

  it("groups file kinds as the desktop list's glyphs do", () => {
    expect(fileKindIcon('linux-desktop-24.04-amd64.iso')).toBe('disc');
    expect(fileKindIcon('project-assets.part2.rar')).toBe('archive-file');
    expect(fileKindIcon('installer-2.4.1.EXE')).toBe('program');
    expect(fileKindIcon('notes')).toBe('file');
    for (const name of ['disc', 'archive-file', 'program', 'document', 'music', 'video', 'image', 'file']) expect(iconPaths[name]).toBeTruthy();
  });
});

describe('review 5 words shared with the desktop list (A8, E2)', () => {
  it('says Not started for a download added with Download later, and Queued for one waiting for a slot', () => {
    expect(jobStateWord(job('a', 'queued', { startMode: 'later' }))).toBe('Not started');
    expect(statusText(job('a', 'queued', { startMode: 'later' }))).toBe('Not started');
    expect(jobStateWord(job('a', 'queued', { startMode: 'startNow' }))).toBe('Queued');
    expect(jobStateWord(job('a', 'queued'))).toBe('Queued');
    expect(jobStateWord(job('a', 'paused', { startMode: 'later' }))).toBe('Paused');
  });

  it('adds what to do next to a failure named by its HTTP code', () => {
    const failed = (kind: string, httpStatus: number | undefined, extra: Partial<Job> = {}) =>
      job('a', 'failed', { failure: { kind, httpStatus, message: `The server returned HTTP ${httpStatus}.` }, ...extra });
    expect(failureText(failed('forbidden', 403))).toBe('The server returned HTTP 403. The site may want you signed in: add a login in Options > Site logins, or download it from the browser.');
    expect(failureText(failed('unauthorized', 401))).toContain('Options > Site logins');
    expect(failureNextStep(failed('unauthorized', 407))).toBeNull();
    expect(failureText(failed('notFound', 404))).toBe('The server returned HTTP 404. The link may have expired: try Refresh Download Address.');
    expect(failureNextStep(failed('notFound', 404, { url: 'remote://torbox/1/2' }))).toBeNull();
    // An FTP 550 is notFound with no HTTP code, and Refresh Download Address doesn't fit an FTP path.
    expect(failureNextStep(failed('notFound', undefined))).toBeNull();
    expect(failureText(failed('http', 503))).toBe('The server returned HTTP 503. The site had a problem. Try again later.');
    expect(failureNextStep(failed('http', 400))).toBeNull();
    // Only a failed download gets the step; a failure kept while it retries is its words alone.
    expect(failureText({ ...failed('forbidden', 403), state: 'retryWaiting' })).toBe('The server returned HTTP 403.');
  });
});
