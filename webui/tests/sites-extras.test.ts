import { describe, expect, it } from 'vitest';
import { limitProblem, limitValue, newForm, saveRequest, type SiteItem, type SiteSyncPlan, type SiteTransfer } from '../src/lib/sites';
import { remoteMenu, serverUpload, uploadSourceProblem } from '../src/lib/sitesBrowse';
import {
  betweenCopy, betweenProblem, canPreview, finishedSince, planSummary, previewImageSrc, previewNote, previewText, rememberFolder, rememberSort,
  sortFromSite, sourceChoices, sourceFromValue, startFolder, touches,
} from '../src/lib/sitesExtras';
import { canResume, menu, route, status, title, valid } from '../src/lib/sitesTransfers';

const size = (n: number) => `${n} B`;
const file = (name: string): SiteItem => ({ name, isFolder: false, size: 1, modified: null } as SiteItem);
const folder = (name: string): SiteItem => ({ name, isFolder: true, size: 0, modified: null } as SiteItem);
const transfer = (over: Partial<SiteTransfer>): SiteTransfer => ({
  id: 't', siteId: 'a', siteName: 'A', direction: 'download', remoteFolder: '/', localFolder: 'D:\\x', conflict: 'keepBoth', state: 'running',
  files: 1, folders: 0, filesDone: 0, filesSkipped: 0, filesFailed: 0, filesRunning: 0, bytes: 0, bytesDone: 0, speed: 0, createdAt: '', ...over,
} as SiteTransfer);

describe('remember the folder and sort', () => {
  it('opens in the last folder, then the start folder, then the top', () => {
    expect(startFolder({ lastFolder: '/a/b', remoteFolder: '/start' })).toBe('/a/b');
    expect(startFolder({ lastFolder: '', remoteFolder: '/start' })).toBe('/start');
    expect(startFolder({ lastFolder: null, remoteFolder: '' })).toBe('/');
  });
  it('sends only a changed folder, and never for an address opened once', () => {
    expect(rememberFolder({ id: 's', lastFolder: '/a' }, '/a')).toBeNull();
    expect(rememberFolder({ id: 's', lastFolder: '/a' }, '/b')).toEqual({ id: 's', lastFolder: '/b' });
    expect(rememberFolder({ id: 's', unsaved: true }, '/b')).toBeNull();
  });
  it('maps the sort both ways; "Already on the server" is not kept', () => {
    expect(rememberSort({ id: 's' }, 'size', false)).toEqual({ id: 's', sort: 'size', sortDescending: true });
    expect(rememberSort({ id: 's' }, 'here', true)).toBeNull();
    expect(sortFromSite({ sort: 'modified', sortDescending: true })).toEqual({ key: 'modified', ascending: false });
    expect(sortFromSite({ sort: 'type', sortDescending: false })).toEqual({ key: 'name', ascending: true });
    expect(sortFromSite({})).toEqual({ key: 'name', ascending: true });
  });
});

describe('the left pane', () => {
  const sites = [{ id: 'a', name: 'A' }, { id: 'b', name: 'B' }, { id: 'c', name: 'C', unsaved: true }];
  it('offers the server and every saved remote, the right pane\'s own included', () => {
    expect(sourceChoices(sites, 'Inbound').map((choice) => choice.value)).toEqual(['server', 'remote:a', 'remote:b']);
  });
  it('falls back to the server for a remote that is gone', () => {
    expect(sourceFromValue('remote:b', sites)).toEqual({ kind: 'remote', siteId: 'b' });
    expect(sourceFromValue('remote:zzz', sites)).toEqual({ kind: 'server' });
    expect(sourceFromValue('server', sites)).toEqual({ kind: 'server' });
  });
  it('builds a copy between remotes, and refuses a folder into itself', () => {
    expect(betweenCopy('a', '/x', 'b', '/y', [file('f'), folder('d')])).toMatchObject({ direction: 'between', siteId: 'a', targetSiteId: 'b', targetFolder: '/y', localFolder: '' });
    expect(betweenCopy('a', '/x', 'b', '', [file('f')])).toBeNull();
    expect(betweenProblem('a', '/x', 'b', '/x', [file('f')])).toBeNull();
    expect(betweenProblem('a', '/x', 'a', '/x', [file('f')])).not.toBeNull();
    expect(betweenProblem('a', '/x', 'a', '/x/d/sub', [folder('d')])).toMatch(/into itself/);
    expect(betweenProblem('a', '/x', 'a', '/y', [folder('d')])).toBeNull();
  });
  it('uploads a file from the server pane as a name in its folder', () => {
    expect(serverUpload('s', '/r', 'D:\\Downloads\\a\\f.bin', 'keepBoth', ['D:\\Downloads'], false))
      .toMatchObject({ localFolder: 'D:\\Downloads\\a', names: [{ name: 'f.bin', isFolder: false }] });
    expect(uploadSourceProblem('D:\\Downloads', ['D:\\Downloads'], false)).not.toBeNull();
    expect(uploadSourceProblem('D:\\Downloads\\a\\f.bin', ['D:\\Downloads'], false)).toBeNull();
  });
  it('lists again the folders a finished transfer changed', () => {
    const before = [transfer({ state: 'running' })];
    const after = [transfer({ state: 'done' })];
    expect(finishedSince(before, after)).toHaveLength(1);
    expect(finishedSince(after, after)).toHaveLength(0);
    expect(touches(transfer({ direction: 'download' }))).toEqual({ server: true, remoteIds: [] });
    expect(touches(transfer({ direction: 'upload', siteId: 'a' }))).toEqual({ server: false, remoteIds: ['a'] });
    expect(touches(transfer({ direction: 'between', siteId: 'a', targetSiteId: 'b' }))).toEqual({ server: false, remoteIds: ['b'] });
  });
});

