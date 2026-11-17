import { describe, expect, it } from 'vitest';
import type { Site, SiteItem, SiteKind, SiteTransfer } from '../src/lib/sites';
import {
  activityText, activityWords, applyHere, arrowsText, badge, busyRemotes, canEdit, defaultName, draftTestText, footerText, forgetBeforeOpening, hereNames,
  closedToMakeRoom, closeQuestion, inTrouble, kindOfTile, lastUsedText, listed, unfinishedOn, needsPassword, phoneActions, phoneRemotesLine, phoneRowLine, switchableKinds, tileOf, tiles, tilesFor, waitingCount,
} from '../src/lib/sitesHome';
import {
  barSpeeds, barSummary, canCancel, canClear, canPause, canResume, canRetry, clearDoneTargets, description, inTab, menu, pauseAllAction, progress, spaceCommand,
  phoneButton, phoneHeader, phoneRows, phoneSheet, phoneSheetLabel, status, tabLabel, tabOf, title, valid,
} from '../src/lib/sitesTransfers';

const transfer = (over: Partial<SiteTransfer> = {}): SiteTransfer => ({
  id: 't1', siteId: 's1', siteName: 'nas.home.lan', direction: 'download', remoteFolder: '/media/isos', localFolder: 'D:\\Downloads\\ISOs', conflict: 'keepBoth',
  state: 'running', files: 1, folders: 0, filesDone: 0, filesSkipped: 0, filesFailed: 0, filesRunning: 1, bytes: 1000, bytesDone: 412, speed: 100,
  createdAt: '2026-11-17T09:00:00Z', picked: ['ubuntu.iso'], pickedCount: 1, ...over,
});
const site = (over: Partial<Site> = {}): Site => ({
  id: 's1', name: 'nas.home.lan', kind: 'sftp', host: 'nas.home.lan', port: 22, user: 'kaan', authMethod: 'password', hasPassword: true, hasPrivateKey: false, hasKeyPassphrase: false,
  hostKeys: [], remoteFolder: '/', stars: [], capabilities: [], encrypted: true, signedIn: false, hasClientSecret: false, signingIn: false, trustedCertificates: [], ...over,
});

describe('transfer rows offer only what a transfer supports', () => {
  it('a running batch can be stopped and cancelled, not resumed, retried or removed', () => {
    const t = transfer();
    expect([canPause(t), canResume(t), canRetry(t), canCancel(t), canClear(t)]).toEqual([true, false, false, true, false]);
    expect(menu([t])).toEqual([['stop'], ['showOnRemote'], ['cancel']]);
  });

  it('a paused batch resumes; a failed one with failed files retries and can be removed', () => {
    expect(menu([transfer({ state: 'paused' })])).toEqual([['resume', 'wait'], ['showOnRemote'], ['cancel']]);
    expect(menu([transfer({ state: 'failed', filesFailed: 1, filesRunning: 0 })])).toEqual([['retry'], ['showOnRemote'], ['remove']]);
  });

  it('a finished batch can only be shown or removed; none of the download list commands leak in', () => {
    const groups = menu([transfer({ state: 'done', filesRunning: 0, filesDone: 1 })]).flat();
    expect(groups).toEqual(['showOnRemote', 'remove']);
    const all = ['running', 'paused', 'failed', 'done', 'canceled'].flatMap((state) => menu([transfer({ state, filesFailed: state === 'failed' ? 1 : 0 })]).flat());
    for (const command of all) expect(['stop', 'resume', 'wait', 'retry', 'showOnRemote', 'cancel', 'remove']).toContain(command);
  });

  it('with several rows a command shows when any can take it, and "Show on the remote" only for one', () => {
    const rows = [transfer(), transfer({ id: 't2', state: 'paused' })];
    expect(menu(rows)).toEqual([['stop', 'resume', 'wait'], ['cancel']]);
    expect(valid('stop', rows[1])).toBe(false);
  });

  it('Space stops what runs, resumes what is paused', () => {
    expect(spaceCommand([transfer()])).toBe('stop');
    expect(spaceCommand([transfer({ state: 'paused' })])).toBe('resume');
    expect(spaceCommand([transfer({ state: 'done' })])).toBeNull();
  });

  it('Clear done takes finished and cancelled batches, never a failed one', () => {
    const list = [transfer({ id: 'a', state: 'done' }), transfer({ id: 'b', state: 'canceled' }), transfer({ id: 'c', state: 'failed' }), transfer({ id: 'd' })];
    expect(clearDoneTargets(list)).toEqual(['a', 'b']);
  });

  it('the bar button pauses what runs, else resumes what is paused', () => {
    expect(pauseAllAction([transfer(), transfer({ id: 'p', state: 'paused' })])?.type).toBe('sites.transfer.pause');
    expect(pauseAllAction([transfer({ state: 'paused' })])?.label).toBe('Resume transfers');
    expect(pauseAllAction([transfer({ state: 'done' })])).toBeNull();
  });
});

