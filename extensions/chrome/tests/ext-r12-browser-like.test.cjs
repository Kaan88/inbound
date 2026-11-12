// EXT.R12: the extension works like the browser's own downloads. The browser keeps the focus (no app windows), the
// toolbar icon shows the progress of downloads sent from the browser, a finish flashes the icon or opens the popup,
// "Show in app" opens the app at one download, and the right-click item sends straight to the app.
const assert = require('node:assert/strict');
const test = require('node:test');
const { installDom, flush } = require('./dom-harness.cjs');
const { loadTs } = require('./page-harness.cjs');
const {
  sendRuntimeMessage, answerNative, settle, startDownload, withBackground, capturingChrome, catching, ownPage,
} = require('./harness.cjs');

const status = loadTs('toolbar-status.ts');
const icon = loadTs('toolbar-icon.ts');
const rules = loadTs('capture-rules.ts');

const JOB = '0f8fad5b-d9cb-469f-a165-70867728950e';
const job = (overrides = {}) => ({ id: JOB, fileName: 'a.zip', source: 'chromeAuto', state: 'downloading', startMode: 'startNow',
  receivedBytes: 25, totalBytes: 100, ...overrides });

// ---- The icon's rules ------------------------------------------------------------------------------------------

test('EXT.R12: only downloads sent from the browser are followed', () => {
  assert.equal(status.sentFromBrowser(job()), true);
  assert.equal(status.sentFromBrowser(job({ source: 'chromeContextMenu' })), true);
  for (const source of ['cli', 'api', 'clipboard', 'batch', 'remote', undefined]) {
    assert.equal(status.sentFromBrowser(job({ source })), false, String(source));
  }
});

test("Review 3 N7: only this browser's downloads are followed, not another browser's sent to the same app", () => {
  const ours = 'ext-ours';
  assert.equal(status.sentFromBrowser(job({ ownerExtensionId: ours }), ours), true);
  assert.equal(status.sentFromBrowser(job({ ownerExtensionId: `web:${ours}` }), ours), true, "the Host's prefix for a web caller");
  assert.equal(status.sentFromBrowser(job({ ownerExtensionId: 'ext-firefox' }), ours), false);
  assert.equal(status.sentFromBrowser(job({ ownerExtensionId: 'web:ext-firefox' }), ours), false);
  // An older app that does not say whose it is: followed, as before.
  assert.equal(status.sentFromBrowser(job(), ours), true);

  const watch = new status.DownloadWatch(ours);
  watch.update(job({ id: 'other', ownerExtensionId: 'ext-firefox' }));
  watch.take([job({ id: 'other2', ownerExtensionId: 'ext-edge' })]);
  assert.equal(watch.size, 0);
  watch.update(job({ id: 'mine', ownerExtensionId: ours }));
  assert.equal(watch.state().kind, 'downloading');
});

test('EXT.R12: running means transferring, about to, retrying or checking; queued only when it was to start now', () => {
  for (const state of ['connecting', 'downloading', 'retryWaiting', 'verifying']) assert.equal(status.isRunning(job({ state })), true, state);
  assert.equal(status.isRunning(job({ state: 'queued', startMode: 'startNow' })), true);
  assert.equal(status.isRunning(job({ state: 'queued', startMode: 'later' })), false);
  assert.equal(status.isRunning(job({ state: 'queued', startMode: 'queue' })), false);
  for (const state of ['paused', 'failed', 'completed', 'cancelled']) assert.equal(status.isRunning(job({ state })), false, state);
});

test('EXT.R12: the icon shows downloading first, then a failure, then a pause, else nothing', () => {
  assert.deepEqual(status.iconState([]), { kind: 'idle', progress: null, count: 0 });
  const two = [job({ id: 'a', receivedBytes: 25, totalBytes: 100 }), job({ id: 'b', receivedBytes: 75, totalBytes: 100 })];
  assert.deepEqual(status.iconState(two), { kind: 'downloading', progress: 0.5, count: 2 });
  // A size not known: the ring can't say how far.
  assert.equal(status.iconState([job({ totalBytes: undefined })]).progress, null);
  assert.equal(status.iconState([job(), job({ id: 'f', state: 'failed' })]).kind, 'downloading');
  assert.deepEqual(status.iconState([job({ id: 'f', state: 'failed' }), job({ id: 'p', state: 'paused' })]),
    { kind: 'error', progress: null, count: 1 });
  assert.deepEqual(status.iconState([job({ state: 'paused', receivedBytes: 40 })]), { kind: 'paused', progress: 0.4, count: 1 });
  // A download for later waits for the user: nothing to show.
  assert.equal(status.iconState([job({ state: 'queued', startMode: 'later' })]).kind, 'idle');
});

