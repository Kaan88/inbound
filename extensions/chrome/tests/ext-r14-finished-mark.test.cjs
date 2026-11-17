// EXT.R14: the toolbar ring is thicker and easier to see, and a finished download from this browser leaves a mark on
// the icon (a full green ring when idle, and the count as badge text) until the popup is opened, where the finished ones are on top.
// EXT.R15: "Open the popup" on a finish (Options > When a download finishes; off by default).
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
  assert.equal(new Set([icon.RING_COLORS.downloading, icon.RING_COLORS.paused, icon.RING_COLORS.error, icon.RING_COLORS.finished]).size, 4);
});

test('EXT.R14: the ring for each state, with and without the finished mark', () => {
  const colors = icon.RING_COLORS;
  assert.deepEqual(icon.ringFor({ kind: 'downloading', progress: 0.5, count: 1 }), { color: colors.downloading, fill: 0.5 });
  assert.deepEqual(icon.ringFor({ kind: 'downloading', progress: 0.5, count: 1 }, 2), { color: colors.downloading, fill: 0.5 }, 'no check while active: the count is the badge text');
  assert.deepEqual(icon.ringFor({ kind: 'paused', progress: null, count: 1 }, 1), { color: colors.paused, fill: 1 });
  assert.deepEqual(icon.ringFor({ kind: 'error', progress: null, count: 1 }, 1), { color: colors.error, fill: 1 });
  assert.equal(icon.ringFor(status.IDLE), null);
  assert.deepEqual(icon.ringFor(status.IDLE, 3), { color: colors.finished, fill: 1 }, 'idle: a full blue ring, no check drawn into the icon');
});

test('final review: the finished ring is not the progress green and reads on light and dark toolbars', () => {
  const colors = icon.RING_COLORS;
  assert.notEqual(colors.finished, colors.downloading);
  const lum = (hex) => {
    const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255).map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
  };
  const contrast = (a, b) => (Math.max(lum(a), lum(b)) + 0.05) / (Math.min(lum(a), lum(b)) + 0.05);
  for (const toolbar of ['#ffffff', '#f1f3f4', '#35363a', '#202124']) {
    assert.ok(contrast(colors.finished, toolbar) >= 3, `finished on ${toolbar}: ${contrast(colors.finished, toolbar).toFixed(2)}`);
  }
  // Clearly apart from the progress arc: a different hue, not a shade of the same green.
  const hue = (hex) => {
    const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
    const max = Math.max(r, g, b); const min = Math.min(r, g, b); const d = max - min;
    const h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
    return (h * 60 + 360) % 360;
  };
  assert.ok(Math.abs(hue(colors.finished) - hue(colors.downloading)) >= 60);
});

test('final review: while sending is off or paused the tooltip says so first, then what finished', () => {
  assert.equal(status.offTitle(false, false, 3), null);
  assert.equal(status.offTitle(true, false, 0), 'Inbound Download Manager: automatic sending is off');
  assert.equal(status.offTitle(true, false, 1), 'Inbound Download Manager: automatic sending is off. A download has finished. Open the popup to see it.');
  assert.equal(status.offTitle(false, true, 3), 'Inbound Download Manager: sending is paused. 3 downloads have finished. Open the popup to see them.');
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
        assert.ok(icons.at(-1).imageData, 'the last icon is the drawn full ring');
        assert.match(titles.at(-1), /a.zip finished/);
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
      assert.ok(icons.at(-1).imageData, 'the full ring is drawn again');
    });
  });
});

// ---- EXT.R15: "Open the popup" on finish (a store setting, off by default) ---------------------------------------

const rules = loadTs('capture-rules.ts');