describe('the transfer row\'s words', () => {
  it('names the batch as the desktop does', () => {
    expect(title(transfer())).toBe('Download ubuntu.iso');
    expect(title(transfer({ picked: ['photos-2026'], files: 1204, folders: 3 }))).toBe('Download photos-2026 (1,204 items)');
    expect(title(transfer({ direction: 'upload', pickedCount: 2, picked: ['a', 'b'], files: 2 }))).toBe('Upload 2 files');
    expect(title(transfer({ pickedCount: 0, picked: [] }))).toBe('Download from nas.home.lan');
  });

  it('status shows the percent and the bar while it moves, Waiting when no file runs', () => {
    expect(status(transfer())).toEqual({ text: 'Downloading 41.2%', kind: 'active', percent: 41.2 });
    expect(status(transfer({ direction: 'upload' })).text).toBe('Uploading 41.2%');
    expect(status(transfer({ filesRunning: 0 })).text).toBe('Waiting');
    expect(status(transfer({ state: 'done' })).text).toBe('Completed');
    expect(status(transfer({ state: 'failed' })).kind).toBe('failed');
  });

  it('progress sets failed and stopped bytes aside', () => {
    expect(progress(transfer({ bytes: 1000, bytesDone: 400, bytesSetAside: 500 }))).toBe(80);
    expect(progress(transfer({ bytes: 0, files: 4, filesDone: 1 }))).toBe(25);
  });

  it('the description says why a batch failed or waits, else the route', () => {
    expect(description(transfer({ state: 'failed', message: 'The server refused the write' }))).toBe('The server refused the write');
    expect(description(transfer())).toBe('nas.home.lan to D:\\Downloads\\ISOs');
    expect(description(transfer({ conflict: 'skip', filesSkipped: 2 }))).toContain('only new files');
    expect(description(transfer({ retryAt: '2026-11-17T14:32:00' }))).toMatch(/^Waiting for nas.home.lan, trying again at 14:32\./);
  });

  it('the tabs follow the state, the active one orders moving, then paused, then waiting', () => {
    expect(['running', 'paused', 'failed', 'done', 'canceled'].map((state) => tabOf({ state }))).toEqual(['active', 'active', 'failed', 'done', 'done']);
    const list = [transfer({ id: 'wait', filesRunning: 0, bytesDone: 0 }), transfer({ id: 'p', state: 'paused' }), transfer({ id: 'go' })];
    expect(inTab(list, 'active').map((t) => t.id)).toEqual(['go', 'p', 'wait']);
    expect(tabLabel(list, 'active')).toBe('Running and waiting (3)');
  });

  it('the bar says what moves and the speeds by direction', () => {
    const list = [transfer({ speed: 300 }), transfer({ id: 'u', direction: 'upload', speed: 50 }), transfer({ id: 'w', filesRunning: 0, bytesDone: 0 })];
    expect(barSummary(list)).toBe('2 running · 1 waiting');
    expect(barSummary([])).toBe('Nothing is moving');
    expect(barSpeeds(list)).toEqual({ down: 300, up: 50 });
  });
});

