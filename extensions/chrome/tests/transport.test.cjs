// EXT.R1 transport: the desktop app over native messaging, the paired server over POST /api/ipc.
const assert = require('node:assert/strict');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
const esbuild = require('esbuild');

const source = path.join(__dirname, '..', 'src', 'transport.ts');
const loaded = new Module(source, module);
loaded.filename = source;
loaded.paths = Module._nodeModulePaths(path.dirname(source));
loaded._compile(esbuild.buildSync({
  entryPoints: [source], bundle: true, write: false, platform: 'node', format: 'cjs', target: 'node20',
}).outputFiles[0].text, source);
const {
  NativeTransport, ServerTransport, ServerUnpairedError, normalizeServerAddress, normalizePairingCode, pairingName,
  pairWithServer, readPairing, availableTargets, automaticTarget, initialTarget, isNativeHostMissing,
} = loaded.exports;

const connectionProblem = (() => {
  const file = path.join(__dirname, '..', 'src', 'connection-problem.ts');
  const m = new Module(file, module);
  m.filename = file;
  m._compile(esbuild.buildSync({ entryPoints: [file], bundle: true, write: false, platform: 'node', format: 'cjs' }).outputFiles[0].text, file);
  return m.exports;
})();

const TOKEN = 'ibt_' + 'a'.repeat(43);
const PAIRING = { address: 'http://192.168.1.20:8080', token: TOKEN, id: 'p1', name: 'Chrome on Windows' };

/** A fetch that answers from a list of handlers and remembers every call. */
function mockFetch(...answers) {
  const calls = [];
  const fetch = async (url, init) => {
    calls.push({ url, init, body: init.body ? JSON.parse(init.body) : undefined });
    let answer = answers.length > 1 ? answers.shift() : answers[0];
    if (typeof answer === 'function') answer = await answer(url, init, calls.at(-1));
    if (answer instanceof Error) throw answer;
    return { status: answer.status ?? 200, async text() { return typeof answer.body === 'string' ? answer.body : JSON.stringify(answer.body); } };
  };
  return { fetch, calls };
}

const envelope = (type, payload, replyTo = 'x') => ({ status: 200, body: { v: 1, type, replyTo, payload } });

test('ServerTransport posts one envelope with the token, never cookies, never following a redirect', async () => {
  const { fetch, calls } = mockFetch((url, init, call) => envelope('handoff.ack', { accepted: true }, call.body.id));
  const transport = new ServerTransport(PAIRING, fetch);
  const payload = await transport.send('handoff.request', { url: 'https://site.test/a.zip' });
  assert.deepEqual(payload, { accepted: true });
  assert.equal(calls.length, 1);
  const [{ url, init, body }] = calls;
  assert.equal(url, 'http://192.168.1.20:8080/api/ipc');
  assert.equal(init.method, 'POST');
  assert.equal(init.headers.Authorization, `Bearer ${TOKEN}`);
  assert.equal(init.headers['Content-Type'], 'application/json');
  assert.equal(init.credentials, 'omit');
  assert.equal(init.redirect, 'error');
  assert.equal(init.cache, 'no-store');
  assert.equal(body.v, 1);
  assert.equal(body.type, 'handoff.request');
  assert.match(body.id, /^[0-9a-f-]{36}$/);
  assert.deepEqual(body.payload, { url: 'https://site.test/a.zip' });
  assert.equal(transport.connected, true);
  assert.equal(transport.lastError, '');
});

test('ServerTransport: error replies reject, a refused command comes back as its envelope', async () => {
  const transport = new ServerTransport(PAIRING, mockFetch(
    envelope('handoff.error', { error: 'Turn on browser downloads.' }),
    { status: 403, body: { v: 1, type: 'host.actionResult', payload: { ok: false, message: "A paired browser can't send 'job.add'." } } },
  ).fetch);
  await assert.rejects(transport.send('handoff.request', {}), /Turn on browser downloads/);
  const refused = await transport.request('job.add', {}, 1000, 'late');
  assert.equal(refused.type, 'host.actionResult');
  assert.equal(refused.payload.ok, false);
  assert.equal(transport.connected, true);
});

