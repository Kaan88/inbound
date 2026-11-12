// EXT.R11 (GitHub #4): the popup's right-click menu and the folder button beside Delete. What each row offers, the
// button order, the messages the popup sends, and what the background passes on (one download each, exact payloads).
const assert = require('node:assert/strict');
const test = require('node:test');
const { installDom, flush } = require('./dom-harness.cjs');
const { sendRuntimeMessage, answerNative, withBackground, capturingChrome, catching, ownPage } = require('./harness.cjs');

const JOB = '6f1c3c2e-8a4b-4c7d-9e2f-1a2b3c4d5e6f';
const finished = (overrides = {}) => ({
  id: JOB, target: 'local', fileName: 'report.pdf', state: 'completed', receivedBytes: 4, totalBytes: 4,
  url: 'https://files.example.test/report.pdf', createdAt: '2026-11-10T10:00:00Z', canPause: false, canResume: false, ...overrides,
});

test('EXT.R11: the menu offers folder, link, redownload, rename and delete, each only where it can work', () => {
  const page = installDom('popup.html');
  try {
    const { rowMenuEntries, showsFolderButton } = page.load('job-list.ts');
    const shape = (job) => rowMenuEntries(job).map((entry) => `${entry.id}:${entry.enabled ? 'on' : 'off'}`);

    assert.deepEqual(shape(finished()), ['show:on', 'reveal:on', 'copyLink:on', 'redownload:on', 'rename:on', 'remove:on']);
    assert.deepEqual(rowMenuEntries(finished()).map((entry) => entry.label),
      ['Show in app', 'Open containing folder', 'Copy download link', 'Redownload', 'Rename…', 'Delete…']);
    assert.match(rowMenuEntries(finished()).find((entry) => entry.id === 'redownload').title, /new copy/);
    // Not built: Open file (owner question).
    assert.equal(rowMenuEntries(finished()).some((entry) => /open file/i.test(entry.label)), false);

    // The server's downloads: no folder actions.
    assert.deepEqual(shape(finished({ target: 'server' })), ['copyLink:on', 'redownload:on', 'rename:on', 'remove:on']);
    // Unfinished: no rename, no redownload while it runs or waits.
    for (const state of ['downloading', 'paused', 'queued', 'connecting']) {
      assert.deepEqual(shape(finished({ state })), ['show:on', 'reveal:on', 'copyLink:on', 'redownload:off', 'rename:off', 'remove:on'], state);
    }
    assert.deepEqual(shape(finished({ state: 'failed' })), ['show:on', 'reveal:on', 'copyLink:on', 'redownload:on', 'rename:off', 'remove:on']);
    // A provider's file and a hidden key: nothing to copy or send again.
    assert.deepEqual(shape(finished({ url: 'remote://torbox/torrents/1/2' })), ['show:on', 'reveal:on', 'copyLink:off', 'redownload:off', 'rename:on', 'remove:on']);
    assert.deepEqual(shape(finished({ addressHidden: true })), ['show:on', 'reveal:on', 'copyLink:off', 'redownload:off', 'rename:on', 'remove:on']);
    assert.deepEqual(shape(finished({ url: undefined })), ['show:on', 'reveal:on', 'copyLink:off', 'redownload:off', 'rename:on', 'remove:on']);
    assert.deepEqual(shape(finished({ fileRemovedAfterExtraction: true })), ['show:on', 'reveal:on', 'copyLink:on', 'redownload:on', 'rename:off', 'remove:on']);

    assert.equal(showsFolderButton(finished()), true);
    assert.equal(showsFolderButton(finished({ target: undefined })), true);
    assert.equal(showsFolderButton(finished({ target: 'server' })), false);
    assert.equal(showsFolderButton(finished({ state: 'downloading' })), false);
  } finally { page.restore(); }
});

