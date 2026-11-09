import { describe, expect, it } from 'vitest';
import { fileCommands, planFileMenu } from '../src/lib/list';

describe('fileCommands: the desktop row menu entries (U-WU-10)', () => {
  const one = planFileMenu(true, false, 0, 0);
  it('offers Move/Rename only for one finished file', () => {
    expect(fileCommands({ state: 'completed', url: 'https://example.com/a.zip' }, one, 1).move).toEqual({ label: 'Move/Rename…', enabled: true, hidden: false });
    expect(fileCommands({ state: 'downloading', url: 'https://example.com/a.zip' }, one, 1).move.enabled).toBe(false);
    const removed = planFileMenu(true, false, 0, 0, true);
    expect(fileCommands({ state: 'completed', url: 'https://example.com/a.zip' }, removed, 1).move.enabled).toBe(false);
  });

  it('says why Redownload and Refresh need one file, and hides Refresh for a provider download', () => {
    const many = planFileMenu(false, true, 0, 0);
    expect(fileCommands(null, many, 3).redownload).toEqual({ label: 'Redownload (select a file, not a package)', enabled: false, hidden: false });
    expect(fileCommands({ state: 'failed', url: 'https://x/a' }, one, 1).refresh).toEqual({ label: 'Refresh Download Address…', enabled: true, hidden: false });
    expect(fileCommands({ state: 'failed', url: 'remote://torbox/1', isRemote: true }, one, 1).refresh.hidden).toBe(true);
  });

  it('changes the category of any number of downloads', () => {
    expect(fileCommands(null, planFileMenu(false, true, 0, 0), 4).category.enabled).toBe(true);
    expect(fileCommands(null, planFileMenu(false, false, 1, 1), 0).category.enabled).toBe(false);
  });
});
