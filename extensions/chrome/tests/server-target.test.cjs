// EXT.R1 with a paired server: the background's target choice, pairing, the popup's list and the fallback
// to the browser on any failure. The server is a fetch mock; the desktop app is the harness's native port.
const assert = require('node:assert/strict');
const test = require('node:test');
const {
  answerNative, capturingChrome, catching, ownPage, sendRuntimeMessage, settle, startDownload, withBackground,
} = require('./harness.cjs');

const TOKEN = 'ibt_' + 'b'.repeat(43);
const ADDRESS = 'http://192.168.1.20:8080';
const PAIRING = { address: ADDRESS, token: TOKEN, id: 'p1', name: 'Chrome on Windows', pairedAt: 1 };

/** A server that answers each envelope type with a handler (reply payload, or a full response). */
function fakeServer(handlers = {}) {
  const calls = [];
  const fetch = async (url, init) => {
    const body = init.body ? JSON.parse(init.body) : undefined;
    calls.push({ url, init, body });
    if (url === `${ADDRESS}/api/pair`) {
      const answer = handlers.pair ? handlers.pair(body) : { status: 200, body: { ok: true, token: TOKEN, id: 'p7', name: body.name } };
      return { status: answer.status, async text() { return JSON.stringify(answer.body); } };
    }
    if (init.headers.Authorization !== `Bearer ${TOKEN}`) {
      return { status: 401, async text() { return JSON.stringify({ ok: false, error: "This browser isn't paired with the server, or its pairing was removed. Pair it again." }); } };
    }
    const handler = handlers[body.type];
    if (handler instanceof Error) throw handler;
    const result = typeof handler === 'function' ? await handler(body.payload) : handler ?? { ok: true };
    if (result && result.status) return { status: result.status, async text() { return JSON.stringify(result.body); } };
    const type = { 'handoff.request': 'handoff.ack', 'jobs.query': 'jobs.snapshot', 'settings.downloadFolders': 'settings.downloadFoldersResult',
      'folders.browse': 'folders.browseResult', 'host.ping': 'host.actionResult' }[body.type] ?? 'job.actionResult';
    return { status: 200, async text() { return JSON.stringify({ v: 1, type, replyTo: body.id, payload: result }); } };
  };
  return { fetch, calls, of: (type) => calls.filter(call => call.body?.type === type) };
}

async function withServer(harness, server, run) {
  const oldFetch = global.fetch;
  global.fetch = server.fetch;
  try { await withBackground(harness, run); } finally { global.fetch = oldFetch; }
}

function paired(settings) {
  const setup = capturingChrome(settings);
  setup.harness.values.server = structuredClone(PAIRING);
  return setup;
}

test('EXT.R1: automatic sending goes to the server when it is the default, with the cookies, and the browser\'s copy goes', async () => {
  const { harness, log } = paired({ ...catching, defaultTarget: 'server' });
  harness.chrome.cookies = { async getAll() { return [{ name: 'session', value: 's', path: '/' }]; } };
  const server = fakeServer({ 'handoff.request': { accepted: true, job: { id: 'j1' } } });
  await withServer(harness, server, async () => {
    void startDownload(harness, { id: 5, url: 'https://host.test/track.flac', filename: 'track.flac' });
    await settle(() => log.cancelled.length);
    const [call] = server.of('handoff.request');
    assert.ok(call, 'the handoff went to the server');
    assert.equal(call.url, `${ADDRESS}/api/ipc`);
    assert.equal(call.body.payload.url, 'https://host.test/track.flac');
    assert.equal(call.body.payload.headers.Cookie, 'session=s');
    assert.equal(call.body.payload.isAutoIntercept, true);
    assert.equal(call.body.payload.extensionId, 'test-extension');
    assert.deepEqual(log.cancelled, [5]);
    assert.equal(harness.nativeMessages.filter(m => m.type === 'handoff.request').length, 0, 'nothing went to this PC');
    assert.equal(log.notes.at(-1).title, 'Sent to the Inbound server');
  });
});

test('EXT.R1: this PC stays the default for automatic sending after pairing', async () => {
  const { harness, log } = paired(catching);
  const server = fakeServer();
  await withServer(harness, server, async () => {
    void startDownload(harness, { id: 6, url: 'https://host.test/a.zip', filename: 'a.zip' });
    await answerNative(harness, 'handoff.request', { accepted: true });
    await settle(() => log.cancelled.length);
    assert.equal(server.of('handoff.request').length, 0);
  });
});

