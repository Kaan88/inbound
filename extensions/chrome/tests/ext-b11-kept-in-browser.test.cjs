// EXT.B11 (owner, 2026-11-23): with "When a download finishes: open the popup" on, the popup also opened for a download
// left to the browser. A download the browser kept (the app never took it, or the app took it but this extension gave it
// back) must open no popup, mark nothing and notify nothing, whatever job the app keeps for its address.
const assert = require('node:assert/strict');
const test = require('node:test');
const { loadTs } = require('./page-harness.cjs');
const {
  answerNative, capturingChrome, catching, ownPage, sendRuntimeMessage, settle, startDownload, withBackground,
} = require('./harness.cjs');

const status = loadTs('toolbar-status.ts');

const JOB = '0f8fad5b-d9cb-469f-a165-70867728950e';
const URL = 'https://site.test/a.zip';
const ASKING = { ...catching, destinationMode: 'ask', onFinish: 'popup' };
// The ask popup counts a download it showed as seen at once; the tests close it straight away.
global.__inboundAskSeenMs = 0;

const job = (overrides = {}) => ({ id: JOB, fileName: 'a.zip', source: 'chromeAuto', state: 'downloading',
  startMode: 'startNow', receivedBytes: 25, totalBytes: 100, url: URL, ...overrides });

// ---- The watch's rule ------------------------------------------------------------------------------------------

test('EXT.B11: a download the browser kept is not followed, and the app finishing its address is no finish', () => {
  const watch = new status.DownloadWatch();
  watch.update(job());
  assert.equal(watch.size, 1);
  // The user keeps it in the browser: the followed job goes, and nothing of that address joins again.
  watch.handedBack(URL);
  assert.equal(watch.size, 0);
  assert.deepEqual(watch.update(job()), { finished: false });
  assert.deepEqual(watch.update(job({ state: 'completed' })), { finished: false }, 'no popup, mark or flash');
  assert.equal(watch.size, 0);
  watch.take([job({ id: 'from-list' })]);
  assert.equal(watch.size, 0, 'the popup list does not bring it back');

  // Another address, and a job that says no address (an older app), are followed as before.
  watch.update(job({ id: 'other', url: 'https://site.test/b.zip' }));
  watch.update(job({ id: 'old-app', url: undefined }));
  assert.equal(watch.size, 2);

  // The app took a download of that address again (sent by hand, say): it counts again.
  watch.takenByApp(URL);
  watch.update(job({ id: 'again' }));
  assert.deepEqual(watch.update(job({ id: 'again', state: 'completed' })), { finished: true });
  // The newest addresses only.
  for (let n = 0; n < status.KEPT_IN_BROWSER_MAX + 5; n++) watch.handedBack(`https://site.test/${n}.zip`);
  watch.update(job({ id: 'fresh', url: 'https://site.test/0.zip' }));
  assert.deepEqual(watch.update(job({ id: 'fresh', url: 'https://site.test/0.zip', state: 'completed' })), { finished: true },
    'an address kept long ago is forgotten');
});

// ---- The background -----------------------------------------------------------------------------------------------

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

/** A browser whose toolbar popup opens, whose icons are drawn and whose notifications are kept. */
function finishChrome(settings) {
  const made = capturingChrome(settings);
  const popups = [];
  const icons = [];
  Object.assign(made.harness.chrome.action, {
    async openPopup(options) { popups.push(options); },
    async setIcon(details) { icons.push(details); },
    async setTitle() {},
  });
  made.harness.chrome.windows.getAll = async () => [{ id: 7, type: 'normal', focused: true }];
  // notify() creates with the options alone; the finish notification with an id and the options.
  made.harness.chrome.notifications.create = (first, second) => {
    if (typeof first === 'string') made.log.notes.push({ id: first, options: second });
    else made.log.notes.push(first);
  };
  return { ...made, popups, icons };
}

const emit = (harness, eventType, payload) => {
  for (const listener of harness.nativeOnMessage.listeners) listener({ type: 'job.event', payload: { eventType, job: payload } });
};

