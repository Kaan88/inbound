import { describe, expect, it } from 'vitest';
import { buildFileTree, downloadChoice, itemMenu, type ItemMenuEntry } from '../src/lib/remote';
import type { RemoteFile, RemoteJob } from '../src/lib/types';

// D2d: My TorBox's download step is a dialog, and each row has a menu with the desktop window's actions for it.

const job = (change: Partial<RemoteJob> = {}): RemoteJob => ({
  id: 'j1', provider: 'torbox', providerName: 'TorBox', kind: 'torrents', state: 'waiting', status: '', progress: 0, speedBytesPerSecond: 0,
  cached: false, approvalNeeded: false, heldForApproval: false, hasLinkPassword: false, hasArchivePassword: false, queueId: 0,
  selectionMode: 'askWhenReady', notes: [], createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', ...change,
});

const idle = { acting: false, loading: false };
const shown = (entries: ItemMenuEntry[]) => entries.filter((entry) => !entry.hidden && !entry.separator).map((entry) => entry.action);
const entry = (entries: ItemMenuEntry[], action: string) => entries.find((e) => e.action === action)!;

describe('the row menu of My TorBox', () => {
  it('offers open, download all, copy name and refresh for an item not on the list, and no delete (it takes a job)', () => {
    const menu = itemMenu({ canDownload: true }, undefined, 'TorBox', idle);
    expect(shown(menu)).toEqual(['open', 'downloadAll', 'copyName', 'refresh']);
    expect(entry(menu, 'open').label).toBe('Choose files and download…');
    expect(entry(menu, 'downloadAll').label).toBe('Download all');
  });

  it('offers Check now and Delete from TorBox once a job knows the item, as the desktop window does', () => {
    const menu = itemMenu({ canDownload: true }, job({ itemId: '7', state: 'waiting' }), 'TorBox', idle);
    expect(shown(menu)).toEqual(['open', 'downloadAll', 'checkNow', 'copyName', 'refresh', 'deleteFromProvider']);
    expect(entry(menu, 'deleteFromProvider').label).toBe('Delete from TorBox…');
    expect(entry(menu, 'downloadAll').label).toBe('Download all its files');
  });

  it('hides Check now where the Host would refuse it, and Delete for a job with no item', () => {
    const menu = itemMenu({ canDownload: true }, job({ state: 'localStarted' }), 'TorBox', idle);
    expect(entry(menu, 'checkNow').hidden).toBe(true);
    expect(entry(menu, 'deleteFromProvider').hidden).toBe(true);
    expect(entry(itemMenu({ canDownload: true }, job({ state: 'intended', heldForApproval: true }), 'TorBox', idle), 'checkNow').hidden).toBe(true);
  });

  it('dims the actions that change something while one is running, and Refresh while a page loads', () => {
    const busy = itemMenu({ canDownload: true }, job({ itemId: '7' }), 'TorBox', { acting: true, loading: true });
    expect(entry(busy, 'downloadAll').disabled).toBe(true);
    expect(entry(busy, 'checkNow').disabled).toBe(true);
    expect(entry(busy, 'deleteFromProvider').disabled).toBe(true);
    expect(entry(busy, 'refresh').disabled).toBe(true);
    // Opening the dialog and copying the name still work.
    expect(entry(busy, 'open').disabled).toBeFalsy();
    expect(entry(busy, 'copyName').disabled).toBeFalsy();
  });

  it('dims Download all for an item that failed on the provider, but still opens it to say why', () => {
    const menu = itemMenu({ canDownload: false }, undefined, 'TorBox', idle);
    expect(entry(menu, 'downloadAll').disabled).toBe(true);
    expect(entry(menu, 'open').disabled).toBeFalsy();
  });

  it('uses the provider name it is given', () => {
    expect(entry(itemMenu({ canDownload: true }, job({ itemId: '7' }), 'Other', idle), 'deleteFromProvider').label).toBe('Delete from Other…');
  });
});

describe('what the dialog\'s Download buttons send', () => {
  const files: RemoteFile[] = [
    { fileId: '1', relativePath: 'Show/a.mkv', size: 100, infected: false, selectable: true },
    { fileId: '2', relativePath: 'Show/b.mkv', size: 200, infected: false, selectable: true },
    { fileId: '3', relativePath: 'Show/c.txt', size: 5, infected: true, selectable: false },
  ];
  const tree = buildFileTree(files);

  it('sends the chosen files', () => {
    expect(downloadChoice(tree, new Set(['2']), false)).toEqual({ all: false, fileIds: ['2'] });
  });

  it('sends all when every file that can be chosen is chosen, as the desktop does', () => {
    expect(downloadChoice(tree, new Set(['1', '2']), false)).toEqual({ all: true, fileIds: [] });
  });

  it('sends nothing for Download selected with nothing chosen', () => {
    expect(downloadChoice(tree, new Set(), false)).toBeNull();
  });

  it('sends all for Download all whatever is ticked, and when the provider lists no files yet', () => {
    expect(downloadChoice(tree, new Set(['1']), true)).toEqual({ all: true, fileIds: [] });
    expect(downloadChoice(undefined, new Set(), false)).toEqual({ all: true, fileIds: [] });
    expect(downloadChoice(buildFileTree([]), new Set(), false)).toEqual({ all: true, fileIds: [] });
  });
});
