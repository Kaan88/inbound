import { describe, expect, it } from 'vitest';
import {
  ArchiveNoticeBoard, archiveLabel, archiveNameProblem, extractSummary, joinArchivePath, looksLikeArchive, noticeTarget, packageArchiveView,
  passwordHint, passwordPrimary, planFor, removeQuestion as archiveRemoveQuestion, targetOf,
} from '../src/lib/archives';
import { archivePrimary } from '../src/lib/list';
import {
  accountView, actionsOf, buildFileTree, canBrowse, canChooseEarly, canDeleteFromProvider, checkable, choiceOf, checkedState, countedItems, downloadStarted,
  failureText, filterHosters, hosterDetails, isFullPath, isInProviderQueue, itemView, jobFor, jobRow, localWords, needsTheRest, removeQuestion,
  restoreChoice, setChecked, shortStatus, showsWaiting, sortItems, treeSummary, visibleNodes, choosableIds,
} from '../src/lib/remote';
import type { ArchiveSet, RemoteAccount, RemoteFile, RemoteItem, RemoteJob } from '../src/lib/types';

const job = (change: Partial<RemoteJob> = {}): RemoteJob => ({
  id: 'j1', provider: 'torbox', providerName: 'TorBox', kind: 'torrents', state: 'waiting', status: '', progress: 0, speedBytesPerSecond: 0,
  cached: false, approvalNeeded: false, heldForApproval: false, hasLinkPassword: false, hasArchivePassword: false, queueId: 0,
  selectionMode: 'askWhenReady', notes: [], createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', ...change,
});

const item = (change: Partial<RemoteItem> = {}): RemoteItem => ({
  provider: 'torbox', kind: 'torrents', itemId: '7', name: 'Linux', size: 0, progress: 0, speedBytesPerSecond: 0, cached: false, fileCount: 0, ...change,
});

const account = (change: Partial<RemoteAccount> = {}): RemoteAccount => ({
  provider: 'torbox', providerName: 'TorBox', capabilities: ['catalog'], itemKinds: ['torrents'], hasKey: true, keyUnreadable: false, premium: false, ...change,
});

const set = (change: Partial<ArchiveSet> = {}): ArchiveSet => ({
  id: 's1', name: 'movie.rar', directory: '/d', firstVolume: 'movie.rar', state: 'failed', stateText: 'Failed', needsPassword: false, hasPassword: false,
  volumes: [], missingVolumes: [], downloadingVolumes: [], failedVolumes: [], jobIds: [], updatedAt: '2026-01-01T00:00:00Z', ...change,
});

