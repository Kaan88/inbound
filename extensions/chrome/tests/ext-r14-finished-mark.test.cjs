// EXT.R14: the toolbar ring is thicker and easier to see, and a finished download from this browser leaves a mark on
// the icon (a check badge and a count) until the popup is opened, where the finished ones are on top.
// EXT.R15: the "open the popup on a finish" switch exists only in the separate test build.
const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { loadTs } = require('./page-harness.cjs');
const {
  sendRuntimeMessage, answerNative, settle, startDownload, withBackground, capturingChrome, catching, ownPage,
} = require('./harness.cjs');

const status = loadTs('toolbar-status.ts');
const icon = loadTs('toolbar-icon.ts');
const list = loadTs('popup-list.ts');

const JOB = '0f8fad5b-d9cb-469f-a165-70867728950e';
const job = (overrides = {}) => ({ id: JOB, fileName: 'a.zip', source: 'chromeAuto', state: 'downloading', startMode: 'startNow',
  receivedBytes: 25, totalBytes: 100, ...overrides });

// ---- The ring -------------------------------------------------------------------------------------------------

test('EXT.R14: the ring is thick enough at 16 and 32 px, with a halo and a track that read on light and dark toolbars', () => {
  for (const size of icon.ICON_SIZES) {
    const g = icon.ringGeometry(size);
    assert.ok(g.line >= 3, `${size}px: stroke ${g.line}`);
    assert.ok(g.line / size >= 0.18, `${size}px: the stroke is at least 18% of the icon`);
    assert.equal(g.halo, g.line + 2, 'a 1 px halo on each side');
    assert.ok(g.radius + g.halo / 2 <= size / 2 + 1e-9, 'the outer edge stays inside the canvas');
    assert.ok(g.inset >= g.line + 1, 'the icon sits inside the ring');
    assert.ok(size - 2 * g.inset >= 8, `${size}px: the icon stays at least 8 px`);
  }
  assert.equal(icon.ringGeometry(16).line, 3);
  assert.equal(icon.ringGeometry(32).line, 6);
  // The halo is light (it outlines the ring on a dark toolbar) and the track is a mid grey (visible on both).
  assert.match(icon.RING_COLORS.halo, /255, 255, 255/);
  const track = parseInt(icon.RING_COLORS.track.slice(1, 3), 16);
  assert.ok(track > 90 && track < 170);
  // Saturated, distinct status colours.
  assert.equal(new Set([icon.RING_COLORS.downloading, icon.RING_COLORS.paused, icon.RING_COLORS.error]).size, 3);
});

test('EXT.R14: the ring for each state, with and without the finished mark', () => {
  const colors = icon.RING_COLORS;
  assert.deepEqual(icon.ringFor({ kind: 'downloading', progress: 0.5, count: 1 }), { color: colors.downloading, fill: 0.5 });
  assert.deepEqual(icon.ringFor({ kind: 'downloading', progress: 0.5, count: 1 }, 2), { color: colors.downloading, fill: 0.5, check: true });
  assert.deepEqual(icon.ringFor({ kind: 'paused', progress: null, count: 1 }, 1), { color: colors.paused, fill: 1, check: true });
  assert.deepEqual(icon.ringFor({ kind: 'error', progress: null, count: 1 }, 1), { color: colors.error, fill: 1, check: true });
  assert.equal(icon.ringFor(status.IDLE), null);
  assert.deepEqual(icon.ringFor(status.IDLE, 3), { color: colors.finished, fill: 1, check: true });
});

test('EXT.R14: the icon is drawn again when the finished count changes', () => {
  assert.notEqual(status.drawKey(status.IDLE, 0), status.drawKey(status.IDLE, 1));
  assert.notEqual(status.drawKey(status.IDLE, 1), status.drawKey(status.IDLE, 2));
  assert.equal(status.drawKey(status.IDLE, 500), status.drawKey(status.IDLE, 99), 'the count is shown up to 99');
});

// ---- The finished list ----------------------------------------------------------------------------------------

test('EXT.R14: the finished list is newest first, without repeats, and bounded', () => {
  let ids = [];
  for (const id of ['a', 'b', 'c']) ids = status.addFinished(ids, id);
  assert.deepEqual(ids, ['c', 'b', 'a']);
  assert.deepEqual(status.addFinished(ids, 'a'), ['a', 'c', 'b']);
  for (let n = 0; n < 40; n++) ids = status.addFinished(ids, `x${n}`);
  assert.equal(ids.length, status.FINISHED_KEEP);
  assert.deepEqual(status.parseFinished(['a', 5, '', null, 'b']), ['a', 'b']);
  assert.deepEqual(status.parseFinished('nope'), []);
  assert.equal(status.finishedBadge(0), '');
  assert.equal(status.finishedBadge(4), '4');
  assert.equal(status.finishedBadge(250), '99');
  assert.equal(status.finishedTitle(0), null);
  assert.match(status.finishedTitle(1), /a download has finished/);
  assert.match(status.finishedTitle(3), /3 downloads have finished/);
});