test("EXT.R12: when the app's events stop, running downloads leave the watch; failures and pauses stay", () => {
  const watch = new status.DownloadWatch();
  watch.update(job({ id: 'r' }));
  watch.update(job({ id: 'f', state: 'failed' }));
  watch.update(job({ id: 'p', state: 'paused' }));
  watch.forgetRunning();
  assert.equal(watch.size, 2);
  assert.equal(watch.state().kind, 'error');
});

test('EXT.R12: the icon is drawn again only when its state or ring step changes', () => {
  const at = (received) => status.drawKey(status.iconState([job({ receivedBytes: received })]));
  assert.equal(at(25), at(26), 'one percent more draws nothing new');
  assert.notEqual(at(25), at(30));
  assert.equal(status.RING_STEPS, 24);
  assert.notEqual(status.drawKey(status.iconState([job({ state: 'paused' })])), at(25));
  assert.equal(status.drawKey(status.IDLE), 'idle:-');
});

test('EXT.R12: the tooltip says what the icon shows, in plain words', () => {
  assert.equal(status.iconTitle(status.IDLE), null);
  assert.equal(status.iconTitle({ kind: 'downloading', progress: 0.42, count: 1 }), 'Inbound Download Manager: 1 download from the browser, 42%');
  assert.equal(status.iconTitle({ kind: 'downloading', progress: null, count: 3 }), 'Inbound Download Manager: 3 downloads from the browser');
  assert.equal(status.iconTitle({ kind: 'paused', progress: 0.1, count: 1 }), 'Inbound Download Manager: a download is paused');
  assert.match(status.iconTitle({ kind: 'error', progress: null, count: 2 }), /2 downloads failed\. Open the popup/);
});

test('EXT.R12: the ring is green with progress, orange when paused, full red on a failure, and none when idle', () => {
  assert.deepEqual(icon.ringFor({ kind: 'downloading', progress: 0.5, count: 1 }), { color: icon.RING_COLORS.downloading, fill: 0.5 });
  assert.deepEqual(icon.ringFor({ kind: 'downloading', progress: null, count: 1 }), { color: icon.RING_COLORS.downloading, fill: 0.25 });
  assert.deepEqual(icon.ringFor({ kind: 'paused', progress: 0.3, count: 1 }), { color: icon.RING_COLORS.paused, fill: 0.3 });
  assert.deepEqual(icon.ringFor({ kind: 'error', progress: null, count: 1 }), { color: icon.RING_COLORS.error, fill: 1 });
  assert.equal(icon.ringFor(status.IDLE), null);
  assert.deepEqual(icon.DEFAULT_ICON_PATHS, { 16: 'icons/icon16.png', 32: 'icons/icon32.png' });
});

test('EXT.R12: the watch keeps a failure or pause until the popup is opened, and reports a finish once', () => {
  const watch = new status.DownloadWatch();
  // Not from the browser: never followed, never "finished".
  assert.deepEqual(watch.update(job({ id: 'cli', source: 'cli' })), { finished: false });
  assert.equal(watch.size, 0);
  watch.update(job());
  assert.equal(watch.state().kind, 'downloading');
  assert.deepEqual(watch.update(job({ state: 'completed', receivedBytes: 100 })), { finished: true });
  assert.equal(watch.state().kind, 'idle');
  // A finish of a download that was never seen running is no news.
  assert.deepEqual(watch.update(job({ state: 'completed' })), { finished: false });

  watch.update(job({ id: 'f', state: 'failed' }));
  watch.update(job({ id: 'p', state: 'paused' }));
  watch.update(job({ id: 'r' }));
  watch.acknowledge();
  assert.equal(watch.size, 1, 'the running one stays');
  assert.equal(watch.state().kind, 'downloading');
  watch.update(job({ id: 'r' }), 'removed');
  assert.equal(watch.state().kind, 'idle');
  watch.update(job({ id: 'c' }));
  watch.update(job({ id: 'c', state: 'cancelled' }));
  assert.equal(watch.size, 0);

  // The popup's list brings in running browser downloads (a restarted worker), never others.
  watch.take([job({ id: 'x' }), job({ id: 'y', state: 'paused' }), job({ id: 'z', source: 'cli' })]);
  assert.equal(watch.size, 1);
});