test('EXT.R11: the folder button sits left of Delete on a finished This PC row only, and two buttons stay the most', () => {
  const page = installDom('popup.html');
  try {
    const { renderPopupRows } = page.load('popup-list.ts');
    const list = page.document.getElementById('jobs');
    const revealed = [];
    const handlers = { onAction() {}, onOpen() {}, onReveal: (job) => revealed.push(job.id), onMenu() {} };
    const jobs = [
      finished(),
      finished({ id: 'server-1', target: 'server', fileName: 'server.iso' }),
      finished({ id: 'running', fileName: 'running.zip', state: 'downloading', canPause: true }),
    ];
    renderPopupRows(list, jobs, { paired: true, serverName: 'home', groupsOk: { local: true, server: true } }, handlers);
    const buttons = (name) => [...[...list.querySelectorAll('.job')].find((row) => row.textContent.includes(name))
      .querySelectorAll('.job-actions button')];
    assert.deepEqual(buttons('report.pdf').map((button) => button.dataset.action), ['reveal', 'remove']);
    assert.deepEqual(buttons('server.iso').map((button) => button.dataset.action), ['remove']);
    assert.deepEqual(buttons('running.zip').map((button) => button.dataset.action), ['pause', 'remove']);
    const folder = buttons('report.pdf')[0];
    assert.equal(folder.title, 'Open containing folder');
    assert.equal(folder.getAttribute('aria-label'), 'Open containing folder of report.pdf');
    folder.focus();
    folder.click();
    assert.deepEqual(revealed, [JOB]);

    // The 1 s poll keeps the button and its focus.
    renderPopupRows(list, jobs.map((job) => ({ ...job })), { paired: true, serverName: 'home', groupsOk: { local: true, server: true } }, handlers);
    assert.equal(buttons('report.pdf')[0], folder);
    assert.equal(page.document.activeElement, folder);

    // Without a handler (an older caller) there is no folder button.
    const plain = page.document.createElement('ul');
    renderPopupRows(plain, [finished()], { paired: false, serverName: '', groupsOk: {} }, { onAction() {}, onOpen() {} });
    assert.deepEqual([...plain.querySelectorAll('.job-actions button')].map((button) => button.dataset.action), ['remove']);
  } finally { page.restore(); }
});

test('EXT.R11: a right-click, the Menu key and Shift+F10 open the row menu', () => {
  const page = installDom('popup.html');
  try {
    const { renderPopupRows } = page.load('popup-list.ts');
    const list = page.document.getElementById('jobs');
    const opened = [];
    renderPopupRows(list, [finished()], { paired: false, serverName: '', groupsOk: {} },
      { onAction() {}, onOpen() {}, onReveal() {}, onMenu: (job, row, at) => opened.push({ id: job.id, row, at }) });
    const row = list.querySelector('.job');
    const click = new page.window.MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 40, clientY: 30, button: 2 });
    row.querySelector('.job-name').dispatchEvent(new page.window.MouseEvent('pointerdown', { bubbles: true, button: 2 }));
    row.querySelector('.job-name').dispatchEvent(click);
    assert.equal(click.defaultPrevented, true, 'the browser\'s own menu stays shut');
    row.dispatchEvent(new page.window.KeyboardEvent('keydown', { key: 'ContextMenu', bubbles: true, cancelable: true }));
    row.dispatchEvent(new page.window.KeyboardEvent('keydown', { key: 'F10', shiftKey: true, bubbles: true, cancelable: true }));
    row.dispatchEvent(new page.window.KeyboardEvent('keydown', { key: 'F10', bubbles: true, cancelable: true }));
    assert.deepEqual(opened.map((entry) => [entry.id, entry.row === row, entry.at]),
      [[JOB, true, { x: 40, y: 30 }], [JOB, true, undefined], [JOB, true, undefined]]);

    // The browser's own contextmenu for that key press comes next, on the row or on the menu that took focus: it opens
    // nothing more, and the browser's menu stays shut.
    const followRow = new page.window.MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 60, clientY: 12 });
    row.dispatchEvent(followRow);
    const elsewhere = page.document.createElement('button');
    page.document.body.append(elsewhere);
    const followMenu = new page.window.MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 60, clientY: 12 });
    elsewhere.dispatchEvent(followMenu);
    assert.equal(opened.length, 3, 'the menu opened once per key press');
    assert.equal(followRow.defaultPrevented, true);
    assert.equal(followMenu.defaultPrevented, true);
  } finally { page.restore(); }
});

