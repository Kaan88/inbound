#!/usr/bin/env node
// PERF.15, web UI baseline. For each combination of held jobs and active downloads: starts an isolated server through
// scripts/perf-seed.sh (random loopback ports, temp data folder, a random password), drives a headless Chrome with a
// temp profile over the DevTools protocol, and writes one CSV row and a Markdown summary.
//
// Zero dependencies: Node's own WebSocket and fetch speak CDP; Chrome is the installed one (no download).
// Run through scripts/perf-webui.sh (it builds first when needed) or directly:
//
//   node scripts/perf-webui.mjs [--jobs 500,5000] [--active 1,8,20] [--duration 60] [--warmup 10]
//        [--out artifacts/perf] [--results DIR] [--chrome google-chrome-stable] [--keep]
//
// What is measured, per combination (docs/plans/perf-baseline-how.md explains each column):
//   sign-in load:  from the click on "Sign in" to the first row, to the last row, and to two frames after it
//                  (the page is "usable"); bytes of that load by resource type; WebSocket hello and snapshot sizes.
//   steady state:  after --warmup seconds, --duration seconds of Performance.getMetrics deltas (script, task, layout,
//                  style recalculation per second), DOM element count, JS heap (before and after a forced GC), and the
//                  WebSocket messages per second the page received while the downloads ran.
//   reload:        the same load with a session and warm HTTP cache (a returning visitor).
import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync, mkdirSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');
const args = process.argv.slice(2);
const opt = (name, fallback) => { const i = args.indexOf('--' + name); return i >= 0 ? args[i + 1] : fallback; };
const flag = (name) => args.includes('--' + name);
const jobsList = opt('jobs', '500,5000').split(',').map(Number);
const activeList = opt('active', '1,8,20').split(',').map(Number);
const durationS = Number(opt('duration', '60'));
const warmupS = Number(opt('warmup', '10'));
const perfOut = resolve(opt('out', process.env.PERF_OUT ?? join(root, 'artifacts/perf')));
const resultsDir = resolve(opt('results', join(perfOut, 'results')));
const chromeBin = opt('chrome', 'google-chrome-stable');
const stamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
mkdirSync(resultsDir, { recursive: true });
const csvPath = join(resultsDir, `webui-${stamp}.csv`);
const mdPath = join(resultsDir, `webui-${stamp}.md`);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const log = (...m) => console.error('[perf-webui]', ...m);

// ---------------------------------------------------------------------------------------------- CDP
class Cdp {
  constructor(ws) { this.ws = ws; this.id = 0; this.waiting = new Map(); this.handlers = []; ws.addEventListener('message', (e) => this.onMessage(e)); }
  static async connect(url) {
    const ws = new WebSocket(url);
    await new Promise((ok, bad) => { ws.addEventListener('open', ok); ws.addEventListener('error', () => bad(new Error('CDP connect failed'))); });
    return new Cdp(ws);
  }
  onMessage(e) {
    const m = JSON.parse(typeof e.data === 'string' ? e.data : Buffer.from(e.data).toString());
    if (m.id !== undefined) {
      const w = this.waiting.get(m.id); this.waiting.delete(m.id);
      if (w) m.error ? w.bad(new Error(`${w.method}: ${m.error.message}`)) : w.ok(m.result);
    } else for (const h of this.handlers) h(m);
  }
  send(method, params = {}, sessionId) {
    const id = ++this.id;
    return new Promise((ok, bad) => { this.waiting.set(id, { ok, bad, method }); this.ws.send(JSON.stringify({ id, method, params, sessionId })); });
  }
  on(fn) { this.handlers.push(fn); }
  close() { try { this.ws.close(); } catch { /* gone */ } }
}

