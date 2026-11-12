// EXT.R9 (GitHub #2): the paired server's name. The server gives one when the browser pairs; the user can set their
// own in Settings, which pairing again never replaces; without either the address's host is shown. Send to shows
// "Home NAS (192.168.1.150)".
const assert = require('node:assert/strict');
const test = require('node:test');
const { JSDOM } = require('jsdom');
const { loadTs } = require('./page-harness.cjs');
const { installDom, flush } = require('./dom-harness.cjs');
const { answerNative, capturingChrome, catching, ownPage, sendRuntimeMessage, withBackground } = require('./harness.cjs');
const data = require('./fixtures/screens-data.json');

const TOKEN = 'ibt_' + 'c'.repeat(43);
const ADDRESS = 'http://192.168.1.150:8080';

test('the shown name: the user\'s own, else the server\'s, else the address\'s host; blanks and control characters do not count', () => {
  const { serverDisplayName, cleanServerName } = loadTs('server-name.ts');
  assert.equal(serverDisplayName(ADDRESS, 'nas-box', 'Home NAS'), 'Home NAS');
  assert.equal(serverDisplayName(ADDRESS, 'nas-box', ''), 'nas-box');
  assert.equal(serverDisplayName(ADDRESS, 'nas-box', '   '), 'nas-box');
  assert.equal(serverDisplayName(ADDRESS, undefined, undefined), '192.168.1.150');
  assert.equal(serverDisplayName(ADDRESS, '\u0007', '\n'), '192.168.1.150');
  assert.equal(cleanServerName('  Home\tNAS \n'), 'HomeNAS');
  assert.equal(cleanServerName('x'.repeat(300)).length, 100);
  // The cut counts code points: an emoji across the 100th place is kept whole, never halved into a lone surrogate.
  const astral = cleanServerName('n'.repeat(99) + '\u{1F600}' + 'tail');
  assert.equal(astral, 'n'.repeat(99) + '\u{1F600}');
  assert.equal(Array.from(astral).length, 100);
  assert.equal(astral.isWellFormed(), true);
  assert.equal(cleanServerName(42), '');
});

test('pairing keeps the server\'s name from its answer; an old stored pairing without one still reads', async () => {
  const { pairWithServer, readPairing } = loadTs('transport.ts');
  const calls = [];
  const fetch = async (url, init) => {
    const body = JSON.parse(init.body);
    calls.push(url);
    const answer = url.endsWith('/api/pair')
      ? { ok: true, token: TOKEN, id: 'p1', name: 'Chrome on Windows', serverName: ' Home NAS ' }
      : { v: 1, type: 'host.actionResult', replyTo: body.id, payload: { ok: true } };
    return { status: 200, async text() { return JSON.stringify(answer); } };
  };
  const pairing = await pairWithServer(ADDRESS, 'ABCDEF23', 'Chrome on Windows', fetch);
  assert.equal(pairing.serverName, 'Home NAS');
  assert.equal(pairing.name, 'Chrome on Windows', 'name stays this browser\'s name on the server');
  assert.equal(readPairing(JSON.parse(JSON.stringify(pairing))).serverName, 'Home NAS');

  const old = readPairing({ address: ADDRESS, token: TOKEN, id: 'p0', name: 'Chrome on Windows', pairedAt: 1 });
  assert.equal(old.address, ADDRESS);
  assert.equal(old.serverName, undefined);
});

/** A server whose pairing answer gives `serverName` (none when undefined) and answers every envelope. */
function fakeServer(serverName) {
  return async (url, init) => {
    const body = init.body ? JSON.parse(init.body) : undefined;
    if (url === `${ADDRESS}/api/pair`) {
      const answer = { ok: true, token: TOKEN, id: 'p1', name: body.name, ...(serverName === undefined ? {} : { serverName }) };
      return { status: 200, async text() { return JSON.stringify(answer); } };
    }
    if (url === `${ADDRESS}/api/pair/self`) return { status: 204, async text() { return ''; } };
    const type = { 'settings.downloadFolders': 'settings.downloadFoldersResult', 'host.ping': 'host.actionResult' }[body.type] ?? 'job.actionResult';
    const payload = body.type === 'settings.downloadFolders' ? { defaultFolder: '/downloads', favorites: [] } : { ok: true };
    return { status: 200, async text() { return JSON.stringify({ v: 1, type, replyTo: body.id, payload }); } };
  };
}

