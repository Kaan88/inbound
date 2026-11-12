// EXT.R8 §8: the popup's rows in a real DOM (jsdom): keyed and updated in place, constant parts, the desktop's words,
// and the popup's notices and status bar for the states the usability test found wrong (U-EX-3, U-EX-4, U-EX-10).
const assert = require('node:assert/strict');
const test = require('node:test');
const { installDom, flush } = require('./dom-harness.cjs');
const data = require('./fixtures/screens-data.json');

const view = { paired: true, serverName: 'home-server', groupsOk: { local: true, server: true } };
const handlers = { onAction() {}, onOpen() {} };
const clone = (value) => JSON.parse(JSON.stringify(value));

test('rows are keyed by place and job: a focused button keeps its node and focus across two polls', () => {
  const page = installDom('popup.html');
  try {
    const { renderPopupRows } = page.load('popup-list.ts');
    const list = page.document.getElementById('jobs');
    const jobs = clone(data.jobs);
    assert.equal(renderPopupRows(list, jobs, view, handlers), 4);
    const row = [...list.children].find((item) => item.textContent.includes('linux-desktop'));
    const button = row.querySelector('[data-action="pause"]');
    button.focus();
    jobs[0].receivedBytes += 1024 ** 3;
    jobs[0].progress = 0.8;
    renderPopupRows(list, jobs, view, handlers);
    const again = [...list.children].find((item) => item.textContent.includes('linux-desktop'));
    assert.equal(again, row, 'the same row node');
    assert.equal(page.document.activeElement, button, 'focus stays on the same button');
    assert.equal(row.querySelector('.job-bar > span').style.width, '80%');
  } finally { page.restore(); }
});

test('every row has the same parts whatever its state (constant height), and an unknown size is blank', () => {
  const page = installDom('popup.html');
  try {
    const { renderPopupRows } = page.load('popup-list.ts');
    const list = page.document.getElementById('jobs');
    const jobs = [...clone(data.jobs), { id: 'x', fileName: 'stream.bin', state: 'downloading', receivedBytes: 10, bytesPerSecond: 100 }];
    renderPopupRows(list, jobs, { paired: false, serverName: '', groupsOk: {} }, handlers);
    const rows = [...list.querySelectorAll('.job')];
    assert.equal(rows.length, 5);
    for (const row of rows) {
      assert.deepEqual([...row.children].map((child) => child.className.split(' ')[0]), ['file-glyph', 'job-main', 'job-actions']);
      assert.ok(row.querySelector('.job-bar'), 'the bar slot is always there');
      assert.ok(row.querySelectorAll('.job-actions button').length <= 2);
    }
    const unknown = rows.find((row) => row.textContent.includes('stream.bin'));
    assert.equal(unknown.querySelector('.job-size').textContent, '');
    assert.ok(unknown.querySelector('.job-bar').classList.contains('indeterminate'));
    const failed = rows.find((row) => row.textContent.includes('installer'));
    assert.ok(failed.querySelector('.job-bar').classList.contains('slot-empty'));
  } finally { page.restore(); }
});

test('the desktop list\'s words: Downloading 62%, 18.4 MB/s, 1m 45s; Retrying; buttons from actionsFor', () => {
  const page = installDom('popup.html');
  try {
    const { renderPopupRows } = page.load('popup-list.ts');
    const { actionsFor } = page.load('job-list.ts');
    const list = page.document.getElementById('jobs');
    const jobs = [...clone(data.jobs), { id: 'r', target: 'local', fileName: 'retry.zip', state: 'retryWaiting', receivedBytes: 0, canPause: true },
      { id: 'l', target: 'local', fileName: 'later.zip', state: 'queued', startMode: 'later', receivedBytes: 0, canPause: true }];
    renderPopupRows(list, jobs, view, handlers);
    const row = (name) => [...list.querySelectorAll('.job')].find((item) => item.textContent.includes(name));
    assert.equal(row('linux-desktop').querySelector('.job-status').textContent, 'Downloading 62%');
    assert.equal(row('linux-desktop').querySelector('.job-detail').textContent, '18.4 MB/s · 1m 45s');
    assert.equal(row('linux-desktop').querySelector('.job-size').textContent, '5.00 GB');
    assert.equal(row('retry.zip').querySelector('.job-status').textContent, 'Retrying');
    assert.equal(row('installer').querySelector('.job-detail').textContent.trim(), '· HTTP 403 Forbidden');
    for (const job of jobs) {
      const shown = [...row(job.fileName).querySelectorAll('.job-actions button')].map((button) => button.dataset.action);
      assert.deepEqual(shown, actionsFor(job), job.fileName);
    }
    // U-EX-8: the "Download later" item can be started.
    assert.equal(row('later.zip').querySelector('[data-action="start"]').getAttribute('aria-label'), 'Start later.zip');
    // Groups, This PC first.
    assert.deepEqual([...list.querySelectorAll('.group-row')].map((item) => item.textContent), ['This PC', 'home-server (server)']);
  } finally { page.restore(); }
});