test('ServerTransport: a revoked pairing (401) says to pair again', async () => {
  const transport = new ServerTransport(PAIRING, mockFetch({ status: 401, body: { ok: false, error: "This browser isn't paired with the server, or its pairing was removed. Pair it again." } }).fetch);
  const error = await transport.send('host.ping', {}).catch(e => e);
  assert.ok(error instanceof ServerUnpairedError);
  assert.match(error.message, /Pair it again/);
  assert.equal(transport.connected, false);
  assert.match(transport.lastError, /Pair it again/);
});

test('ServerTransport: no answer, a time-out and a non-envelope answer fail in the server\'s own words', async () => {
  const down = new ServerTransport(PAIRING, mockFetch(new TypeError('Failed to fetch')).fetch);
  const error = await down.send('host.ping', {}).catch(e => e);
  assert.match(error.message, /The Inbound server at http:\/\/192\.168\.1\.20:8080 is not answering/);
  // Never explained as the desktop app missing or stopped.
  assert.equal(connectionProblem.readableError(error.message), error.message);
  assert.equal(down.connected, false);

  const slow = new ServerTransport(PAIRING, (url, init) => new Promise((_, reject) => {
    init.signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
  }));
  await assert.rejects(slow.send('host.ping', {}, 30), /Timed out waiting for the Inbound server/);

  const proxy = new ServerTransport(PAIRING, mockFetch({ status: 502, body: '<html>Bad gateway</html>' }).fetch);
  await assert.rejects(proxy.send('host.ping', {}), /answered HTTP 502/);
  assert.equal(proxy.connected, false);
});

test('ServerTransport.revokeSelf: false when the server never answers within the time limit, or refuses the token', async () => {
  let aborted = false;
  const silent = new ServerTransport(PAIRING, (url, init) => new Promise((_, reject) => {
    init.signal.addEventListener('abort', () => { aborted = true; reject(new DOMException('aborted', 'AbortError')); });
  }));
  const started = Date.now();
  assert.equal(await silent.revokeSelf(30), false);
  assert.equal(aborted, true, 'the time limit aborts the request');
  assert.ok(Date.now() - started < 2000, 'it does not wait past the time limit');

  // A server that does not know this token (wrong or already removed) answers 401: not revoked by this call.
  const seen = [];
  const refusing = new ServerTransport({ ...PAIRING, token: 'ibt_' + 'z'.repeat(43) }, async (url, init) => {
    seen.push(init.headers.Authorization);
    return { status: init.headers.Authorization === `Bearer ${TOKEN}` ? 204 : 401, async text() { return ''; } };
  });
  assert.equal(await refusing.revokeSelf(1000), false);
  assert.deepEqual(seen, ['Bearer ibt_' + 'z'.repeat(43)]);
  const accepting = new ServerTransport(PAIRING, async () => ({ status: 204, async text() { return ''; } }));
  assert.equal(await accepting.revokeSelf(1000), true);
});

test('server addresses: a scheme is added, paths and ports handled, credentials refused', () => {
  assert.deepEqual(normalizeServerAddress('192.168.1.20:8080'),
    { ok: true, origin: 'http://192.168.1.20:8080', pattern: 'http://192.168.1.20/*', cleartext: true });
  assert.deepEqual(normalizeServerAddress(' https://nas.home.arpa/signin/ '),
    { ok: true, origin: 'https://nas.home.arpa', pattern: 'https://nas.home.arpa/*', cleartext: false });
  assert.equal(normalizeServerAddress('http://localhost:8080').cleartext, false);
  assert.equal(normalizeServerAddress('http://[::1]:8080').pattern, 'http://[::1]/*');
  assert.equal(normalizeServerAddress('http://user:pw@nas:8080').ok, false);
  assert.equal(normalizeServerAddress('ftp://nas').ok, false);
  assert.equal(normalizeServerAddress('').ok, false);
  assert.equal(normalizeServerAddress('http://').ok, false);
});

test('pairing codes and names', () => {
  assert.equal(normalizePairingCode('abcd-ef23'), 'ABCDEF23');
  assert.equal(normalizePairingCode(' AB CD EF 23 '), 'ABCDEF23');
  assert.equal(normalizePairingCode('ABC'), null);
  assert.equal(normalizePairingCode('ABCDEFGH1'), null);
  assert.equal(pairingName('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36'), 'Chrome on Windows');
  assert.equal(pairingName('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36 Edg/141.0'), 'Edge on Windows');
  assert.equal(pairingName('Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0'), 'Firefox on Linux');
});

