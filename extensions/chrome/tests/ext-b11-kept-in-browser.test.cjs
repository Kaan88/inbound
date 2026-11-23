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

test('EXT.B11: an address the app writes its own way still marks the same kept download', () => {
  // The Host writes a job's address as .NET's URI does: scheme and host in lower case, no default port, no fragment.
  const watch = new status.DownloadWatch();
  watch.handedBack('HTTPS://Site.Test:443/a.zip#part');
  assert.equal(watch.isKept('https://site.test/a.zip'), true);
  assert.equal(watch.isKept('https://site.test/a.zip?x=1'), false, 'a query is another address');
  assert.equal(watch.isKept('https://site.test/A.zip'), false, 'the path is compared as it is');
  assert.equal(watch.isKept('http://site.test/a.zip'), false, 'another scheme');
  assert.equal(watch.isKept('https://site.test:8443/a.zip'), false, 'another port');
  assert.equal(watch.isKept('https://other.test/a.zip'), false, 'another host');
  assert.deepEqual(watch.update(job({ url: 'https://site.test/a.zip' })), { finished: false });
  assert.equal(watch.size, 0, 'not followed: the browser kept it');
  assert.deepEqual(watch.update(job({ id: 'completed-anyway', url: 'https://site.test/a.zip', state: 'completed' })),
    { finished: false }, 'no finish popup for the app job of that address');

  // The other way round: the browser's text has the parts, the app's job is the plain address.
  const plain = new status.DownloadWatch();
  plain.handedBack('https://site.test/b.zip');
  plain.update(job({ url: 'HTTPS://SITE.TEST:443/b.zip#x' }));
  assert.equal(plain.size, 0);
  // The kept list is stored normalised, so a worker restart compares the same way.
  assert.deepEqual(plain.keptAddresses(), ['https://site.test/b.zip']);
  const restarted = new status.DownloadWatch();
  restarted.restoreKept(plain.keptAddresses());
  assert.equal(restarted.isKept('HTTPS://Site.Test:443/b.zip'), true);
  restarted.takenByApp('HTTPS://Site.Test:443/b.zip#again');
  assert.equal(restarted.isKept('https://site.test/b.zip'), false, 'the app took it again, in either spelling');
});

test('EXT.B11: addressKey reads an address the way the Host does, and leaves other text alone', () => {
  assert.equal(status.addressKey('HTTPS://Site.Test:443/a.zip#part'), 'https://site.test/a.zip');
  assert.equal(status.addressKey('http://Site.Test:80/a.zip'), 'http://site.test/a.zip');
  assert.equal(status.addressKey('https://site.test:8443/a.zip'), 'https://site.test:8443/a.zip');
  assert.equal(status.addressKey('  https://site.test/a.zip?x=1&y=2  '), 'https://site.test/a.zip?x=1&y=2');
  assert.equal(status.addressKey('https://site.test'), 'https://site.test/');
  assert.equal(status.addressKey('not an address'), null);
  assert.equal(status.addressKey(''), null);
  assert.equal(status.addressKey(undefined), null);
});

test('EXT.B11: a new worker takes back the addresses an earlier one kept', () => {
  const before = new status.DownloadWatch();
  before.handedBack(URL);
  before.handedBack('https://site.test/b.zip');
  const saved = JSON.parse(JSON.stringify(before.keptAddresses()));
  // The worker stopped while the app's job for URL ran; the new one is told about it after it marked another.
  const after = new status.DownloadWatch();
  after.handedBack('https://site.test/c.zip');
  after.restoreKept(saved);
  assert.equal(after.isKept(URL), true);
  assert.deepEqual(after.keptAddresses().at(-1), 'https://site.test/c.zip', "this worker's own mark stays newest");
  assert.deepEqual(after.update(job()), { finished: false });
  assert.deepEqual(after.update(job({ state: 'completed' })), { finished: false }, 'no popup after the restart');
  after.restoreKept('not a list');
  assert.equal(after.isKept(URL), true);
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

test('EXT.B11: no finish popup when the app\'s job writes the kept address differently', async () => {
  // The owner's report: the download stayed in the browser (a refused or lost answer), yet the app kept a job for it and
  // its finish opened the popup. The two addresses differed only in the parts the Host normalises.
  global.__inboundFlashMs = 1;
  try {
    await withCanvas(async () => {
      const { harness, log, popups } = finishChrome({ ...catching, onFinish: 'popup' });
      await withBackground(harness, async () => {
        // The browser's own text for the download: upper-case scheme and host, the default port, a fragment.
        const answered = startDownload(harness, { id: 61, url: 'HTTPS://Site.Test:443/a.zip#part', filename: 'a.zip' });
        await answerNative(harness, 'handoff.request', { accepted: false, error: 'The app is busy.' });
        void answered;
        await settle(() => log.resumed.length === 1);
        assert.deepEqual(log.resumed, [61], 'the browser keeps it');

        // The app's job for the same download, in the Host's spelling, finishes.
        emit(harness, 'progress', job({ url: 'https://site.test/a.zip' }));
        emit(harness, 'completed', job({ url: 'https://site.test/a.zip', state: 'completed', receivedBytes: 100 }));
        await new Promise((resolve) => setTimeout(resolve, 120));
        assert.deepEqual(popups, [], 'no finish popup for the same address');
        assert.equal('finishedIds' in harness.session, false, 'no finished mark');
        assert.equal(log.notes.some((note) => /finished/.test(note.title ?? '')), false, 'no notification');
      });
    });
  } finally { delete global.__inboundFlashMs; }
});

test('EXT.B11: a kept download of another address is still followed and opens its popup', async () => {
  global.__inboundFlashMs = 1;
  try {
    await withCanvas(async () => {
      const { harness, log, popups } = finishChrome({ ...catching, onFinish: 'popup' });
      await withBackground(harness, async () => {
        const answered = startDownload(harness, { id: 71, url: 'https://site.test/a.zip', filename: 'a.zip' });
        await answerNative(harness, 'handoff.request', { accepted: false, error: 'The app is busy.' });
        void answered;
        await settle(() => log.resumed.length === 1);

        // Another path, query or host is another download: the app finishing that one is a finish to show.
        for (const other of ['https://site.test/b.zip', 'https://site.test/a.zip?x=1', 'https://other.test/a.zip']) {
          const before = popups.length;
          emit(harness, 'progress', job({ id: `job-${other}`, url: other }));
          emit(harness, 'completed', job({ id: `job-${other}`, url: other, state: 'completed', receivedBytes: 100 }));
          await settle(() => popups.length === before + 1);
          assert.equal(popups.length, before + 1, other);
        }
      });
    });
  } finally { delete global.__inboundFlashMs; }
});