// ---- Settings --------------------------------------------------------------------------------------------------

test('EXT.R12: by default the app opens no window for browser downloads, and a finish flashes the icon', () => {
  assert.equal(rules.DEFAULT_SETTINGS.openAppWindows, false);
  assert.equal(rules.DEFAULT_SETTINGS.onFinish, 'flash');
  const { settings } = rules.migrateSettings({ rulesVersion: 2, openAppWindows: 'yes', onFinish: 'beep' });
  assert.equal(settings.openAppWindows, false);
  assert.equal(settings.onFinish, 'flash');
  for (const onFinish of ['popup', 'nothing', 'flash']) {
    assert.equal(rules.migrateSettings({ rulesVersion: 2, onFinish }).settings.onFinish, onFinish);
  }
  const changed = rules.applySettingChanges(rules.DEFAULT_SETTINGS, { openAppWindows: true, onFinish: 'popup' });
  assert.equal(changed.openAppWindows, true);
  assert.equal(changed.onFinish, 'popup');
});

test('EXT.R12: Options sets the app windows and the finish action, and sends only those changes', async () => {
  const data = require('./fixtures/screens-data.json');
  const saves = [];
  const page = installDom('options.html', { handlers: {
    'dm.status': async () => ({ ok: true, settings: { ...data.settings }, connected: true, server: null, extensionVersion: '0.6.3' }),
    'dm.saveSettings': async (message) => { saves.push(message); return { ok: true, settings: { ...data.settings, ...message.changes } }; },
  } });
  page.load('options.ts');
  await page.ready();
  await flush(10);
  try {
    const doc = page.document;
    assert.equal(doc.getElementById('openAppWindows').checked, false);
    assert.equal(doc.getElementById('onFinish').value, 'flash');
    assert.deepEqual([...doc.getElementById('onFinish').options].map((option) => option.textContent),
      ['Flash the icon', 'Open the popup', 'Do nothing']);
    doc.getElementById('openAppWindows').checked = true;
    doc.getElementById('openAppWindows').dispatchEvent(new page.window.Event('change', { bubbles: true }));
    doc.getElementById('onFinish').value = 'popup';
    doc.getElementById('onFinish').dispatchEvent(new page.window.Event('change', { bubbles: true }));
    doc.getElementById('apply').click();
    await flush();
    assert.deepEqual(saves[0].changes, { openAppWindows: true, onFinish: 'popup' });
  } finally { page.restore(); }
});

// ---- The background ---------------------------------------------------------------------------------------------

/** A capturing harness whose toolbar icon calls are recorded. */
function iconChrome(settings) {
  const made = capturingChrome(settings);
  const icons = [];
  const titles = [];
  const popups = [];
  Object.assign(made.harness.chrome.action, {
    async setIcon(details) { icons.push(details); },
    async setTitle(details) { titles.push(details.title); },
    async openPopup(options) { popups.push(options); },
  });
  made.harness.chrome.windows.getAll = async () => [{ id: 7, type: 'normal', focused: true }];
  return { ...made, icons, titles, popups };
}

/** A fake OffscreenCanvas, so the ring is "drawn" under Node. */
function withCanvas(run) {
  const saved = { OffscreenCanvas: global.OffscreenCanvas, createImageBitmap: global.createImageBitmap, fetch: global.fetch };
  global.OffscreenCanvas = class {
    constructor(width) { this.width = width; }
    getContext() {
      const size = this.width;
      return { drawImage() {}, beginPath() {}, arc() {}, stroke() {}, getImageData: () => ({ size }) };
    }
  };
  global.createImageBitmap = async () => ({ bitmap: true });
  global.fetch = async () => ({ blob: async () => ({}) });
  return Promise.resolve(run()).finally(() => Object.assign(global, saved));
}

function emit(harness, payload) {
  for (const listener of harness.nativeOnMessage.listeners) listener({ type: 'job.event', payload });
}

async function sendOne(harness) {
  // A caught download opens the shared connection, on which the app's job events arrive.
  const answered = startDownload(harness, { id: 3, url: 'https://file.test/a.zip' });
  const request = await answerNative(harness, 'handoff.request', { accepted: true, jobId: JOB });
  void answered;
  return request;
}

