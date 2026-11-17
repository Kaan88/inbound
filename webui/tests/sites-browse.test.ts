import { describe, expect, it } from 'vitest';
import {
  actionable, activation, deleteText, addressOf, autoRetryAllowed, commandKey, commandLabel, countText, deviceDownloadAllowed, downloadTarget, downloadWhere,
  filterMatches, footerText, keptSelection, kindChip, linkedLine, nextSort, onRowControl, pathFromText, remoteMenu, retryAtText, retryDelaySeconds, rowKeys,
  rowsOf, serverDownload, serverFolder, serverUpload, sortRows, splitServerFolder, troubleTitle, troubleWhy, uploadSourceProblem,
  type DownloadTrigger,
} from '../src/lib/sitesBrowse';
import type { Site, SiteItem } from '../src/lib/sites';

describe('the linked folder line', () => {
  it('says where downloads go', () => {
    expect(linkedLine('D:\\Downloads\\ISOs', '/media/isos')).toBe('Downloads from /media/isos go to D:\\Downloads\\ISOs');
    expect(linkedLine('', '/x')).toBeNull();
  });
});

describe('the delete sheet says "N items (size)" (R7)', () => {
  const size = (n: number) => `${n} B`;
  const count = { files: 1800, folders: 42, bytes: 900, sizeUnknown: false };
  it('names a folder and what is inside it, once counted', () => {
    expect(deleteText([{ name: 'backups', isFolder: true }], null, size)).toBe("backups and everything in it will be deleted on the server. This can't be undone. Counting what is in it…");
    expect(deleteText([{ name: 'backups', isFolder: true }], count, size)).toBe("backups and the 1,800 items inside it (900 B) will be deleted on the server. This can't be undone.");
  });
  it('does not count the chosen folder as inside itself', () => {
    expect(deleteText([{ name: 'iso', isFolder: true }], { files: 5, folders: 1, bytes: 0, sizeUnknown: false }, size)).toBe("iso and the 5 items inside it will be deleted on the server. This can't be undone.");
  });
  it('counts several items with their size', () => {
    expect(deleteText([{ name: 'a', isFolder: false }, { name: 'b', isFolder: false }], { files: 2, folders: 0, bytes: 10, sizeUnknown: false }, size)).toBe("2 items (10 B) will be deleted on the server. This can't be undone.");
  });
});

const file = (name: string, size = 10, extra: Partial<SiteItem> = {}): SiteItem => ({ name, isFolder: false, size, ...extra });
const folder = (name: string): SiteItem => ({ name, isFolder: true, size: 0 });

const sftp = {
  id: 's1', name: 'nas.home.lan', kind: 'sftp', host: 'nas.home.lan', port: 22, user: 'r0', stars: [], capabilities: ['newFolder'],
  hostKeys: [], trustedCertificates: [], remoteFolder: '/', authMethod: 'password',
} as unknown as Site;

// The one guard on source text: a file goes to the browser only through startDeviceDownload, from the dialog.
const sitesScreens = import.meta.glob(['../src/screens/sites/*.svelte', '../src/screens/SitesScreen.svelte'], { query: '?raw', import: 'default', eager: true }) as Record<string, string>;
const allSources = import.meta.glob(['../src/**/*.svelte', '../src/**/*.ts'], { query: '?raw', import: 'default', eager: true }) as Record<string, string>;
const helperSource = allSources['../src/lib/deviceDownload.ts'];

