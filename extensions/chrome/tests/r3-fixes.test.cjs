// The third browser check and design review of EXT.R8, in a real DOM (jsdom) and the background harness: the folder
// window's first focus, the primary first and "Sending…" on it, focus after a menu choice and after Delete, the
// popup's room for a dialog, Download All's filter and selection, long folders, Options > Server after pairing, the
// server notice with "ask where to save" on, and right-click window entries dropped once their window is closed.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { installDom, flush } = require('./dom-harness.cjs');
const { loadTs } = require('./page-harness.cjs');
const { capturingChrome, catching, ownPage, sendRuntimeMessage, withBackground } = require('./harness.cjs');
const data = require('./fixtures/screens-data.json');

const clone = (value) => JSON.parse(JSON.stringify(value));
const TARGETS = [{ id: 'local', label: 'This PC', chooser: 'dialog', defaultFolder: 'D:\\Downloads', favorites: [] }];

function handoffAnswer(startMode, extra = {}) {
  return async () => ({ ok: true, held: false, handoff: { id: 'h1', url: 'https://site.test/a.iso', suggestedFileName: 'a.iso' },
    defaults: { destination: 'D:\\Downloads', favorites: [], startMode }, targets: TARGETS, target: 'local', ...extra });
}

const footerOrder = (doc, selector) => [...doc.querySelector(selector).children].map((button) => button.id);

test('the folder window: the primary has the focus once loaded, so Enter presses it; with Download later as default it comes first', async () => {
  for (const [mode, primary, order] of [['now', 'confirm', ['confirm', 'later']], ['later', 'later', ['later', 'confirm']]]) {
    const page = installDom('handoff.html', { hash: '?id=h1', handlers: { 'dm.getHandoff': handoffAnswer(mode) } });
    try {
      page.load('handoff.ts');
      await page.ready();
      await flush(10);
      const doc = page.document;
      assert.equal(doc.activeElement.id, primary, `${mode}: the primary is focused`);
      assert.equal(doc.activeElement.className, 'btn-primary');
      assert.deepEqual(footerOrder(doc, '.window-footer').slice(0, 2), order, `${mode}: the primary first`);
    } finally { page.restore(); }
  }
});

test('the folder window with nothing to confirm focuses Close', async () => {
  const page = installDom('handoff.html', { hash: '?id=gone', handlers: { 'dm.getHandoff': async () => ({ ok: false, error: 'Expired.' }) } });
  try {
    page.load('handoff.ts');
    await page.ready();
    await flush(10);
    assert.equal(page.document.activeElement.id, 'cancel');
    assert.equal(page.document.activeElement.textContent, 'Close');
  } finally { page.restore(); }
});

function askPopup(startMode, confirm) {
  const status = { ok: true, enabled: true, autoIntercept: true, automaticSending: true, connected: true, lastError: '', server: null,
    automaticTarget: 'local', pauseUntil: 0, settings: { ...data.settings, destinationMode: 'ask' }, leftInBrowser: [], jobs: [] };
  let items = [{ id: 'h1', fileName: 'a.iso', url: 'https://site.test/a.iso', totalBytes: 1024 }];
  const page = installDom('popup.html', { handlers: {
    'dm.status': async () => status,
    'dm.jobs': async () => ({ ok: true, jobs: [] }),
    'dm.askQueue': async () => ({ ok: true, items }),
    'dm.getHandoff': handoffAnswer(startMode),
    'dm.confirmHandoff': async (message) => { const answer = await confirm(message); items = []; return answer; },
  } });
  page.load('popup.ts');
  return page;
}

test('the ask view: the primary has the focus at once, comes first, and reads "Sending…" while it sends', async () => {
  for (const [mode, primary, other, otherText] of [['now', 'askConfirm', 'askLater', 'Download later'], ['later', 'askLater', 'askConfirm', 'Start download']]) {
    let release;
    const page = askPopup(mode, () => new Promise((resolve) => { release = () => resolve({ ok: true, ack: { accepted: true } }); }));
    try {
      await page.ready();
      await flush(14);
      const doc = page.document;
      assert.equal(doc.activeElement.id, primary, `${mode}: Enter presses the primary`);
      assert.deepEqual(footerOrder(doc, '#askCard > .dialog-footer').slice(0, 2), [primary, other]);
      doc.activeElement.click();
      await flush();
      assert.equal(doc.getElementById(primary).textContent, 'Sending…', `${mode}: the primary says it is sending`);
      assert.equal(doc.getElementById(other).textContent, otherText);
      release();
      await flush(10);
      const sent = page.sent.find((message) => message.type === 'dm.confirmHandoff');
      assert.equal(sent.startMode, mode);
    } finally { page.restore(); }
  }
});

