import { describe, expect, it } from 'vitest';
import { pathsOf } from '../src/lib/sitesBrowse';
import { formSignature, needsTestBeforeSave, saveButtonText } from '../src/lib/sitesHome';
import { barSummary, finishedNotices, graphRates, isPreparing, startedText, status, withRate } from '../src/lib/sitesTransfers';
import type { SiteTransfer } from '../src/lib/sites';

const t = (over: Partial<SiteTransfer> = {}): SiteTransfer => ({
  id: 'a', siteId: 's', siteName: 'Box', direction: 'download', remoteFolder: '/x', localFolder: 'D:\\in', conflict: 'keepBoth', state: 'running',
  files: 0, folders: 0, filesDone: 0, filesSkipped: 0, filesFailed: 0, bytes: 0, bytesDone: 0, speed: 0, createdAt: '2026-11-17T10:00:00Z',
  picked: ['thingino'], pickedCount: 1, ...over,
} as SiteTransfer);

describe('a big folder just accepted (SITES.14)', () => {
  it('says it is preparing until files are counted', () => {
    expect(isPreparing(t())).toBe(true);
    expect(status(t()).text).toBe('Preparing… (counting files)');
    expect(isPreparing(t({ files: 40 }))).toBe(false);
    expect(barSummary([t()])).toBe('1 preparing');
  });
  it('has the toast words', () => expect(startedText('thingino')).toBe('Downloading thingino to the server'));
});

describe('the speed graph takes the transfers in', () => {
  it('adds down and up by direction, only for files moving', () => {
    const rates = graphRates([t({ files: 2, filesRunning: 1, speed: 500 }), t({ id: 'b', direction: 'upload', files: 1, filesRunning: 1, speed: 70 }), t({ id: 'c', files: 1, speed: 999 })]);
    expect(rates).toEqual({ down: 500, up: 70 });
  });
  it('keeps a gap a gap', () => {
    expect(withRate(null, 5)).toBeNull();
    expect(withRate(10, 5)).toBe(15);
  });
});

describe('a finished transfer toast', () => {
  it('fires for a batch that was running and is done, not one first seen done', () => {
    const before = [t({ files: 3, filesRunning: 1 })];
    const done = t({ files: 3, filesDone: 3, state: 'done' });
    expect(finishedNotices(before, [done])).toEqual([{ id: 'a', text: 'Download thingino (3 files) finished.' }]);
    expect(finishedNotices([], [done])).toEqual([]);
    expect(finishedNotices([done], [done])).toEqual([]);
  });
});

describe('copy path', () => {
  it('names each pick, or the folder', () => {
    expect(pathsOf('/a', ['x', 'y z'])).toBe('/a/x\n/a/y z');
    expect(pathsOf('/a', [])).toBe('/a');
  });
});

describe('test before save', () => {
  it('tests a form that changed since the last test, never a sign-in one', () => {
    const one = formSignature({ host: 'a' });
    expect(needsTestBeforeSave(one, null, false)).toBe(true);
    expect(needsTestBeforeSave(one, one, false)).toBe(false);
    expect(needsTestBeforeSave(formSignature({ host: 'b' }), one, false)).toBe(true);
    expect(needsTestBeforeSave(one, null, true)).toBe(false);
  });
  it('offers Save anyway after a failed test', () => {
    expect(saveButtonText(false, false, false)).toBe('Save');
    expect(saveButtonText(false, false, true)).toBe('Save anyway');
    expect(saveButtonText(false, true, true)).toBe('Testing…');
    expect(saveButtonText(true, false, false)).toBe('Saving…');
  });
});
