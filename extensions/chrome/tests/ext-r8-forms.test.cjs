// EXT.R8 and the usability findings on the save forms and Download All (U-EX-7, U-EX-11, U-EX-12, U-EX-14), plus the
// badge: decision 1's two Start buttons, This PC first when the server is down, a stale folder error cleared, a
// non-web tab told so, and the count of downloads waiting in the popup.
const assert = require('node:assert/strict');
const test = require('node:test');
const { installPage, loadTs, tick } = require('./page-harness.cjs');
const { makeChrome, withBackground, sendRuntimeMessage, catching, ownPage, capturingChrome } = require('./harness.cjs');

const TARGETS = [
  { id: 'local', label: 'This PC', chooser: 'dialog', defaultFolder: 'D:\\Downloads', favorites: [] },
  { id: 'server', label: 'Server', chooser: 'list', address: 'http://home-server:8080', defaultFolder: '/downloads', favorites: [], error: 'No answer.' },
];

test('U-EX-11: a form starts on This PC when the default server did not answer; the server stays offered and says why', () => {
  const { firstTarget } = loadTs('ui/save-form.ts');
  assert.equal(firstTarget(TARGETS, 'server').id, 'local');
  assert.equal(firstTarget([TARGETS[0], { ...TARGETS[1], error: undefined }], 'server').id, 'server');
  assert.equal(firstTarget([TARGETS[1]], 'server').id, 'server', 'no This PC to start on: the server, with its error');
});

const handoff = (startMode, targets = TARGETS, target = 'server') => async () => ({
  ok: true, held: false, handoff: { id: 'h1', url: 'https://site.test/a.iso' },
  defaults: { destination: 'D:\\Downloads', favorites: [], startMode }, targets, target,
});

test('decision 1 and U-EX-7: Start download and Download later each send this download only; Options picks the primary', async () => {
  const sent = [];
  const page = installPage('?id=h1', { 'dm.getHandoff': handoff('later'), 'dm.confirmHandoff': async (message) => { sent.push(message); return { ok: false, error: 'no' }; } });
  try {
    loadTs('handoff.ts');
    await page.domReady();
    assert.equal(page.el('later').className, 'btn-primary', 'Default start = Download later');
    assert.equal(page.el('confirm').className, 'btn');
    // U-EX-11 in the window: This PC chosen in place of the server that did not answer.
    const checked = page.el('targetChoice').children.flatMap((label) => label.children).find((node) => node.type === 'radio' && node.checked);
    assert.equal(checked.value, 'local');
    const serverLabel = page.el('targetChoice').children.find((label) => label.children.some((node) => node.value === 'server'));
    assert.match(serverLabel.children.find((node) => node.textContent).textContent, /home-server \(not answering\)/);
    await page.el('confirm').click();
    await page.el('later').click();
    assert.deepEqual(sent.map((message) => message.startMode), ['now', 'later']);
    // Enter presses the primary one.
    await page.key({ key: 'Enter', target: page.el('fileName') });
    await tick();
    assert.equal(sent.at(-1).startMode, 'later');
  } finally { page.restore(); }
});

test('U-EX-12: a folder error goes once a folder is picked in the server\'s list', async () => {
  const page = installPage('?id=h1', {
    'dm.getHandoff': handoff('now', [TARGETS[0], { ...TARGETS[1], error: undefined }]),
    'dm.confirmHandoff': async () => ({ ok: true, ack: { accepted: false, error: 'That folder is outside the folders this server may use.' } }),
    'dm.browseServerFolders': async (message) => ({ ok: true, path: message.path || '/downloads', parent: null, roots: ['/downloads'],
      folders: [{ name: 'movies', path: '/downloads/movies' }] }),
  });
  try {
    loadTs('handoff.ts');
    await page.domReady();
    page.el('destination').value = 'C:\\Outside';
    await page.el('confirm').click();
    assert.equal(page.el('error').classList.contains('hidden'), false);
    await page.el('browseDestination').click();
    await tick();
    assert.equal(page.el('error').classList.contains('hidden'), true, 'the stale error is gone once a list shows');
    assert.equal(page.el('error').textContent, '');
  } finally { page.restore(); }
});