test('EXT.R12: every handoff asks the app to keep its windows closed, unless Settings asks for them', async () => {
  for (const [openAppWindows, quiet] of [[false, true], [true, false]]) {
    const { harness } = capturingChrome({ ...catching, openAppWindows });
    await withBackground(harness, async () => {
      const request = await sendOne(harness);
      assert.equal(request.payload.quietWindows, quiet);
    });
  }
});

test('EXT.R12: the icon follows the browser\'s downloads from the app\'s events, and is drawn only on a change', async () => {
  await withCanvas(async () => {
    const { harness, icons, titles } = iconChrome(catching);
    await withBackground(harness, async () => {
      await sendOne(harness);
      const drawnBefore = icons.length;
      emit(harness, { eventType: 'progress', job: job({ receivedBytes: 25 }) });
      await settle(() => icons.length > drawnBefore);
      assert.equal(icons.length, drawnBefore + 1);
      assert.deepEqual(Object.keys(icons.at(-1).imageData), ['16', '32']);
      assert.match(titles.at(-1), /1 download from the browser, 25%/);
      // The same ring step: nothing drawn.
      emit(harness, { eventType: 'progress', job: job({ receivedBytes: 26 }) });
      await new Promise((resolve) => setTimeout(resolve, 20));
      assert.equal(icons.length, drawnBefore + 1);
      // Another source's download: not followed.
      emit(harness, { eventType: 'progress', job: job({ id: 'other', source: 'cli', receivedBytes: 90 }) });
      await new Promise((resolve) => setTimeout(resolve, 20));
      assert.equal(icons.length, drawnBefore + 1);

      // Paused, then seen in the popup: the plain icon again.
      emit(harness, { eventType: 'paused', job: job({ state: 'paused' }) });
      await settle(() => icons.length > drawnBefore + 1);
      assert.match(titles.at(-1), /a download is paused/);
      const jobs = sendRuntimeMessage(harness, { type: 'dm.jobs' }, ownPage('popup.html'));
      await answerNative(harness, 'jobs.query', { jobs: [job({ state: 'paused' })] });
      await jobs;
      await settle(() => icons.at(-1).path);
      assert.deepEqual(icons.at(-1), { path: { 16: 'icons/icon16.png', 32: 'icons/icon32.png' } });
    });
  });
});

test('EXT.R12: a new worker draws the plain icon once, so a ring left by the old worker goes', async () => {
  const { harness, icons } = iconChrome(catching);
  await withBackground(harness, async () => {
    await settle(() => icons.length > 0);
    assert.deepEqual(icons, [{ path: { 16: 'icons/icon16.png', 32: 'icons/icon32.png' } }]);
  });
});

test("EXT.R12: the ring goes when the app's connection closes or the app says it is exiting", async () => {
  await withCanvas(async () => {
    for (const how of ['disconnect', 'exiting']) {
      const { harness, icons, titles } = iconChrome(catching);
      await withBackground(harness, async () => {
        await sendOne(harness);
        emit(harness, { eventType: 'progress', job: job({ receivedBytes: 25 }) });
        await settle(() => icons.length > 0 && icons.at(-1).imageData);
        if (how === 'disconnect') {
          for (const listener of harness.nativePorts[0].onDisconnect.listeners) listener();
        } else {
          for (const listener of harness.nativeOnMessage.listeners) listener({ type: 'host.exiting', payload: {} });
        }
        await settle(() => icons.at(-1).path);
        assert.deepEqual(icons.at(-1), { path: { 16: 'icons/icon16.png', 32: 'icons/icon32.png' } }, how);
        assert.doesNotMatch(titles.at(-1) ?? '', /from the browser/, how);
      });
    }
  });
});

test('EXT.R12: a right-click that wakes the worker uses the saved "Ask where to save", not the defaults', async () => {
  const { harness, log } = capturingChrome({ ...catching, destinationMode: 'ask' });
  // The settings are slow to read, as on a worker that the click itself woke.
  let release;
  const gate = new Promise((resolve) => { release = resolve; });
  const read = harness.chrome.storage.local.get.bind(harness.chrome.storage.local);
  harness.chrome.storage.local.get = async (key) => { if (key === 'settings') await gate; return read(key); };
  await withBackground(harness, async () => {
    harness.contextMenuClicked.listeners[0]({ linkUrl: 'https://file.test/report.pdf', pageUrl: 'https://file.test/' }, { id: 4, url: 'https://file.test/' });
    await new Promise((resolve) => setTimeout(resolve, 20));
    release();
    await settle(() => log.windows.length > 0);
    assert.equal(harness.nativeMessages.filter((message) => message.type === 'handoff.request').length, 0);
  });
});