test('pairing turns the code into a token, checks it with one ping, and keeps nothing else', async () => {
  const { fetch, calls } = mockFetch(
    { status: 200, body: { ok: true, token: TOKEN, id: 'p9', name: 'Chrome on Windows' } },
    (url, init, call) => envelope('host.actionResult', { ok: true }, call.body.id),
  );
  const pairing = await pairWithServer('192.168.1.20:8080', 'abcd-ef23', 'Chrome on Windows', fetch);
  assert.equal(calls[0].url, 'http://192.168.1.20:8080/api/pair');
  assert.deepEqual(calls[0].body, { code: 'ABCDEF23', name: 'Chrome on Windows' });
  assert.equal(calls[0].init.headers.Authorization, undefined);
  assert.equal(calls[0].init.credentials, 'omit');
  assert.equal(calls[1].url, 'http://192.168.1.20:8080/api/ipc');
  assert.equal(calls[1].body.type, 'host.ping');
  assert.equal(calls[1].init.headers.Authorization, `Bearer ${TOKEN}`);
  assert.equal(pairing.address, 'http://192.168.1.20:8080');
  assert.equal(pairing.token, TOKEN);
  assert.equal(pairing.id, 'p9');
  assert.equal(typeof pairing.pairedAt, 'number');
  assert.deepEqual(readPairing(JSON.parse(JSON.stringify(pairing))), pairing);
});

