import { describe, expect, it } from 'vitest';
import { justCompleted } from '../src/lib/notify';
import {
  applyForm, archiveChanges, archiveValues, composeProxyAddress, formFrom, isUnsupportedReply, parseExtensions, profileRows, readProfiles,
  friendlyRefusal, problemsText, routeShown, routeToSave, splitProxyAddress, validateAll, validateForm, type OptionsForm,
} from '../src/lib/settingsRules';
import type { ArchiveSettings, Settings } from '../src/lib/types';

function settings(extra: Partial<Settings> = {}): Settings {
  return {
    defaultDownloadDirectory: '/data/downloads', maxConcurrentDownloads: 3, defaultQueueId: 0, queues: [], schedules: [],
    defaultConnections: 8, useCategoryFolders: true, maxConnectionsPerHost: 8, connectionProfiles: [{ pattern: '*.a.com', maxConnections: 2 }],
    proxy: { mode: 'none', hasPassword: false }, siteLogins: [], allowLocalNetwork: true, autoResumeUnfinishedOnStartup: true,
    browserIntegrationEnabled: true, allowSystemCompletionActions: false, favoriteFolders: [], recentDestinationDirectories: [], downloadGroups: [],
    excludedFileExtensions: [], excludedHosts: [], ...extra,
  };
}

const formOf = (extra: Partial<Settings> = {}): OptionsForm => formFrom(settings(extra));

describe('proxy address', () => {
  it('splits and composes the stored address like the desktop', () => {
    expect(splitProxyAddress('proxy:3128')).toEqual({ type: 0, host: 'proxy:3128' });
    expect(splitProxyAddress('socks5://h:1080/')).toEqual({ type: 3, host: 'h:1080' });
    expect(splitProxyAddress('socks4a://h:1')).toEqual({ type: 2, host: 'h:1' });
    expect(splitProxyAddress('https://h:8')).toEqual({ type: 0, host: 'h:8' });
    expect(splitProxyAddress('')).toEqual({ type: 0, host: '' });
    expect(composeProxyAddress(0, 'proxy:3128')).toBe('proxy:3128');
    expect(composeProxyAddress(3, ' http://h:1080/ ')).toBe('socks5://h:1080');
    expect(composeProxyAddress(1, '[::1]:9050')).toBe('socks4://[::1]:9050');
  });

  it('refuses a missing host or port, a port out of range and stray characters', () => {
    for (const bad of ['', 'host', 'host:', 'host:0', 'host:65536', 'host:abc', 'a b:80', 'h/x:80', 'a:b:80', ':80']) expect(composeProxyAddress(0, bad), bad).toBeNull();
  });
});

// Review 5:F6b: a row each, a site and its connections, not typed pattern=max lines.
describe('per-site limits', () => {
  it('gives a row per limit and reads them back', () => {
    const rows = profileRows([{ pattern: '*.a.com', maxConnections: 2 }, { pattern: 'b.org', maxConnections: 4 }]);
    expect(rows.map((row) => [row.pattern, row.max])).toEqual([['*.a.com', 2], ['b.org', 4]]);
    rows[0].pattern = ' *.a.com ';
    rows.push({ key: 99, pattern: '', max: null });
    expect(readProfiles(rows)).toEqual({ profiles: [{ pattern: '*.a.com', maxConnections: 2 }, { pattern: 'b.org', maxConnections: 4 }], error: null });
  });

  it('names the row that is wrong, in the Windows words', () => {
    expect(readProfiles([{ key: 1, pattern: ' ', max: 3 }])).toEqual({ profiles: [], error: 'Enter a site for each per-site limit, or remove it.', field: 'profile-site-1' });
    expect(readProfiles([{ key: 2, pattern: 'a.com', max: 33 }]).error).toBe('The limit for a.com must be 1 to 32 connections.');
    expect(readProfiles([{ key: 3, pattern: 'a.com', max: null }]).field).toBe('profile-max-3');
    expect(readProfiles([{ key: 4, pattern: 'a.com', max: 0 }]).error).not.toBeNull();
  });
});