describe('remote rows', () => {
  it('names a waiting item by the desktop\'s words, not the raw stage', () => {
    expect(shortStatus(job({ stage: 'queued', itemId: '9' }))).toBe('Waiting for TorBox');
    expect(shortStatus(job({ stage: 'queued' }))).toBe('In TorBox\'s queue');
    expect(shortStatus(job({ stage: 'metadataPending' }))).toBe('Waiting for TorBox');
    expect(shortStatus(job({ state: 'awaitingSelection' }))).toBe('Choose files');
    expect(shortStatus(job({ state: 'failed', failureKind: 'linkPasswordRequired' }))).toBe('Needs the link\'s password');
    expect(shortStatus(job({ state: 'intended', heldForApproval: true, approvalNeeded: true }))).toBe('Copied link');
  });

  it('says the progress is the provider\'s and adds the next check', () => {
    const row = jobRow(job({ progress: 0.5, speedBytesPerSecond: 2048, nextCheckAt: '2026-01-01T00:00:10Z' }), new Date('2026-01-01T00:00:00Z'));
    expect(row.detail).toContain('TorBox reports 50%, 2.0 KB/s');
    expect(row.detail).toContain('Next check');
    expect(jobRow(job({ nextCheckAt: '2025-12-31T00:00:00Z' }), new Date('2026-01-01T00:00:00Z')).detail).toBe('Checking soon');
  });

  it('offers only what the Host would take', () => {
    expect(actionsOf(job({ state: 'blocked' }))).toMatchObject({ retry: true, checkNow: true });
    expect(actionsOf(job({ state: 'failed', failureKind: 'linkPasswordRequired' }))).toMatchObject({ enterLinkPassword: true, retry: true, checkNow: false });
    expect(actionsOf(job({ state: 'intended', heldForApproval: true, approvalNeeded: true }))).toMatchObject({ checkNow: false, approve: true });
    expect(actionsOf(job({ state: 'awaitingSelection' })).selectFiles).toBe(true);
    expect(canChooseEarly(job({ state: 'waiting', selectionMode: 'askWhenReady' }))).toBe(true);
    expect(canChooseEarly(job({ state: 'waiting', selectionMode: 'all' }))).toBe(false);
    expect(canChooseEarly(job({ state: 'intended', approvalNeeded: true }))).toBe(false);
  });

  it('knows a job in the provider\'s queue and one that can be deleted there', () => {
    expect(isInProviderQueue(job({ stage: 'queued' }))).toBe(true);
    expect(isInProviderQueue(job({ stage: 'queued', itemId: '1' }))).toBe(false);
    expect(canDeleteFromProvider(job({ itemId: '1' }))).toBe(true);
    expect(canDeleteFromProvider(job())).toBe(false);
    expect(checkable([job({ state: 'failed' })], 'torbox')).toBe(false);
    expect(checkable([job()], 'TorBox')).toBe(true);
  });

  it('finds the job that covers an item, a failed one last', () => {
    const jobs = [job({ id: 'a', itemId: '7', state: 'failed', updatedAt: '2026-02-01T00:00:00Z' }), job({ id: 'b', itemId: '7', state: 'waiting' }), job({ id: 'c', itemId: '8' })];
    expect(jobFor(jobs, item())?.id).toBe('b');
    expect(jobFor([jobs[0]], item())?.id).toBe('a');
    expect(jobFor(jobs, item({ itemId: '9' }))).toBeUndefined();
  });

  it('counts a started download only when the Host took it without a note', () => {
    expect(downloadStarted({ ok: true, job: { state: 'waiting' } })).toBe(true);
    expect(downloadStarted({ ok: true, job: { state: 'waiting' }, note: 'Already on the list.' })).toBe(false);
    expect(downloadStarted({ ok: true, job: { state: 'failed' } })).toBe(false);
    expect(downloadStarted({ ok: false })).toBe(false);
  });

  it('asks the remove question in the desktop\'s words', () => {
    expect(removeQuestion([job({ name: 'Linux' })])).toBe('Take \'Linux\' off this list? This doesn\'t touch TorBox: anything on your account stays there, and this app just stops watching it.');
    expect(removeQuestion([job(), job({ id: 'x' })])).toContain('these 2 items');
  });

  it('turns a failure kind into plain words, and never shows an address', () => {
    expect(failureText(null, 'authentication', 'TorBox')).toBe('TorBox refused the API key. Check it, then save it again.');
    expect(failureText('See https://example.com/x?key=1 now')).toBe('See an address now');
    expect(failureText()).toBe('That could not be done.');
  });
});