describe('the sidebar entries and All remotes', () => {
  it('counts files moving by direction, batches waiting, and the progress line', () => {
    const list = [transfer({ filesRunning: 2 }), transfer({ id: 'u', direction: 'upload' }), transfer({ id: 'w', filesRunning: 0, bytesDone: 0 }), transfer({ id: 'other', siteId: 's2' })];
    const b = badge(list, 's1');
    expect(b).toMatchObject({ down: 2, up: 1, waiting: 1, running: 2 });
    expect(arrowsText(b)).toBe('↓2 ↑1');
    expect(waitingCount(b)).toBe('1');
    expect(activityText(b)).toBe('2 transfers running');
    expect(activityWords(b)).toBe('2 downloading, 1 uploading, 1 waiting');
    expect(b.progress).toBeCloseTo(41.2, 1);
  });

  it('an idle remote has no arrows, no count and no line', () => {
    const b = badge([transfer({ state: 'done' })], 's1');
    expect(b.progress).toBeNull();
    expect([arrowsText(b), waitingCount(b), activityText(b), activityWords(b)]).toEqual(['', '', null, null]);
    expect(activityText(badge([transfer({ filesRunning: 0, bytesDone: 0 })], 's1'))).toBe('1 waiting');
  });

  it('lists saved remotes first, those opened once after, and an opened one is not edited', () => {
    const once = site({ id: 'o', unsaved: true });
    expect(listed([once, site({ id: 'a' })]).map((s) => s.id)).toEqual(['a', 'o']);
    expect(canEdit(once)).toBe(false);
    expect(canEdit(site())).toBe(true);
  });

  it('the dot warns when the Host could not reach it or a sign-in is missing', () => {
    expect(inTrouble(site({ reach: 'trouble' }))).toBe(true);
    expect(inTrouble(site({ capabilities: ['signIn'], signedIn: false }))).toBe(true);
    expect(inTrouble(site({ capabilities: ['signIn'], signedIn: true, reach: 'ok' }))).toBe(false);
  });

  it('Last used reads Today, Yesterday, the date, Never', () => {
    const now = new Date(2026, 10, 17, 12, 0);
    expect(lastUsedText(new Date(2026, 10, 17, 9, 14).toISOString(), now)).toBe('Today 09:14');
    expect(lastUsedText(new Date(2026, 10, 16, 22, 15).toISOString(), now)).toBe('Yesterday 22:15');
    expect(lastUsedText(new Date(2026, 9, 30, 8, 0).toISOString(), now)).toBe('Oct 30');
    expect(lastUsedText(new Date(2025, 9, 30, 8, 0).toISOString(), now)).toBe('Oct 30, 2025');
    expect(lastUsedText(null, now)).toBe('Never');
  });

  it('the footer counts remotes', () => {
    expect([footerText(0), footerText(1), footerText(5)]).toEqual(['No remotes yet', '1 remote', '5 remotes']);
  });

  it('opening a ninth address forgets the oldest idle one, never the busy one, else refuses', () => {
    const open = Array.from({ length: 8 }, (_, i) => site({ id: `o${i}`, unsaved: true }));
    expect(forgetBeforeOpening(open.slice(0, 7), null, new Set())).toEqual([]);
    expect(forgetBeforeOpening(open, null, new Set())).toEqual(['o0']);
    expect(forgetBeforeOpening(open, 'o0', new Set(['o1']))).toEqual(['o2']);
    expect(forgetBeforeOpening(open, null, new Set(open.map((s) => s.id)))).toBeNull();
    expect(busyRemotes([transfer(), transfer({ id: 'd', siteId: 's9', state: 'done' })])).toEqual(new Set(['s1']));
  });

  it('an opened address asks for its password only when the Host says so', () => {
    expect(needsPassword('passwordNeeded', true)).toBe(true);
    expect(needsPassword('passwordNeeded', false)).toBe(false);
    expect(needsPassword('cantReach', true)).toBe(false);
  });
});