test('EXT.R14: the just-finished downloads come first in the popup, newest finish first', () => {
  const rows = ['a', 'b', 'c', 'd'].map((id) => ({ id }));
  assert.deepEqual(list.finishedOnTop(rows, ['c', 'a']).map((row) => row.id), ['c', 'a', 'b', 'd']);
  assert.deepEqual(list.finishedOnTop(rows, undefined).map((row) => row.id), ['a', 'b', 'c', 'd']);
  assert.deepEqual(list.finishedOnTop(rows, ['zzz']).map((row) => row.id), ['a', 'b', 'c', 'd']);
});

// ---- The background -------------------------------------------------------------------------------------------

function withCanvas(run) {
  const saved = { OffscreenCanvas: global.OffscreenCanvas, createImageBitmap: global.createImageBitmap, fetch: global.fetch };
  global.OffscreenCanvas = class {
    constructor(width) { this.width = width; }
    getContext() {
      const size = this.width;
      return new Proxy({ getImageData: () => ({ size }) }, { get: (target, key) => (key in target ? target[key] : () => undefined), set: () => true });
    }
  };
  global.createImageBitmap = async () => ({ bitmap: true });
  global.fetch = async () => ({ blob: async () => ({}) });
  return Promise.resolve(run()).finally(() => Object.assign(global, saved));
}

function iconChrome(settings) {
  const made = capturingChrome(settings);
  const icons = [];
  const titles = [];
  Object.assign(made.harness.chrome.action, {
    async setIcon(details) { icons.push(details); },
    async setTitle(details) { titles.push(details.title); },
  });
  made.harness.chrome.windows.getAll = async () => [{ id: 7, type: 'normal', focused: true }];
  return { ...made, icons, titles };
}

const emit = (harness, payload) => {
  for (const listener of harness.nativeOnMessage.listeners) listener({ type: 'job.event', payload });
};

async function sendOne(harness) {
  const answered = startDownload(harness, { id: 3, url: 'https://file.test/a.zip' });
  await answerNative(harness, 'handoff.request', { accepted: true, jobId: JOB });
  void answered;
}

async function popupJobs(harness, jobs) {
  const reply = sendRuntimeMessage(harness, { type: 'dm.jobs' }, ownPage('popup.html'));
  await answerNative(harness, 'jobs.query', { jobs });
  return reply;
}

test('EXT.R14: a finish marks the icon (check and count) until the popup opens, which lists it first and clears the mark', async () => {
  global.__inboundFlashMs = 1;
  try {
    await withCanvas(async () => {
      const { harness, log, icons, titles } = iconChrome(catching);
      await withBackground(harness, async () => {
        await sendOne(harness);
        emit(harness, { eventType: 'progress', job: job() });
        await settle(() => icons.length > 0 && icons.at(-1).imageData);
        emit(harness, { eventType: 'completed', job: job({ state: 'completed', receivedBytes: 100 }) });
        await settle(() => log.badge.at(-1) === '1');
        for (let i = 0; i < 300 && !(icons.at(-1)?.imageData && titles.at(-1)?.includes('finished')); i++) {
          await new Promise((resolve) => setTimeout(resolve, 4));
        }
        assert.equal(log.badge.at(-1), '1');
        assert.ok(icons.at(-1).imageData, 'the last icon is a drawn ring with the check');
        assert.match(titles.at(-1), /a download has finished/);
        assert.deepEqual(harness.session.finishedIds, [JOB], 'kept for a worker restart');

        // The popup opens: the finished one is named, the mark clears.
        const answer = await popupJobs(harness, [job({ state: 'completed' }), job({ id: 'older', state: 'completed' })]);
        assert.deepEqual(answer.justFinished, [JOB]);
        await settle(() => log.badge.at(-1) === '');
        assert.equal(log.badge.at(-1), '');
        assert.deepEqual(harness.session.finishedIds, []);
        for (let i = 0; i < 100 && !icons.at(-1).path; i++) await new Promise((resolve) => setTimeout(resolve, 4));
        assert.deepEqual(icons.at(-1), { path: { 16: 'icons/icon16.png', 32: 'icons/icon32.png' } });

        // Asked again: nothing new finished.
        const again = await popupJobs(harness, [job({ state: 'completed' })]);
        assert.equal(again.justFinished, undefined);
      });
    });
  } finally { delete global.__inboundFlashMs; }
});

