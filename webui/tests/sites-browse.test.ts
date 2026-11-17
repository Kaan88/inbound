import browserSource from '../src/screens/sites/SiteBrowser.svelte?raw';
import { describe, expect, it } from 'vitest';
import {
  actionable, activation, addressOf, autoRetryAllowed, commandLabel, countText, deviceDownloadAllowed, filterMatches, footerText,
  kindChip, nextSort, pathFromText, remoteMenu, retryAtText, retryDelaySeconds, rowKeys, rowsOf, serverDownload, serverFolder,
  serverUpload, sortRows, splitServerFolder, troubleTitle, troubleWhy,
} from '../src/lib/sitesBrowse';
import type { Site, SiteItem } from '../src/lib/sites';

const file = (name: string, size = 10, extra: Partial<SiteItem> = {}): SiteItem => ({ name, isFolder: false, size, ...extra });
const folder = (name: string): SiteItem => ({ name, isFolder: true, size: 0 });
const source = (_path: string) => browserSource;

const sftp = {
  id: 's1', name: 'nas.home.lan', kind: 'sftp', host: 'nas.home.lan', port: 22, user: 'kaan', stars: [], capabilities: ['newFolder'],
  hostKeys: [], trustedCertificates: [], remoteFolder: '/', authMethod: 'password',
} as unknown as Site;