test('pairing failures: the server\'s reason, a server that is not ours, a bad code before any request', async () => {
  const wrong = mockFetch({ status: 401, body: { ok: false, error: 'That code is wrong, used or expired. Show a new one in the web UI (Settings, Sign-in and browsers).' } });
  await assert.rejects(pairWithServer('nas:8080', 'ABCDEF23', 'x', wrong.fetch), /code is wrong, used or expired/);
  assert.equal(wrong.calls.length, 1);
  await assert.rejects(pairWithServer('nas:8080', 'ABCDEF23', 'x', mockFetch({ status: 404, body: 'Not found' }).fetch), /not an Inbound server/);
  await assert.rejects(pairWithServer('nas:8080', 'ABCDEF23', 'x', mockFetch({ status: 200, body: { ok: true, token: 'nope' } }).fetch), /HTTP 200/);
  const none = mockFetch({ status: 200, body: {} });
  await assert.rejects(pairWithServer('nas:8080', 'abc', 'x', none.fetch), /8 letters and digits/);
  await assert.rejects(pairWithServer('ftp://nas', 'ABCDEF23', 'x', none.fetch), /http:\/\/ or https:\/\//);
  assert.equal(none.calls.length, 0);
  // A token the ping refuses is never handed back to be stored.
  const revoked = mockFetch({ status: 200, body: { ok: true, token: TOKEN, id: 'p1' } }, { status: 401, body: { ok: false, error: 'Pair it again.' } });
  await assert.rejects(pairWithServer('nas:8080', 'ABCDEF23', 'x', revoked.fetch), /Pair it again/);
});

test('a stored pairing is used only when it is whole and its address is an origin', () => {
  assert.equal(readPairing(null), null);
  assert.equal(readPairing({ address: 'http://nas:8080' }), null);
  assert.equal(readPairing({ address: 'http://nas:8080', token: 'plain' }), null);
  assert.equal(readPairing({ address: 'http://nas:8080/elsewhere', token: TOKEN }), null);
  assert.equal(readPairing({ address: 'http://user@nas:8080', token: TOKEN }), null);
  assert.equal(readPairing({ address: 'http://nas:8080', token: TOKEN }).address, 'http://nas:8080');
});

test('the target choice: both when paired, the server alone without the desktop app, never empty', () => {
  assert.deepEqual(availableTargets({ serverPaired: false, localMissing: false }), ['local']);
  assert.deepEqual(availableTargets({ serverPaired: false, localMissing: true }), ['local']);
  assert.deepEqual(availableTargets({ serverPaired: true, localMissing: false }), ['local', 'server']);
  assert.deepEqual(availableTargets({ serverPaired: true, localMissing: true }), ['server']);

  // Automatic sending: this PC unless the server is the default and paired. No fallback either way.
  assert.equal(automaticTarget(undefined, true), 'local');
  assert.equal(automaticTarget('local', true), 'local');
  assert.equal(automaticTarget('server', true), 'server');
  assert.equal(automaticTarget('server', false), 'local');

  assert.equal(initialTarget('server', ['local', 'server']), 'server');
  assert.equal(initialTarget('local', ['server']), 'server');
  assert.equal(initialTarget(undefined, ['local', 'server']), 'local');

  assert.equal(isNativeHostMissing('Specified native messaging host not found.'), true);
  assert.equal(isNativeHostMissing('No such native application com.kaank.downloadmanager'), true);
  assert.equal(isNativeHostMissing('Native host has exited.'), false);
  assert.equal(isNativeHostMissing(''), false);
});

/** A fake runtime.connectNative: each port records what was posted and can answer or drop. */
function fakeRuntime() {
  const ports = [];
  const runtime = {
    lastError: undefined,
    connectNative(name) {
      const listeners = { message: [], disconnect: [] };
      const port = {
        name, posted: [], disconnected: false,
        onMessage: { addListener: l => listeners.message.push(l) },
        onDisconnect: { addListener: l => listeners.disconnect.push(l) },
        postMessage(message) { this.posted.push(message); },
        disconnect() { this.disconnected = true; },
        answer(message) { for (const l of listeners.message) l(message); },
        drop(error) { runtime.lastError = { message: error }; for (const l of listeners.disconnect) l(); runtime.lastError = undefined; },
      };
      ports.push(port);
      return port;
    },
  };
  return { runtime, ports };
}

test('NativeTransport: one shared connection, replies matched by id, events passed on, error replies rejected', async () => {
  const { runtime, ports } = fakeRuntime();
  const events = [];
  const transport = new NativeTransport('com.kaank.downloadmanager', m => events.push(m), runtime);
  assert.equal(transport.connected, false);
  const ping = transport.send('host.ping', {});
  assert.equal(ports.length, 1);
  assert.equal(ports[0].name, 'com.kaank.downloadmanager');
  const sent = ports[0].posted[0];
  assert.equal(sent.v, 1);
  assert.equal(sent.type, 'host.ping');
  ports[0].answer({ type: 'job.event', payload: { job: { id: 'j' } } });
  ports[0].answer({ type: 'host.actionResult', replyTo: sent.id, payload: { ok: true } });
  assert.deepEqual(await ping, { ok: true });
  assert.equal(events.length, 1);
  assert.equal(transport.connected, true);

  // retries 0: with the default one retry, send would ask once more on a new connection first.
  const refused = transport.send('handoff.request', {}, 1000, 0);
  ports[0].answer({ type: 'handoff.error', replyTo: ports[0].posted[1].id, payload: { error: 'Forbidden extension.' } });
  await assert.rejects(refused, /Forbidden extension/);
  assert.equal(ports.length, 1);
});

test('NativeTransport: a missing desktop app fails every waiting request with the browser\'s reason', async () => {
  const { runtime, ports } = fakeRuntime();
  const transport = new NativeTransport('com.kaank.downloadmanager', undefined, runtime);
  const waiting = transport.send('settings.downloadFolders', {}, 1000, 0);
  ports[0].drop('Specified native messaging host not found.');
  await assert.rejects(waiting, /not found/);
  assert.equal(transport.connected, false);
  assert.equal(isNativeHostMissing(transport.lastError), true);
});

test('NativeTransport: a long request goes on a connection of its own, closed after the answer', async () => {
  const { runtime, ports } = fakeRuntime();
  const transport = new NativeTransport('com.kaank.downloadmanager', undefined, runtime);
  const pick = transport.request('host.pickFolder', { initialPath: 'D:\\' }, 1000, 'late');
  assert.equal(ports.length, 1);
  ports[0].answer({ type: 'host.folderPicked', replyTo: ports[0].posted[0].id, payload: { ok: true, path: 'E:\\' } });
  const reply = await pick;
  assert.equal(reply.type, 'host.folderPicked');
  assert.equal(ports[0].disconnected, true);
  // The shared connection was never opened for it.
  assert.equal(transport.connected, false);
  await assert.rejects(transport.request('job.remove', {}, 20, 'Timed out waiting for the desktop app.'), /Timed out/);
});
