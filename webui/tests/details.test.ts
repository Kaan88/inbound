import { describe, expect, it } from 'vitest';
import {
  addressLabel, barValue, cancelTargets, categoryChoices, categoryOf, completionFrom, detailRows, failureLabel, fileToShow, hasOwnAddress,
  heading, looksLikeArchive, mergeSegments, moveTarget, noticeFor, noticeText, packageActionNotice, packageFigures, pauseTargets,
  programModeOf, displaySource, hideAddresses, sortedByName, updateRates, redownloadRefusal, refreshStart, resumeTargets, segmentRows, shownAddress, showsArchiveSection, speedLimitFrom,
  speedLimitText, splitPath, stemLength,
} from '../src/lib/details';
import type { Job, Segment } from '../src/lib/types';

function job(patch: Partial<Job> = {}): Job {
  return {
    id: 'a', url: 'https://example.com/a.zip', fileName: 'a.zip', destinationDirectory: 'C:\\Downloads', finalFilePath: 'C:\\Downloads\\a.zip',
    receivedBytes: 0, state: 'queued', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', queueId: 0, ...patch,
  };
}

describe('address (D13, D14)', () => {
  it('has none for a provider file, and names a hidden one a site', () => {
    expect(hasOwnAddress(job())).toBe(true);
    expect(hasOwnAddress(job({ isRemote: true }))).toBe(false);
    expect(hasOwnAddress(job({ url: 'remote://torbox/1' }))).toBe(false);
    expect(shownAddress(job({ isRemote: true }))).toBe('');
    expect(addressLabel(job({ addressHidden: true }))).toBe('Site');
    expect(shownAddress(job({ addressHidden: true, url: 'https://files.example.net/x?token=abc' }))).toBe('files.example.net');
    expect(shownAddress(job({ addressHidden: true, url: 'nonsense' }))).toBe('A direct link');
    expect(shownAddress(job())).toBe('https://example.com/a.zip');
  });

  it('starts the refresh box empty for a hidden address and with the address otherwise', () => {
    expect(refreshStart(job())).toBe('https://example.com/a.zip');
    expect(refreshStart(job({ addressHidden: true }))).toBe('');
  });
});

describe('rows and bar (D3, D11, D18, D19)', () => {
  it('always shows the six rows in the desktop order, with a dash for no value', () => {
    const rows = detailRows(job());
    expect(rows.map((r) => r.label)).toEqual(['Status', 'File size', 'Downloaded', 'Transfer rate', 'Time left', 'Resume capability']);
    expect(rows.map((r) => r.value)).toEqual(['Queued', 'Unknown', '0 B', '—', '—', 'Unknown']);
  });

  it('shows the percent next to the byte count, Complete once finished, and the resume answer', () => {
    const rows = detailRows(job({ state: 'completed', totalBytes: 2048, receivedBytes: 2048, resumeSupported: true, estimatedRemaining: '00:00:05' }));
    expect(rows.find((r) => r.label === 'Downloaded')?.value).toBe('2.00 KB (100.00%)');
    expect(rows.find((r) => r.label === 'Time left')?.value).toBe('Completed');
    expect(rows.find((r) => r.label === 'Resume capability')?.value).toBe('Yes');
    expect(detailRows(job({ resumeSupported: false })).at(-1)?.value).toBe('No');
  });

  it('pulses only while bytes are worked on and the size is unknown', () => {
    expect(barValue(job({ state: 'downloading' }))).toBeNull();
    expect(barValue(job({ state: 'connecting' }))).toBeNull();
    expect(barValue(job({ state: 'verifying' }))).toBeNull();
    for (const state of ['queued', 'paused', 'failed', 'cancelled', 'retryWaiting']) expect(barValue(job({ state }))).toBe(0);
    expect(barValue(job({ state: 'downloading', totalBytes: 100, receivedBytes: 50 }))).toBe(50);
    expect(barValue(job({ state: 'completed' }))).toBe(100);
  });

  it('keeps the failure on show while retrying, and hints about the state otherwise', () => {
    const failed = job({ state: 'failed', failure: { kind: 'x', message: 'Server said no' } });
    const retrying = job({ state: 'retryWaiting', failure: { kind: 'x', message: 'Timed out' } });
    expect(failureLabel(failed)).toBe('Why it failed');
    expect(failureLabel(retrying)).toBe('Last error');
    expect(failureLabel(job())).toBeNull();
    expect(noticeText(retrying)).toBe('Timed out');
    expect(noticeText(job({ state: 'paused' }))).toBe('The download is paused. Resume it or close this window.');
    expect(noticeText(job({ state: 'downloading' }))).toBe('Closing this window will not stop the download.');
    expect(noticeText(job({ state: 'completed', fileRemovedAfterExtraction: true }))).toBe('Archive removed after extraction, so there is no file to open.');
    expect(heading(job({ state: 'completed' }))).toBe('Download complete');
    expect(heading(job({ state: 'paused' }))).toBe('Stopped');
  });
});