describe('items on the account', () => {
  it('lets any item that has not failed be downloaded', () => {
    expect(itemView(item({ cached: false, stage: 'processing' }), 'TorBox').canDownload).toBe(true);
    expect(itemView(item({ stage: 'failed' }), 'TorBox').canDownload).toBe(false);
    expect(itemView(item({ error: 'bad' }), 'TorBox').problem).toBe('bad');
  });

  it('shows the status of downloads already started', () => {
    const started = job({ itemId: '7', state: 'localStarted' });
    expect(itemView(item(), 'TorBox', started, 'Downloading locally, 2 of 5 done').status).toBe('Downloading locally, 2 of 5 done');
    expect(itemView(item(), 'TorBox', started, 'Completed').tone).toBe('done');
    expect(itemView(item(), 'TorBox', started, '1 of 3 local downloads failed').tone).toBe('failed');
    expect(itemView(item({ stage: 'ready', cached: true }), 'TorBox').status).toBe('Cached on TorBox');
    expect(itemView(item({ stage: 'ready', cached: true }), 'TorBox', job({ itemId: '7', stage: 'queued' })).status).toBe('Waiting for TorBox');
  });

  it('words local progress like the desktop', () => {
    expect(localWords([])).toBeNull();
    expect(localWords([{ state: 'completed' }, { state: 'completed' }])).toBe('Completed');
    expect(localWords([{ state: 'downloading' }])).toBe('Downloading locally');
    expect(localWords([{ state: 'downloading' }, { state: 'completed' }, { state: 'queued' }])).toBe('Downloading locally, 1 of 3 done');
    expect(localWords([{ state: 'failed' }, { state: 'completed' }])).toBe('1 of 2 local downloads failed');
    expect(localWords([{ state: 'paused' }, { state: 'completed' }])).toBe('Paused locally');
    expect(localWords([{ state: 'queued' }])).toBe('Waiting to download locally');
  });

  it('sorts by a column and keeps items with nothing to sort by at the end', () => {
    const rows = [item({ itemId: '1', name: 'b', size: 0 }), item({ itemId: '2', name: 'a', size: 30 }), item({ itemId: '3', name: 'c', size: 10 })];
    const order = (key: Parameters<typeof sortItems>[3], asc: boolean) => sortItems(rows, (r) => r, (r) => itemView(r, 'TorBox'), key, asc).map((r) => r.itemId);
    expect(order('default', true)).toEqual(['1', '2', '3']);
    expect(order('size', true)).toEqual(['3', '2', '1']);
    expect(order('size', false)).toEqual(['2', '3', '1']);
    expect(order('name', true)).toEqual(['2', '1', '3']);
  });

  it('reads the rest of the list only when an order needs it', () => {
    expect(needsTheRest('size', true, 50)).toBe(true);
    expect(needsTheRest('default', true, 50)).toBe(false);
    expect(needsTheRest('size', true, 0)).toBe(false);
    expect(countedItems(50, true)).toBe('50 items so far. There may be more.');
    expect(countedItems(1, false)).toBe('1 item');
  });

  it('gates browsing and the Waiting entry on the desktop\'s rule', () => {
    expect(canBrowse(account())).toBe(true);
    expect(canBrowse(account({ capabilities: [] }))).toBe(false);
    expect(canBrowse(account({ keyUnreadable: true }))).toBe(false);
    expect(canBrowse(account({ itemKinds: [] }))).toBe(false);
    expect(showsWaiting(account({ hasKey: false }), [])).toBe(false);
    expect(showsWaiting(account({ hasKey: false }), [job()])).toBe(true);
    expect(showsWaiting(account({ hasKey: false }), [job({ state: 'localStarted' })])).toBe(false);
  });
});

describe('file tree', () => {
  const files: RemoteFile[] = [
    { fileId: '1', relativePath: 'Show/S01/a.mkv', size: 100, infected: false, selectable: true },
    { fileId: '2', relativePath: 'Show/S01/b.mkv', size: 200, infected: false, selectable: true },
    { fileId: '3', relativePath: 'Show/readme.txt', size: 5, infected: true, selectable: false },
    { fileId: '4', relativePath: 'show/S01/c.mkv', size: 50, infected: false, selectable: true },
    { fileId: '5', size: 9, infected: false, selectable: false },
  ];

  it('builds folders from the relative paths and never makes an unsafe file choosable', () => {
    const tree = buildFileTree(files);
    expect(tree.files).toHaveLength(5);
    expect(tree.roots.map((n) => n.name)).toEqual(['Show', 'File with no safe path']);
    expect(choosableIds(tree).sort()).toEqual(['1', '2', '4']);
    const infected = tree.files.find((f) => f.fileId === '3')!;
    expect(infected.note).toBe('Marked as infected.');
  });

  it('chooses a folder as a whole and reports a partial one as mixed', () => {
    const tree = buildFileTree(files);
    const season = tree.roots[0].children[0];
    expect(season.name).toBe('S01');
    let chosen = setChecked(season, new Set(), true);
    expect([...chosen].sort()).toEqual(['1', '2', '4']);
    expect(checkedState(season, chosen)).toBe(true);
    chosen = new Set(['1']);
    expect(checkedState(season, chosen)).toBeNull();
    expect(checkedState(season, new Set())).toBe(false);
    expect(treeSummary(tree, chosen)).toBe('1 of 3 files selected, 100.0 B. 2 can\'t be selected'.replace('100.0 B', '100 B'));
  });

  it('sends "all" when everything that can be chosen is, otherwise the ids', () => {
    const tree = buildFileTree(files);
    expect(choiceOf(tree, new Set(choosableIds(tree)))).toEqual({ all: true, fileIds: [] });
    expect(choiceOf(tree, new Set(['2']))).toEqual({ all: false, fileIds: ['2'] });
    expect(choiceOf(tree, new Set())).toEqual({ all: false, fileIds: [] });
  });

  it('puts back an earlier choice and drops ids that are gone', () => {
    const tree = buildFileTree(files);
    expect([...restoreChoice(tree, ['1', 'zzz', '3'])]).toEqual(['1']);
  });

  it('hides what is under a closed folder', () => {
    const tree = buildFileTree(files);
    const all = visibleNodes(tree, new Set());
    const closed = visibleNodes(tree, new Set([tree.roots[0].key]));
    expect(closed.length).toBeLessThan(all.length);
    expect(closed[0].name).toBe('Show');
  });
});