for (const [name, handler, reason] of [
  ['no answer', new TypeError('Failed to fetch'), /not answering/],
  ['a revoked pairing', { status: 401, body: { ok: false, error: 'Pair it again.' } }, /Pair it again/],
  ['a refusal', { accepted: false, error: 'That folder is outside the server\'s download folders.' }, /outside the server/],
]) {
  test(`EXT.R1: ${name} from the server leaves the download in the browser, and nothing goes to this PC instead`, async () => {
    const { harness, log } = paired({ ...catching, defaultTarget: 'server' });
    const server = fakeServer({ 'handoff.request': handler });
    await withServer(harness, server, async () => {
      void startDownload(harness, { id: 8, url: 'https://host.test/b.iso', filename: 'b.iso' });
      await settle(() => log.resumed.length);
      assert.deepEqual(log.resumed, [8]);
      assert.deepEqual(log.cancelled, []);
      assert.equal(harness.nativeMessages.filter(m => m.type === 'handoff.request').length, 0);
      assert.match(log.notes.at(-1).message, reason);
      assert.match(log.notes.at(-1).message, /stays in your browser/);
    });
  });
}

test('EXT.R1: "ask each time" offers this PC and the server, each with its own default folder', async () => {
  const { harness, log } = paired({ ...catching, destinationMode: 'ask', defaultTarget: 'server' });
  const server = fakeServer({
    'settings.downloadFolders': { ok: true, defaultFolder: '/downloads', favorites: [{ name: 'Music', path: '/downloads/music' }] },
    'handoff.request': (payload) => ({ accepted: true, job: { id: 'j2', fileName: payload.suggestedFileName } }),
  });
  await withServer(harness, server, async () => {
    harness.downloadsCreated.listeners[0]({ id: 31, url: 'https://site.test/c.iso', filename: 'c.iso', referrer: '', mime: '', totalBytes: 1, fileSize: 1 }, () => {});
    await settle(() => log.windows.length);
    const id = decodeURIComponent(log.windows[0].url.split('id=')[1]);

    const info = sendRuntimeMessage(harness, { type: 'dm.getHandoff', id }, ownPage('handoff.html'));
    await answerNative(harness, 'settings.downloadFolders', { ok: true, defaultFolder: 'D:\\Downloads', favorites: [] });
    const shown = await info;
    assert.deepEqual(shown.targets.map(t => [t.id, t.label, t.chooser, t.defaultFolder]),
      [['local', 'This PC', 'dialog', 'D:\\Downloads'], ['server', 'Server', 'list', '/downloads']]);
    assert.equal(shown.targets[1].address, ADDRESS);
    assert.equal(shown.target, 'server', 'the window starts on the default target');
    assert.equal(shown.defaults.destination, '/downloads');
    assert.equal(JSON.stringify(shown).includes(TOKEN), false, 'the token never reaches a page');

    // Choose… on the server lists its folders, from the folder window only.
    const listing = sendRuntimeMessage(harness, { type: 'dm.browseServerFolders', path: '/downloads' }, ownPage('handoff.html'));
    assert.equal((await listing).ok, true);
    assert.deepEqual(server.of('folders.browse').at(-1).body.payload, { path: '/downloads' });
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.browseServerFolders', path: '/' }, ownPage('popup.html'))).ok, false);

    const confirmed = await sendRuntimeMessage(harness,
      { type: 'dm.confirmHandoff', id, destination: '/downloads/music', startMode: 'now', target: 'server' }, ownPage('handoff.html'));
    assert.equal(confirmed.ok, true);
    assert.equal(confirmed.target, 'server');
    assert.equal(server.of('handoff.request')[0].body.payload.destinationDirectory, '/downloads/music');
    await settle(() => log.cancelled.length);
    assert.deepEqual(log.cancelled, [31]);
  });
});