const statusWith = (overrides) => ({
  ok: true, enabled: true, autoIntercept: true, automaticSending: true, connected: true, lastError: '', localMissing: false,
  server: { ...data.server }, automaticTarget: 'local', pauseUntil: 0, settings: { ...data.settings }, leftInBrowser: [], jobs: clone(data.jobs),
  ...overrides,
});

async function openPopup(status) {
  const page = installDom('popup.html', { handlers: {
    'dm.status': async () => status,
    'dm.jobs': async () => ({ ok: true, jobs: status.jobs.filter((job) => !(status.server && !status.server.connected && job.target === 'server')),
      groups: status.server ? [{ target: 'local', ok: status.connected }, { target: 'server', ok: status.server.connected }] : undefined }),
    'dm.askQueue': async () => ({ ok: true, items: [] }),
  } });
  page.load('popup.ts');
  await page.ready();
  await flush(12);
  return page;
}

test('U-EX-4: downloads going to a server that is down: a notice, "Not answering" in its group, and the status bar says so', async () => {
  const page = await openPopup(statusWith({ server: { ...data.server, connected: false, lastError: 'No answer.' }, automaticTarget: 'server' }));
  try {
    const doc = page.document;
    const notices = [...doc.querySelectorAll('#notices .notice-text')].map((item) => item.textContent);
    assert.deepEqual(notices, ['home-server isn\'t answering. Downloads you start stay in the browser.']);
    assert.equal(doc.getElementById('connection').textContent, 'Server not answering');
    assert.ok(doc.getElementById('connection').classList.contains('tone-failed'));
    assert.ok([...doc.querySelectorAll('.group-note')].some((item) => item.textContent === 'Not answering'));
    assert.match(doc.getElementById('targetSwitch').textContent, /^Send to: home-server/);
  } finally { page.restore(); }
});

test('U-EX-3, U-EX-10: with a server paired every popup action stays in the header, outside the list; each state is said once', async () => {
  const page = await openPopup(statusWith({ automaticSending: false, autoIntercept: false, settings: { ...data.settings, autoIntercept: false } }));
  try {
    const doc = page.document;
    for (const id of ['downloadAll', 'pauseCatching', 'openApp', 'options']) {
      assert.equal(doc.getElementById(id).closest('header').className, 'pop-header', id);
      assert.equal(doc.getElementById('listArea').contains(doc.getElementById(id)), false, id);
    }
    const text = doc.body.textContent;
    assert.equal(text.match(/not sent automatically/g)?.length, 1);
    assert.equal(/Connected to the desktop app|go to the server/.test(text), false);
    assert.equal(doc.getElementById('connection').textContent, 'Connected');
  } finally { page.restore(); }
});

test('Delete asks in a dialog over the list; a finished download\'s file only when ticked, an unfinished one\'s partial data always', async () => {
  const status = statusWith({});
  const page = await openPopup(status);
  try {
    const doc = page.document;
    const remove = (name) => [...doc.querySelectorAll('.job')].find((row) => row.textContent.includes(name)).querySelector('[data-action="remove"]');
    remove('project-assets.part1').click();
    await flush();
    const dialog = doc.querySelector('.dialog');
    assert.equal(dialog.querySelector('.dialog-header').textContent, 'Delete download?');
    assert.equal(doc.activeElement.textContent, 'Cancel', 'a destructive question starts on Cancel');
    dialog.querySelector('input[type=checkbox]').checked = true;
    dialog.querySelector('.btn-danger').click();
    await flush();
    let sent = page.sent.filter((message) => message.type === 'dm.jobAction').at(-1);
    assert.deepEqual({ action: sent.action, deleteFile: sent.deleteFile, target: sent.target }, { action: 'remove', deleteFile: true, target: 'server' });

    remove('project-assets.part2').click();
    await flush();
    assert.equal(doc.querySelector('.dialog input[type=checkbox]'), null);
    assert.equal(doc.querySelector('.dialog .hint').textContent, 'Partial data is always removed.');
    doc.querySelector('.dialog .btn-danger').click();
    await flush();
    sent = page.sent.filter((message) => message.type === 'dm.jobAction').at(-1);
    assert.equal(sent.deleteFile, true);
  } finally { page.restore(); }
});