test('a name set in Settings before pairing is used, and pairing again (with another server name) never replaces it', async () => {
  const { harness } = capturingChrome(catching);
  harness.chrome.permissions = { async contains() { return true; } };
  const oldFetch = global.fetch;
  try {
    await withBackground(harness, async () => {
      const options = ownPage('options.html');
      // Set before any pairing.
      await sendRuntimeMessage(harness, { type: 'dm.saveSettings', changes: { serverCustomName: '  Home NAS ' } }, options);
      assert.equal(harness.values.settings.serverCustomName, 'Home NAS');

      global.fetch = fakeServer('nas-box');
      const first = await sendRuntimeMessage(harness, { type: 'dm.pairServer', address: ADDRESS, code: 'ABCDEF23' }, options);
      assert.equal(first.ok, true);
      assert.equal(first.server.serverName, 'nas-box');
      assert.equal(first.server.displayName, 'Home NAS');
      assert.equal(harness.values.server.serverName, 'nas-box');

      // Unpair and pair again: the server's name is the new one, the user's stays.
      await sendRuntimeMessage(harness, { type: 'dm.unpairServer' }, options);
      global.fetch = fakeServer('renamed-box');
      const again = await sendRuntimeMessage(harness, { type: 'dm.pairServer', address: ADDRESS, code: 'ABCDEF23' }, options);
      assert.equal(again.server.serverName, 'renamed-box');
      assert.equal(again.server.displayName, 'Home NAS');
      const asked = sendRuntimeMessage(harness, { type: 'dm.targets' }, ownPage('popup.html'));
      await answerNative(harness, 'settings.downloadFolders', { defaultFolder: 'D:/downloads', favorites: [] });
      const targets = await asked;
      assert.equal(targets.targets.find((target) => target.id === 'server').name, 'Home NAS');

      // Cleared: the server's name.
      await sendRuntimeMessage(harness, { type: 'dm.saveSettings', changes: { serverCustomName: '' } }, options);
      let status = await sendRuntimeMessage(harness, { type: 'dm.status' }, ownPage('popup.html'));
      assert.equal(status.server.displayName, 'renamed-box');

      // A server that gives none (a container id): the address's host.
      await sendRuntimeMessage(harness, { type: 'dm.unpairServer' }, options);
      global.fetch = fakeServer(undefined);
      await sendRuntimeMessage(harness, { type: 'dm.pairServer', address: ADDRESS, code: 'ABCDEF23' }, options);
      status = await sendRuntimeMessage(harness, { type: 'dm.status' }, ownPage('popup.html'));
      assert.equal(status.server.serverName, undefined);
      assert.equal(status.server.displayName, '192.168.1.150');
    });
  } finally {
    global.fetch = oldFetch;
  }
});

function renderChoice(server) {
  const dom = new JSDOM('<div id="field"></div><div id="list"></div>');
  const saved = Object.getOwnPropertyDescriptor(global, 'document');
  Object.defineProperty(global, 'document', { value: dom.window.document, configurable: true, writable: true });
  try {
    const { renderTargetChoice } = loadTs('destination-target.ts');
    const list = dom.window.document.getElementById('list');
    renderTargetChoice(dom.window.document.getElementById('field'), list, [
      { id: 'local', label: 'This PC', chooser: 'dialog', defaultFolder: 'D:\\Downloads', favorites: [] },
      { id: 'server', label: 'Server', chooser: 'list', address: ADDRESS, defaultFolder: '/downloads', favorites: [], ...server },
    ], 'server', () => undefined);
    return list.querySelectorAll('label')[1];
  } finally {
    if (saved) Object.defineProperty(global, 'document', saved);
    else delete global.document;
  }
}

test('Send to: the name first, then the address\'s host smaller; the host once when it is the name', () => {
  const named = renderChoice({ name: 'Home NAS' });
  assert.equal(named.querySelector('span').textContent, 'Home NAS (192.168.1.150)');
  assert.equal(named.querySelector('.target-address').textContent, ' (192.168.1.150)');
  assert.equal(named.title, '192.168.1.150:8080');

  const plain = renderChoice({});
  assert.equal(plain.querySelector('span').textContent, '192.168.1.150');
  assert.equal(plain.querySelector('.target-address'), null);
  assert.equal(renderChoice({ name: '192.168.1.150' }).querySelector('.target-address'), null);

  const down = renderChoice({ name: 'Home NAS', error: 'No answer.' });
  assert.equal(down.querySelector('span').textContent, 'Home NAS (192.168.1.150) (not answering)');
});

