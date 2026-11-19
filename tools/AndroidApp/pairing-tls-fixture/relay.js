#!/usr/bin/env node
'use strict';

// A deliberately transparent test relay: it never parses or logs application data.
const fs = require('node:fs');
const net = require('node:net');
const tls = require('node:tls');

const usage = 'Usage: node relay.js --key <pem> --cert <pem> --upstream-port <port> ' +
  '[--listen-host <IP>] [--listen-port <port>]';

function parseArgs(args) {
  const values = { '--listen-host': '127.0.0.1', '--listen-port': '8443' };
  const allowed = new Set(['--key', '--cert', '--upstream-port', '--listen-host', '--listen-port']);
  const seen = new Set();
  for (let i = 0; i < args.length; i += 2) {
    const flag = args[i];
    if (!allowed.has(flag) || seen.has(flag) || !args[i + 1] || args[i + 1].startsWith('--')) {
      throw new Error(usage);
    }
    seen.add(flag);
    values[flag] = args[i + 1];
  }
  if (!values['--key'] || !values['--cert'] || !values['--upstream-port'] ||
      !net.isIP(values['--listen-host'])) throw new Error(usage);
  for (const flag of ['--listen-port', '--upstream-port']) {
    const port = values[flag];
    if (!/^\d+$/.test(port) || Number(port) < 1 || Number(port) > 65535) {
      throw new Error(`Invalid ${flag}`);
    }
    values[flag] = Number(port);
  }
  return values;
}

try {
  const options = parseArgs(process.argv.slice(2));
  const server = tls.createServer({
    key: fs.readFileSync(options['--key']),
    cert: fs.readFileSync(options['--cert']),
    minVersion: 'TLSv1.2',
  }, client => {
    const upstream = net.connect({ host: '127.0.0.1', port: options['--upstream-port'] });
    // Close both halves on any failure; do not print potentially sensitive errors/data.
    client.on('error', () => upstream.destroy());
    upstream.on('error', () => client.destroy());
    client.on('close', () => upstream.destroy());
    upstream.on('close', () => client.destroy());
    client.pipe(upstream);
    upstream.pipe(client);
  });
  server.on('tlsClientError', () => {});
  server.on('error', () => {
    console.error('Relay could not listen. Check the address and port.');
    process.exitCode = 1;
  });
  server.listen(options['--listen-port'], options['--listen-host'], () => {
    console.log(`TLS relay listening on ${options['--listen-host']}:${options['--listen-port']}; upstream 127.0.0.1:${options['--upstream-port']}`);
  });
} catch (error) {
  // Do not echo paths or contents of private key files.
  console.error(error.message.startsWith('Usage:') || error.message.startsWith('Invalid ')
    ? error.message : 'Could not load TLS key/certificate or start relay.');
  process.exitCode = 1;
}