test('EXT.R12: a finish flashes the icon a few times and then stops', async () => {
  global.__inboundFlashMs = 1;
  try {
    await withCanvas(async () => {
      const { harness, icons, popups } = iconChrome(catching);
      await withBackground(harness, async () => {
        await sendOne(harness);
        emit(harness, { eventType: 'progress', job: job() });
        await settle(() => icons.length > 0 && icons.at(-1).imageData);
        const before = icons.length;
        emit(harness, { eventType: 'completed', job: job({ state: 'completed', receivedBytes: 100 }) });
        for (let i = 0; i < 200 && icons.length < before + 7; i++) await new Promise((resolve) => setTimeout(resolve, 2));
        const flash = icons.slice(before);
        // Three blinks (ring, plain), then the state now: idle, the plain icon.
        assert.equal(flash.length, 7);
        assert.deepEqual(flash.map((call) => (call.path ? 'plain' : 'ring')), ['ring', 'plain', 'ring', 'plain', 'ring', 'plain', 'plain']);
        await new Promise((resolve) => setTimeout(resolve, 30));
        assert.equal(icons.length, before + 7, 'nothing more runs');
        assert.deepEqual(popups, []);
      });
    });
  } finally { delete global.__inboundFlashMs; }
});

test('EXT.R12: "Open the popup" opens it on a finish; where the browser refuses, the icon flashes; "Do nothing" does neither', async () => {
  global.__inboundFlashMs = 1;
  try {
    await withCanvas(async () => {
      for (const [onFinish, refuse, expectPopup, expectFlash] of [
        ['popup', false, true, false], ['popup', true, true, true], ['nothing', false, false, false]]) {
        const { harness, icons, popups } = iconChrome({ ...catching, onFinish });
        if (refuse) harness.chrome.action.openPopup = async (options) => { popups.push(options); throw new Error('Not allowed without a click.'); };
        await withBackground(harness, async () => {
          await sendOne(harness);
          emit(harness, { eventType: 'progress', job: job() });
          await settle(() => icons.length > 0 && icons.at(-1).imageData);
          const before = icons.length;
          emit(harness, { eventType: 'completed', job: job({ state: 'completed' }) });
          await new Promise((resolve) => setTimeout(resolve, 60));
          assert.equal(popups.length > 0, expectPopup, onFinish);
          if (expectPopup) assert.deepEqual(popups[0], { windowId: 7 });
          // The plain icon for idle is drawn either way; a flash draws six more.
          assert.equal(icons.length - before, expectFlash ? 7 : 1, `${onFinish} refuse=${refuse}`);
        });
      }
    });
  } finally { delete global.__inboundFlashMs; }
});

test('EXT.R12: Show in app asks the app for its main window at that download; never for a server\'s', async () => {
  const { harness } = capturingChrome(catching);
  await withBackground(harness, async () => {
    const shown = sendRuntimeMessage(harness, { type: 'dm.jobCommand', command: 'show', jobId: JOB, target: 'local' }, ownPage('popup.html'));
    const request = await answerNative(harness, 'host.openUi', { ok: true, message: 'Showing the download in the app.' });
    assert.deepEqual(request.payload, { action: '', jobId: JOB });
    assert.equal((await shown).ok, true);

    const refused = await sendRuntimeMessage(harness, { type: 'dm.jobCommand', command: 'show', jobId: JOB, target: 'server' }, ownPage('popup.html'));
    assert.equal(refused.ok, false);
    assert.equal(harness.nativeMessages.filter((message) => message.type === 'host.openUi').length, 1);
  });
});