describe('validateForm', () => {
  it('passes a form made from good settings', () => {
    expect(validateForm(formOf())).toBeNull();
  });

  it('turns an emptied number box into the desktop message, on its page', () => {
    const form = formOf();
    form.maxConcurrentDownloads = null;
    expect(validateForm(form)).toMatchObject({ message: 'Maximum simultaneous downloads must be between 1 and 16.', page: 'connection' });
    form.maxConcurrentDownloads = 17;
    expect(validateForm(form)?.message).toContain('between 1 and 16');
    form.maxConcurrentDownloads = 3;
    form.defaultConnections = 33;
    expect(validateForm(form)?.message).toBe('Connections per download must be between 1 and 32.');
    form.defaultConnections = 8;
    form.maxConnectionsPerHost = 65;
    expect(validateForm(form)?.message).toBe('Connections per site must be between 1 and 64.');
    form.maxConnectionsPerHost = 64;
    form.speedKb = -5;
    expect(validateForm(form)?.message).toBe('Speed limit must be between 1 and 10000000 KB/s.');
    form.speedKb = 10_000_001;
    expect(validateForm(form)?.message).toBe('Speed limit must be between 1 and 10000000 KB/s.');
    form.speedKb = 10_000_000;
    expect(validateForm(form)).toBeNull();
  });

  it('checks a manual proxy address, but not one that is switched off', () => {
    const form = formOf();
    form.proxy.host = 'nonsense';
    expect(validateForm(form)).toBeNull();
    form.proxy.mode = 'manual';
    expect(validateForm(form)).toMatchObject({ message: 'Enter the proxy address as host:port (port 1-65535).', page: 'proxy' });
  });

  it('does not ask for a manual proxy address the server hides from a browser', () => {
    // A web session is sent only the proxy's mode (WebSettingsRules.ForWeb); the host keeps its own address.
    const form = formOf({ proxy: { mode: 'manual', hasPassword: false } });
    expect(form.proxy.host).toBe('');
    expect(validateAll(form)).toEqual([]);
    form.proxy.host = 'nonsense';
    expect(validateForm(form)).toMatchObject({ page: 'proxy', field: 'proxy-address' });
  });

  it('does not check the proxy where only the PC may change it', () => {
    const form = formOf({ proxy: { mode: 'manual', address: 'h:80', hasPassword: false } });
    form.proxy.host = 'nonsense';
    expect(validateForm(form)).toMatchObject({ page: 'proxy' });
    expect(validateAll(form, null, { pcOnly: true })).toEqual([]);
  });

  it('asks for a login password again when the site changed, and for a name and site on every row', () => {
    const form = formOf({ siteLogins: [{ id: 'a', site: 'x.com', username: 'u', hasPassword: true }] });
    form.logins[0].site = 'y.com';
    expect(validateForm(form)).toMatchObject({ message: 'Re-enter the password for a login whose site or user name changed.', page: 'logins' });
    form.logins[0].password = 'p';
    expect(validateForm(form)).toBeNull();
    form.logins[0].removed = true;
    form.logins[0].password = '';
    expect(validateForm(form)).toBeNull();
    form.logins.push({ id: 'n', site: '', username: '', password: '', hasPassword: false, isNew: true, removed: false, originalSite: '', originalUser: '' });
    expect(validateForm(form)?.message).toBe('Every site login needs a site and user name. Remove unused rows before saving.');
  });

  it('shows an extraction problem first, on the Extraction page', () => {
    const saved: ArchiveSettings = { autoExtract: false, destination: 'nextToArchive', preserveFolders: true, conflictPolicy: 'rename', deleteArchives: false };
    const values = archiveValues(saved);
    values.destination = 2;
    const form = formOf();
    form.maxConcurrentDownloads = null;
    expect(validateForm(form, { saved, values })).toMatchObject({ message: 'Choose the folder to extract to.', page: 'extraction' });
  });
});