describe('speed limiter (D1)', () => {
  it('turns KB/s into bytes per second and off into 0', () => {
    expect(speedLimitFrom(false, 'garbage')).toEqual({ ok: true, bytesPerSecond: 0 });
    expect(speedLimitFrom(true, '100')).toEqual({ ok: true, bytesPerSecond: 102400 });
    expect(speedLimitFrom(true, '1,5')).toEqual({ ok: true, bytesPerSecond: 1536 });
    expect(speedLimitFrom(true, ' 0.5 ')).toEqual({ ok: true, bytesPerSecond: 512 });
  });

  it('refuses anything that is not a positive number, in the desktop words', () => {
    for (const text of ['', '0', '-3', 'abc', '1e5', '0.0001']) {
      const result = speedLimitFrom(true, text);
      expect(result.ok).toBe(false);
    }
    expect(speedLimitFrom(true, 'abc')).toEqual({ ok: false, error: 'Enter a positive speed in KB/s, or turn the limiter off.' });
    expect(speedLimitFrom(true, '0.0001')).toEqual({ ok: false, error: 'The speed limit must be at least one byte per second.' });
  });

  it('writes a stored limit back as KB/s', () => {
    expect(speedLimitText(102400)).toBe('100');
    expect(speedLimitText(1536)).toBe('1.5');
    expect(speedLimitText(0)).toBe('');
    expect(speedLimitText(null)).toBe('');
  });
});

describe('segments (D2)', () => {
  const segment = (start: number, end: number, completed: number): Segment => ({ start, end, completed, isComplete: false, fraction: 0 });

  it('shows a single-stream download as one segment over the whole file', () => {
    const merged = mergeSegments([], [], { totalBytes: 1000, receivedBytes: 250 });
    expect(merged).toHaveLength(1);
    expect(merged[0]).toMatchObject({ start: 0, end: 999, completed: 250, isComplete: false });
    expect(mergeSegments([], [], { totalBytes: undefined, receivedBytes: 5 })).toEqual([]);
  });

  it('takes the furthest known point from the live connections and never goes backwards', () => {
    const water = new Map<number, number>();
    const conn = (rangeStart: number, receivedBytes: number) => ({ id: 'c', host: 'h', state: 'x', rangeStart, receivedBytes, bytesPerSecond: 100, startedAt: '' });
    const first = mergeSegments([segment(0, 99, 10), segment(100, 199, 0)], [conn(0, 40), conn(150, 20)], { totalBytes: 200, receivedBytes: 0 }, water);
    expect(first.map((s) => s.completed)).toEqual([40, 70]);
    const second = mergeSegments([segment(0, 99, 10), segment(100, 199, 0)], [], { totalBytes: 200, receivedBytes: 0 }, water);
    expect(second.map((s) => s.completed)).toEqual([40, 70]);
    const done = mergeSegments([segment(0, 99, 100)], [], { totalBytes: 100, receivedBytes: 100 });
    expect(done[0].isComplete).toBe(true);
  });

  it('words the table rows like the desktop', () => {
    const segments = mergeSegments([segment(0, 1023, 512), segment(1024, 2047, 1024)], [{ id: 'c', host: 'h', state: 'x', rangeStart: 0, receivedBytes: 512, bytesPerSecond: 2048, startedAt: '' }], { totalBytes: 2048, receivedBytes: 0 });
    const live = [{ id: 'c', host: 'h', state: 'x', rangeStart: 0, receivedBytes: 512, bytesPerSecond: 2048, startedAt: '' }];
    const rates = new Map<number, number>();
    updateRates(segments, live, rates);
    const rows = segmentRows(segments, live, rates);
    expect(rows[0]).toMatchObject({ number: 1, percent: 50, downloaded: '512 B / 1.00 KB', speed: '2.00 KB/s', info: 'Receiving data…' });
    expect(rows[1]).toMatchObject({ number: 2, percent: 100, speed: '—', info: 'Done' });
    const idle = segmentRows(mergeSegments([segment(0, 99, 0)], [], { totalBytes: 100, receivedBytes: 0 }), []);
    expect(idle[0].info).toBe('Waiting');
  });
});