describe('account page', () => {
  const now = new Date('2026-06-01T12:00:00Z');

  it('says the key is missing, unreadable or saved', () => {
    expect(accountView(account({ hasKey: false }), now)).toMatchObject({ canValidate: false, canClear: false, keyBoxHeader: 'API key' });
    expect(accountView(account({ hasKey: false }), now).keyStatus).toContain('You\'ll find it in your account settings on the TorBox site.');
    expect(accountView(account({ keyUnreadable: true }), now)).toMatchObject({ canValidate: false, canClear: true, keyStatusIsProblem: true, keyBoxHeader: 'Enter the API key again' });
    expect(accountView(account({ keyUnreadable: true }), now).keyStatus).toContain('on this server');
    expect(accountView(account(), now)).toMatchObject({ canValidate: true, canClear: true, keyBoxHeader: 'Replace the API key' });
  });

  it('says "Premium ended on" once the date has passed, and hides a cooldown that is over', () => {
    const base = { checkedAt: '2026-06-01T11:00:00Z', plan: 'Pro' };
    const past = accountView(account({ ...base, premiumUntil: '2026-05-01T00:00:00Z' }), now).accountDetails;
    expect(past.some((line) => line.startsWith('Premium ended on'))).toBe(true);
    const future = accountView(account({ ...base, premiumUntil: '2026-07-01T00:00:00Z', cooldownUntil: '2026-06-01T13:00:00Z' }), now).accountDetails;
    expect(future.some((line) => line.startsWith('Premium until'))).toBe(true);
    expect(future.some((line) => line.includes('reports a cooldown until'))).toBe(true);
    const over = accountView(account({ ...base, cooldownUntil: '2026-06-01T10:00:00Z' }), now).accountDetails;
    expect(over.some((line) => line.includes('cooldown'))).toBe(false);
    expect(over).toContain('Not premium');
    expect(accountView(account(), now).accountDetails[0]).toContain('Select Validate to ask TorBox.');
  });
});

describe('hosters', () => {
  const hosters = [
    { name: 'Alpha', domains: ['alpha.example'], available: true },
    { name: 'Beta', domains: ['beta.example'], available: false },
  ];

  it('filters by name or domain and by down only', () => {
    expect(filterHosters(hosters, '', true).map((h) => h.name)).toEqual(['Beta']);
    expect(filterHosters(hosters, 'ALPHA', false).map((h) => h.name)).toEqual(['Alpha']);
    expect(filterHosters(hosters, 'beta.ex', false).map((h) => h.name)).toEqual(['Beta']);
  });

  it('words the limits like the desktop', () => {
    expect(hosterDetails({ dailyLinkLimit: 20, dailyLinksUsed: 3, perLinkSizeLimit: 1024 * 1024 })).toBe('Links today 3 of 20 · Up to 1.0 MB a file');
    expect(hosterDetails({ dailyLinkLimit: 20 })).toBe('20 links a day');
  });

  it('checks a destination is a full path', () => {
    expect(isFullPath('D:\\Downloads')).toBe(true);
    expect(isFullPath('\\\\nas\\share')).toBe(true);
    expect(isFullPath('/srv/downloads')).toBe(true);
    expect(isFullPath('downloads')).toBe(false);
  });
});