test("EXT.R11: a contextmenu with no pointer press before it is the keyboard's, so the menu opens under the row", () => {
  const page = installDom('popup.html');
  try {
    const { renderPopupRows } = page.load('popup-list.ts');
    const list = page.document.getElementById('jobs');
    const opened = [];
    renderPopupRows(list, [finished()], { paired: false, serverName: '', groupsOk: {} },
      { onAction() {}, onOpen() {}, onReveal() {}, onMenu: (job, row, at) => opened.push(at) });
    const row = list.querySelector('.job');
    // Shift+F10 in a browser that sends only the contextmenu, at the focused element's position.
    const keyboard = new page.window.MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 120, clientY: 18 });
    row.dispatchEvent(keyboard);
    assert.equal(keyboard.defaultPrevented, true);
    row.dispatchEvent(new page.window.MouseEvent('pointerdown', { bubbles: true, button: 2 }));
    row.dispatchEvent(new page.window.MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 33, clientY: 22, button: 2 }));
    assert.deepEqual(opened, [undefined, { x: 33, y: 22 }]);
  } finally { page.restore(); }
});

test("EXT.R11: an older app's refusal says to update, and a rename counts only when the name came back changed", () => {
  const page = installDom('popup.html');
  try {
    const { jobCommandOutcome } = page.load('job-list.ts');
    // The 0.6.6 native host, an older Host and an older server, in their own words.
    for (const reply of [{ ok: false, message: 'The browser extension cannot send this message.' },
      { ok: false, message: "Unknown message type 'job.reveal'." }]) {
      assert.deepEqual(jobCommandOutcome('reveal', reply), { ok: false, error: 'Update Inbound to use this.' });
    }
    assert.deepEqual(jobCommandOutcome('redownload', { ok: false, message: "A paired browser can't send 'job.redownload'." }, { server: true }),
      { ok: false, error: 'Update the Inbound server to use this.' });
    // The app's own refusal is shown as it is.
    assert.deepEqual(jobCommandOutcome('reveal', { ok: false, message: 'The file and its folder are gone.' }),
      { ok: false, error: 'The file and its folder are gone.' });
    // A Host older than Rename ignores the name and says ok: not reported as renamed.
    const old = jobCommandOutcome('rename', { ok: true, job: { id: JOB, fileName: 'report.pdf' } }, { newFileName: 'Report.pdf' });
    assert.equal(old.ok, false);
    assert.equal(old.error, 'The file was not renamed. Update Inbound to use this.');
    assert.equal(jobCommandOutcome('rename', { ok: true, job: { id: JOB, fileName: 'Report.pdf' } }, { newFileName: 'Report.pdf' }).ok, true);
    assert.deepEqual(jobCommandOutcome('reveal', { ok: true, message: 'Showing the file in its folder.' }),
      { ok: true, job: undefined, message: 'Showing the file in its folder.' });
  } finally { page.restore(); }
});

async function openPopup(jobs, handlers = {}) {
  const status = { ok: true, enabled: true, autoIntercept: true, automaticSending: true, connected: true, lastError: '', localMissing: false,
    server: null, automaticTarget: 'local', pauseUntil: 0, settings: { enabled: true, autoIntercept: true }, leftInBrowser: [], jobs };
  const page = installDom('popup.html', { handlers: {
    'dm.status': async () => status,
    'dm.jobs': async () => ({ ok: true, jobs }),
    'dm.askQueue': async () => ({ ok: true, items: [] }),
    ...handlers,
  } });
  page.load('popup.ts');
  await page.ready();
  await flush(12);
  return page;
}

function menuFor(page, name) {
  const row = [...page.document.querySelectorAll('.job')].find((item) => item.textContent.includes(name));
  row.dispatchEvent(new page.window.MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 20, clientY: 20, button: 2 }));
  const menu = page.document.querySelector('.popup-menu');
  assert.ok(menu, 'the menu opened');
  return Object.fromEntries([...menu.querySelectorAll('.menu-item')].map((item) => [item.textContent, item]));
}