describe('review fixes', () => {
  it('moves the smoothed speed only when updateRates is called, never when rows are drawn', () => {
    const segments: Segment[] = [{ start: 0, end: 99, completed: 10, isComplete: false, fraction: 0.1 }];
    const live = [{ id: 'c', host: 'h', state: 'x', rangeStart: 0, receivedBytes: 10, bytesPerSecond: 1000, startedAt: '' }];
    const rates = new Map<number, number>();
    updateRates(segments, live, rates);
    expect(rates.get(0)).toBe(1000);
    segmentRows(segments, live, rates);
    segmentRows(segments, live, rates);
    expect(rates.get(0)).toBe(1000);
    updateRates(segments, [{ ...live[0], bytesPerSecond: 2000 }], rates);
    expect(rates.get(0)).toBe(1250);
  });

  it('does not take a plain .t10 or .s99 file for an archive part', () => {
    expect(looksLikeArchive('notes.t10')).toBe(false);
    expect(looksLikeArchive('report.s99')).toBe(false);
    expect(looksLikeArchive('movie.z01')).toBe(true);
    expect(looksLikeArchive('movie.r07')).toBe(true);
  });

  it('accepts a speed that starts with a point', () => {
    expect(speedLimitFrom(true, '.5')).toEqual({ ok: true, bytesPerSecond: 512 });
  });

  it('sorts package files plainly, like the desktop', () => {
    const names = sortedByName([job({ id: '1', fileName: 'file2' }), job({ id: '2', fileName: 'File10' }), job({ id: '3', fileName: 'a' })]).map((j) => j.fileName);
    expect(names).toEqual(['a', 'File10', 'file2']);
  });

  it("shows time left in the desktop list's words (EXT.R8 Q2)", () => {
    const left = (estimatedRemaining: string) =>
      detailRows(job({ state: 'downloading', totalBytes: 100, receivedBytes: 50, estimatedRemaining })).find((r) => r.label === 'Time left')?.value;
    expect(left('00:00:05')).toBe('5s');
    expect(left('00:01:45')).toBe('1m 45s');
    expect(left('01:02:05')).toBe('1h 2m');
    expect(left('1.00:01:01')).toBe('24h 1m');
    expect(left('00:00:00')).toBe('—');
  });

  it('takes the percent from the byte counts', () => {
    const rows = detailRows(job({ state: 'downloading', totalBytes: 1000, receivedBytes: 250, progress: 0.9 }));
    expect(rows.find((r) => r.label === 'Downloaded')?.value).toContain('(25.00%)');
  });

  it('hides web addresses in notes and words the source like the desktop', () => {
    expect(hideAddresses('Moved to https://cdn.example.com/a?token=1 today')).toBe('Moved to a web address today');
    expect(hideAddresses(undefined)).toBe('');
    expect(displaySource('chromeContextMenu')).toBe('Browser menu');
    expect(displaySource('chromeAuto')).toBe('Browser');
    expect(displaySource('cli')).toBe('CLI');
    expect(displaySource('web')).toBe('App');
  });
});

describe('redownload (D12)', () => {
  it('refuses with the desktop words', () => {
    expect(redownloadRefusal(job({ state: 'downloading' }))).toBe('Stop the current download before redownloading.');
    expect(redownloadRefusal(job({ state: 'paused' }))).toBe('Stop the current download before redownloading.');
    expect(redownloadRefusal(job({ state: 'failed', isRemote: true }))).toMatch(/provider account/);
    expect(redownloadRefusal(job({ state: 'completed', addressHidden: true }))).toMatch(/key hidden/);
    expect(redownloadRefusal(job({ state: 'completed' }))).toBeNull();
    expect(redownloadRefusal(job({ state: 'cancelled' }))).toBeNull();
  });
});