// ---------------------------------------------------------------------------------------------- page-side helpers
// Runs in every document before its scripts. The row observer stops 3 s after the first row, so it is gone before the
// steady-state window; the socket counter adds two additions per message (it is what gives messages per second).
const PAGE_HOOK = `(() => {
  const st = window.__perf = { rows: 0, firstRowAt: null, lastGrowthAt: null, usableAt: null, longTasks: 0, longTaskMs: 0, maxLongMs: 0, ws: { n: 0, chars: 0 } };
  try { new PerformanceObserver((l) => { for (const e of l.getEntries()) { st.longTasks++; st.longTaskMs += e.duration; if (e.duration > st.maxLongMs) st.maxLongMs = e.duration; } }).observe({ type: 'longtask', buffered: true }); } catch {}
  const Native = window.WebSocket;
  window.WebSocket = function (...a) {
    const s = new Native(...a);
    s.addEventListener('message', (ev) => { st.ws.n++; st.ws.chars += typeof ev.data === 'string' ? ev.data.length : (ev.data.size || 0); });
    return s;
  };
  window.WebSocket.prototype = Native.prototype;
  Object.assign(window.WebSocket, { CONNECTING: 0, OPEN: 1, CLOSING: 2, CLOSED: 3 });
  const check = () => {
    const grid = document.querySelector('[role="grid"]');
    if (!grid) return;
    const n = grid.querySelectorAll('[role="row"]').length;
    if (n !== st.rows) st.lastGrowthAt = performance.now();
    st.rows = n;
    if (n > 0 && st.firstRowAt === null) {
      st.firstRowAt = performance.now();
      requestAnimationFrame(() => requestAnimationFrame(() => { st.usableAt = performance.now(); }));
      setTimeout(() => mo.disconnect(), 3000);
    }
  };
  const mo = new MutationObserver(check);
  mo.observe(document, { childList: true, subtree: true });
})();`;

const TYPE_ORDER = ['Document', 'Script', 'Stylesheet', 'Image', 'Font', 'Fetch', 'XHR', 'Other'];