const statusWith = (overrides) => ({
  ok: true, enabled: true, autoIntercept: true, automaticSending: true, connected: true, lastError: '', localMissing: false,
  server: { ...data.server }, automaticTarget: 'local', pauseUntil: 0, settings: { ...data.settings }, leftInBrowser: [], jobs: clone(data.jobs),
  ...overrides,
});

async function openPopup(status, extra = {}) {
  let jobs = status.jobs;
  const page = installDom('popup.html', { handlers: {
    'dm.status': async () => status,
    'dm.jobs': async () => ({ ok: true, jobs }),
    'dm.askQueue': async () => ({ ok: true, items: [] }),
    'dm.jobAction': async (message) => { if (message.action === 'remove') jobs = jobs.filter((job) => job.id !== message.jobId); return { ok: true }; },
    ...extra,
  } });
  page.load('popup.ts');
  await page.ready();
  await flush(12);
  return page;
}

test('the server notice with "ask where to save" on says the popup asks, with This PC chosen', async () => {
  const down = { server: { ...data.server, connected: false, lastError: 'No answer.' }, automaticTarget: 'server' };
  const page = await openPopup(statusWith({ ...down, settings: { ...data.settings, destinationMode: 'ask' } }));
  try {
    const notices = [...page.document.querySelectorAll('#notices .notice-text')].map((item) => item.textContent);
    assert.deepEqual(notices, ['home-server isn\'t answering. When you start a download you\'ll be asked, with This PC chosen.']);
  } finally { page.restore(); }
});

test('Delete: the popup grows while the question is open, and focus goes to the row now in the deleted one\'s place', async () => {
  const page = await openPopup(statusWith({}));
  try {
    const doc = page.document;
    const rows = () => [...doc.querySelectorAll('#jobs .job')];
    const before = rows().map((row) => row.querySelector('.job-name').textContent);
    const at = before.findIndex((name) => name.includes('project-assets.part2'));
    const button = rows()[at].querySelector('[data-action="remove"]');
    button.focus();
    button.click();
    await flush();
    assert.ok(doc.body.classList.contains('dialog-open'), 'room for the question');
    doc.querySelector('.dialog .btn-danger').click();
    await flush(12);
    assert.equal(doc.body.classList.contains('dialog-open'), false);
    assert.equal(rows().length, before.length - 1);
    const focused = doc.activeElement;
    assert.ok(focused.classList.contains('job'), `a row has the focus, not ${focused.tagName}`);
    assert.equal(rows().indexOf(focused), Math.min(at, rows().length - 1));
    const css = fs.readFileSync(path.join(__dirname, '..', 'extension.css'), 'utf8');
    assert.match(css, /body\.popup\.dialog-open \{ min-height: \d+px; \}/);
  } finally { page.restore(); }
});

test('a menu choice gives the focus back to the button the menu came from', async () => {
  const page = await openPopup(statusWith({}), { 'dm.setDefaultTarget': async () => ({ ok: true }) });
  try {
    const doc = page.document;
    const anchor = doc.getElementById('targetSwitch');
    anchor.focus();
    anchor.click();
    await flush();
    const items = [...doc.querySelectorAll('.popup-menu .menu-item')];
    assert.equal(items.length, 2);
    assert.ok(items.includes(doc.activeElement), 'the menu has the focus while open');
    items[1].click();
    await flush();
    assert.equal(doc.querySelector('.popup-menu'), null);
    assert.equal(doc.activeElement, anchor);
  } finally { page.restore(); }
});

test('Download All: Start adds only the ticked links the filter shows, the footer says how many are left out; types in upper case', async () => {
  const links = [
    { url: 'https://site.test/a.zip', text: 'a.zip', extension: 'zip', kind: 'file', host: 'site.test' },
    { url: 'https://site.test/b.iso', text: 'b.iso', extension: 'iso', kind: 'file', host: 'site.test' },
    { url: 'https://site.test/page', text: 'A page', extension: '', kind: 'link', host: 'site.test' },
  ];
  const page = installDom('batch.html', { hash: '?tabId=4', handlers: {
    'dm.pageLinks': async () => ({ ok: true, pageUrl: 'https://site.test/', links }),
    'dm.targets': async () => ({ ok: true, targets: TARGETS, target: 'local', startMode: 'now' }),
    'dm.confirmBatch': async () => ({ ok: true, accepted: 0, failed: 1, results: [{ accepted: false, error: 'Refused here.' }] }),
  } });
  try {
    page.load('batch.ts');
    await page.ready();
    await flush(12);
    const doc = page.document;
    assert.deepEqual([...doc.querySelectorAll('.link-type')].map((cell) => cell.textContent), ['ZIP', 'ISO', 'LINK']);
    assert.equal(doc.getElementById('selection').textContent, '2 selected');
    const filter = doc.getElementById('filter');
    filter.value = 'iso';
    filter.dispatchEvent(new page.window.Event('input'));
    assert.equal(doc.getElementById('selection').textContent, '1 selected (1 more hidden by the filter, not added)');
    doc.getElementById('confirm').click();
    await flush(8);
    const sent = page.sent.find((message) => message.type === 'dm.confirmBatch');
    assert.deepEqual(sent.urls, [{ url: 'https://site.test/b.iso' }]);
    assert.equal(sent.packageName, undefined, 'one link is not a package');
  } finally { page.restore(); }
});