describe('archives', () => {
  it('opens every kind of record, and names what the row does on Enter', () => {
    expect(archivePrimary(set({ needsPassword: true }))).toBe('password');
    expect(archivePrimary(set({ state: 'failed', failureKind: 'cancelled' }))).toBe('retry');
    expect(archivePrimary(set({ state: 'completed' }))).toBe('retry');
    expect(archivePrimary(set({ state: 'completed', archivesDeleted: true }))).toBe('none');
    expect(archivePrimary(set({ state: 'extracting' }))).toBe('none');
  });

  it('labels a set the way the desktop does', () => {
    expect(archiveLabel(set({ state: 'failed', failureKind: 'wrongPassword', needsPassword: true }))).toEqual({ text: 'Wrong password', tone: 'failed' });
    expect(archiveLabel(set({ state: 'completed' })).text).toBe('Extraction completed');
    expect(archiveLabel(set({ state: 'extracting', percent: 40 })).text).toBe('Extracting 40%');
    expect(archiveLabel(set({ state: 'downloading', downloadingVolumes: ['a.part1.rar'] })).text).toBe('');
    expect(archiveLabel(set({ state: 'waiting', downloadingVolumes: ['a.part2.rar'], pausedVolumes: ['a.part2.rar'] })).text).toBe('Waiting: part2 paused');
    expect(archiveLabel(set({ state: 'failed', failureKind: 'missingVolume', missingVolumes: ['a.part3.rar'] }))).toEqual({ text: 'Waiting: part3 missing', tone: 'waiting' });
  });

  it('shows a package\'s extraction by what most needs attention', () => {
    expect(packageArchiveView([])).toBeNull();
    const ask = set({ id: 'a', needsPassword: true, failureKind: 'passwordRequired' });
    const done = set({ id: 'b', state: 'completed' });
    expect(packageArchiveView([ask, done])?.text).toBe('1 needs a password');
    expect(packageArchiveView([done, set({ id: 'c', state: 'completed' })])?.text).toBe('Extraction completed');
    expect(packageArchiveView([done, set({ id: 'c', state: 'failed', failureKind: 'corrupted' })])?.text).toBe('1 extraction problem');
    expect(packageArchiveView([done, set({ id: 'c', state: 'downloaded' })])?.text).toBe('Extracted 1 of 2');
  });

  it('offers Extract for a selection by the file name, and says why not', () => {
    expect(looksLikeArchive('a.part1.rar')).toBe(true);
    expect(looksLikeArchive('a.r01')).toBe(true);
    expect(looksLikeArchive('a.7z.001')).toBe(true);
    expect(looksLikeArchive('movie.mkv')).toBe(false);
    const rar = { jobId: 'j', fileName: 'a.rar', filePath: '/d/a.rar' };
    expect(planFor([rar], [], null).canExtract).toBe(true);
    expect(planFor([{ ...rar, fileName: 'a.mkv' }], [], null).extractText).toBe('Extract (no archive selected)');
    expect(planFor([], [], null).extractText).toBe('Extract (select an archive download first)');
    expect(planFor([rar], [], null, false).extractText).toBe('Extract (the background Host isn\'t connected)');
    expect(planFor([{ ...rar, set: set({ state: 'completed' }) }], [], null).extractText).toBe('Extract (already extracted)');
    expect(planFor([{ ...rar, set: set({ state: 'extracting' }) }], [], null).extractText).toBe('Extract (already extracting)');
    expect(planFor([{ ...rar, set: set({ needsPassword: true }) }], [], null).extractText).toBe('Extract (enter the archive\'s password first)');
  });

  it('allows a password ahead of time, for a package, and never for a run or a finished set', () => {
    const rar = { jobId: 'j', fileName: 'a.rar', filePath: '/d/a.rar' };
    expect(planFor([rar], [], null).password).toMatchObject({ filePath: '/d/a.rar', needed: false });
    expect(planFor([], [], { id: 'p', name: 'Pack' }).password).toMatchObject({ packageId: 'p', name: 'Pack' });
    expect(planFor([{ ...rar, set: set({ state: 'extracting' }) }], [], null).password).toBeNull();
    expect(planFor([{ ...rar, set: set({ state: 'completed' }) }], [], null).password).toBeNull();
    expect(planFor([{ ...rar, set: set({ needsPassword: true }) }], [], null).password).toMatchObject({ setId: 's1', needed: true });
  });

  it('offers Retry for a failed or finished set, Cancel for a running one, and no Remove while extracting', () => {
    const plan = planFor([], [set({ state: 'extracting' })], null);
    expect(plan.cancel).toHaveLength(1);
    expect(plan.remove).toHaveLength(0);
    expect(plan.retry).toHaveLength(0);
    const failed = planFor([], [set({ state: 'failed' })], null);
    expect(failed.retry).toHaveLength(1);
    expect(failed.remove).toHaveLength(1);
    // Retry already says it for the same set, so Extract is not shown twice.
    expect(failed.showExtract).toBe(false);
  });

  it('words the password dialog by case', () => {
    expect(passwordPrimary(targetOf(set({ needsPassword: true })))).toBe('Extract with This Password');
    expect(passwordPrimary(targetOf(set()))).toBe('Save Password');
    expect(passwordHint(targetOf(set({ name: 'x.rar', failureKind: 'wrongPassword', needsPassword: true })))).toContain('was wrong');
    expect(passwordHint({ packageId: 'p', name: 'Pack', needed: false, wasWrong: false, hasPassword: false })).toContain('including ones still downloading');
  });

  it('asks the remove question once and for many', () => {
    expect(archiveRemoveQuestion([set()])).toContain('Forget the extraction record for "movie.rar"');
    expect(archiveRemoveQuestion([set(), set({ id: 'x' })])).toContain('these 2 archives');
  });

  it('puts one password for a package when several of its sets ask', () => {
    const a = set({ id: 'a', needsPassword: true, packageId: 'p' });
    const b = set({ id: 'b', needsPassword: true, packageId: 'p' });
    expect(noticeTarget(a, [a, b], 'Pack')).toMatchObject({ packageId: 'p' });
    expect(noticeTarget(a, [a], 'Pack')).toMatchObject({ setId: 'a' });
    expect(noticeTarget(a, [a, b], undefined)).toMatchObject({ setId: 'a' });
  });

  it('summarises what extraction asked for', () => {
    expect(extractSummary({ sets: [set({ state: 'extracting' })], ignored: [] })).toBe('Extraction asked for: movie.rar.');
    expect(extractSummary({ sets: [set({ state: 'waiting' }), set({ id: 'y', state: 'waiting' })], ignored: [{ reason: 'Not an archive.' }] })).toBe('Extraction asked for: 2 archives. Not an archive.');
  });

  it('builds the path of an archive on disk and rejects names that are not archives', () => {
    expect(joinArchivePath('/srv/dl', 'a.rar')).toBe('/srv/dl/a.rar');
    expect(joinArchivePath('D:\\Dl\\', 'a.zip')).toBe('D:\\Dl\\a.zip');
    expect(joinArchivePath('D:\\Dl', 'a.zip')).toBe('D:\\Dl\\a.zip');
    expect(archiveNameProblem('', 'a.rar')).toContain('Choose the folder');
    expect(archiveNameProblem('/d', '')).toContain('Enter the archive');
    expect(archiveNameProblem('/d', 'x/a.rar')).toContain('file name only');
    expect(archiveNameProblem('/d', 'a.mkv')).toContain('doesn\'t look like an archive');
    expect(archiveNameProblem('/d', 'a.rar')).toBeNull();
  });
});