test('EXT.R12: the popup\'s row menu offers Show in app for this PC\'s downloads and sends it', async () => {
  const finished = { id: JOB, fileName: 'report.pdf', state: 'completed', receivedBytes: 10, totalBytes: 10, isTerminal: true,
    createdAt: '2026-11-12T10:00:00Z', url: 'https://files.example.test/report.pdf' };
  const page = installDom('popup.html', { handlers: {
    'dm.status': async () => ({ ok: true, enabled: true, autoIntercept: true, automaticSending: true, connected: true, lastError: '',
      localMissing: false, server: null, automaticTarget: 'local', pauseUntil: 0, settings: { enabled: true, autoIntercept: true },
      leftInBrowser: [], jobs: [finished] }),
    'dm.jobs': async () => ({ ok: true, jobs: [finished] }),
    'dm.askQueue': async () => ({ ok: true, items: [] }),
    'dm.jobCommand': async () => ({ ok: true }),
  } });
  page.load('popup.ts');
  await page.ready();
  await flush(12);
  try {
    const row = [...page.document.querySelectorAll('.job')].find((item) => item.textContent.includes('report.pdf'));
    row.dispatchEvent(new page.window.MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 20, clientY: 20, button: 2 }));
    const items = [...page.document.querySelectorAll('.popup-menu .menu-item')];
    assert.equal(items[0].textContent, 'Show in app');
    items[0].click();
    await flush();
    assert.deepEqual(page.sent.filter((message) => message.type === 'dm.jobCommand').at(-1),
      { type: 'dm.jobCommand', command: 'show', jobId: JOB, target: 'local' });
  } finally { page.restore(); }
});

test('EXT.R12: the right-click item sends the link straight to the app with its cookies, and opens nothing', async () => {
  const { harness, log } = capturingChrome({ ...catching, notifications: true });
  harness.chrome.cookies = {
    async getAllCookieStores() { return [{ id: '0', tabIds: [4] }]; },
    async getAll() { return [{ name: 'session', value: 's', path: '/' }]; },
  };
  let popups = 0;
  harness.chrome.action.openPopup = async () => { popups++; };
  await withBackground(harness, async () => {
    harness.contextMenuClicked.listeners[0]({ linkUrl: 'https://file.test/dl/report.pdf', pageUrl: 'https://file.test/' }, { id: 4, url: 'https://file.test/' });
    const request = await answerNative(harness, 'handoff.request', { accepted: true, jobId: JOB });
    assert.equal(request.payload.url, 'https://file.test/dl/report.pdf');
    // No name: the app takes the site's own name (Content-Disposition), falling back to the address.
    assert.equal(request.payload.suggestedFileName, undefined);
    assert.equal(request.payload.isAutoIntercept, false);
    assert.equal(request.payload.quietWindows, true);
    assert.equal(request.payload.headers.Cookie, 'session=s');
    await settle(() => log.notes.length > 0);
    assert.equal(log.notes.at(-1).title, 'Sent to Inbound');
    assert.equal(log.notes.at(-1).message, 'report.pdf', 'the address names it in the notification only');
    assert.deepEqual(log.windows, [], 'no folder window');
    assert.equal(popups, 0, 'no popup');

    // A script address such as get.php?id=5 is never sent as the name: the site's answer names the file.
    harness.contextMenuClicked.listeners[0]({ linkUrl: 'https://file.test/get.php?id=5', pageUrl: 'https://file.test/' }, { id: 4, url: 'https://file.test/' });
    const script = await answerNative(harness, 'handoff.request', { accepted: true, jobId: JOB });
    assert.equal(script.payload.suggestedFileName, undefined);
    await settle(() => log.notes.length > 1);

    // A link without a file name: the app names the file from the site's answer.
    harness.contextMenuClicked.listeners[0]({ linkUrl: 'https://file.test/get?id=9', pageUrl: 'https://file.test/' }, { id: 4, url: 'https://file.test/' });
    const unnamed = await answerNative(harness, 'handoff.request', { accepted: false, error: 'No space left.' });
    assert.equal(unnamed.payload.suggestedFileName, undefined);
    await settle(() => log.notes.length > 2);
    assert.equal(log.notes.at(-1).title, 'Inbound did not accept the download');
    assert.deepEqual(log.windows, []);
  });
});

test('EXT.R12: with "Ask where to save" the right-click item still asks in its window', async () => {
  const { harness, log } = capturingChrome({ ...catching, destinationMode: 'ask' });
  await withBackground(harness, async () => {
    harness.contextMenuClicked.listeners[0]({ linkUrl: 'https://file.test/report.pdf', pageUrl: 'https://file.test/' }, { id: 4, url: 'https://file.test/' });
    await settle(() => log.windows.length > 0);
    assert.match(log.windows[0].url, /handoff\.html\?id=/);
    assert.equal(harness.nativeMessages.filter((message) => message.type === 'handoff.request').length, 0);
  });
});