/** Answers every waiting request of this type with an error, as a lost answer does (the app took it, we never knew). */
async function failRequestsWhile(harness, type, error, promise) {
  let settled = false;
  const done = promise.then((value) => { settled = true; return value; }, (reason) => { settled = true; throw reason; });
  for (let waited = 0; waited < 10_000 && !settled; waited += 5) {
    for (const request of harness.nativeMessages) {
      if (request.type !== type || request.answered) continue;
      request.answered = true;
      for (const listener of harness.nativeOnMessage.listeners) {
        listener({ type: 'handoff.error', replyTo: request.id, payload: { error } });
      }
    }
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  return await done;
}

/** The popup's port to the worker, as popup.ts opens it; close() is the popup closing. */
function openAskPort(harness) {
  const posted = [];
  const port = { name: 'dm.ask', sender: { id: 'test-extension', url: 'chrome-extension://test/popup.html' },
    onMessage: { addListener() {} }, onDisconnect: { addListener(listener) { this.listener = listener; } },
    postMessage(message) { posted.push(message); }, disconnect() {} };
  for (const listener of harness.runtimeOnConnect.listeners) listener(port);
  return { posted, close() { port.onDisconnect.listener?.(port); } };
}

const waitFor = async (done, ms = 2000) => {
  for (let waited = 0; !done() && waited < ms; waited += 5) await new Promise((resolve) => setTimeout(resolve, 5));
};

function catchHeld(harness, id, name) {
  const answers = [];
  const held = harness.downloadsCreated.listeners[0](
    { id, url: `https://site.test/${name}`, filename: name, referrer: '', mime: '', totalBytes: 2048, fileSize: 2048 },
    () => { answers.push(id); });
  return { held, answers };
}

test('EXT.B11: a download the ask popup gave back to the browser opens no finish popup when the app finishes a job for it', async () => {
  global.__inboundFlashMs = 1;
  try {
    await withCanvas(async () => {
      const { harness, log, popups } = finishChrome(ASKING);
      await withBackground(harness, async () => {
        const page = ownPage('popup.html');
        const { held } = catchHeld(harness, 21, 'a.zip');
        assert.equal(held, true, 'caught and held for a folder');
        await waitFor(() => popups.length === 1);
        const popup = openAskPort(harness);
        const [item] = (await sendRuntimeMessage(harness, { type: 'dm.askQueue' }, page)).items;
        assert.ok(item, 'waiting in the popup');

        // Start download: the app takes it, but its answer never reaches the extension.
        const confirmed = failRequestsWhile(harness, 'handoff.request', 'Timed out waiting for the desktop app.',
          sendRuntimeMessage(harness,
            { type: 'dm.confirmHandoff', id: item.id, fileName: 'a.zip', destination: '', startMode: 'now', target: 'local' }, page));
        assert.equal((await confirmed).ok, false, 'the popup shows the error');
        assert.equal(harness.session[`handoff.${item.id}`] !== undefined, true, 'still waiting');

        // Closing the popup gives it back: the browser downloads it itself.
        popup.close();
        await settle(() => log.resumed.length === 1);
        assert.deepEqual(log.resumed, [21], 'the browser has it');
        assert.deepEqual(await (await sendRuntimeMessage(harness, { type: 'dm.askQueue' }, page)).items, []);

        // The app keeps a job for that address and finishes it later.
        const asked = popups.length;
        const notes = log.notes.length;
        emit(harness, 'progress', job());
        emit(harness, 'completed', job({ state: 'completed', receivedBytes: 100 }));
        await new Promise((resolve) => setTimeout(resolve, 120));

        assert.equal(popups.length, asked, 'no finish popup for a download the browser kept');
        assert.equal(log.notes.length, notes, 'no notification');
        assert.equal(log.badge.at(-1), '', 'nothing finished: no count');
        assert.equal('finishedIds' in harness.session, false, 'no finished mark');
      });
    });
  } finally { delete global.__inboundFlashMs; }
});

test('EXT.B11: the same when the app refuses an automatic send and the browser keeps the download', async () => {
  global.__inboundFlashMs = 1;
  try {
    await withCanvas(async () => {
      const { harness, log, popups } = finishChrome({ ...catching, onFinish: 'popup' });
      await withBackground(harness, async () => {
        const answered = startDownload(harness, { id: 31, url: URL, filename: 'a.zip' });
        await answerNative(harness, 'handoff.request', { accepted: false, error: 'The app is busy.' });
        void answered;
        await settle(() => log.resumed.length === 1);
        assert.deepEqual(log.resumed, [31], 'the browser keeps it');

        emit(harness, 'progress', job());
        emit(harness, 'completed', job({ state: 'completed', receivedBytes: 100 }));
        await new Promise((resolve) => setTimeout(resolve, 120));
        assert.deepEqual(popups, [], 'no finish popup');
        assert.equal('finishedIds' in harness.session, false);
      });
    });
  } finally { delete global.__inboundFlashMs; }
});

test('EXT.B11: a download the app took keeps its finish popup, and a later send by hand counts again', async () => {
  global.__inboundFlashMs = 1;
  try {
    await withCanvas(async () => {
      // The app takes an automatic download: its finish opens the popup as before.
      const accepted = finishChrome({ ...catching, onFinish: 'popup' });
      await withBackground(accepted.harness, async () => {
        const answered = startDownload(accepted.harness, { id: 41, url: URL, filename: 'a.zip' });
        await answerNative(accepted.harness, 'handoff.request', { accepted: true, jobId: JOB });
        void answered;
        emit(accepted.harness, 'progress', job());
        emit(accepted.harness, 'completed', job({ state: 'completed', receivedBytes: 100 }));
        await settle(() => accepted.popups.length === 1);
        assert.deepEqual(accepted.popups, [{ windowId: 7 }]);
        assert.deepEqual(accepted.harness.session.finishedIds, [JOB]);
      });

      // Kept in the browser, then sent again by hand and taken: the app's finish counts again.
      const sent = finishChrome({ ...catching, onFinish: 'popup' });
      await withBackground(sent.harness, async () => {
        const answered = startDownload(sent.harness, { id: 51, url: URL, filename: 'a.zip' });
        await answerNative(sent.harness, 'handoff.request', { accepted: false, error: 'The app is busy.' });
        void answered;
        await settle(() => sent.log.resumed.length === 1);

        sent.harness.contextMenuClicked.listeners[0]({ linkUrl: URL, pageUrl: 'https://site.test/' }, { id: 4, url: 'https://site.test/' });
        await answerNative(sent.harness, 'handoff.request', { accepted: true, jobId: JOB });
        await settle(() => sent.log.notes.some((note) => /Sent to/.test(note.title)));
        emit(sent.harness, 'progress', job());
        emit(sent.harness, 'completed', job({ state: 'completed', receivedBytes: 100 }));
        await settle(() => sent.popups.length === 1);
        assert.deepEqual(sent.popups, [{ windowId: 7 }], 'a finish popup for the app\'s download');
      });
    });
  } finally { delete global.__inboundFlashMs; }
});