describe('Add a remote: tiles to the form', () => {
  const kind = (name: string): SiteKind => ({ kind: name, title: name, defaultPort: 0, authMethods: [], fields: [], choices: {}, capabilities: [] });
  const all = ['sftp', 'ftp', 'webdav', 'drive', 'onedrive', 'dropbox', 's3', 'b2'].map(kind);

  it('the tiles are drawn in order, without "Ask each time" or "More…"', () => {
    expect(tiles.map((t) => t.name)).toEqual(['SFTP', 'FTP', 'WebDAV', 'Google Drive', 'OneDrive', 'Dropbox', 'S3 or B2']);
  });

  it('shows the tiles whose kinds the Host offers; an older Host offers SFTP', () => {
    expect(tilesFor(all)).toHaveLength(7);
    expect(tilesFor([kind('ftp'), kind('b2')]).map((t) => t.id)).toEqual(['ftp', 's3b2']);
    expect(tilesFor([]).map((t) => t.id)).toEqual(['sftp']);
  });

  it('a tile opens the form of its first offered kind; S3 or B2 lets the user switch', () => {
    const s3b2 = tileOf('b2')!;
    expect(kindOfTile(s3b2, all)).toBe('s3');
    expect(kindOfTile(s3b2, [kind('b2')])).toBe('b2');
    expect(switchableKinds(s3b2, all).map((k) => k.kind)).toEqual(['s3', 'b2']);
    expect(switchableKinds(tileOf('sftp'), all)).toEqual([]);
    expect(tileOf('drive')?.formTitle).toBe('Add a Google Drive remote');
  });

  it('a remote with no name gets its server\'s, else the service\'s', () => {
    expect(defaultName('sftp', 'nas.home.lan')).toBe('nas.home.lan');
    expect(defaultName('webdav', '', 'https://cloud.example.net/dav/')).toBe('cloud.example.net');
    expect(defaultName('drive', '')).toBe('Google Drive');
  });

  it('the Test line follows the Host\'s answer to the form\'s draft', () => {
    expect(draftTestText({ ok: true, itemCount: 6 }, true, 0.4)).toBe('It works. Signed in and listed the start folder (6 items) in 0.4 s.');
    expect(draftTestText({ ok: false, problem: 'hostKeyNotAccepted', offeredKeys: [{ type: 'ssh-ed25519', fingerprint: 'SHA256:x', entry: 'e' }] }, false, 1)).toContain('press Trust');
    expect(draftTestText({ ok: false, problem: 'signInNeeded' }, true, 1)).toContain('press Save');
    expect(draftTestText({ ok: false, message: 'Connection refused' }, false, 1)).toBe('Connection refused');
  });
});

describe('folders: "Some of it"', () => {
  const items: SiteItem[] = [
    { name: 'backups', isFolder: true, size: -1, here: 'notHere' }, { name: 'photos-2026', isFolder: true, size: -1, here: 'notHere' },
    { name: 'a.iso', isFolder: false, size: 10, here: 'same' },
  ];

  it('asks about folders only', () => {
    expect(hereNames(items)).toEqual(['backups', 'photos-2026']);
    expect(hereNames(Array.from({ length: 150 }, (_, i) => ({ name: `f${i}`, isFolder: true, size: -1 })))).toHaveLength(100);
  });

  it('lays the answer over the folders it compared and leaves the rest as browsed', () => {
    const next = applyHere(items, [{ name: 'photos-2026', here: 'someOfIt' }, { name: 'backups', here: null }, { name: 'a.iso', here: 'different' }]);
    expect(next.map((item) => item.here)).toEqual(['notHere', 'someOfIt', 'same']);
  });
});

describe('phone width: tick boxes and the action bar', () => {
  it('shows no bar until a row is ticked; one row also offers Rename', () => {
    expect(phoneActions(0)).toEqual([]);
    expect(phoneActions(1)).toEqual(['download', 'rename', 'delete']);
    expect(phoneActions(3)).toEqual(['download', 'delete']);
  });

  it('a row says Folder or its size, then how it compares', () => {
    expect(phoneRowLine({ name: 'backups', isFolder: true, size: -1 }, '', null, 'home-server')).toBe('Folder');
    expect(phoneRowLine({ name: 'photos', isFolder: true, size: -1 }, '', 'someOfIt', 'home-server')).toBe('Folder · some of it on home-server');
    expect(phoneRowLine({ name: 'a.iso', isFolder: false, size: 1 }, '2.1 GB', 'same', 'home-server')).toBe('2.1 GB · already on home-server');
    expect(phoneRowLine({ name: 'b.iso', isFolder: false, size: 1 }, '5.8 GB', 'notHere', 'home-server')).toBe('5.8 GB');
    expect(phoneRemotesLine(5, 2)).toBe('5 remotes · 2 with transfers');
    expect(phoneRemotesLine(1, 0)).toBe('1 remote');
  });
});