test('EXT.R11: the popup\'s menu sends reveal, redownload and rename for one download, copies only its own link', async () => {
  const replies = { reveal: { ok: true, message: 'Showing the file in its folder.' }, rename: { ok: true }, redownload: { ok: true } };
  const page = await openPopup([finished(), finished({ id: 'run-1', fileName: 'running.zip', state: 'downloading', canPause: true })],
    { 'dm.jobCommand': async (message) => replies[message.command] });
  const copied = [];
  Object.defineProperty(page.window.navigator, 'clipboard', { configurable: true, value: { writeText: async (text) => { copied.push(text); } } });
  try {
    const doc = page.document;
    const commands = () => page.sent.filter((message) => message.type === 'dm.jobCommand');

    let items = menuFor(page, 'report.pdf');
    assert.deepEqual(Object.keys(items), ['Show in app', 'Open containing folder', 'Copy download link', 'Redownload', 'Rename…', 'Delete…']);
    assert.match(items.Redownload.title, /new copy/);
    items['Open containing folder'].click();
    await flush();
    assert.deepEqual(commands().at(-1), { type: 'dm.jobCommand', command: 'reveal', jobId: JOB, target: 'local' });
    assert.equal(doc.querySelector('.popup-menu'), null, 'the menu closed');
    assert.equal(doc.getElementById('live').textContent, 'Showing the file in its folder.');

    // The folder button sends the same.
    [...doc.querySelectorAll('.job')].find((row) => row.textContent.includes('report.pdf')).querySelector('[data-action="reveal"]').click();
    await flush();
    assert.equal(commands().length, 2);
    assert.equal(commands().at(-1).command, 'reveal');

    menuFor(page, 'report.pdf')['Copy download link'].click();
    await flush();
    assert.deepEqual(copied, ['https://files.example.test/report.pdf']);
    assert.equal(commands().length, 2, 'copying asks nothing of the app');

    menuFor(page, 'report.pdf').Redownload.click();
    await flush();
    let dialog = doc.querySelector('.dialog');
    assert.equal(dialog.querySelector('.dialog-header').textContent, 'Redownload?');
    assert.match(dialog.querySelector('.hint').textContent, /new copy/);
    dialog.querySelector('.btn-primary').click();
    await flush();
    assert.deepEqual(commands().at(-1), { type: 'dm.jobCommand', command: 'redownload', jobId: JOB, target: 'local' });

    menuFor(page, 'report.pdf')['Rename…'].click();
    await flush();
    dialog = doc.querySelector('.dialog');
    const field = dialog.querySelector('input[type=text]');
    assert.equal(field.value, 'report.pdf');
    assert.equal(doc.activeElement, field);
    assert.deepEqual([field.selectionStart, field.selectionEnd], [0, 'report'.length], 'the name before the extension is selected');
    field.value = 'bad/name.pdf';
    dialog.querySelector('.btn-primary').click();
    await flush();
    assert.match(doc.getElementById('errorText').textContent, /can't contain/);
    assert.equal(commands().at(-1).command, 'redownload', 'a bad name is not sent');
    dialog = doc.querySelector('.dialog');
    dialog.querySelector('input[type=text]').value = 'report-2026.pdf';
    dialog.querySelector('input[type=text]').dispatchEvent(new page.window.KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    await flush();
    assert.deepEqual(commands().at(-1), { type: 'dm.jobCommand', command: 'rename', jobId: JOB, target: 'local', newFileName: 'report-2026.pdf' });

    // A running download: Redownload and Rename are off.
    items = menuFor(page, 'running.zip');
    assert.equal(items.Redownload.disabled, true);
    assert.equal(items['Rename…'].disabled, true);
    assert.equal(items['Open containing folder'].disabled, false);
  } finally { page.restore(); }
});

test('EXT.R11: the app\'s refusal is shown, and Delete in the menu is the Delete button\'s question', async () => {
  const page = await openPopup([finished()], {
    'dm.jobCommand': async () => ({ ok: false, error: 'The file and its folder are gone.' }),
  });
  try {
    const doc = page.document;
    menuFor(page, 'report.pdf')['Open containing folder'].click();
    await flush();
    assert.equal(doc.getElementById('errorText').textContent, 'The file and its folder are gone.');
    assert.equal(doc.getElementById('error').classList.contains('hidden'), false);

    menuFor(page, 'report.pdf')['Delete…'].click();
    await flush();
    assert.equal(doc.querySelector('.dialog .dialog-header').textContent, 'Delete download?');
    doc.querySelector('.dialog .btn-danger').click();
    await flush();
    const sent = page.sent.filter((message) => message.type === 'dm.jobAction').at(-1);
    assert.deepEqual({ action: sent.action, jobId: sent.jobId, deleteFile: sent.deleteFile }, { action: 'remove', jobId: JOB, deleteFile: false });
  } finally { page.restore(); }
});

test('EXT.R11: the background sends each command for one download with exactly its payload, and only from the popup', async () => {
  const { harness } = capturingChrome(catching);
  await withBackground(harness, async () => {
    const cases = [
      [{ command: 'reveal' }, 'job.reveal', { jobId: JOB }, { ok: true, message: 'Showing the file in its folder.' }],
      [{ command: 'redownload' }, 'job.redownload', { jobId: JOB }, { ok: true, job: { id: 'new' } }],
      [{ command: 'rename', newFileName: ' report-2026.pdf ' }, 'job.update', { jobId: JOB, newFileName: 'report-2026.pdf' }, { ok: true, job: { id: JOB, fileName: 'report-2026.pdf' } }],
    ];
    for (const [message, type, payload, answer] of cases) {
      const reply = sendRuntimeMessage(harness, { type: 'dm.jobCommand', jobId: JOB, target: 'local', ...message, deleteFile: true, newFilePath: 'C:\\x' },
        ownPage('popup.html'));
      const request = await answerNative(harness, type, answer);
      assert.deepEqual(request.payload, payload, type);
      assert.equal((await reply).ok, true, type);
    }

    const before = harness.nativeMessages.length;
    for (const message of [
      { command: 'open', jobId: JOB }, { command: 'constructor', jobId: JOB }, { command: 'reveal', jobId: 'not-a-job' },
      { command: 'rename', jobId: JOB }, { command: 'rename', jobId: JOB, newFileName: '   ' },
    ]) {
      assert.equal((await sendRuntimeMessage(harness, { type: 'dm.jobCommand', ...message }, ownPage('popup.html'))).ok, false,
        JSON.stringify(message));
    }
    // Another extension page, or a web page, cannot ask.
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.jobCommand', command: 'reveal', jobId: JOB }, ownPage('options.html'))).ok, false);
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.jobCommand', command: 'reveal', jobId: JOB },
      { id: 'test-extension', url: 'https://evil.test/', tab: { id: 4 } })).ok, false);
    // No server is paired: a server download's command is refused rather than sent to this PC.
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.jobCommand', command: 'reveal', jobId: JOB, target: 'server' },
      ownPage('popup.html'))).ok, false);
    assert.equal(harness.nativeMessages.length, before);

    const refused = sendRuntimeMessage(harness, { type: 'dm.jobCommand', command: 'reveal', jobId: JOB }, ownPage('popup.html'));
    await answerNative(harness, 'job.reveal', { ok: false, message: 'The file and its folder are gone.' });
    assert.deepEqual(await refused, { ok: false, error: 'The file and its folder are gone.' });

    // The 0.6.6 native host refuses the message; an older Host takes the rename's id, drops the name and says ok.
    const tooOld = sendRuntimeMessage(harness, { type: 'dm.jobCommand', command: 'redownload', jobId: JOB }, ownPage('popup.html'));
    await answerNative(harness, 'job.redownload', { ok: false, message: 'The browser extension cannot send this message.' });
    assert.deepEqual(await tooOld, { ok: false, error: 'Update Inbound to use this.' });
    const ignored = sendRuntimeMessage(harness, { type: 'dm.jobCommand', command: 'rename', jobId: JOB, newFileName: 'new.pdf' }, ownPage('popup.html'));
    await answerNative(harness, 'job.update', { ok: true, job: { id: JOB, fileName: 'report.pdf' } });
    const notRenamed = await ignored;
    assert.equal(notRenamed.ok, false);
    assert.match(notRenamed.error, /not renamed\. Update Inbound/);
  });
});