describe('preview', () => {
  it('is offered for one file only', () => {
    expect(canPreview([file('a')])).toBe(true);
    expect(canPreview([folder('a')])).toBe(false);
    expect(canPreview([file('a'), file('b')])).toBe(false);
    expect(remoteMenu([], [file('a')]).flat()).toContain('preview');
    expect(remoteMenu([], [folder('a')]).flat()).not.toContain('preview');
  });
  it('reads text as UTF-8 and draws only the image types the Host names', () => {
    expect(previewText({ data: btoa(String.fromCharCode(0xc3, 0xa9)) })).toBe('\u00e9');
    expect(previewText({ data: null })).toBe('');
    expect(previewImageSrc({ kind: 'image', mimeType: 'image/png', data: 'AA==' })).toBe('data:image/png;base64,AA==');
    expect(previewImageSrc({ kind: 'image', mimeType: 'image/svg+xml', data: 'AA==' })).toBeNull();
    expect(previewImageSrc({ kind: 'text', mimeType: 'image/png', data: 'AA==' })).toBeNull();
  });
  it('says when it shows the first part only, or why there is none', () => {
    expect(previewNote({ ok: true, kind: 'text', name: 'a', size: 10, truncated: true }, size)).toMatch(/first part/);
    expect(previewNote({ ok: false, message: 'Too large.', kind: 'none', name: 'a', size: 0, truncated: false }, size)).toBe('Too large.');
  });
});

describe('speed limit', () => {
  it('reads empty as no limit and refuses what the Host would', () => {
    expect(limitValue('')).toBe(0);
    expect(limitValue(' 256 ')).toBe(256);
    expect(limitValue('-1')).toBeNull();
    expect(limitValue('1.5')).toBeNull();
    expect(limitValue('10000001')).toBeNull();
    expect(limitProblem({ downLimit: '5', upLimit: 'x' })).not.toBeNull();
    expect(limitProblem({ downLimit: '', upLimit: '' })).toBeNull();
  });
  it('is sent with the form and comes back into it', () => {
    const form = newForm(undefined, { id: 's', name: 'S', kind: 'sftp', host: 'h', port: 22, user: 'u', authMethod: 'password', remoteFolder: '/', downLimit: 100, upLimit: 0 } as never);
    expect(form).toMatchObject({ downLimit: '100', upLimit: '' });
    expect(saveRequest(form, undefined)).toMatchObject({ downLimit: 100, upLimit: 0 });
  });
});

describe('sync plan', () => {
  const plan = (over: Partial<SiteSyncPlan>): SiteSyncPlan => ({ ok: true, direction: 'download', remoteFolder: '/', localFolder: 'D:\\x', files: [], count: 0, bytes: 0, same: 0, newerThere: 0, ...over });
  it('says what will be copied, what matches and what is left alone', () => {
    expect(planSummary(plan({}), size)).toBe('Nothing to copy: the folders match.');
    expect(planSummary(plan({ newerThere: 2 }), size)).toMatch(/2 newer there are left alone/);
    const text = planSummary(plan({ count: 2, bytes: 30, same: 5, newerThere: 1, files: [{ path: 'a', size: 10, reason: 'new' }, { path: 'b', size: 20, reason: 'changed' }] }), size);
    expect(text).toBe('2 files to copy (30 B): 1 new, 1 changed. 5 already match. 1 newer there is left alone.');
    expect(planSummary(plan({ ok: false, message: 'No.' }), size)).toBe('No.');
  });
});

describe('copy between remotes and schedules in the transfers list', () => {
  const between = transfer({ direction: 'between', siteName: 'A', targetSiteName: 'B', picked: ['x.iso'], pickedCount: 1, state: 'running', filesRunning: 1, bytes: 10, bytesDone: 5 });
  it('is named as a copy from one remote to the other', () => {
    expect(title(between)).toBe('Copy x.iso');
    expect(route(between)).toBe('A to B');
    expect(status(between).text).toMatch(/^Copying/);
  });
  it('a batch waiting for a schedule says so and can be started or held', () => {
    const waiting = transfer({ state: 'paused', waitsForSchedule: true });
    expect(status(waiting).text).toBe('Waits for a schedule');
    expect(valid('wait', waiting)).toBe(false);
    expect(canResume(waiting)).toBe(true);
    const plain = transfer({ state: 'paused' });
    expect(valid('wait', plain)).toBe(true);
    expect(menu([plain]).flat()).toContain('wait');
    expect(menu([transfer({ state: 'running' })]).flat()).not.toContain('wait');
  });
});