describe('applyForm', () => {
  it('writes the draft over the server copy and keeps the fields the page has no box for', () => {
    const current = settings({ updateManifestUrl: 'https://feed', siteLogins: [{ id: 'a', site: 'x.com', username: 'u', hasPassword: true }] });
    const form = formFrom(current);
    form.maxConcurrentDownloads = 5;
    form.speedKb = 100;
    form.proxy.mode = 'manual';
    form.proxy.type = 3;
    form.proxy.host = 'p:1080';
    form.proxy.username = ' me ';
    form.proxy.password = 'secret';
    form.profiles = [{ key: 1, pattern: 'b.org', max: 4 }];
    form.logins[0].removed = true;
    applyForm(current, form);
    expect(current.maxConcurrentDownloads).toBe(5);
    expect(current.globalSpeedLimitBytesPerSecond).toBe(102400);
    expect(current.proxy).toMatchObject({ mode: 'manual', address: 'socks5://p:1080', username: 'me', password: 'secret', clearPassword: false });
    expect(current.connectionProfiles).toEqual([{ pattern: 'b.org', maxConnections: 4 }]);
    expect(current.siteLogins).toEqual([]);
    expect(current.updateManifestUrl).toBe('https://feed');
  });

  it('reads and writes the Torrents page: off with files chosen first by default, an unknown route is this PC', () => {
    const fresh = formOf();
    expect(fresh).toMatchObject({ torrentsEnabled: false, torrentRoute: 'thisPc', torrentChooseFiles: true });
    expect(formOf({ torrentRoute: 'somewhere' }).torrentRoute).toBe('thisPc');

    const current = settings({ torrentsEnabled: false, torrentRoute: 'ask', torrentChooseFiles: true, torrentHandler: true });
    const form = formFrom(current);
    form.torrentsEnabled = true;
    form.torrentChooseFiles = false;
    applyForm(current, form);
    // T9: a saved "ask" now starts on this PC too, so keeping This PC leaves it as it was.
    expect(current).toMatchObject({ torrentsEnabled: true, torrentRoute: 'ask', torrentChooseFiles: false, torrentHandler: true });
    form.torrentRoute = 'provider';
    applyForm(current, form);
    expect(current.torrentRoute).toBe('provider');
    expect(friendlyRefusal('These settings can only be changed in the app on this PC: torrentsEnabled. Nothing was saved.')).toContain('Download torrents.');
  });

  it('clears the proxy address when the proxy is not manual, and forgets the password when asked', () => {
    const current = settings({ proxy: { mode: 'manual', address: 'h:80', username: 'u', hasPassword: true } });
    const form = formFrom(current);
    form.proxy.password = 'typed';
    form.proxy.forget = true;
    applyForm(current, form);
    expect(current.proxy).toMatchObject({ address: 'h:80', password: undefined, clearPassword: true, hasPassword: false });
    form.proxy.mode = 'system';
    applyForm(current, form);
    expect(current.proxy.address).toBeUndefined();
    expect(current.proxy.username).toBe("u");
  });

  it('sends category folders that have a value, edits custom categories and stages removals', () => {
    const current = settings({
      categoryFolders: { Video: '/v' },
      customCategories: [{ id: 'c1', name: 'Books', extensions: ['epub'], destinationDirectory: '/b' }, { id: 'c2', name: 'Old', extensions: [] }],
    });
    const form = formFrom(current);
    expect(form.categoryFolders.video).toBe('/v');
    form.categoryFolders.music = ' /m ';
    form.customCategories[0].extensions = 'epub, mobi  azw';
    form.customCategories[0].folder = '';
    form.customCategories[1].removed = true;
    applyForm(current, form);
    expect(current.categoryFolders).toEqual({ video: '/v', music: '/m' });
    expect(current.customCategories).toEqual([{ id: 'c1', name: 'Books', extensions: ['epub', 'mobi', 'azw'], destinationDirectory: undefined }]);
    expect(current.deleteCategoryIds).toEqual(['c2']);
  });

  it('keeps order and edits of favorites, drops empty folders, and keeps one saved meanwhile', () => {
    const open = settings({ favoriteFolders: [{ name: 'A', path: '/a' }, { name: 'B', path: '/b' }] });
    const form = formFrom(open);
    form.favorites.reverse();
    form.favorites[0].name = '';
    form.favorites.push({ key: 99, name: 'x', path: '  ' });
    const now = settings({ favoriteFolders: [{ name: 'A', path: '/a' }, { name: 'B', path: '/b' }, { name: 'C', path: '/c' }] });
    applyForm(now, form);
    expect(now.favoriteFolders).toEqual([{ name: '', path: '/b' }, { name: 'A', path: '/a' }, { name: 'C', path: '/c' }]);
  });
});

describe('archiveChanges', () => {
  const saved: ArchiveSettings = { autoExtract: false, destination: 'nextToArchive', folder: '/old', preserveFolders: true, conflictPolicy: 'rename', deleteArchives: false };

  it('sends nothing when nothing changed, and only what changed otherwise', () => {
    const values = archiveValues(saved);
    expect(archiveChanges(saved, values)).toEqual({ update: null, error: null });
    values.autoExtract = true;
    values.conflict = 2;
    expect(archiveChanges(saved, values).update).toEqual({ autoExtract: true, conflictPolicy: 'skip' });
  });

  it('leaves the folder alone while another destination is chosen, and checks it when chosen', () => {
    const values = archiveValues(saved);
    values.folder = 'relative';
    expect(archiveChanges(saved, values).update).toBeNull();
    values.destination = 2;
    expect(archiveChanges(saved, values).error).toBe('The extraction folder must be a full path.');
    values.folder = '  ';
    expect(archiveChanges(saved, values).error).toBe('Choose the folder to extract to.');
    values.folder = 'C:\\out';
    expect(archiveChanges(saved, values).update).toEqual({ destination: 'folder', folder: 'C:\\out' });
  });
});