// ---------------------------------------------------------------------------------------------- one combination
async function runCombo(jobs, active) {
  const name = `webui-${jobs}-${active}`;
  const seed = spawnSync('bash', [join(root, 'scripts/perf-seed.sh'), 'start', '--name', name, '--jobs', String(jobs),
    '--active', String(active), '--target', 'server', '--out', perfOut], { encoding: 'utf8', timeout: 20 * 60_000 });
  if (seed.status !== 0) throw new Error(`seeding ${name} failed: ${seed.stderr.split('\n').filter((l) => !l.includes('bind:')).join(' | ')}`);
  const env = Object.fromEntries(readFileSync(join(perfOut, 'inst', name, 'instance.env'), 'utf8').split('\n')
    .flatMap((l) => l.split(/;\s*/)).map((l) => l.replace(/^export\s+/, '').match(/^(\w+)=(.*)$/)).filter(Boolean).map((m) => [m[1], m[2].replace(/^'|'$/g, '')]));
  const base = env.PERF_URL, password = env.PERF_PASSWORD;
  const profile = mkdtempSync(join(tmpdir(), 'perf-chrome-'));
  let chrome = null, cdp = null;
  const row = { jobs, active };
  try {
    chrome = spawn(chromeBin, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`, '--no-first-run',
      '--no-default-browser-check', '--disable-extensions', '--disable-background-networking', '--disable-sync',
      '--window-size=1600,1000', 'about:blank'], { detached: true, stdio: 'ignore' });
    const portFile = join(profile, 'DevToolsActivePort');
    for (let i = 0; i < 100 && !existsSync(portFile); i++) await sleep(100);
    if (!existsSync(portFile)) throw new Error('Chrome did not start');
    await sleep(200);
    const [port, path] = readFileSync(portFile, 'utf8').trim().split('\n');
    cdp = await Cdp.connect(`ws://127.0.0.1:${port}${path}`);
    const { targetId } = await cdp.send('Target.createTarget', { url: 'about:blank' });
    const { sessionId } = await cdp.send('Target.attachToTarget', { targetId, flatten: true });
    const S = (method, params) => cdp.send(method, params, sessionId);
    await S('Page.enable'); await S('Runtime.enable'); await S('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
    await S('Page.addScriptToEvaluateOnNewDocument', { source: PAGE_HOOK });

    // ---- network capture (load phases only)
    let net = null;
    const startNet = async () => { net = { reqs: new Map(), frames: [], from: Date.now() }; await S('Network.enable', { maxTotalBufferSize: 200e6, maxResourceBufferSize: 100e6 }); };
    const stopNet = async () => { const n = net; net = null; await S('Network.disable'); return n; };
    cdp.on((m) => {
      if (!net || m.sessionId !== sessionId) return;
      const p = m.params;
      if (m.method === 'Network.requestWillBeSent') net.reqs.set(p.requestId, { type: p.type ?? 'Other', url: p.request.url, decoded: 0, encoded: 0 });
      else if (m.method === 'Network.dataReceived') { const r = net.reqs.get(p.requestId); if (r) r.decoded += p.dataLength; }
      else if (m.method === 'Network.loadingFinished') { const r = net.reqs.get(p.requestId); if (r) r.encoded = p.encodedDataLength; }
      else if (m.method === 'Network.webSocketFrameReceived') {
        const d = p.response.payloadData ?? '';
        net.frames.push({ t: Date.now(), bytes: Buffer.byteLength(d), type: (d.slice(0, 200).match(/"type"\s*:\s*"([^"]+)"/) ?? [])[1] ?? '?' });
      }
    });
    const summarize = (n) => {
      const byType = Object.fromEntries(TYPE_ORDER.map((t) => [t, 0]));
      let encoded = 0, decoded = 0;
      for (const r of n.reqs.values()) { encoded += r.encoded; decoded += r.decoded; byType[TYPE_ORDER.includes(r.type) ? r.type : 'Other'] += r.encoded; }
      const wsBytes = n.frames.reduce((a, f) => a + f.bytes, 0);
      return { encoded, decoded, byType, requests: n.reqs.size, wsBytes, frames: n.frames };
    };
    const evalNum = async (expr) => (await S('Runtime.evaluate', { expression: expr, returnByValue: true })).result.value;
    const waitFor = async (expr, what, ms = 180_000) => {
      const end = Date.now() + ms;
      while (Date.now() < end) { try { if (await evalNum(expr)) return; } catch { /* page is navigating */ } await sleep(50); }
      throw new Error('timed out waiting for ' + what);
    };

    // ---- 1. the sign-in page, then the sign-in
    const navT = Date.now();
    await S('Page.navigate', { url: base + '/' });
    await waitFor(`!!document.querySelector('input[type=password]')`, 'the sign-in page');
    row.signinPageMs = Date.now() - navT;
    await S('Runtime.evaluate', { expression: `document.querySelector('input[type=password]').focus()` });
    await S('Input.insertText', { text: password });
    await startNet();
    const t0 = Date.now();
    await S('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r' });
    await S('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 });
    await waitFor(`!!(window.__perf && window.__perf.usableAt !== null)`, 'a usable list');
    const first = await S('Runtime.evaluate', { expression: `JSON.stringify({ origin: performance.timeOrigin, p: window.__perf })`, returnByValue: true }).then((r) => JSON.parse(r.result.value));
    row.rowsRendered = first.p.rows;
    row.firstRowMs = Math.round(first.origin + first.p.firstRowAt - t0);
    row.usableMs = Math.round(first.origin + first.p.usableAt - t0);
    await sleep(3500); // the list's last rows and the network settle
    const grown = await S('Runtime.evaluate', { expression: `JSON.stringify({ origin: performance.timeOrigin, p: window.__perf })`, returnByValue: true }).then((r) => JSON.parse(r.result.value));
    row.rowsRendered = grown.p.rows;
    row.fullListMs = Math.round(grown.origin + grown.p.lastGrowthAt - t0);
    row.longTasks = grown.p.longTasks; row.longTaskMs = Math.round(grown.p.longTaskMs); row.maxLongTaskMs = Math.round(grown.p.maxLongMs);
    const load = summarize(await stopNet());
    row.loadBytesTransferred = load.encoded; row.loadBytesDecoded = load.decoded; row.loadRequests = load.requests;
    for (const t of TYPE_ORDER) row['load' + t + 'Bytes'] = load.byType[t];
    row.wsFramesInLoad = load.frames.length;
    row.wsBytesInLoad = load.wsBytes;
    row.wsFirstFrames = load.frames.slice(0, 4).map((f) => `${f.type}:${f.bytes}`).join(' ');
    row.snapshotBytes = Math.max(0, ...load.frames.filter((f) => /snapshot/i.test(f.type)).map((f) => f.bytes));

    // ---- 2. steady state, with the downloads running
    await sleep(Math.max(0, warmupS * 1000 - 3500));
    await S('Performance.enable', { timeDomain: 'timeTicks' });
    const metrics = async () => Object.fromEntries((await S('Performance.getMetrics')).metrics.map((m) => [m.name, m.value]));
    const wsBefore = await evalNum(`window.__perf.ws.n`), charsBefore = await evalNum(`window.__perf.ws.chars`);
    const m0 = await metrics(); const w0 = Date.now();
    await sleep(durationS * 1000);
    const m1 = await metrics(); const secs = (Date.now() - w0) / 1000;
    const wsAfter = await evalNum(`window.__perf.ws.n`), charsAfter = await evalNum(`window.__perf.ws.chars`);
    const per = (k) => +(((m1[k] - m0[k]) / secs) * 1000).toFixed(2); // ms per second
    row.steadySeconds = +secs.toFixed(1);
    row.scriptMsPerSec = per('ScriptDuration'); row.taskMsPerSec = per('TaskDuration');
    row.layoutMsPerSec = per('LayoutDuration'); row.recalcStyleMsPerSec = per('RecalcStyleDuration');
    row.layoutsPerSec = +((m1.LayoutCount - m0.LayoutCount) / secs).toFixed(2); row.recalcsPerSec = +((m1.RecalcStyleCount - m0.RecalcStyleCount) / secs).toFixed(2);
    row.wsMessagesPerSec = +((wsAfter - wsBefore) / secs).toFixed(2); row.wsKBPerSec = +(((charsAfter - charsBefore) / secs) / 1024).toFixed(2);
    row.domElements = await evalNum(`document.getElementsByTagName('*').length`);
    row.domNodes = m1.Nodes;
    row.jsHeapMB = +(m1.JSHeapUsedSize / 1048576).toFixed(1);
    await S('HeapProfiler.enable'); await S('HeapProfiler.collectGarbage');
    row.jsHeapAfterGcMB = +((await S('Runtime.getHeapUsage')).usedSize / 1048576).toFixed(1);
    row.activeSeen = await evalNum(`[...document.querySelectorAll('[role="grid"] [role="row"]')].filter((r) => /Downloading/.test(r.textContent)).length`);

    // ---- 3. reload with a session and a warm cache
    await startNet();
    const r0 = Date.now();
    await S('Page.reload', { ignoreCache: false });
    await waitFor(`!!(window.__perf && window.__perf.usableAt !== null)`, 'a usable list after reload');
    const again = await S('Runtime.evaluate', { expression: `JSON.stringify({ origin: performance.timeOrigin, p: window.__perf })`, returnByValue: true }).then((r) => JSON.parse(r.result.value));
    row.reloadUsableMs = Math.round(again.origin + again.p.usableAt - r0);
    await sleep(1000);
    const reload = summarize(await stopNet());
    row.reloadBytesTransferred = reload.encoded; row.reloadRequests = reload.requests;
  } finally {
    cdp?.close();
    if (chrome?.pid) { try { process.kill(-chrome.pid, 'SIGTERM'); } catch { /* gone */ } await sleep(500); try { process.kill(-chrome.pid, 'SIGKILL'); } catch { /* gone */ } }
    rmSync(profile, { recursive: true, force: true });
    spawnSync('bash', [join(root, 'scripts/perf-seed.sh'), 'stop', '--name', name, '--out', perfOut, ...(flag('keep') ? [] : ['--purge'])], { stdio: 'ignore' });
  }
  return row;
}

// ---------------------------------------------------------------------------------------------- run and report
const rows = [];
const cleanup = () => { /* every combination cleans up in its own finally */ };
process.on('SIGINT', () => { cleanup(); process.exit(130); });
for (const jobs of jobsList) for (const active of activeList) {
  log(`jobs ${jobs}, active ${active}: seeding, then ${warmupS + durationS} s of measuring`);
  try { rows.push(await runCombo(jobs, active)); } catch (e) { log(`FAILED jobs ${jobs}, active ${active}: ${e.message}`); rows.push({ jobs, active, error: e.message }); }
  writeCsv();
}
writeMarkdown();
log(`wrote ${csvPath} and ${mdPath}`);

function writeCsv() {
  const cols = [...new Set(rows.flatMap((r) => Object.keys(r)))];
  const cell = (v) => (v === undefined ? '' : `"${String(v).replaceAll('"', '""')}"`);
  writeFileSync(csvPath, [cols.join(','), ...rows.map((r) => cols.map((c) => cell(r[c])).join(','))].join('\n') + '\n');
}

function writeMarkdown() {
  const mb = (b) => (b / 1048576).toFixed(2);
  const f = (v, d = 0) => (v === undefined ? '-' : Number(v).toFixed(d));
  const ok = rows.filter((r) => !r.error);
  const lines = [
    `# Web UI baseline (${new Date().toISOString()})`, '',
    `Chrome: ${spawnSync(chromeBin, ['--version'], { encoding: 'utf8' }).stdout.trim()} headless, 1600x1000, temp profile; steady window ${durationS} s after ${warmupS} s.`,
    'Server, Chrome, the seeding and anything else on the machine share its cores: read the numbers only from a quiet machine.', '',
    '## Load (sign-in click to a usable list)', '',
    '| Jobs | Active | Rows | First row ms | Usable ms | Last row ms | Long tasks (n / total ms / max ms) | Transferred MB | Snapshot MB | Reload usable ms | Reload KB |',
    '| ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: |',
    ...ok.map((r) => `| ${r.jobs} | ${r.active} | ${r.rowsRendered} | ${f(r.firstRowMs)} | ${f(r.usableMs)} | ${f(r.fullListMs)} | ${r.longTasks} / ${r.longTaskMs} / ${r.maxLongTaskMs} | ${mb(r.loadBytesTransferred)} | ${mb(r.snapshotBytes)} | ${f(r.reloadUsableMs)} | ${f(r.reloadBytesTransferred / 1024)} |`),
    '', '## Steady state with the downloads running (per second)', '',
    '| Jobs | Active | Script ms/s | Task ms/s | Layout ms/s | Style ms/s | Layouts/s | WS msgs/s | WS KB/s | DOM elements | Heap MB | Heap after GC MB | Downloading rows seen |',
    '| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |',
    ...ok.map((r) => `| ${r.jobs} | ${r.active} | ${f(r.scriptMsPerSec, 1)} | ${f(r.taskMsPerSec, 1)} | ${f(r.layoutMsPerSec, 1)} | ${f(r.recalcStyleMsPerSec, 1)} | ${f(r.layoutsPerSec, 1)} | ${f(r.wsMessagesPerSec, 1)} | ${f(r.wsKBPerSec, 1)} | ${r.domElements} | ${f(r.jsHeapMB, 1)} | ${f(r.jsHeapAfterGcMB, 1)} | ${r.activeSeen} |`),
    '', '## First load by resource type (transferred bytes)', '',
    `| Jobs | Active | ${TYPE_ORDER.join(' | ')} | Decoded total |`, `| ---: | ---: | ${TYPE_ORDER.map(() => '---:').join(' | ')} | ---: |`,
    ...ok.map((r) => `| ${r.jobs} | ${r.active} | ${TYPE_ORDER.map((t) => r['load' + t + 'Bytes']).join(' | ')} | ${r.loadBytesDecoded} |`),
    '', '## First WebSocket frames of the sign-in load (type:bytes)', '',
    ...ok.map((r) => `- ${r.jobs} jobs, ${r.active} active: ${r.wsFirstFrames}`),
  ];
  const failed = rows.filter((r) => r.error);
  if (failed.length) lines.push('', '## Failed', '', ...failed.map((r) => `- ${r.jobs} jobs, ${r.active} active: ${r.error}`));
  writeFileSync(mdPath, lines.join('\n') + '\n');
}
