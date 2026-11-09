import { describe, expect, it } from 'vitest';
import { clockTime, dateOnly, dateTime } from '../src/lib/format';
import { lastTryText, liveSpeed, statusBarText } from '../src/lib/list';
import { foldTools, type ToolSize } from '../src/lib/toolbar';

// The toolbar of the list at 1360 px in Waiting for TorBox: the buttons that measured 1226 px in a 1150 px bar (U-WU-2).
const bar: ToolSize[] = [
  { id: 'add', width: 64, keep: 2, group: 0 },
  { id: 'resume', width: 64, keep: 1, group: 1 },
  { id: 'stop', width: 64, keep: 1, group: 1 },
  { id: 'stopAll', width: 64, keep: 3, group: 1 },
  { id: 'delete', width: 64, keep: 1, group: 2 },
  { id: 'deleteCompleted', width: 104, keep: 4, group: 2 },
  { id: 'extract', width: 64, keep: 7, group: 3 },
  { id: 'archivePassword', width: 110, keep: 7, group: 3 },
  { id: 'extractDisk', width: 100, keep: 8, group: 3 },
  { id: 'checkNow', width: 70, keep: 2, group: 4 },
  { id: 'options', width: 64, keep: 6, group: 5 },
  { id: 'scheduler', width: 66, keep: 6, group: 5 },
  { id: 'startQueue', width: 92, keep: 5, group: 6 },
  { id: 'stopQueue', width: 92, keep: 5, group: 6 },
];

function used(tools: ToolSize[], folded: Set<string>) {
  const shown = tools.filter((tool) => !folded.has(tool.id));
  return shown.reduce((sum, tool) => sum + tool.width + 2, 0) + new Set(shown.map((tool) => tool.group)).size * 9;
}

describe('foldTools (U-WU-2, U-WU-9)', () => {
  it('folds nothing when everything fits', () => {
    expect(foldTools(bar, 5000).size).toBe(0);
  });

  it('folds the least needed buttons first, the rightmost first among equals, until the rest fit', () => {
    const available = 1150 - 16 - 64 - 2;
    const folded = foldTools(bar, available);
    expect([...folded]).toEqual(['extractDisk', 'archivePassword']);
    expect(used(bar, folded)).toBeLessThanOrEqual(available);
  });

  it('keeps Resume, Stop and Delete on a phone, with Add URL and Stop All when they fit', () => {
    const phone = bar.map((tool) => ({ ...tool, width: 52 }));
    const available = 390 - 16 - 52 - 2;
    const folded = foldTools(phone, available);
    const shown = phone.filter((tool) => !folded.has(tool.id)).map((tool) => tool.id);
    expect(shown).toEqual(expect.arrayContaining(['resume', 'stop', 'delete']));
    expect(shown).not.toContain('extractDisk');
    expect(used(phone, folded)).toBeLessThanOrEqual(available);
  });

  it('folds everything when there is no room at all', () => {
    expect(foldTools(bar, 0).size).toBe(bar.length);
  });
});

describe('the status bar and dates (U-WR-19, U-WU-21)', () => {
  it('says 1 download, not 1 downloads', () => {
    expect(statusBarText({ total: 1, active: 1, queued: 0, retrying: 0 } as never, 0)).toBe('1 download · 1 active · 0 queued');
    expect(statusBarText({ total: 2, active: 0, queued: 0, retrying: 0 } as never, 0)).toBe('2 downloads · 0 active · 0 queued');
  });

  it('writes every moment the way the list does', () => {
    const date = new Date(2026, 10, 9, 5, 19, 47);
    expect(dateTime(date)).toBe('Nov 09 05:19:47 2026');
    expect(lastTryText(date)).toBe(dateTime(date));
    expect(dateOnly(date)).toBe('Nov 09 2026');
    expect(clockTime(date)).toBe('05:19');
    expect(dateTime('0001-01-01T00:00:00Z')).toBe('');
    expect(dateTime(undefined)).toBe('');
  });
});

describe('liveSpeed (U-WU-14)', () => {
  it('adds up the rates the rows show, however long ago the job was saved, and is empty while offline', () => {
    const jobs = [
      { state: 'downloading', bytesPerSecond: 228_000, updatedAt: '2020-01-01T00:00:00Z' },
      { state: 'paused', bytesPerSecond: 50_000 },
      { state: 'retryWaiting', bytesPerSecond: 1_000 },
    ] as never[];
    expect(liveSpeed(jobs, true, true)).toBe(229_000);
    expect(liveSpeed(jobs, false, true)).toBeNull();
  });
});