test('EXT.R1: the window can still send to this PC with a server paired', async () => {
  const { harness, log } = paired({ ...catching, destinationMode: 'ask' });
  const server = fakeServer({ 'settings.downloadFolders': { ok: true, defaultFolder: '/downloads', favorites: [] } });
  await withServer(harness, server, async () => {
    harness.downloadsCreated.listeners[0]({ id: 32, url: 'https://site.test/d.iso', filename: 'd.iso', referrer: '', mime: '', totalBytes: 1, fileSize: 1 }, () => {});
    await settle(() => log.windows.length);
    const id = decodeURIComponent(log.windows[0].url.split('id=')[1]);
    const info = sendRuntimeMessage(harness, { type: 'dm.getHandoff', id }, ownPage('handoff.html'));
    await answerNative(harness, 'settings.downloadFolders', { ok: true, defaultFolder: 'D:\\Downloads', favorites: [] });
    assert.equal((await info).target, 'local');
    const confirmed = sendRuntimeMessage(harness, { type: 'dm.confirmHandoff', id, destination: '', startMode: 'now', target: 'local' }, ownPage('handoff.html'));
    await answerNative(harness, 'handoff.request', { accepted: true });
    assert.equal((await confirmed).target, 'local');
    assert.equal(server.of('handoff.request').length, 0);
  });
});

test('EXT.R1: without the desktop app here, the paired server is the only target and no choice is shown', async () => {
  const { harness, log } = paired({ ...catching, destinationMode: 'ask' });
  const server = fakeServer({ 'settings.downloadFolders': { ok: true, defaultFolder: '/downloads', favorites: [] } });
  await withServer(harness, server, async () => {
    harness.downloadsCreated.listeners[0]({ id: 33, url: 'https://site.test/e.iso', filename: 'e.iso', referrer: '', mime: '', totalBytes: 1, fileSize: 1 }, () => {});
    await settle(() => log.windows.length);
    const id = decodeURIComponent(log.windows[0].url.split('id=')[1]);
    const info = sendRuntimeMessage(harness, { type: 'dm.getHandoff', id }, ownPage('handoff.html'));
    // The browser has no native host registered.
    await settle(() => harness.nativePorts.length);
    harness.chrome.runtime.lastError = { message: 'Specified native messaging host not found.' };
    for (const listener of harness.nativePorts[0].onDisconnect.listeners) listener();
    harness.chrome.runtime.lastError = undefined;
    const shown = await info;
    assert.deepEqual(shown.targets.map(t => t.id), ['server']);
    assert.equal(shown.target, 'server');
    assert.equal(shown.defaults.destination, '/downloads');
  });
});

test('EXT.R1: without a server there is one target, and nothing is asked of a server', async () => {
  const { harness, log } = capturingChrome({ ...catching, destinationMode: 'ask', defaultTarget: 'server' });
  const server = fakeServer();
  await withServer(harness, server, async () => {
    harness.downloadsCreated.listeners[0]({ id: 34, url: 'https://site.test/f.iso', filename: 'f.iso', referrer: '', mime: '', totalBytes: 1, fileSize: 1 }, () => {});
    await settle(() => log.windows.length);
    const id = decodeURIComponent(log.windows[0].url.split('id=')[1]);
    const info = sendRuntimeMessage(harness, { type: 'dm.getHandoff', id }, ownPage('handoff.html'));
    await answerNative(harness, 'settings.downloadFolders', { ok: true, defaultFolder: 'D:\\Downloads', favorites: [] });
    const shown = await info;
    assert.deepEqual(shown.targets.map(t => t.id), ['local']);
    // A page asking for the server without one paired gets this PC.
    const confirmed = sendRuntimeMessage(harness, { type: 'dm.confirmHandoff', id, destination: '', startMode: 'now', target: 'server' }, ownPage('handoff.html'));
    await answerNative(harness, 'handoff.request', { accepted: true });
    assert.equal((await confirmed).target, 'local');
    assert.equal(server.calls.length, 0);
  });
});

