import { describe, expect, it } from 'vitest';
import { allShownSelected, folderCrumbs, toggleAllSelection } from '../src/lib/sitesBrowse';
import { openSyncState, scheduleAction, scheduleReplaceNote, syncKey } from '../src/lib/sitesExtras';
import { draftTestText } from '../src/lib/sitesHome';
import type { SiteSyncRequest } from '../src/lib/sites';

describe('server pane breadcrumbs', () => {
  it('lines up names and paths on a POSIX path', () => {
    expect(folderCrumbs('/downloads/isos')).toEqual([
      { name: 'downloads', path: '/downloads/' },
      { name: 'isos', path: '/downloads/isos' },
    ]);
  });
  it('lines up names and paths on a Windows path', () => {
    expect(folderCrumbs('D:\\Downloads\\ISOs')).toEqual([
      { name: 'D:', path: 'D:\\' },
      { name: 'Downloads', path: 'D:\\Downloads\\' },
      { name: 'ISOs', path: 'D:\\Downloads\\ISOs' },
    ]);
  });
  it('has none for the list of roots and one for the POSIX root', () => {
    expect(folderCrumbs('')).toEqual([]);
    expect(folderCrumbs('/')).toEqual([{ name: '/', path: '/' }]);
  });
});

describe('sync dialog rules', () => {
  const saved: SiteSyncRequest = { siteId: 'a', direction: 'upload', remoteFolder: '/media', localFolder: 'D:\\media' };
  it('opens with the saved schedule, else a download of this folder', () => {
    expect(openSyncState({ scheduledSync: saved, localFolder: 'D:\\x' }, '/media/isos')).toEqual({ direction: 'upload', remoteFolder: '/media', localFolder: 'D:\\media', every: true });
    expect(openSyncState({ scheduledSync: null, localFolder: 'D:\\x' }, '/a')).toEqual({ direction: 'download', remoteFolder: '/a', localFolder: 'D:\\x', every: false });
  });
  it('can remove or turn on the schedule without a list of files', () => {
    expect(scheduleAction(saved, saved, false)).toBe('remove');
    expect(scheduleAction(null, saved, true)).toBe('save');
    expect(scheduleAction(saved, saved, true)).toBe('none');
    expect(scheduleAction(null, saved, false)).toBe('none');
  });
  it('says so before a different folder replaces the saved schedule', () => {
    const other = { ...saved, direction: 'download' as const, remoteFolder: '/media/isos' };
    expect(scheduleAction(saved, other, true)).toBe('save');
    expect(scheduleReplaceNote(saved, other, true, 'NAS')).toContain('replaces the scheduled sync of NAS');
    expect(scheduleReplaceNote(saved, saved, true, 'NAS')).toBeNull();
    expect(scheduleReplaceNote(saved, other, false, 'NAS')).toBeNull();
  });
  it('a compared list belongs to its folders only', () => {
    const a = { direction: 'download' as const, remoteFolder: '/a', localFolder: 'D:\\x' };
    expect(syncKey(a)).not.toBe(syncKey({ ...a, localFolder: 'D:\\y' }));
    expect(syncKey(a)).toBe(syncKey({ ...a, localFolder: ' D:\\x ' }));
  });
});

describe('Select all with a filter', () => {
  const shown = [{ key: 'c' }, { key: 'd' }];
  it('does not count selected rows the filter hides', () => {
    const selected = new Set(['a', 'b']);
    expect(allShownSelected(shown, selected)).toBe(false);
    expect([...toggleAllSelection(shown, selected)]).toEqual(['c', 'd']);
  });
  it('clears when every shown row is selected', () => {
    expect(allShownSelected(shown, new Set(['a', 'c', 'd']))).toBe(true);
    expect(toggleAllSelection(shown, new Set(['a', 'c', 'd'])).size).toBe(0);
    expect(allShownSelected([], new Set(['a']))).toBe(false);
  });
});

describe('certificate test line', () => {
  it('tells the user to press Trust rather than promising it', () => {
    const text = draftTestText({ ok: false, problem: 'certificateNotTrusted', certificate: {} } as never, false, 1);
    expect(text).toContain('press Trust');
    expect(text).not.toContain('trusted when you save');
  });
});
