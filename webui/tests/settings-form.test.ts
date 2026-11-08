import { describe, expect, it } from 'vitest';
import { justCompleted } from '../src/lib/notify';
import {
  applyForm, archiveChanges, archiveValues, composeProxyAddress, formFrom, isUnsupportedReply, parseExtensions, parseProfiles, profilesToText,
  splitProxyAddress, validateForm, type OptionsForm,
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

describe('per-site limits', () => {
  it('writes and reads pattern=max lines', () => {
    expect(profilesToText([{ pattern: '*.a.com', maxConnections: 2 }, { pattern: 'b.org', maxConnections: 4 }])).toBe('*.a.com=2\nb.org=4');
    expect(parseProfiles(' *.a.com = 2 \r\n\r\nb.org=4\n').profiles).toEqual([{ pattern: '*.a.com', maxConnections: 2 }, { pattern: 'b.org', maxConnections: 4 }]);
  });

  it('names the bad line', () => {
    expect(parseProfiles('nolimit').error).toBe("Per-site limit 'nolimit' must use pattern=max (1-32).");
    expect(parseProfiles('a=33').error).toContain("'a=33'");
    expect(parseProfiles('=3').error).toContain("'=3'");
    expect(parseProfiles('a=0').error).not.toBeNull();
  });
});

describe('validateForm', () => {
  it('passes a form made from good settings', () => {
    expect(validateForm(formOf())).toBeNull();
  });

  it('turns an emptied number box into the desktop message, on its page', () => {
    const form = formOf();
    form.maxConcurrentDownloads = null;
    expect(validateForm(form)).toEqual({ message: 'Maximum simultaneous downloads must be between 1 and 16.', page: 'connection' });
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
    expect(validateForm(form)?.message).toBe('Speed limit must be a positive number of KB/s.');
  });

  it('checks a manual proxy address, but not one that is switched off', () => {
    const form = formOf();
    form.proxy.host = 'nonsense';
    expect(validateForm(form)).toBeNull();
    form.proxy.mode = 'manual';
    expect(validateForm(form)).toEqual({ message: 'Enter the proxy address as host:port (port 1-65535).', page: 'proxy' });
  });

  it('asks for a login password again when the site changed, and for a name and site on every row', () => {
    const form = formOf({ siteLogins: [{ id: 'a', site: 'x.com', username: 'u', hasPassword: true }] });
    form.logins[0].site = 'y.com';
    expect(validateForm(form)).toEqual({ message: 'Re-enter the password for a login whose site or user name changed.', page: 'logins' });
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
    expect(validateForm(form, { saved, values })).toEqual({ message: 'Choose the folder to extract to.', page: 'extraction' });
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
    form.profiles = 'b.org=4';
    form.logins[0].removed = true;
    applyForm(current, form);
    expect(current.maxConcurrentDownloads).toBe(5);
    expect(current.globalSpeedLimitBytesPerSecond).toBe(102400);
    expect(current.proxy).toMatchObject({ mode: 'manual', address: 'socks5://p:1080', username: 'me', password: 'secret', clearPassword: false });
    expect(current.connectionProfiles).toEqual([{ pattern: 'b.org', maxConnections: 4 }]);
    expect(current.siteLogins).toEqual([]);
    expect(current.updateManifestUrl).toBe('https://feed');
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
    expect(current.proxy.username).toBeUndefined();
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