test('EXT.R1: pairing from Settings stores the token where no page can read it; unpairing sends everything back to this PC', async () => {
  const { harness } = capturingChrome(catching);
  harness.chrome.permissions = { async contains() { return true; } };
  const server = fakeServer();
  await withServer(harness, server, async () => {
    // Only Settings may pair.
    const refused = await sendRuntimeMessage(harness, { type: 'dm.pairServer', address: ADDRESS, code: 'ABCDEF23' }, ownPage('popup.html'));
    assert.equal(refused.ok, false);
    assert.equal(server.calls.length, 0);

    const pairedReply = await sendRuntimeMessage(harness, { type: 'dm.pairServer', address: '192.168.1.20:8080', code: 'abcd-ef23' }, ownPage('options.html'));
    assert.equal(pairedReply.ok, true);
    assert.equal(pairedReply.server.address, ADDRESS);
    assert.equal(pairedReply.server.connected, true);
    assert.deepEqual(server.calls[0].body, { code: 'ABCDEF23', name: 'Browser' });
    assert.equal(harness.values.server.token, TOKEN);
    assert.equal(JSON.stringify(pairedReply).includes(TOKEN), false);

    const status = await sendRuntimeMessage(harness, { type: 'dm.status' }, ownPage('popup.html'));
    assert.equal(status.server.address, ADDRESS);
    assert.equal(JSON.stringify(status).includes(TOKEN), false, 'the status never carries the token');

    await sendRuntimeMessage(harness, { type: 'dm.saveSettings', settings: { ...catching, defaultTarget: 'server' } }, ownPage('options.html'));
    assert.equal(harness.values.settings.defaultTarget, 'server');
    const unpaired = await sendRuntimeMessage(harness, { type: 'dm.unpairServer' }, ownPage('options.html'));
    assert.equal(unpaired.ok, true);
    assert.equal(harness.values.server, null);
    assert.equal(harness.values.settings.defaultTarget, 'local');
  });
});

test('EXT.R1: a wrong pairing code stores nothing', async () => {
  const { harness } = capturingChrome(catching);
  const server = fakeServer({ pair: () => ({ status: 401, body: { ok: false, error: 'That code is wrong, used or expired.' } }) });
  await withServer(harness, server, async () => {
    const reply = await sendRuntimeMessage(harness, { type: 'dm.pairServer', address: ADDRESS, code: 'ABCDEF23' }, ownPage('options.html'));
    assert.equal(reply.ok, false);
    assert.match(reply.error, /code is wrong/);
    assert.equal(harness.values.server, undefined);
  });
});

test('EXT.R1: the popup lists the downloads of both places, labelled, and acts on the right one', async () => {
  const { harness } = paired(catching);
  const job = '6f1c3c2e-8a4b-4c7d-9e2f-1a2b3c4d5e6f';
  const server = fakeServer({
    'jobs.query': { jobs: [{ id: job, fileName: 'on-server.iso', state: 'downloading', receivedBytes: 1, createdAt: '2026-11-08T10:00:00Z' }] },
    'job.pause': (payload) => ({ ok: true, job: { id: payload.jobId } }),
  });
  await withServer(harness, server, async () => {
    const reply = sendRuntimeMessage(harness, { type: 'dm.jobs' }, ownPage('popup.html'));
    await answerNative(harness, 'jobs.query', { jobs: [{ id: 'l1', fileName: 'here.zip', state: 'completed', receivedBytes: 1 }] });
    const listed = await reply;
    assert.equal(listed.ok, true);
    assert.deepEqual(listed.jobs.map(j => [j.fileName, j.target]), [['here.zip', 'local'], ['on-server.iso', 'server']]);
    assert.deepEqual(listed.groups.map(g => [g.target, g.label, g.ok]), [['local', 'This PC', true], ['server', 'Server', true]]);

    const paused = await sendRuntimeMessage(harness, { type: 'dm.jobAction', action: 'pause', jobId: job, target: 'server' }, ownPage('popup.html'));
    assert.equal(paused.ok, true);
    assert.deepEqual(server.of('job.pause')[0].body.payload, { jobId: job, deleteFile: false });
    assert.equal(harness.nativeMessages.filter(m => m.type === 'job.pause').length, 0);
  });
});

test('EXT.R1: the popup keeps one place\'s downloads when the other does not answer', async () => {
  const { harness } = paired(catching);
  const server = fakeServer({ 'jobs.query': new TypeError('Failed to fetch') });
  await withServer(harness, server, async () => {
    const reply = sendRuntimeMessage(harness, { type: 'dm.jobs' }, ownPage('popup.html'));
    await answerNative(harness, 'jobs.query', { jobs: [{ id: 'l1', fileName: 'here.zip', state: 'completed', receivedBytes: 1 }] });
    const listed = await reply;
    assert.equal(listed.ok, true);
    assert.deepEqual(listed.jobs.map(j => j.fileName), ['here.zip']);
    assert.equal(listed.groups[1].ok, false);
    assert.match(listed.groups[1].error, /not answering/);
  });
});