describe('bug 1: a download goes to the server, not the browser', () => {
  const triggers: DownloadTrigger[] = ['button', 'enter', 'doubleClick', 'menu', 'arrow'];

  it('Download, Enter, a double click, the menu and the arrow always mean the server', () => {
    for (const trigger of triggers) {
      expect(downloadWhere(trigger, [file('a.iso')])).toBe('server');
      expect(downloadWhere(trigger, [file('a.iso')], 'device')).toBe('server');
      expect(downloadWhere(trigger, [file('a.iso'), file('b.iso')])).toBe('server');
    }
  });

  it('only the dialog with "To this device" picked means the device, and only for one file', () => {
    expect(downloadWhere('dialog', [file('a.iso')], 'device')).toBe('device');
    expect(downloadWhere('dialog', [file('a.iso')], 'server')).toBe('server');
    expect(downloadWhere('dialog', [file('a.iso')])).toBe('server');
    expect(downloadWhere('dialog', [folder('isos')], 'device')).toBe('server');
    expect(downloadWhere('dialog', [file('a.iso'), file('b.iso')], 'device')).toBe('server');
  });

  it('the download attribute lives only in startDeviceDownload, called only from the dialog\'s device choice', () => {
    const names = Object.keys(sitesScreens);
    expect(names.length).toBeGreaterThanOrEqual(4);
    for (const name of names) {
      expect(sitesScreens[name], name).not.toMatch(/<a\b[^>]*\sdownload\b/);
      expect(sitesScreens[name], name).not.toMatch(/\.download\s*=/);
    }
    // The helper sets it, once, inside itself.
    const body = helperSource.slice(helperSource.indexOf('export function startDeviceDownload('));
    expect(helperSource.match(/\.download\s*=/g)).toHaveLength(1);
    expect(body).toMatch(/\.download\s*=/);
    // One caller in the whole web UI: SiteBrowser's confirmDownload, after the device check.
    const callers = Object.entries(allSources).flatMap(([name, text]) =>
      (text.match(/startDeviceDownload\(/g) ?? []).map(() => name)).filter((name) => !name.endsWith('lib/deviceDownload.ts'));
    expect(callers).toEqual(['../src/screens/sites/SiteBrowser.svelte']);
    const browser = allSources['../src/screens/sites/SiteBrowser.svelte'];
    const confirm = browser.slice(browser.indexOf('function confirmDownload('));
    const call = confirm.indexOf('startDeviceDownload(');
    expect(call).toBeGreaterThan(confirm.indexOf("downloadWhere('dialog', current.items, current.where) !== 'device'"));
    expect(call).toBeLessThan(confirm.indexOf('\n  function ', 1));
  });

  it('Enter and a double click on a file download; on one folder they open it', () => {
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
});

describe('the download target: one folder for every control and the footer', () => {
  const all = { leftSelected: '/srv/in/picked', leftShown: '/srv/in', linkedFolder: '/srv/isos', defaultDownloadDirectory: '/srv/downloads' };

  it('takes the folder selected in the left pane first', () => {
    expect(downloadTarget({ view: 'two', ...all })).toBe('/srv/in/picked');
  });
  it('then the folder the left pane shows', () => {
    expect(downloadTarget({ view: 'two', ...all, leftSelected: null })).toBe('/srv/in');
  });
  it("not at the left pane's roots, which show no folder: the linked folder then", () => {
    expect(downloadTarget({ view: 'two', ...all, leftSelected: null, leftShown: '' })).toBe('/srv/isos');
  });
  it("then the server's download folder, and empty when there is none", () => {
    expect(downloadTarget({ view: 'two', leftSelected: null, leftShown: '', linkedFolder: ' ', defaultDownloadDirectory: '/srv/downloads' })).toBe('/srv/downloads');
    expect(downloadTarget({ view: 'server' })).toBe('');
  });
  it('ignores the left pane in "Remote only"', () => {
    expect(downloadTarget({ view: 'server', ...all })).toBe('/srv/isos');
    expect(downloadTarget({ view: 'server', ...all, linkedFolder: null })).toBe('/srv/downloads');
  });
  it('the footer names that same folder', () => {
    const where = downloadTarget({ view: 'two', ...all, leftSelected: null });
    expect(footerText({ shown: 3, total: 3, selected: 1, selectedBytes: 0, same: 0, linked: false, where, hostName: 'x', format: String }))
      .toBe('1 of 3 selected · downloads go to /srv/in');
  });
});

describe('uploading a folder of the server', () => {
  const roots = ['D:\\Downloads', '/srv/data'];

  it('splits a folder into its parent and name', () => {
    expect(splitServerFolder('/data/in/photos/')).toEqual({ parent: '/data/in', name: 'photos' });
    expect(splitServerFolder('D:\\Media\\photos')).toEqual({ parent: 'D:\\Media', name: 'photos' });
    expect(splitServerFolder('/photos')).toEqual({ parent: '/', name: 'photos' });
    expect(splitServerFolder('D:\\')).toBeNull();
    const start = serverUpload('s1', '/media', '/data/in/photos');
    expect(start).toMatchObject({ direction: 'upload', localFolder: '/data/in', names: [{ name: 'photos', isFolder: true }] });
    expect(serverUpload('s1', '/media', 'D:\\')).toBeNull();
  });

  it('an allowed folder itself cannot go up (its parent is outside them); one inside it can', () => {
    expect(uploadSourceProblem('D:\\Downloads', roots)).toBe('Choose a folder inside D:\\Downloads.');
    expect(uploadSourceProblem('d:\\downloads\\', roots)).toBe('Choose a folder inside D:\\Downloads.');
    expect(uploadSourceProblem('/srv/data/', roots)).toBe('Choose a folder inside /srv/data.');
    expect(uploadSourceProblem('D:\\Downloads\\ISOs', roots)).toBeNull();
    expect(uploadSourceProblem('', roots)).toBe('Choose a folder on the server.');
    expect(serverUpload('s1', '/media', 'D:\\Downloads', 'keepBoth', roots)).toBeNull();
    expect(serverUpload('s1', '/media', 'D:\\Downloads\\ISOs', 'keepBoth', roots))
      .toMatchObject({ localFolder: 'D:\\Downloads', names: [{ name: 'ISOs', isFolder: true }] });
  });
});

describe('keys and controls inside a row', () => {
  const el = (tag: string) => ({ closest: (wanted: string) => (wanted.split(',').map((part) => part.trim()).includes(tag) ? {} : null) });
  it("a key or double click on a row's button, checkbox or link belongs to it, not to the row", () => {
    expect(onRowControl(el('button'))).toBe(true);
    expect(onRowControl(el('input'))).toBe(true);
    expect(onRowControl(el('a'))).toBe(true);
    expect(onRowControl(el('li'))).toBe(false);
    expect(onRowControl(null)).toBe(false);
  });
  it("shows only keys the list answers; Ctrl+U and Ctrl+Shift+C are the browser's", () => {
    expect(commandKey('upload')).toBe('');
    expect(commandKey('copyAddress')).toBe('');
    expect(commandKey('newFolder')).toBe('');
    expect(commandKey('downloadTo')).toBe('Ctrl+Shift+S');
    expect(commandKey('refresh')).toBe('F5');
  });
});

describe('bug 2: two items of one name in one folder', () => {
  const twins = [file('report.pdf', 1), file('report.pdf', 2), folder('report.pdf'), file('other.txt')];

  it('gives each row its own key', () => {
    const keys = rowKeys(twins);
    expect(new Set(keys).size).toBe(4);
    expect(keys[0]).not.toBe(keys[1]);
  });

  it('keys hold no control character, which CSS.escape would change (U+0000 becomes U+FFFD)', () => {
    const keys = rowKeys(twins);
    expect(keys).toEqual(['0/report.pdf', '1/report.pdf', '2/report.pdf', '0/other.txt']);
    for (const key of keys) expect(key).not.toMatch(/[\u0000-\u001f]/);
  });

  it('a refresh of the same folder keeps the selected rows still there; another folder clears them', () => {
    const before = new Set(['0/report.pdf', '1/report.pdf', '0/gone.txt']);
    expect([...keptSelection(before, rowKeys(twins), true)]).toEqual(['0/report.pdf', '1/report.pdf']);
    expect(keptSelection(before, rowKeys(twins), false).size).toBe(0);
  });

  it('keeps a row key through sorting and filtering', () => {
    const rows = rowsOf(twins);
    const bySize = sortRows(rows, 'size', false);
    const big = bySize.find((row) => row.item.size === 2)!;
    expect(big.key).toBe(rows[1].key);
    expect(new Set(bySize.map((row) => row.key)).size).toBe(4);
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
    expect(remoteMenu(['newFolder'], [file('a')])).toEqual([['download', 'downloadTo'], ['rename', 'delete', 'newFolder'], ['copyPath', 'copyAddress']]);
  });
  it('one folder can be starred; several cannot be renamed', () => {
    expect(remoteMenu([], [folder('a')]).at(-1)).toEqual(['copyPath', 'copyAddress', 'starFolder']);
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
    expect(addressOf({ ...sftp, port: 2222 }, '/media/my files')).toBe('sftp://r0@nas.home.lan:2222/media/my%20files');
    expect(addressOf(sftp, '/x')).toBe('sftp://r0@nas.home.lan/x');
    expect(pathFromText(sftp, 'sftp://r0@nas.home.lan/media/a%20b')).toBe('/media/a b');
    expect(pathFromText(sftp, 'sftp://r0@nas.home.lan:2222/x')).toBeNull();
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