describe('move and rename (D6)', () => {
  it('splits a path of either kind and selects the stem', () => {
    expect(splitPath('C:\\Downloads\\a.tar.gz')).toEqual({ directory: 'C:\\Downloads', name: 'a.tar.gz' });
    expect(splitPath('/data/a.zip')).toEqual({ directory: '/data', name: 'a.zip' });
    expect(stemLength('a.zip')).toBe(1);
    expect(stemLength('noext')).toBe(5);
    expect(stemLength('.hidden')).toBe(7);
  });

  it('builds the target with the old separator and sends nothing when it did not change', () => {
    expect(moveTarget('C:\\Downloads\\a.zip', 'D:\\Files\\', 'b.zip')).toBe('D:\\Files\\b.zip');
    expect(moveTarget('/data/a.zip', '/other', ' b.zip ')).toBe('/other/b.zip');
    expect(moveTarget('C:\\Downloads\\a.zip', 'C:\\Downloads', 'a.zip')).toBeNull();
    expect(moveTarget('C:\\Downloads\\a.zip', '', 'a.zip')).toBeNull();
    expect(moveTarget('C:\\Downloads\\a.zip', 'C:\\Downloads', '  ')).toBeNull();
  });
});

describe('categories and completion options (D7, D8)', () => {
  it('offers automatic, the six built in, then the custom ones', () => {
    const choices = categoryChoices([{ id: 'c1', name: 'Books' }]);
    expect(choices.map((c) => c.label)).toEqual(['Automatic (file extension)', 'Compressed', 'Documents', 'Music', 'Programs', 'Video', 'Other', 'Books']);
    expect(choices[0].id).toBe('');
    expect(categoryChoices(undefined)).toHaveLength(7);
  });

  it('starts on automatic unless the category was chosen', () => {
    expect(categoryOf(job({ categoryId: 'video', categoryIsAutomatic: true }))).toBe('');
    expect(categoryOf(job({ categoryId: 'video', categoryIsAutomatic: false }))).toBe('video');
  });

  it('reads the program mode from the path', () => {
    expect(programModeOf(undefined)).toBe('global');
    expect(programModeOf({ programPath: null })).toBe('global');
    expect(programModeOf({ programPath: '' })).toBe('none');
    expect(programModeOf({ programPath: 'C:\\x.exe' })).toBe('run');
  });

  it('builds options and says what is wrong', () => {
    expect(completionFrom('global', '', false, 'none', false, false)).toEqual({ ok: true, options: { programPath: null, openFile: false, systemAction: 'none', forceSystemAction: false } });
    expect(completionFrom('none', '', true, 'none', false, false)).toMatchObject({ ok: true, options: { programPath: '', openFile: true } });
    expect(completionFrom('run', 'C:\\Tools\\run.exe', false, 'shutdown', true, true)).toMatchObject({ ok: true, options: { programPath: 'C:\\Tools\\run.exe', systemAction: 'shutdown', forceSystemAction: true } });
    expect(completionFrom('run', 'run.exe', false, 'none', false, false)).toEqual({ ok: false, error: 'Choose an absolute path to an existing .exe file.' });
    expect(completionFrom('run', 'C:\\x.txt', false, 'none', false, false).ok).toBe(false);
    expect(completionFrom('global', '', false, 'restart', false, false)).toEqual({ ok: false, error: 'Allow computer actions in the Scheduler before choosing one.' });
    expect(completionFrom('global', '', false, 'explode', false, true)).toEqual({ ok: false, error: 'Choose a valid computer action.' });
    // Force only counts for the actions that can be forced.
    expect(completionFrom('global', '', false, 'exit', true, true)).toMatchObject({ options: { forceSystemAction: false } });
  });
});

