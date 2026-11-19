'use strict';

const assert = require('node:assert/strict');
const { spawn, spawnSync } = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const https = require('node:https');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const tls = require('node:tls');
const { once } = require('node:events');

async function main() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pairing-tls-'));
  let relay;
  let upstream;
  const sockets = new Set();
  const deadline = setTimeout(() => {
    console.error('Smoke test timed out');
    if (relay && relay.exitCode === null) relay.kill();
    for (const socket of sockets) socket.destroy();
    if (upstream) upstream.close();
    fs.rmSync(dir, { recursive: true, force: true });
    process.exit(1);
  }, 15000);
  deadline.unref();
  try {
    const key = path.join(dir, 'key.pem');
    const cert = path.join(dir, 'cert.pem');
    const generated = spawnSync(process.env.OPENSSL || 'openssl', [
      'req', '-x509', '-newkey', 'rsa:2048', '-sha256', '-nodes', '-days', '1',
      '-keyout', key, '-out', cert, '-subj', '/CN=pairing-test',
      '-addext', 'subjectAltName=IP:127.0.0.1',
    ], { timeout: 8000, stdio: 'ignore' });
    assert.equal(generated.status, 0, 'OpenSSL certificate generation failed');
    const ca = fs.readFileSync(cert);
    upstream = http.createServer((req, res) => {
      assert.equal(req.headers.authorization, 'Bearer invented-test-token');
      res.end('isolated-response');
    });
    upstream.on('connection', socket => {
      sockets.add(socket);
      socket.on('close', () => sockets.delete(socket));
    });
    upstream.on('upgrade', (req, socket, head) => {
      assert.equal(head.length, 0);
      const accept = crypto.createHash('sha1').update(req.headers['sec-websocket-key'] +
        '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
      socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n' +
        `Connection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
      let frame = Buffer.alloc(0);
      socket.on('data', function receive(data) {
        frame = Buffer.concat([frame, data]);
        if (frame.length < 8) return;
        socket.off('data', receive);
        // A masked text frame containing "hi", echoed as an unmasked frame.
        assert.deepEqual(frame, Buffer.from([0x81, 0x82, 1, 2, 3, 4, 0x69, 0x6b]));
        socket.write(Buffer.from([0x81, 2, 0x68, 0x69]));
      });
    });
    upstream.listen(0, '127.0.0.1');
    await once(upstream, 'listening');
    const upstreamPort = upstream.address().port;
    const probe = net.createServer();
    probe.listen(0, '127.0.0.1');
    await once(probe, 'listening');
    const relayPort = probe.address().port;
    await new Promise(resolve => probe.close(resolve));
    relay = spawn(process.execPath, [path.join(__dirname, 'relay.js'), '--key', key,
      '--cert', cert, '--upstream-port', String(upstreamPort), '--listen-port', String(relayPort)],
    { stdio: ['ignore', 'pipe', 'pipe'] });
    let output = '';
    relay.stdout.on('data', chunk => { output += chunk; });
    relay.stderr.on('data', chunk => { output += chunk; });
    await Promise.race([
      once(relay.stdout, 'data'),
      once(relay, 'exit').then(() => { throw new Error('Relay exited before listening'); }),
    ]);
    assert.match(output, /listening on 127\.0\.0\.1:/);

    function get(extra) {
      return new Promise((resolve, reject) => {
        const request = https.get({ host: '127.0.0.1', port: relayPort, path: '/invented-test-path',
          headers: { Authorization: 'Bearer invented-test-token' }, ...extra }, response => {
          let body = '';
          response.setEncoding('utf8');
          response.on('data', data => { body += data; });
          response.on('end', () => resolve(body));
        });
        request.on('error', reject);
      });
    }
    await assert.rejects(get({}), { code: 'DEPTH_ZERO_SELF_SIGNED_CERT' });
    assert.equal(await get({ ca }), 'isolated-response');
    console.log('PASS: self-signed certificate rejected by default and accepted with explicit test CA; HTTPS forwarded');

    const socket = tls.connect({ host: '127.0.0.1', port: relayPort, ca });
    await once(socket, 'secureConnect');
    const wsKey = crypto.randomBytes(16).toString('base64');
    const expected = crypto.createHash('sha1').update(wsKey +
      '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
    socket.write('GET /invented-ws HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\n' +
      `Connection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: ${wsKey}\r\n\r\n`);
    let received = Buffer.alloc(0);
    while (!received.includes('\r\n\r\n')) received = Buffer.concat([received, (await once(socket, 'data'))[0]]);
    assert.match(received.toString(), /^HTTP\/1\.1 101 /);
    assert.match(received.toString(), new RegExp(`Sec-WebSocket-Accept: ${expected.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}`, 'i'));
    socket.write(Buffer.from([0x81, 0x82, 1, 2, 3, 4, 0x69, 0x6b]));
    let frame = Buffer.alloc(0);
    while (frame.length < 4) frame = Buffer.concat([frame, (await once(socket, 'data'))[0]]);
    assert.deepEqual(frame, Buffer.from([0x81, 2, 0x68, 0x69]));
    socket.destroy();
    assert.doesNotMatch(output, /invented-test-token|invented-test-path|invented-ws/);
    console.log('PASS: WebSocket upgrade and masked/unmasked frame round trip; no test request data logged');
  } finally {
    if (relay && relay.exitCode === null) {
      const exited = once(relay, 'exit');
      relay.kill();
      await exited;
    }
    for (const socket of sockets) socket.destroy();
    if (upstream && upstream.listening) await new Promise(resolve => upstream.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
    clearTimeout(deadline);
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