test('EXT.R15: the popup is off by default (On finish defaults to flash), and the test-build switch is gone', () => {
  assert.equal(rules.DEFAULT_SETTINGS.onFinish, 'flash');
  assert.equal(rules.migrateSettings({}).settings.onFinish, 'flash');
  const root = path.join(__dirname, '..');
  assert.equal(fs.existsSync(path.join(root, 'src', 'test-popup.ts')), false);
  assert.equal(JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8')).scripts['build:test-popup'], undefined);
  const out = fs.mkdtempSync(path.join(os.tmpdir(), 'inbound-r15-'));
  try {
    for (const [name, args] of [['chrome', []], ['firefox', ['--target', 'firefox']]]) {
      execFileSync(process.execPath, [path.join(root, 'scripts', 'build.mjs'), ...args, '--outdir', path.join(out, name)], { cwd: root, stdio: 'pipe' });
      const text = ['background.js', 'options.js'].map((file) => fs.readFileSync(path.join(out, name, file), 'utf8')).join(' ');
      assert.equal(text.includes('testOpenPopupOnFinish'), false, name);
      assert.equal(text.includes('__TEST_POPUP__'), false, name);
      assert.equal(JSON.parse(fs.readFileSync(path.join(out, name, 'manifest.json'), 'utf8')).name.includes('popup test'), false);
    }
  } finally { fs.rmSync(out, { recursive: true, force: true }); }
});

/** One finish with "Open the popup" chosen; tweak configures the harness first and may return a hook run before the finish. */
async function finishWithPopupChoice(tweak, onFinish = 'popup') {
  global.__inboundFlashMs = 1;
  try {
    let result;
    await withCanvas(async () => {
      const made = iconChrome({ ...catching, onFinish });
      const popups = [];
      made.harness.chrome.action.openPopup = async (options) => { popups.push(options); };
      const beforeFinish = tweak?.(made.harness, popups);
      await withBackground(made.harness, async () => {
        await sendOne(made.harness);
        emit(made.harness, { eventType: 'progress', job: job() });
        await settle(() => made.icons.length > 0 && made.icons.at(-1).imageData);
        const before = made.icons.length;
        beforeFinish?.();
        emit(made.harness, { eventType: 'completed', job: job({ state: 'completed' }) });
        await new Promise((resolve) => setTimeout(resolve, 80));
        for (let waited = 0; waited < 2000 && made.icons.length - before < 2 && made.icons.at(-1)?.imageData === undefined; waited += 20) {
          await new Promise((resolve) => setTimeout(resolve, 20));
        }
        await new Promise((resolve) => setTimeout(resolve, 80));
        result = { popups, badge: made.log.badge.at(-1), flashed: made.icons.length - before > 1 };
      });
    });
    return result;
  } finally { delete global.__inboundFlashMs; }
}

test('EXT.R15: "Open the popup" opens it in the focused window and does not flash', async () => {
  const { popups, flashed } = await finishWithPopupChoice();
  assert.deepEqual(popups, [{ windowId: 7 }]);
  assert.equal(flashed, false);
});

test('EXT.R15: "Flash the icon" (the default) never opens the popup', async () => {
  const { popups, flashed } = await finishWithPopupChoice(undefined, 'flash');
  assert.deepEqual(popups, []);
  assert.equal(flashed, true);
});

test('EXT.R15: no focused normal window: the popup is not opened; the finished mark and the flash stand in', async () => {
  const { popups, badge, flashed } = await finishWithPopupChoice((harness) => {
    harness.chrome.windows.getAll = async () => [{ id: 7, type: 'normal', focused: false }, { id: 8, type: 'popup', focused: true }];
  });
  assert.deepEqual(popups, []);
  assert.equal(badge, '1', 'the finished mark is kept');
  assert.equal(flashed, true);
});

test('EXT.R15: where openPopup is missing (older Chrome, Firefox) or throws, the mark and the flash stand in, without an error', async () => {
  const missing = await finishWithPopupChoice((harness) => { delete harness.chrome.action.openPopup; });
  assert.equal(missing.badge, '1');
  assert.equal(missing.flashed, true);
  const refused = await finishWithPopupChoice((harness, popups) => {
    harness.chrome.action.openPopup = async (options) => { popups.push(options); throw new Error('Not allowed without a click.'); };
  });
  assert.equal(refused.popups.length, 1);
  assert.equal(refused.badge, '1');
  assert.equal(refused.flashed, true);
});

test('EXT.R15: never over the ask-where-to-save popup: while it is open, no second openPopup; the mark and the flash stand in', async () => {
  const { popups, badge, flashed } = await finishWithPopupChoice((harness) => () => {
    const noop = { addListener() {}, removeListener() {}, hasListener: () => false };
    const port = { name: 'dm.ask', sender: { id: 'test-extension', url: 'chrome-extension://test/popup.html' },
      onMessage: noop, onDisconnect: noop, postMessage() {}, disconnect() {} };
    for (const listener of harness.runtimeOnConnect.listeners) listener(port);
  });
  assert.deepEqual(popups, []);
  assert.equal(badge, '1');
  assert.equal(flashed, true);
});

// ---- EXT.R16 -----------------------------------------------------------------------------------------------------

test('EXT.R16: the tooltip names the newest finished file', () => {
  assert.match(status.finishedTitle(1, 'ubuntu.iso'), /ubuntu\.iso finished/);
  assert.match(status.finishedTitle(3, 'ubuntu.iso'), /ubuntu\.iso and 2 more finished/);
  assert.match(status.finishedTitle(2), /2 downloads have finished/, 'no name: the old words');
  assert.deepEqual(status.keepNames({ a: 'a.zip', b: 'b.zip' }, ['b']), { b: 'b.zip' });
  assert.deepEqual(status.parseFinishedNames({ a: 'x', b: 3, c: '' }), { a: 'x' });
});

test('EXT.R16: a "Finished just now" divider above the promoted rows, and cleared finished rows are not drawn', () => {
  const { installDom } = require('./dom-harness.cjs');
  const page = installDom('popup.html');
  try {
    const { renderPopupRows, withoutCleared } = page.load('popup-list.ts');
    const el = page.document.getElementById('jobs');
    const jobs = [
      { id: 'a', fileName: 'a.zip', state: 'downloading', receivedBytes: 1, totalBytes: 10 },
      { id: 'b', fileName: 'b.zip', state: 'completed', receivedBytes: 10, totalBytes: 10 },
      { id: 'c', fileName: 'c.zip', state: 'completed', receivedBytes: 10, totalBytes: 10 },
    ];
    const handlers = { onAction() {}, onOpen() {} };
    const view = { paired: false, serverName: '', groupsOk: {}, finishedFirst: ['c'] };
    assert.equal(renderPopupRows(el, jobs, view, handlers), 3);
    const texts = [...el.children].map((child) => child.classList.contains('group-row') ? `#${child.textContent}` : child.textContent);
    assert.equal(texts[0], '#Finished just now');
    assert.match(texts[1], /c\.zip/);
    assert.equal(texts[2], '#Earlier');
    renderPopupRows(el, jobs, { ...view, finishedFirst: [] }, handlers);
    assert.equal(el.querySelectorAll('.finished-divider').length, 0, 'no promoted row: no divider');
    assert.deepEqual(withoutCleared(jobs, new Set(['b', 'a'])).map((job) => job.id), ['a', 'c'], 'only finished ones leave');
    assert.equal(renderPopupRows(el, jobs, { ...view, hiddenIds: new Set(['b', 'c']) }, handlers), 1);
  } finally { page.restore(); }
});

/** One finish with the given choice; then the notification's buttons (or its body) are clicked. */
async function finishWithNotice({ refuseButtons = false, onFinish = 'notify' } = {}) {
  global.__inboundFlashMs = 1;
  try {
    let result;
    await withCanvas(async () => {
      const made = iconChrome({ ...catching, onFinish });
      const notes = [];
      const clicks = { button: [], body: [] };
      made.harness.chrome.notifications.create = (id, options) => {
        if (typeof id !== 'string') return;
        notes.push({ id, options });
        if (refuseButtons && options.buttons) throw new Error('Property buttons is unsupported.');
      };
      made.harness.chrome.notifications.onButtonClicked = { addListener: (fn) => clicks.button.push(fn) };
      made.harness.chrome.notifications.onClicked = { addListener: (fn) => clicks.body.push(fn) };
      const popups = [];
      made.harness.chrome.action.openPopup = async (options) => { popups.push(options); };
      await withBackground(made.harness, async () => {
        await sendOne(made.harness);
        emit(made.harness, { eventType: 'progress', job: job() });
        await settle(() => made.icons.length > 0 && made.icons.at(-1).imageData);
        const before = made.icons.length;
        emit(made.harness, { eventType: 'completed', job: job({ state: 'completed' }) });
        await new Promise((resolve) => setTimeout(resolve, 150));
        result = { notes, badge: made.log.badge.at(-1), flashed: made.icons.length - before > 1, popups, title: made.titles.at(-1), sent: [] };
        const first = notes[0];
        if (!first) return;
        const click = async (fire, type) => {
          fire();
          const request = await answerNative(made.harness, type, { ok: true });
          result.sent.push({ type: request.type, jobId: request.payload.jobId });
        };
        if (!refuseButtons) {
          await click(() => clicks.button[0](first.id, 0), 'job.open');
          await click(() => clicks.button[0](first.id, 1), 'job.reveal');
        } else {
          await click(() => clicks.body[0](first.id), 'job.reveal');
        }
      });
    });
    return result;
  } finally { delete global.__inboundFlashMs; }
}

test('EXT.R16: "Show a notification" shows Open and Show in folder, runs them on the app, and keeps the finished mark', async () => {
  const made = await finishWithNotice();
  assert.equal(made.notes.length, 1);
  assert.equal(made.notes[0].id, `inbound-finished:${JOB}`);
  assert.equal(made.notes[0].options.message, 'a.zip');
  assert.deepEqual(made.notes[0].options.buttons.map((button) => button.title), ['Open', 'Show in folder']);
  assert.equal(made.badge, '1');
  assert.equal(made.flashed, false);
  assert.deepEqual(made.popups, []);
  assert.match(made.title, /a\.zip finished/);
  assert.deepEqual(made.sent, [{ type: 'job.open', jobId: JOB }, { type: 'job.reveal', jobId: JOB }]);
});

test('EXT.R16: where the browser refuses buttons (Firefox) the notification is shown without them; a click shows the folder', async () => {
  const made = await finishWithNotice({ refuseButtons: true });
  assert.equal(made.notes.length, 2);
  assert.equal(made.notes.at(-1).options.buttons, undefined);
  assert.deepEqual(made.sent, [{ type: 'job.reveal', jobId: JOB }]);
});

test('EXT.R16: "Flash the icon" shows no notification', async () => {
  const made = await finishWithNotice({ onFinish: 'flash' });
  assert.equal(made.notes.length, 0);
});