test('a long folder shows its end, the folder\'s name, with the middle left out', () => {
  const { middleEllipsis } = loadTs('ui/path-field.ts');
  const fits = (limit) => (text) => text.length <= limit;
  const folder = 'D:\\Downloads\\Projects\\2026\\Client work\\Final renders';
  assert.equal(middleEllipsis(folder, fits(100)), folder);
  const short = middleEllipsis(folder, fits(30));
  assert.ok(short.length <= 30, short);
  assert.ok(short.endsWith('\\Final renders'), short);
  assert.ok(short.startsWith('D:\\'), short);
  assert.match(short, /…/);
  assert.equal(middleEllipsis('/srv/media/films', fits(12)), '/srv/…/films');
  // Not even the name fits: as much of its end as does.
  assert.equal(middleEllipsis('/srv/averyveryverylongname', fits(8)), '…ongname');
});

test('the folder field keeps its real value and tooltip; the shortened copy is drawn over it', async () => {
  const page = installDom('handoff.html', { hash: '?id=h1', handlers: { 'dm.getHandoff': handoffAnswer('now') } });
  try {
    page.load('handoff.ts');
    await page.ready();
    await flush(10);
    const input = page.document.getElementById('destination');
    assert.ok(input.parentElement.classList.contains('path-field'));
    assert.ok(input.parentElement.querySelector('.path-display'));
    input.value = 'E:\\Somewhere\\Else';
    assert.equal(input.value, 'E:\\Somewhere\\Else');
    assert.equal(input.title, 'E:\\Somewhere\\Else', 'the whole path as the tooltip, also when set from code');
  } finally { page.restore(); }
});

test('Options > Server: the "once one is paired" sentence goes once a server is paired', async () => {
  for (const [server, hidden] of [[null, false], [{ ...data.server, connected: true }, true]]) {
    const page = installDom('options.html', { hash: '#server', handlers: {
      'dm.status': async () => ({ ok: true, settings: { ...data.settings }, connected: true, server, extensionVersion: '0.6.0' }),
    } });
    try {
      page.load('options.ts');
      await page.ready();
      await flush(10);
      assert.equal(page.document.getElementById('targetPairHint').classList.contains('hidden'), hidden, server ? 'paired' : 'not paired');
    } finally { page.restore(); }
  }
});

test('right-click window entries are dropped once no window shows them; held and fresh ones stay', async () => {
  const { harness } = capturingChrome(catching);
  const old = Date.now() - 5 * 60 * 1000;
  harness.session['handoff.closed'] = { id: 'closed', url: 'https://a.test/x.zip', isAutoIntercept: false, createdAt: old };
  harness.session['handoff.open'] = { id: 'open', url: 'https://a.test/y.zip', isAutoIntercept: false, createdAt: old };
  harness.session['handoff.fresh'] = { id: 'fresh', url: 'https://a.test/z.zip', isAutoIntercept: false, createdAt: Date.now() };
  harness.session['handoff.held'] = { id: 'held', url: 'https://a.test/w.zip', isAutoIntercept: true, browserDownloadId: 3, createdAt: old };
  harness.chrome.runtime.getContexts = async () => [{ documentUrl: harness.chrome.runtime.getURL('handoff.html') + '?id=open' }];
  const get = harness.chrome.storage.session.get;
  harness.chrome.storage.session.get = async (keys) => keys === null ? { ...harness.session } : get(keys);
  await withBackground(harness, async () => {
    await sendRuntimeMessage(harness, { type: 'dm.status' }, ownPage('popup.html')).catch(() => undefined);
    for (let i = 0; i < 40 && harness.session['handoff.closed']; i++) await new Promise((resolve) => setTimeout(resolve, 5));
    assert.equal(harness.session['handoff.closed'], undefined, 'a closed window\'s entry is gone');
    assert.ok(harness.session['handoff.open'], 'an open window keeps its entry');
    assert.ok(harness.session['handoff.fresh'], 'a window still loading keeps its entry');
    assert.ok(harness.session['handoff.held'], 'a held download is given back by its port, not here');
  });
});