test('EXT.R14: only the popup clears the mark, and "Do nothing" marks nothing', async () => {
  global.__inboundFlashMs = 1;
  try {
    await withCanvas(async () => {
      const kept = iconChrome(catching);
      await withBackground(kept.harness, async () => {
        await sendOne(kept.harness);
        emit(kept.harness, { eventType: 'progress', job: job() });
        emit(kept.harness, { eventType: 'completed', job: job({ state: 'completed' }) });
        await settle(() => kept.log.badge.at(-1) === '1');
        // The options page asking is not "the popup was opened".
        const answer = sendRuntimeMessage(kept.harness, { type: 'dm.jobs' }, ownPage('options.html'));
        await answerNative(kept.harness, 'jobs.query', { jobs: [] });
        assert.equal((await answer).justFinished, undefined);
        assert.deepEqual(kept.harness.session.finishedIds, [JOB]);
      });

      const none = iconChrome({ ...catching, onFinish: 'nothing' });
      await withBackground(none.harness, async () => {
        await sendOne(none.harness);
        emit(none.harness, { eventType: 'progress', job: job() });
        emit(none.harness, { eventType: 'completed', job: job({ state: 'completed' }) });
        await new Promise((resolve) => setTimeout(resolve, 60));
        assert.equal(none.log.badge.at(-1) === '1', false);
        assert.equal(none.harness.session.finishedIds, undefined);
      });
    });
  } finally { delete global.__inboundFlashMs; }
});

test('EXT.R14: a restarted worker still shows what finished (session storage)', async () => {
  await withCanvas(async () => {
    const { harness, log, icons } = iconChrome(catching);
    harness.session.finishedIds = ['one', 'two'];
    await withBackground(harness, async () => {
      await settle(() => log.badge.at(-1) === '2');
      assert.equal(log.badge.at(-1), '2');
      for (let i = 0; i < 100 && !icons.at(-1)?.imageData; i++) await new Promise((resolve) => setTimeout(resolve, 4));
      assert.ok(icons.at(-1).imageData, 'the check mark is drawn again');
    });
  });
});

// ---- EXT.R15: the test build ----------------------------------------------------------------------------------

const SWITCH = 'testOpenPopupOnFinish';

test('EXT.R15: the switch is in the test build only, not in the Chrome or Firefox store builds', () => {
  const root = path.join(__dirname, '..');
  const build = path.join(root, 'scripts', 'build.mjs');
  const out = fs.mkdtempSync(path.join(os.tmpdir(), 'inbound-r15-'));
  try {
    const dirs = { chrome: [], firefox: ['--target', 'firefox'], test: ['--test-popup'] };
    for (const [name, args] of Object.entries(dirs)) {
      execFileSync(process.execPath, [build, ...args, '--outdir', path.join(out, name)], { cwd: root, stdio: 'pipe' });
    }
    const bundle = (name) => ['background.js', 'options.js', 'popup.js'].map((file) => fs.readFileSync(path.join(out, name, file), 'utf8')).join('\n');
    for (const store of ['chrome', 'firefox']) {
      const text = bundle(store);
      assert.equal(text.includes(SWITCH), false, `${store}: the option's key`);
      assert.equal(text.includes('Open the popup when a download from this browser finishes'), false, `${store}: the option's text`);
      assert.equal(text.includes('openPopupForTest'), false, `${store}: the code path`);
      assert.equal(JSON.parse(fs.readFileSync(path.join(out, store, 'manifest.json'), 'utf8')).name.includes('popup test'), false);
    }
    const test = bundle('test');
    assert.ok(test.includes(SWITCH));
    assert.ok(test.includes('Open the popup when a download from this browser finishes'));
    assert.match(JSON.parse(fs.readFileSync(path.join(out, 'test', 'manifest.json'), 'utf8')).name, /\(popup test\)$/);
  } finally { fs.rmSync(out, { recursive: true, force: true }); }
});

test('EXT.R15: the test switch opens the popup only when it is on, a window is focused and the ask popup is not up', async () => {
  const testPopup = loadTs('test-popup.ts');
  const old = global.chrome;
  try {
    const opened = [];
    const values = {};
    global.chrome = {
      storage: { local: { async get(key) { return { [key]: values[key] }; }, async set(update) { Object.assign(values, update); } } },
      action: { async openPopup(options) { opened.push(options); } },
    };
    const deps = (over = {}) => ({ focusedWindow: async () => ({ id: 7 }), askPopupBusy: () => false, ...over });
    assert.equal(await testPopup.openPopupForTest(deps()), false, 'off by default');
    values[SWITCH] = true;
    assert.equal(await testPopup.openPopupForTest(deps({ askPopupBusy: () => true })), false, 'the ask popup is up');
    assert.equal(await testPopup.openPopupForTest(deps({ focusedWindow: async () => undefined })), false, 'no focused window');
    assert.deepEqual(opened, []);
    assert.equal(await testPopup.openPopupForTest(deps()), true);
    assert.deepEqual(opened, [{ windowId: 7 }]);
    global.chrome.action.openPopup = async () => { throw new Error('Not allowed.'); };
    assert.equal(await testPopup.openPopupForTest(deps()), false, 'a refusal falls back silently');
  } finally { global.chrome = old; }
});