test('U-EX-14: Download all on a browser or extension page says there is nothing to collect, and opens no window', async () => {
  const harness = makeChrome(catching, { id: 'seed' });
  let windows = 0;
  harness.chrome.windows.create = async () => { windows++; };
  harness.chrome.tabs.query = async () => [{ id: 9, url: 'chrome://extensions/' }];
  harness.chrome.tabs.get = async () => ({ id: 9, url: 'chrome://extensions/' });
  await withBackground(harness, async () => {
    const opened = await sendRuntimeMessage(harness, { type: 'dm.openBatch' }, ownPage('popup.html'));
    assert.equal(opened.ok, false);
    assert.match(opened.error, /works on web pages/);
    assert.doesNotMatch(opened.error, /Reload/);
    assert.equal(windows, 0);
    const links = await sendRuntimeMessage(harness, { type: 'dm.pageLinks', tabId: 9 }, ownPage('batch.html'));
    assert.equal(links.notWebPage, true);
    assert.match(links.error, /no links to collect/);
  });
});

test('U-EX-14: the Add links window shows no "Scanning" once the scan has failed', async () => {
  const page = installPage('?tabId=9', {
    'dm.pageLinks': async () => ({ ok: false, notWebPage: true, error: 'Download all links works on web pages. This page has no links to collect.' }),
    'dm.targets': async () => ({ ok: true, targets: [TARGETS[0]], target: 'local', startMode: 'now' }),
  });
  try {
    loadTs('batch.ts');
    await tick(); await tick(); await tick();
    assert.equal(page.el('count').textContent, '');
    assert.match(page.el('links').children[0].textContent, /not a web page/);
  } finally { page.restore(); }
});

test('Download All: the package name typed in the window names the package', async () => {
  const pages = new Map([[4, async () => ({ ok: true, pageUrl: 'https://site.test/', links: [] })]]);
  const harness = makeChrome(catching, { id: 'seed' }, pages);
  harness.chrome.tabs.get = async () => ({ id: 4, title: 'Page title' });
  await withBackground(harness, async () => {
    const reply = sendRuntimeMessage(harness, { type: 'dm.confirmBatch', pageUrl: 'https://site.test/', tabId: 4, packageName: '  My set ',
      urls: [{ url: 'https://site.test/a.zip' }, { url: 'https://site.test/b.zip' }], startMode: 'later' }, ownPage('batch.html'));
    for (let i = 0; i < 50 && !harness.nativeMessages.some((message) => message.type === 'handoff.request'); i++) await new Promise((r) => setTimeout(r, 5));
    const request = harness.nativeMessages.find((message) => message.type === 'handoff.request');
    assert.equal(request.payload.packageName, 'My set');
    assert.equal(request.payload.startMode, 'later');
    for (const message of harness.nativeMessages.filter((m) => m.type === 'handoff.request')) {
      for (const listener of harness.nativeOnMessage.listeners) listener({ type: 'handoff.ack', replyTo: message.id, payload: { accepted: true } });
    }
    await Promise.race([reply, new Promise((r) => setTimeout(r, 200))]);
  });
});

test('the badge: OFF on the status amber, no longer a grey', async () => {
  const { harness, log } = capturingChrome({ ...catching, autoIntercept: false });
  const colours = [];
  harness.chrome.action.setBadgeBackgroundColor = async ({ color }) => { colours.push(color); };
  await withBackground(harness, async () => {
    await sendRuntimeMessage(harness, { type: 'dm.status' }, ownPage('popup.html')).catch(() => undefined);
    assert.equal(log.badge.at(-1), 'OFF');
    assert.equal(colours.at(-1), '#8a5a00');
  });
});
