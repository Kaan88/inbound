import { describe, expect, it } from 'vitest';
import { apply, emptyModel } from '../src/lib/model';
import {
  crumbs, deviceDownloadUrl, deviceUploadUrl, joinPath, nameProblem, newForm, parentPath, rcloneLine, saveRequest, siteSummary,
  transferFraction, transferLine, uploadToSite, type Site, type SiteKind, type SiteTransfer, type UploadLike,
} from '../src/lib/sites';

const sftp: SiteKind = {
  kind: 'sftp', title: 'SFTP', defaultPort: 22, authMethods: ['password', 'key'], fields: ['host', 'port', 'user', 'password', 'key'],
  choices: {}, capabilities: ['newFolder', 'emptyFolders', 'hostKey'],
};
const drive: SiteKind = {
  kind: 'drive', title: 'Google Drive', defaultPort: 0, authMethods: ['signIn'], fields: ['signIn'], choices: {},
  capabilities: ['newFolder', 'emptyFolders', 'signIn'],
};
const ftp: SiteKind = {
  kind: 'ftp', title: 'FTP', defaultPort: 21, authMethods: ['password'], fields: ['host', 'port', 'tls', 'user', 'password'],
  choices: { tls: [{ value: 'explicit', title: 'Encrypted' }, { value: 'none', title: 'Not encrypted' }] }, capabilities: [],
};

function site(change: Partial<Site> = {}): Site {
  return {
    id: 'a'.repeat(32), name: 'Box', kind: 'sftp', host: 'srv.example', port: 22, user: 'me', authMethod: 'password', hasPassword: true,
    hasPrivateKey: false, hasKeyPassphrase: false, hostKeys: [], remoteFolder: '/srv', localFolder: null, stars: [], capabilities: sftp.capabilities,
    encrypted: true, signedIn: false, hasClientSecret: false, signingIn: false, trustedCertificates: [], ...change,
  };
}

function batch(change: Partial<SiteTransfer> = {}): SiteTransfer {
  return {
    id: 't1', siteId: 's', siteName: 'Box', direction: 'download', remoteFolder: '/srv', localFolder: '/data', conflict: 'skip', state: 'running',
    files: 4, folders: 0, filesDone: 1, filesSkipped: 1, filesFailed: 0, filesRunning: 1, bytes: 400, bytesDone: 100, speed: 10,
    createdAt: '2026-11-15T10:00:00Z', ...change,
  };
}