describe('bug 1: a download goes to the server, not the browser', () => {
  const browser = source('../src/screens/sites/SiteBrowser.svelte');

  it('has no plain download link on a row', () => {
    expect(browser).not.toMatch(/<a\b[^>]*\bdownload\b/);
    expect(browser).not.toMatch(/<a\b[^>]*href=\{deviceDownloadUrl/);
  });

  it('starts a download to the server (sites.transfer.start), not through the device route', () => {
    expect(browser).toMatch(/sites\.transfer\.start/);
    // The device route is reached only from the dialog's second choice.
    const uses = browser.match(/deviceDownloadUrl\(/g) ?? [];
    expect(uses.length).toBe(1);
    expect(browser).toMatch(/where === 'device'/);
  });

  it('Enter and a double click on a file mean to the server', () => {
    expect(activation([file('a.iso')])).toBe('download');
    expect(activation([file('a.iso'), file('b.iso')])).toBe('download');
    expect(activation([folder('isos')])).toBe('openFolder');
    expect(activation([], true)).toBe('goUp');
    expect(activation([])).toBe('none');
    expect(activation([{ name: '..', isFolder: true, size: 0 }])).toBe('none');
  });

  it('builds the transfer for the server folder and never skips or replaces unasked', () => {
    const start = serverDownload('s1', '/media/isos', ' /downloads/ISOs ', [file('a.iso'), folder('dir'), file('a.iso'), file('..')]);
    expect(start).toEqual({
      siteId: 's1', direction: 'download', remoteFolder: '/media/isos', localFolder: '/downloads/ISOs',
      names: [{ name: 'a.iso', isFolder: false }, { name: 'dir', isFolder: true }], conflict: 'keepBoth',
    });
  });

  it('the server folder is the linked folder, else the download folder', () => {
    expect(serverFolder({ localFolder: ' /d/ISOs ' }, '/d')).toBe('/d/ISOs');
    expect(serverFolder({ localFolder: null }, '/d')).toBe('/d');
    expect(serverFolder({ localFolder: '' }, undefined)).toBe('');
  });

  it('"To this device" is offered for one file only', () => {
    expect(deviceDownloadAllowed([file('a')])).toBe(true);
    expect(deviceDownloadAllowed([folder('a')])).toBe(false);
    expect(deviceDownloadAllowed([file('a'), file('b')])).toBe(false);
  });

  it('uploads a folder of the server by its parent and name', () => {
    expect(splitServerFolder('/data/in/photos/')).toEqual({ parent: '/data/in', name: 'photos' });
    expect(splitServerFolder('D:\\Media\\photos')).toEqual({ parent: 'D:\\Media', name: 'photos' });
    expect(splitServerFolder('/photos')).toEqual({ parent: '/', name: 'photos' });
    expect(splitServerFolder('D:\\')).toBeNull();
    const start = serverUpload('s1', '/media', '/data/in/photos');
    expect(start).toMatchObject({ direction: 'upload', localFolder: '/data/in', names: [{ name: 'photos', isFolder: true }] });
    expect(serverUpload('s1', '/media', 'D:\\')).toBeNull();
  });
});

describe('bug 2: two items of one name in one folder', () => {
  const twins = [file('report.pdf', 1), file('report.pdf', 2), folder('report.pdf'), file('other.txt')];

  it('gives each row its own key', () => {
    const keys = rowKeys(twins);
    expect(new Set(keys).size).toBe(4);
    expect(keys[0]).not.toBe(keys[1]);
  });

  it('keeps a row key through sorting and filtering', () => {
    const rows = rowsOf(twins);
    const bySize = sortRows(rows, 'size', false);
    const big = bySize.find((row) => row.item.size === 2)!;
    expect(big.key).toBe(rows[1].key);
    expect(new Set(bySize.map((row) => row.key)).size).toBe(4);
  });

  it('the list is not keyed by name any more', () => {
    const browser = source('../src/screens/sites/SiteBrowser.svelte');
    expect(browser).not.toMatch(/\(item\.name\)\}/);
    expect(browser).not.toMatch(/selected\.has\(item\.name\)/);
    expect(browser).toMatch(/\(row\.key\)/);
  });
});

describe('sorting, filter and footer', () => {
  const rows = rowsOf([file('b.iso', 5, { modified: '2026-11-05T10:00:00Z' }), folder('zzz'), file('a.iso', 9, { modified: '2026-10-05T10:00:00Z' })]);
  it('puts folders first whatever the direction', () => {
    expect(sortRows(rows, 'name', true).map((r) => r.item.name)).toEqual(['zzz', 'a.iso', 'b.iso']);
    expect(sortRows(rows, 'name', false).map((r) => r.item.name)).toEqual(['zzz', 'b.iso', 'a.iso']);
    expect(sortRows(rows, 'size', false).map((r) => r.item.name)).toEqual(['zzz', 'a.iso', 'b.iso']);
    expect(sortRows(rows, 'modified', true).map((r) => r.item.name)).toEqual(['zzz', 'a.iso', 'b.iso']);
  });
  it('a click on the same column turns it round', () => {
    expect(nextSort('name', true, 'name')).toEqual({ key: 'name', ascending: false });
    expect(nextSort('name', false, 'size')).toEqual({ key: 'size', ascending: true });
  });
  it('filters by part of a name without case', () => {
    expect(filterMatches('Ubuntu.ISO', ' iso ')).toBe(true);
    expect(filterMatches('notes.txt', 'iso')).toBe(false);
    expect(filterMatches('x', '')).toBe(true);
  });
  it('words the footer as the desktop does', () => {
    const f = (n: number) => `${n} B`;
    expect(countText(0, 0)).toBe('Empty folder');
    expect(countText(1, 1)).toBe('1 item');
    expect(countText(500, 1200)).toBe('Showing 500 of 1,200 items');
    expect(footerText({ shown: 6, total: 6, selected: 1, selectedBytes: 58, same: 2, linked: true, where: '/downloads/ISOs', hostName: 'the server', format: f }))
      .toBe('1 of 6 selected · 58 B · downloads go to /downloads/ISOs');
    expect(footerText({ shown: 6, total: 6, selected: 0, selectedBytes: 0, same: 2, linked: true, where: null, hostName: 'the server', format: f }))
      .toBe('6 items · 2 already on the server');
    expect(footerText({ shown: 6, total: 6, selected: 2, selectedBytes: 0, same: 0, linked: false, where: null, hostName: 'x', format: f }))
      .toBe('2 of 6 selected · downloads go by your category rules');
  });
  it('names the kind', () => {
    expect(kindChip('webdav')).toBe('WebDAV');
    expect(kindChip('drive')).toBe('Drive');
    expect(kindChip('')).toBe('');
  });
});

describe('the context menu', () => {
  it('one file', () => {
    expect(remoteMenu(['newFolder'], [file('a')])).toEqual([['download', 'downloadTo'], ['rename', 'delete', 'newFolder'], ['copyAddress']]);
  });
  it('one folder can be starred; several cannot be renamed', () => {
    expect(remoteMenu([], [folder('a')]).at(-1)).toEqual(['copyAddress', 'starFolder']);
    expect(remoteMenu([], [file('a'), file('b')])[1]).toEqual(['delete']);
  });
  it('has nothing that only makes sense on the computer the server runs on', () => {
    const labels = remoteMenu(['newFolder'], [file('a')]).flat().map(commandLabel).join('|');
    expect(labels).not.toMatch(/Open|Explorer|Recycle/);
    expect(commandLabel('delete')).toBe('Delete on server…');
  });
  it('empty space', () => {
    expect(remoteMenu(['newFolder'], [])).toEqual([['newFolder', 'upload'], ['refresh', 'pasteAddress']]);
    expect(remoteMenu([], [{ name: '..', isFolder: true, size: 0 }])).toEqual([['upload'], ['refresh', 'pasteAddress']]);
  });
  it('actionable drops the dot names and repeats', () => {
    expect(actionable([{ name: '.' }, { name: '..' }, { name: 'a' }, { name: 'a' }, { name: '' }])).toEqual([{ name: 'a' }]);
  });
});

describe('addresses', () => {
  it('writes and reads them', () => {
    expect(addressOf({ ...sftp, port: 2222 }, '/media/my files')).toBe('sftp://kaan@nas.home.lan:2222/media/my%20files');
    expect(addressOf(sftp, '/x')).toBe('sftp://kaan@nas.home.lan/x');
    expect(pathFromText(sftp, 'sftp://kaan@nas.home.lan/media/a%20b')).toBe('/media/a b');
    expect(pathFromText(sftp, 'sftp://kaan@nas.home.lan:2222/x')).toBeNull();
    expect(pathFromText(sftp, 'sftp://other/x')).toBeNull();
    expect(pathFromText(sftp, '/a/../b')).toBe('/b');
    expect(pathFromText(sftp, 'hello')).toBeNull();
  });
});

describe('trouble', () => {
  it('titles and words', () => {
    expect(troubleTitle('cantReach', 'nas')).toBe("Can't reach nas");
    expect(troubleTitle('signInNeeded', 'nas')).toBe("Can't connect to nas");
    expect(troubleWhy('cantReach', '')).toBe('The server did not answer.');
    expect(troubleWhy(null, 'Boom')).toBe('Boom');
  });
  it('retries on its own 10, 30, 60 seconds, five times, ten minutes at most', () => {
    expect([0, 1, 2, 9].map(retryDelaySeconds)).toEqual([10, 30, 60, 60]);
    expect(autoRetryAllowed(4, 1000)).toBe(true);
    expect(autoRetryAllowed(5, 1000)).toBe(false);
    expect(autoRetryAllowed(1, 11 * 60 * 1000)).toBe(false);
    expect(retryAtText(new Date(2026, 10, 17, 9, 5))).toBe('Trying again at 09:05.');
  });
});