describe('small helpers', () => {
  it('reads extensions separated by spaces, commas or semicolons', () => {
    expect(parseExtensions(' a, b;c  d\n')).toEqual(['a', 'b', 'c', 'd']);
  });

  it('recognises the server saying it does not know a message', () => {
    expect(isUnsupportedReply("Unknown message type 'archive.settings.get'.")).toBe(true);
    expect(isUnsupportedReply('The extractor failed.')).toBe(false);
  });

  it('notifies only for a download seen finishing, not one first seen finished', () => {
    expect(justCompleted('downloading', 'completed')).toBe(true);
    expect(justCompleted(undefined, 'completed')).toBe(false);
    expect(justCompleted('completed', 'completed')).toBe(false);
    expect(justCompleted('downloading', 'failed')).toBe(false);
  });
});

describe('speed limit and proxy user name round trips', () => {
  it('keeps a stored limit that is not a multiple of 1024 until the box is edited', () => {
    const current = settings({ globalSpeedLimitBytesPerSecond: 1536 });
    const form = formFrom(current);
    expect(form.speedKb).toBe(1);
    expect(validateForm(form)).toBeNull();
    applyForm(current, form);
    expect(current.globalSpeedLimitBytesPerSecond).toBe(1536);
    form.speedKb = 3;
    applyForm(current, form);
    expect(current.globalSpeedLimitBytesPerSecond).toBe(3072);
    form.speedKb = null;
    applyForm(current, form);
    expect(current.globalSpeedLimitBytesPerSecond).toBeUndefined();
  });

  it('shows a tiny stored limit as 1 KB/s and lets it through untouched', () => {
    const current = settings({ globalSpeedLimitBytesPerSecond: 300 });
    const form = formFrom(current);
    expect(form.speedKb).toBe(1);
    applyForm(current, form);
    expect(current.globalSpeedLimitBytesPerSecond).toBe(300);
  });

  it('keeps the proxy user name when the mode is not manual', () => {
    const current = settings({ proxy: { mode: 'manual', address: 'p:1', username: 'me', hasPassword: true } });
    const form = formFrom(current);
    form.proxy.mode = 'none';
    applyForm(current, form);
    expect(current.proxy?.username).toBe('me');
    expect(current.proxy?.address).toBeUndefined();
  });
});

describe('every Options problem at once (U-WU-12)', () => {
  it('lists each bad value with its page and its box, in the desktop order', () => {
    const form = formOf();
    form.maxConcurrentDownloads = 0;
    form.defaultConnections = 999;
    const problems = validateAll(form);
    expect(problems.map((p) => [p.page, p.field])).toEqual([['connection', 'set-concurrent'], ['connection', 'set-connections']]);
    expect(validateForm(form)).toEqual(problems[0]);
  });

  it('says one problem as it is, and counts several with their pages', () => {
    const form = formOf();
    form.maxConcurrentDownloads = 0;
    expect(problemsText(validateAll(form), () => 'Connection')).toBe('Maximum simultaneous downloads must be between 1 and 16.');
    form.proxy.mode = 'manual';
    form.proxy.host = 'nope';
    const label = (page: string) => (page === 'proxy' ? 'Proxy' : 'Connection');
    expect(problemsText(validateAll(form), label)).toBe('2 values need fixing. Connection: Maximum simultaneous downloads must be between 1 and 16. Proxy: Enter the proxy address as host:port (port 1-65535).');
    expect(problemsText([], label)).toBe('');
  });
});

describe('a refusal that names settings (U-WR-14)', () => {
  it('uses the names Options shows', () => {
    expect(friendlyRefusal('These settings can only be changed in the app on this PC: browserIntegrationEnabled. Nothing was saved.'))
      .toBe('These settings can only be changed in the app on this PC: Accept downloads sent from your browser. Nothing was saved.');
    expect(friendlyRefusal('These settings can only be changed in the app on this PC: proxy, someNewThing. Nothing was saved.'))
      .toBe('These settings can only be changed in the app on this PC: Proxy, someNewThing. Nothing was saved.');
    expect(friendlyRefusal('Something else.')).toBe('Something else.');
  });

  it('offers two torrent routes: a saved "ask" shows and stays as this PC (T9)', () => {
    expect(routeShown('ask')).toBe('thisPc');
    expect(routeShown('thisPc')).toBe('thisPc');
    expect(routeShown('provider')).toBe('provider');
    expect(routeShown(undefined)).toBe('thisPc');
    expect(routeToSave('thisPc', 'ask')).toBe('ask');
    expect(routeToSave('thisPc', 'provider')).toBe('thisPc');
    expect(routeToSave('thisPc', undefined)).toBe('thisPc');
    expect(routeToSave('provider', 'ask')).toBe('provider');
  });
});