test('the popup calls the server by its shown name, in the group row and the Send to switch', async () => {
  const clone = (value) => JSON.parse(JSON.stringify(value));
  const status = {
    ok: true, enabled: true, autoIntercept: true, automaticSending: true, connected: true, lastError: '', localMissing: false,
    server: { ...data.server, serverName: 'nas-box', displayName: 'Home NAS' }, automaticTarget: 'server', pauseUntil: 0,
    settings: { ...data.settings, serverCustomName: 'Home NAS' }, leftInBrowser: [], jobs: clone(data.jobs),
  };
  const page = installDom('popup.html', { handlers: {
    'dm.status': async () => status,
    'dm.jobs': async () => ({ ok: true, jobs: status.jobs, groups: [{ target: 'local', ok: true }, { target: 'server', ok: true }] }),
    'dm.askQueue': async () => ({ ok: true, items: [] }),
  } });
  try {
    page.load('popup.ts');
    await page.ready();
    await flush(12);
    const doc = page.document;
    assert.match(doc.getElementById('targetSwitch').textContent, /^Send to: Home NAS/);
    assert.ok([...doc.querySelectorAll('.group-row')].some((row) => row.textContent === 'Home NAS (server)'));
  } finally { page.restore(); }
  // Without a shown name the popup keeps the address's host (popup-render.test.cjs, "home-server").
});

test('Settings: the Name field saves the user\'s name, says which name an empty one uses, and shows the name when paired', async () => {
  const saves = [];
  const settings = { ...data.settings, serverCustomName: '' };
  const server = { address: ADDRESS, name: 'Chrome on Windows', serverName: 'nas-box', displayName: 'nas-box', connected: true, lastError: '' };
  const page = installDom('options.html', { hash: '#server', handlers: {
    'dm.status': async () => ({ ok: true, settings, connected: true, server, extensionVersion: '0.6.0' }),
    'dm.saveSettings': async (message) => { saves.push(message); return { ok: true, settings: { ...settings, ...message.changes } }; },
  } });
  try {
    page.load('options.ts');
    await page.ready();
    await flush(10);
    const doc = page.document;
    const input = doc.getElementById('serverCustomName');
    assert.equal(input.value, '');
    assert.equal(input.placeholder, 'nas-box');
    assert.match(doc.getElementById('serverNameHint').textContent, /server's own name, "nas-box"/);
    assert.match(doc.getElementById('serverState').textContent, /^Paired with "nas-box" at 192\.168\.1\.150:8080 as "Chrome on Windows"\. It is answering\.$/);

    input.value = ' Home NAS ';
    input.dispatchEvent(new page.window.Event('input', { bubbles: true }));
    doc.getElementById('apply').click();
    await flush();
    assert.deepEqual(saves[0].changes, { serverCustomName: 'Home NAS' });
    assert.match(doc.getElementById('serverState').textContent, /^Paired with "Home NAS" at /);
  } finally { page.restore(); }
});

test('Settings: after Unpair the Name field no longer names the old server', async () => {
  const settings = { ...data.settings, serverCustomName: '' };
  const server = { address: ADDRESS, name: 'Chrome on Windows', serverName: 'nas-box', displayName: 'nas-box', connected: true, lastError: '' };
  const page = installDom('options.html', { hash: '#server', handlers: {
    'dm.status': async () => ({ ok: true, settings, connected: true, server, extensionVersion: '0.6.0' }),
    // No settings in the answer, so this checks showServer itself (the background's answer also carries them).
    'dm.unpairServer': async () => ({ ok: true, revoked: true, wasPaired: true }),
  } });
  try {
    page.load('options.ts');
    await page.ready();
    await flush(10);
    const doc = page.document;
    const input = doc.getElementById('serverCustomName');
    assert.equal(input.placeholder, 'nas-box');
    doc.getElementById('unpairServer').click();
    await flush();
    doc.querySelector('.dialog .btn-danger').click();
    await flush(10);
    assert.equal(doc.getElementById('serverPaired').classList.contains('hidden'), true);
    assert.equal(input.placeholder, 'Home NAS');
    assert.doesNotMatch(doc.getElementById('serverNameHint').textContent, /nas-box|server's own name/);
    assert.match(doc.getElementById('serverNameHint').textContent, /name the server gives when it is paired/);
  } finally { page.restore(); }
});