describe('package (D15, D16)', () => {
  const part = (id: string, patch: Partial<Job> = {}) => job({ id, fileName: `${id}.rar`, ...patch });

  it('decides Pause, Resume and Cancel targets like PackageRules', () => {
    const files = [part('a', { canPause: true, state: 'downloading' }), part('b', { canResume: true, state: 'paused' }), part('c', { state: 'cancelled', canResume: true, isTerminal: true }), part('d', { state: 'completed', isTerminal: true })];
    expect(pauseTargets(files).map((f) => f.id)).toEqual(['a']);
    expect(resumeTargets('p', files).map((f) => f.id)).toEqual(['b']);
    expect(cancelTargets(files).map((f) => f.id)).toEqual(['a', 'b']);
  });

  it('brings every file back only when the package cancel stopped all of them', () => {
    const cancelled = (id: string, by?: string) => part(id, { state: 'cancelled', canResume: true, isTerminal: true, cancelledByPackageId: by });
    expect(resumeTargets('p', [cancelled('a', 'p'), cancelled('b', 'p')]).map((f) => f.id)).toEqual(['a', 'b']);
    expect(resumeTargets('p', [cancelled('a', 'p'), cancelled('b')]).map((f) => f.id)).toEqual([]);
    expect(resumeTargets('p', [cancelled('a', 'p'), part('c', { state: 'completed', isTerminal: true })]).map((f) => f.id)).toEqual([]);
  });

  it('shows the picked file while it is there, else the first working, unfinished, or first', () => {
    const files = [part('a', { state: 'completed', isTerminal: true }), part('b', { state: 'paused' }), part('c', { state: 'downloading' })];
    expect(fileToShow(files, null)).toBe('c');
    expect(fileToShow(files, 'a')).toBe('a');
    expect(fileToShow(files, 'gone')).toBe('c');
    expect(fileToShow(files.slice(0, 2), null)).toBe('b');
    expect(fileToShow([files[0]], null)).toBe('a');
    expect(fileToShow([], null)).toBeNull();
  });

  it('sums the figures and the bar', () => {
    const files = [part('a', { state: 'completed', totalBytes: 1024, receivedBytes: 1024 }), part('b', { state: 'downloading', totalBytes: 1024, receivedBytes: 512, bytesPerSecond: 256 })];
    const figures = packageFigures(files);
    expect(figures.summary).toBe('1 of 2 files complete');
    expect(figures.bar).toBe(75);
    expect(figures.detail).toBe('1.50 KB of 2.00 KB   ·   256 B/s   ·   2s left');
    expect(packageFigures([part('a', { state: 'completed', totalBytes: 10, receivedBytes: 10 })]).bar).toBe(100);
    expect(packageFigures([part('a', { state: 'downloading', receivedBytes: 10, bytesPerSecond: 5 })]).bar).toBeNull();
    expect(packageFigures([part('a', { state: 'queued' })]).bar).toBe(0);
    expect(packageFigures([]).summary).toBe('');
  });

  it('words the result of a package action', () => {
    expect(packageActionNotice({ ok: true })).toBe('');
    expect(packageActionNotice({ ok: true, leftCancelled: 2 })).toBe('2 cancelled file(s) stayed cancelled. Resume them one by one to bring them back.');
    expect(packageActionNotice({ ok: false, failedJobs: { a: 'x', b: 'y' } })).toBe('2 file(s) could not be changed.');
    expect(packageActionNotice({ ok: false, error: 'No such package.' })).toBe('No such package.');
    expect(packageActionNotice({ ok: false })).toBe('The package could not be changed.');
  });

  it('shows the archive password section by the desktop rule', () => {
    expect(looksLikeArchive('movie.part1.rar')).toBe(true);
    expect(looksLikeArchive('movie.r00')).toBe(true);
    expect(looksLikeArchive('data.7z.001')).toBe(true);
    expect(looksLikeArchive('.zip')).toBe(false);
    expect(looksLikeArchive('song.mp3')).toBe(false);
    expect(showsArchiveSection(['song.mp3'], 0, false)).toBe(false);
    expect(showsArchiveSection(['song.mp3', 'a.zip'], 0, false)).toBe(true);
    expect(showsArchiveSection(['song.mp3'], 1, false)).toBe(true);
    expect(showsArchiveSection(['song.mp3'], 0, true)).toBe(true);
    expect(showsArchiveSection([], 0, false)).toBe(true);
    expect(showsArchiveSection(['song.mp3', ''], 0, false)).toBe(true);
  });
});

describe('notices (D10)', () => {
  it('says when a download completed or failed, and nothing else', () => {
    expect(noticeFor('downloading', job({ state: 'completed' }), true)).toEqual({ text: 'Download complete: a.zip', tone: 'info', jobId: 'a' });
    expect(noticeFor('downloading', job({ state: 'completed' }), false)).toBeNull();
    expect(noticeFor('downloading', job({ state: 'failed', failure: { kind: 'x', message: 'Disk full' } }), false)).toEqual({ text: 'Download failed: a.zip. Disk full', tone: 'error', jobId: 'a' });
    expect(noticeFor('downloading', job({ state: 'downloading' }), true)).toBeNull();
    expect(noticeFor(undefined, job({ state: 'completed' }), true)).toBeNull();
    expect(noticeFor('completed', job({ state: 'completed' }), true)).toBeNull();
    expect(noticeFor('downloading', job({ state: 'paused' }), true)).toBeNull();
  });
});