// SITES.12: new visible text says "remote" and "Remote files", never "Sites" or "a site".
const pages = import.meta.glob(['../src/screens/sites/*.svelte', '../src/screens/SitesScreen.svelte'], { query: '?raw', import: 'default', eager: true }) as Record<string, string>;
const texts = import.meta.glob(['../src/lib/sites.ts', '../src/lib/sitesBrowse.ts', '../src/lib/sitesHome.ts', '../src/lib/sitesTransfers.ts'], { query: '?raw', import: 'default', eager: true }) as Record<string, string>;
const withoutComments = (source: string) => source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/<!--[\s\S]*?-->/g, '').replace(/(^|[^:])\/\/.*$/gm, '$1');
// "a site" or "the site" in what Remote files shows; "site logins" (Options) is another thing and is let through.
const aSite = /\b(?:[Aa]|[Tt]he) site\b(?!\s+logins?)/;
describe('the words (SITES.12)', () => {
  it('the Remote files pages and their texts never say "a site" or "the site"', () => {
    for (const [path, source] of Object.entries({ ...pages, ...texts })) expect(withoutComments(source), path).not.toMatch(aSite);
  });

  it('the new pages never say Sites, All sites, Add a site or Your sites', () => {
    for (const [path, source] of Object.entries(pages)) {
      const markup = source.replace(/<script[\s\S]*?<\/script>/g, '').replace(/<!--[\s\S]*?-->/g, '');
      expect(markup, path).not.toMatch(/\b(All sites|Add a site|Your sites|Edit site|Use Sites)\b|>\s*Sites\s*</);
    }
  });
});

describe('a phone\'s transfer list (R7)', () => {
  const moving = transfer({ id: 'm' });
  const queued = transfer({ id: 'q', filesRunning: 0, bytesDone: 0 });
  const paused = transfer({ id: 'p', state: 'paused' });
  const failed = transfer({ id: 'f', state: 'failed', filesFailed: 1 });
  const done = transfer({ id: 'd', state: 'done' });

  it('lists what runs and waits, then what failed, then what is done', () => {
    expect(phoneRows([done, failed, queued, moving, paused]).map((t) => t.id)).toEqual(['m', 'p', 'q', 'f', 'd']);
  });

  it('says "N running · M waiting · K failed" in place of the tabs', () => {
    expect(phoneHeader([moving, queued, paused, failed, done])).toBe('1 running · 2 waiting · 1 failed');
    expect(phoneHeader([moving])).toBe('1 running');
    expect(phoneHeader([done])).toBe('Nothing is moving');
  });

  it('gives a moving card Pause and a waiting or failed card ×', () => {
    expect(phoneButton(moving)).toBe('stop');
    expect(phoneButton(queued)).toBe('cancel');
    expect(phoneButton(paused)).toBe('cancel');
    expect(phoneButton(failed)).toBe('remove');
    expect(phoneButton(transfer({ state: 'failed', filesFailed: 0, problem: 'auth' }))).toBe('cancel');
    expect(phoneButton(done)).toBe('remove');
  });

  it('a long press offers what the card allows, in a fixed order', () => {
    expect(phoneSheet(moving)).toContain('stop');
    expect(phoneSheet(moving)).not.toContain('resume');
    expect(phoneSheet(paused)).toContain('resume');
    expect(phoneSheet(failed)).toContain('retry');
    expect(phoneSheet(failed).at(-1)).toBe('remove');
    expect(phoneSheet(done)).toEqual(['remove']);
    for (const t of [moving, queued, paused, failed, done]) expect(phoneSheet(t).every((c) => valid(c, t))).toBe(true);
    expect(phoneSheetLabel('cancel')).toBe('Cancel');
    expect(phoneSheetLabel('remove')).toBe('Remove from list');
  });
});

describe('closing an address opened once (final review)', () => {
  const on = (over: Partial<SiteTransfer>) => transfer({ siteId: 'once', ...over });

  it('counts the batches Close would cancel', () => {
    expect(unfinishedOn([on({ state: 'running' }), on({ state: 'paused' }), on({ state: 'done' }), on({ state: 'failed' }), transfer({ siteId: 'other' })], 'once')).toBe(2);
  });

  it('asks first only when transfers would be cancelled', () => {
    expect(closeQuestion('nas', 0)).toBeNull();
    expect(closeQuestion('nas', 1)?.text).toMatch(/^1 transfer will be cancelled\./);
    expect(closeQuestion('nas', 3)).toEqual({ title: 'Close nas?', text: '3 transfers will be cancelled. Inbound forgets this address; nothing on the server is deleted.' });
  });

  it('names the address closed to make room', () => {
    expect(closedToMakeRoom(['nas.home.lan'])).toBe('Closed nas.home.lan to make room.');
  });
});