describe('archive notice', () => {
  const since = new Date('2026-01-01T00:00:00Z');

  it('lists password requests oldest first with the desktop\'s sentences', () => {
    const board = new ArchiveNoticeBoard();
    const newer = set({ id: 'n', name: 'new.rar', needsPassword: true, updatedAt: '2026-02-02T00:00:00Z' });
    const older = set({ id: 'o', name: 'old.rar', needsPassword: true, failureKind: 'wrongPassword', updatedAt: '2026-02-01T00:00:00Z' });
    const notice = board.update([newer, older], since)!;
    expect(notice.setId).toBe('o');
    expect(notice.text).toBe('The password for "old.rar" was wrong, so it wasn\'t extracted. The download is fine. 1 more archive needs one too.');
  });

  it('notices a wrong password for a set that is not marked as asking by hasPassword', () => {
    const board = new ArchiveNoticeBoard();
    const wrong = set({ needsPassword: true, hasPassword: true, failureKind: 'wrongPassword' });
    expect(board.update([wrong], since)?.kind).toBe('password');
  });

  it('can be dismissed until the set has news', () => {
    const board = new ArchiveNoticeBoard();
    const asking = set({ needsPassword: true, attempts: 1 });
    const notice = board.update([asking], since)!;
    board.dismiss(notice, [asking]);
    expect(board.update([asking], since)).toBeNull();
    expect(board.update([{ ...asking, attempts: 2 }], since)?.kind).toBe('password');
  });

  it('tells of a completion that happened while the page was open, once a package is all done', () => {
    const board = new ArchiveNoticeBoard();
    const running = set({ id: 'r', state: 'extracting', packageId: 'p' });
    expect(board.update([running], since)).toBeNull();
    const done = { ...running, state: 'completed', completedAt: '2026-03-01T00:00:00Z' };
    const second = set({ id: 'q', state: 'extracting', packageId: 'p' });
    expect(board.update([done, second], since)).toBeNull();
    const notice = board.update([done, { ...second, state: 'completed', completedAt: '2026-03-01T00:01:00Z' }], since)!;
    expect(notice.kind).toBe('completed');
    expect(notice.text).toBe('Extraction completed: "movie.rar" and 1 more.');
    board.dismiss(notice, [done]);
    expect(board.update([done], since)).toBeNull();
  });

  it('ignores what finished before the page opened, and stays quiet when told to', () => {
    const board = new ArchiveNoticeBoard();
    const old = set({ state: 'completed', completedAt: '2025-01-01T00:00:00Z' });
    expect(board.update([old], since)).toBeNull();
    const quiet = new ArchiveNoticeBoard();
    expect(quiet.update([set({ state: 'completed', completedAt: '2026-03-01T00:00:00Z' })], since, false)).toBeNull();
  });
});