describe('Sites in the web UI (SITES.7)', () => {
  it('walks paths on a site', () => {
    expect(joinPath('/', 'a')).toBe('/a');
    expect(joinPath('/srv/pub', 'a b')).toBe('/srv/pub/a b');
    expect(parentPath('/srv/pub')).toBe('/srv');
    expect(parentPath('/srv')).toBe('/');
    expect(parentPath('/')).toBe('/');
    expect(crumbs('/srv/pub')).toEqual([{ name: '/', path: '/' }, { name: 'srv', path: '/srv' }, { name: 'pub', path: '/srv/pub' }]);
  });

  it('puts paths into the device addresses encoded, and names a folder for an upload', () => {
    expect(deviceDownloadUrl('id1', '/a b/c&d.bin')).toBe('/api/sites/device?id=id1&path=%2Fa%20b%2Fc%26d.bin');
    expect(deviceUploadUrl('id1', '/pub', false)).toBe('/api/sites/device?id=id1&folder=%2Fpub');
    expect(deviceUploadUrl('id1', '/pub', true)).toBe('/api/sites/device?id=id1&folder=%2Fpub&replace=1');
  });

  it('refuses names a site cannot take', () => {
    expect(nameProblem('')).toBe('Type a name.');
    expect(nameProblem('a/b')).not.toBeNull();
    expect(nameProblem('..')).not.toBeNull();
    expect(nameProblem('report.pdf')).toBeNull();
  });

  it("sends only the kind's fields, and a secret only when one was typed", () => {
    const form = newForm(sftp);
    expect(form.port).toBe(22);
    form.name = ' Box ';
    form.host = 'srv.example';
    form.user = 'me';
    const request = saveRequest(form, sftp);
    expect(request).toMatchObject({ name: 'Box', kind: 'sftp', host: 'srv.example', port: 22, user: 'me' });
    expect(request.password).toBeUndefined();
    expect(request.url).toBeUndefined();
    form.password = 'typed';
    expect(saveRequest(form, sftp).password).toBe('typed');

    // Editing keeps the saved secrets unless new ones are typed.
    const edited = newForm(sftp, site());
    expect(edited.password).toBe('');
    expect(saveRequest(edited, sftp)).toMatchObject({ id: 'a'.repeat(32), password: undefined });

    // A key is sent only when signing in with one.
    form.privateKey = 'KEY';
    expect(saveRequest(form, sftp).privateKey).toBeUndefined();
    form.authMethod = 'key';
    expect(saveRequest(form, sftp).privateKey).toBe('KEY');

    // A sign-in kind sends its app fields and nothing of a server.
    const cloud = newForm(drive);
    cloud.name = 'Drive';
    cloud.clientId = 'id';
    expect(saveRequest(cloud, drive)).toMatchObject({ kind: 'drive', clientId: 'id' });
    expect(saveRequest(cloud, drive).host).toBeUndefined();

    // A choice starts at the catalog's first value.
    expect(newForm(ftp).tls).toBe('explicit');
  });

  it("says where a site is without its secrets", () => {
    expect(siteSummary(site(), [sftp])).toBe('SFTP · me@srv.example:22');
    expect(siteSummary(site({ kind: 'drive', capabilities: drive.capabilities }), [drive])).toBe('Google Drive · not signed in yet');
    expect(siteSummary(site({ kind: 'webdav', url: 'https://dav.example/' }), [])).toBe('WEBDAV · https://dav.example/');
  });

  it('shows a batch in the words of the desktop', () => {
    expect(transferLine(batch())).toBe('Moving · 2 of 4 files · 1 skipped');
    expect(transferLine(batch({ state: 'failed', filesFailed: 2 }))).toContain('2 failed');
    expect(transferFraction(batch())).toBe(0.25);
    expect(transferFraction(batch({ bytes: 0 }))).toBe(0.5);
    expect(transferFraction(batch({ bytes: 0, files: 0 }))).toBeNull();
  });

  it("says what rclone is doing", () => {
    const status = { enabled: true, rclone: 'missing', autoUpdateRclone: true, rcloneInstalling: false };
    expect(rcloneLine(status)).toContain('not on this computer');
    expect(rcloneLine({ ...status, rcloneInstalling: true })).toBe('Downloading rclone…');
    expect(rcloneLine({ ...status, rclone: 'running', rcloneVersion: 'v1.76.0' })).toBe('rclone v1.76.0 is running.');
    expect(rcloneLine({ ...status, rclone: 'stopping' })).toBe('rclone is stopping.');
    expect(rcloneLine({ ...status, rclone: 'off' })).toBe('rclone starts when a site is used.');
  });

  it('keeps the sites, their status and every batch from the server', () => {
    const model = emptyModel();
    expect(apply(model, { type: 'sites.changed', payload: { status: { enabled: true, rclone: 'off' }, sites: [site()] } } as never)).toBe(true);
    expect(model.sitesStatus?.enabled).toBe(true);
    expect(model.sites).toHaveLength(1);
    apply(model, { type: 'sites.status', payload: { enabled: false, rclone: 'off' } } as never);
    expect(model.sitesStatus?.enabled).toBe(false);
    apply(model, { type: 'sites.listResult', payload: { sites: [] } } as never);
    expect(model.sites).toHaveLength(0);
    apply(model, { type: 'sites.transfer.changed', payload: { transfers: [batch()] } } as never);
    expect(model.siteTransfers).toHaveLength(1);
    apply(model, { type: 'sites.transfer.listResult', payload: { transfers: [] } } as never);
    expect(model.siteTransfers).toHaveLength(0);
  });

  it('sends a file with its name in a header and reads the answer', async () => {
    const made: FakeUpload[] = [];
    const make = () => {
      const upload = new FakeUpload();
      made.push(upload);
      return upload;
    };
    const file = Object.assign(new Blob(['abc']), { name: 'ü report.pdf' });
    const progress: number[] = [];
    const sent = uploadToSite('id1', '/pub', file, false, (n) => progress.push(n), make);
    expect(made[0].url).toBe('/api/sites/device?id=id1&folder=%2Fpub');
    expect(made[0].headers['X-File-Name']).toBe(encodeURIComponent('ü report.pdf'));
    made[0].upload.onprogress?.({ loaded: 2, total: 3, lengthComputable: true });
    made[0].finish(200, '{"ok":true}');
    expect(await sent.done).toEqual({ ok: true });
    expect(progress).toEqual([2]);

    const taken = uploadToSite('id1', '/pub', file, false, () => {}, make);
    made[1].finish(409, '{"ok":false,"error":"ü report.pdf is already in that folder.","problem":"exists"}');
    expect(await taken.done).toEqual({ ok: false, status: 409, error: 'ü report.pdf is already in that folder.', problem: 'exists' });

    // Signed out: said, and the page goes to the sign-in.
    let signedOut = 0;
    const expired = uploadToSite('id1', '/pub', file, false, () => {}, make, () => signedOut++);
    made[2].finish(401, '');
    expect((await expired.done).ok).toBe(false);
    expect(signedOut).toBe(1);

    const lost = uploadToSite('id1', '/pub', file, true, () => {}, make);
    expect(made[3].url).toContain('replace=1');
    made[3].onerror?.();
    expect((await lost.done).ok).toBe(false);
  });
});

class FakeUpload implements UploadLike {
  url = '';
  headers: Record<string, string> = {};
  status = 0;
  responseText = '';
  upload: UploadLike['upload'] = { onprogress: null };
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  onabort: (() => void) | null = null;
  open(_method: string, url: string) { this.url = url; }
  setRequestHeader(name: string, value: string) { this.headers[name] = value; }
  send() {}
  abort() { this.onabort?.(); }
  finish(status: number, text: string) {
    this.status = status;
    this.responseText = text;
    this.onload?.();
  }
}
