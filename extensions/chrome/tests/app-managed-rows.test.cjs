// Wave B (owner, 2026-11-11, "safe parity only"): rows the app manages on its own stay generic in the popup. A paused
// download the app can't resume offers Open in app (this PC) or nothing (a paired server), never a Resume that does
// nothing; a finished one offers no Stop; a link that is no web address is neither copied nor redownloaded.
const assert = require('node:assert/strict');
const test = require('node:test');
const { installDom } = require('./dom-harness.cjs');

const local = { paired: false, serverName: '', groupsOk: {} };
const paired = { paired: true, serverName: 'home-server', groupsOk: { local: true, server: true } };

// What the native host sends for such a download: canResume false on a paused one, appManaged, a non-web address.
const waiting = (extra = {}) => ({
  id: '11111111-1111-4111-8111-111111111111', fileName: 'Album', state: 'paused', receivedBytes: 0, totalBytes: 3000,
  canPause: false, canResume: false, isTerminal: false, appManaged: true, url: 'urn:example:abc', ...extra,
});

test('a paused download the app cannot resume offers Open in app on this PC, and nothing but Delete on a server', () => {
  const page = installDom('popup.html');
  try {
    const { actionsFor, waitsInApp } = page.load('job-list.ts');
    assert.equal(waitsInApp(waiting()), true);
    assert.deepEqual(actionsFor(waiting()), ['open', 'remove']);
    assert.deepEqual(actionsFor(waiting({ target: 'local' })), ['open', 'remove']);
    assert.deepEqual(actionsFor(waiting({ target: 'server' })), ['remove']);
    // An app older than this says canResume true for every paused download: the row keeps Resume, as before.
    assert.deepEqual(actionsFor(waiting({ canResume: true })), ['resume', 'remove']);
    // canResume missing is not a "no".
    assert.deepEqual(actionsFor(waiting({ canResume: undefined })), ['remove']);
    assert.equal(waitsInApp(waiting({ canResume: undefined })), false);
  } finally { page.restore(); }
});

test('Open in app is a button with neutral words that opens the app, and the row says it waits there', () => {
  const page = installDom('popup.html');
  try {
    const { renderPopupRows, OPEN_IN_APP_LABEL, WAITS_IN_APP_NOTE } = page.load('popup-list.ts');
    const list = page.document.getElementById('jobs');
    const opened = [];
    const acted = [];
    renderPopupRows(list, [waiting()], local, {
      onAction: (job, action) => acted.push(action), onOpen: (job) => opened.push(job.id),
    });
    const row = list.querySelector('.job');
    assert.deepEqual([...row.querySelectorAll('.job-actions button')].map((button) => button.dataset.action), ['open', 'remove']);
    const open = row.querySelector('[data-action="open"]');
    assert.equal(open.title, OPEN_IN_APP_LABEL);
    assert.equal(open.title, 'Open in app');
    assert.equal(row.querySelector('[data-action="resume"]'), null);
    open.click();
    assert.deepEqual(opened, ['11111111-1111-4111-8111-111111111111']);
    assert.deepEqual(acted, [], 'no command goes to the download');
    assert.match(row.querySelector('.job-detail').textContent, new RegExp(WAITS_IN_APP_NOTE));
  } finally { page.restore(); }
});

test('on a paired server such a row has Delete only, with no Open and no Resume', () => {
  const page = installDom('popup.html');
  try {
    const { renderPopupRows, WAITS_IN_APP_NOTE, WAITS_ON_SERVER_NOTE } = page.load('popup-list.ts');
    const list = page.document.getElementById('jobs');
    renderPopupRows(list, [waiting({ target: 'server' })], paired, { onAction() {}, onOpen() {} });
    const row = list.querySelector('.job');
    assert.deepEqual([...row.querySelectorAll('.job-actions button')].map((button) => button.dataset.action), ['remove']);
    // The choice for a server's download is made on the server, not in this PC's app.
    const detail = row.querySelector('.job-detail').textContent;
    assert.equal(WAITS_ON_SERVER_NOTE, 'Waiting for you on the server');
    assert.match(detail, new RegExp(WAITS_ON_SERVER_NOTE));
    assert.doesNotMatch(detail, new RegExp(WAITS_IN_APP_NOTE));
  } finally { page.restore(); }
});

test('a finished download the app still works with is a plain finished row: no Stop, Resume or Open', () => {
  const page = installDom('popup.html');
  try {
    const { actionsFor, rowMenuEntries } = page.load('job-list.ts');
    // The app may still report it can pause a finished download while it keeps using it; the popup shows none.
    const done = waiting({ state: 'completed', canPause: true, canResume: false, isTerminal: true, receivedBytes: 3000 });
    assert.deepEqual(actionsFor(done), ['remove']);
    assert.deepEqual(actionsFor({ ...done, target: 'server' }), ['remove']);
    const menu = Object.fromEntries(rowMenuEntries(done).map((entry) => [entry.id, entry]));
    assert.equal(menu.copyLink.enabled, false, 'no web link to copy');
    assert.equal(menu.redownload.enabled, false);
    assert.equal(menu.rename.enabled, false);
    assert.equal(menu.rename.title, 'This download is managed in the app.');
    assert.equal(menu.reveal.enabled, true);
    assert.equal(menu.remove.enabled, true);
  } finally { page.restore(); }
});

test('only a web address counts as a link; an ordinary finished download keeps every entry', () => {
  const page = installDom('popup.html');
  try {
    const { hasUsableLink, rowMenuEntries } = page.load('job-list.ts');
    for (const url of ['https://files.test/a.zip', 'http://files.test/a.zip']) {
      assert.equal(hasUsableLink({ url }), true, url);
    }
    for (const url of ['', 'ftp://files.test/a.zip', 'FTPS://files.test/a.zip', 'remote://provider/1', 'urn:example:abc', 'javascript:alert(1)', 'file:///C:/a.zip', 'data:,x']) {
      assert.equal(hasUsableLink({ url }), false, url);
    }
    const done = { id: 'd', fileName: 'a.zip', state: 'completed', receivedBytes: 1, url: 'https://files.test/a.zip' };
    assert.ok(rowMenuEntries(done).every((entry) => entry.enabled));
  } finally { page.restore(); }
});
