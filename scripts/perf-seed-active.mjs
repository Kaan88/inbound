#!/usr/bin/env node
// Used by perf-seed.sh --target server: the headless server has no CLI, so this signs in over HTTP and adds the active
// downloads with one job.add each on POST /api/ipc, the way the web UI does (webui/src/lib/protocol.ts).
// Zero dependencies (Node's own fetch). Prints nothing on success; exits 1 with a reason otherwise.
const arg = (name, fallback) => { const i = process.argv.indexOf('--' + name); return i > 0 ? process.argv[i + 1] : fallback; };
const url = arg('url'), password = arg('password'), count = Number(arg('count', '1'));
const base = arg('base'), size = arg('size', '268435456'), bps = arg('bps', '262144'), dest = arg('dest');
if (!url || !password || !base || !dest) { console.error('usage: --url --password --count --base --size --bps --dest'); process.exit(1); }

const signin = await fetch(url + '/api/auth/signin', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password }) });
const cookie = (signin.headers.getSetCookie?.() ?? []).map((c) => c.split(';')[0]).join('; ');
if (!signin.ok || !cookie) { console.error('sign-in failed: ' + signin.status); process.exit(1); }

for (let i = 1; i <= count; i++) {
  const request = {
    v: 1, type: 'job.add', id: crypto.randomUUID().replaceAll('-', ''),
    payload: {
      url: `${base}/slow/active-${i}.bin?size=${size}&bps=${bps}&seed=${i}`, destinationDirectory: dest,
      startMode: 'startNow', source: 'perf', connections: 1, conflictPolicy: 'rename',
    },
  };
  const response = await fetch(url + '/api/ipc', { method: 'POST', headers: { 'Content-Type': 'application/json', Cookie: cookie }, body: JSON.stringify(request) });
  const reply = await response.json().catch(() => ({}));
  if (!response.ok || reply.payload?.accepted !== true) { console.error(`job.add ${i} failed: ${response.status} ${JSON.stringify(reply).slice(0, 300)}`); process.exit(1); }
}