import { planFor as planForPath } from '../src/lib/archives';
import { noticeBoard } from '../src/lib/archives';
describe('review fixes', () => {
  it('offers a password in advance only for a full path', () => {
    const file = (filePath: string) => ({ jobId: 'j', fileName: 'a.rar', filePath, set: undefined });
    expect(planForPath([file('C:/dl/a.rar')], [], null).password).not.toBeNull();
    expect(planForPath([file('a.rar')], [], null).password ?? null).toBeNull();
  });
  it('keeps one notice board for the page', () => {
    expect(noticeBoard).toBeDefined();
  });
});

import { containerAddSummary, containerChoice, containerEntryName } from '../src/lib/remote';

describe('importing a container file', () => {
  const preview = {
    entries: [
      { entryId: 0, displayName: 'one.rar', kind: 'hosterLink', canAdd: true },
      { entryId: 1, displayHost: 'files.example', kind: 'hosterLink', canAdd: true },
      { entryId: 2, kind: 'unsupported', reason: 'An ordinary link.', canAdd: false },
    ],
  };

  it('starts with every entry that can be added chosen', () => {
    expect(containerChoice(preview)).toEqual([0, 1]);
  });

  it('names an entry by its name, else its site, else its number', () => {
    expect(preview.entries.map(containerEntryName)).toEqual(['one.rar', 'files.example', 'Link 3']);
  });

  it('says how the add went and what failed', () => {
    const summary = containerAddSummary({
      ok: true,
      outcomes: [
        { entryId: 0, ok: true },
        { entryId: 1, ok: true, existing: true },
        { entryId: 2, ok: false, error: 'Not a provider link.' },
      ],
    }, preview, 'TorBox');
    expect(summary.text).toBe("1 sent to TorBox. 1 was already listed. 1 couldn't be added.");
    expect(summary.problems).toEqual(['Link 3: Not a provider link.']);
    expect(containerAddSummary({ ok: false, error: 'This preview has expired.', outcomes: [] }, preview, 'TorBox').text).toBe('This preview has expired.');
  });
});
