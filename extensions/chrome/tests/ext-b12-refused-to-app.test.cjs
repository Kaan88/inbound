// EXT.B12 (owner, 2026-11-23): a site that refuses the app's own request (HTTP 401 or 403 before any byte) is tried in
// the browser once, with the browser's own cookies; the app's failed job and its empty part file go, and the user is
// told once. Only a download this extension sent to this PC's app, only its first failure, and once per address.
const assert = require('node:assert/strict');
const test = require('node:test');
const { loadTs } = require('./page-harness.cjs');
const {
  answerNative, capturingChrome, catching, ownPage, sendRuntimeMessage, settle, withBackground,
} = require('./harness.cjs');

const rules = loadTs('refused-fallback.ts');

const JOB = '0f8fad5b-d9cb-469f-a165-70867728950e';
const SECOND = '1a2b3c4d-0000-4000-8000-000000000002';
const URL = 'https://claude.ai/api/files/abc/content';

const job = (overrides = {}) => ({ id: JOB, fileName: 'report.pdf', source: 'chromeAuto', state: 'failed',
  startMode: 'startNow', receivedBytes: 0, totalBytes: 2048, url: URL, createdAt: new Date().toISOString(),
  failure: { httpStatus: 403, message: 'The server returned HTTP 403.', occurredAt: new Date().toISOString() }, ...overrides });

test('EXT.B12: only the failure of a download just sent counts, never a later change to an old one', () => {
  const now = Date.now();
  const at = (ms) => new Date(now - ms).toISOString();
  const fresh = { createdAt: at(1000), failure: { occurredAt: at(500) } };
  assert.equal(rules.freshFailure(fresh, 'updated', now), true);
  assert.equal(rules.freshFailure(fresh, 'removed', now), false, 'deleting it from the app');
  assert.equal(rules.freshFailure({ ...fresh, failure: { occurredAt: at(3 * 60 * 1000) } }, 'updated', now), false, 'a rename of a download that failed earlier');
  assert.equal(rules.freshFailure({ ...fresh, createdAt: at(60 * 60 * 1000) }, 'updated', now), false, 'added an hour ago, retried now');
  assert.equal(rules.freshFailure({ failure: {} }, 'updated', now), false, 'an app that sends no times');
});

// ---- The rule ----------------------------------------------------------------------------------------------------

test('EXT.B12: only a 401 or 403 with no byte received is a site refusal the browser can win', () => {
  assert.equal(rules.refusedBySite(job()), 403);
  assert.equal(rules.refusedBySite(job({ failure: { httpStatus: 401 } })), 401);
  assert.equal(rules.refusedBySite(job({ receivedBytes: 1 })), null, 'some bytes: not a refusal of the whole request');
  for (const httpStatus of [400, 404, 410, 429, 500, undefined]) {
    assert.equal(rules.refusedBySite(job({ failure: { httpStatus } })), null, String(httpStatus));
  }
  assert.equal(rules.refusedBySite(job({ failure: undefined })), null);
  assert.equal(rules.refusedBySite(job({ state: 'downloading' })), null);
  assert.equal(rules.refusedBySite(job({ state: 'paused' })), null);
  assert.equal(rules.refusedNote(403), 'The site refused Inbound (HTTP 403), so your browser downloads it.');
  assert.equal(rules.refusedNote(401), 'The site refused Inbound (HTTP 401), so your browser downloads it.');
});

// ---- The background ----------------------------------------------------------------------------------------------

function refusedChrome() {
  const made = capturingChrome({ ...catching, onFinish: 'popup' });
  const popups = [];
  Object.assign(made.harness.chrome.action, { async openPopup(options) { popups.push(options); } });
  made.harness.chrome.windows.getAll = async () => [{ id: 7, type: 'normal', focused: true }];
  return { ...made, popups };
}

/** Opens the app's connection, on which job events arrive (a worker start has not opened it yet). */
async function openNative(harness) {
  const ping = sendRuntimeMessage(harness, { type: 'dm.ping' }, ownPage('options.html'));
  await answerNative(harness, 'host.ping', { ok: true });
  await ping;
}

const emit = (harness, payload) => {
  for (const listener of harness.nativeOnMessage.listeners) {
    listener({ type: 'job.event', payload: { eventType: payload.state === 'failed' ? 'failed' : 'progress', job: payload } });
  }
};

const wait = (ms = 80) => new Promise((resolve) => setTimeout(resolve, ms));

/** The app's answer to the removal, so the fallback finishes (it tells the user after removing the job). */
async function answerRemoval(harness, log) {
  const request = await answerNative(harness, 'job.remove', { ok: true });
  await settle(() => log.notes.length > 0);
  return request;
}

test('EXT.B12: a sent job the site refuses with 403 and no bytes goes to the browser once, and the app\'s job goes', async () => {
  const { harness, log, popups } = refusedChrome();
  await withBackground(harness, async () => {
    await openNative(harness);
    emit(harness, job());
    const request = await answerRemoval(harness, log);

    // (1) started again in the browser, with the job's file name, and its end watched for a refusal.
    assert.deepEqual(log.downloaded, [{ url: URL, filename: 'report.pdf' }], 'the browser downloads it with its own cookies');
    assert.deepEqual(harness.session['restartWatch.101'], { fileName: 'report.pdf' }, 'its end is watched');
    // (3) the app's failed job and its empty part file go through the popup's own Delete message.
    assert.deepEqual(request.payload, { jobId: JOB, deleteFile: true });
    // (4) told once, in the popup's words.
    assert.deepEqual(log.notes.map((note) => note.title), ['Left in your browser']);
    assert.equal(log.notes[0].message, 'report.pdf: The site refused Inbound (HTTP 403), so your browser downloads it.');
    // The done set is kept for a worker restart.
    assert.deepEqual(harness.session.refusedInBrowser, { jobs: [JOB], urls: [URL] });

    // (2) the app's job for that address is not followed: no finish popup and no finished mark.
    emit(harness, job({ state: 'downloading' }));
    emit(harness, job({ state: 'completed' }));
    await wait();
    assert.deepEqual(popups, [], 'no finish popup for a download the browser has');
    assert.equal('finishedIds' in harness.session, false);
  });
});

test("EXT.B12: when the browser cannot start it either, the app's failed job stays and nothing claims the browser has it", async () => {
  const { harness, log } = refusedChrome();
  harness.chrome.downloads.download = async () => { throw new Error('Download not allowed'); };
  await withBackground(harness, async () => {
    await openNative(harness);
    emit(harness, job());
    await settle(() => log.notes.length > 0);
    await wait();
    assert.equal(harness.nativeMessages.some((message) => message.type === 'job.remove'), false, 'the app keeps it to retry');
    assert.equal(log.notes.length, 1);
    assert.doesNotMatch(log.notes[0].message, /so your browser downloads it/);
  });
});

test('EXT.B12: a 403 after some bytes, a 404, a server\'s job, another browser\'s job and a second failure do nothing', async () => {
  const cases = [
    ['403 after bytes', job({ receivedBytes: 512 })],
    ['404', job({ failure: { httpStatus: 404 } })],
    ['a server\'s job', job({ target: 'server' })],
    ['another browser\'s job', job({ ownerExtensionId: 'another-extension' })],
    ['not from a browser', job({ source: 'cli' })],
    ['no address', job({ url: undefined })],
    ['not a web address', job({ url: 'remote://torbox/torrents/1/2' })],
    ['a masked address', job({ addressHidden: true })],
  ];
  for (const [name, payload] of cases) {
    const { harness, log } = refusedChrome();
    await withBackground(harness, async () => {
      await openNative(harness);
      emit(harness, payload);
      await wait();
      assert.deepEqual(log.downloaded, [], name);
      assert.equal(harness.nativeMessages.some((message) => message.type === 'job.remove'), false, name);
      assert.deepEqual(log.notes, [], name);
    });
  }

  // Once per job: a second failure of the same download is the user's own retry in the app.
  const once = refusedChrome();
  await withBackground(once.harness, async () => {
    await openNative(once.harness);
    emit(once.harness, job());
    await answerRemoval(once.harness, once.log);
    emit(once.harness, job());
    await wait();
    assert.equal(once.log.downloaded.length, 1, 'only once per download');
    assert.equal(once.harness.nativeMessages.filter((message) => message.type === 'job.remove').length, 1);
  });

  // Once per address: another download of the same address does not go to the browser again.
  const second = refusedChrome();
  await withBackground(second.harness, async () => {
    await openNative(second.harness);
    emit(second.harness, job());
    await answerRemoval(second.harness, second.log);
    emit(second.harness, job({ id: SECOND }));
    await wait();
    assert.equal(second.log.downloaded.length, 1, 'only once per address');
  });
});

test('EXT.B12: a done set kept in session storage means a restarted worker does not try it again', async () => {
  const { harness, log } = refusedChrome();
  harness.session.refusedInBrowser = { jobs: [JOB], urls: [URL] };
  await withBackground(harness, async () => {
    await openNative(harness);
    emit(harness, job());
    await wait();
    assert.deepEqual(log.downloaded, []);
    assert.equal(harness.nativeMessages.some((message) => message.type === 'job.remove'), false);
  });
});

test('EXT.B12: the popup lists the refusal as a note, as it does why any download stayed in the browser', async () => {
  const { harness, log } = refusedChrome();
  await withBackground(harness, async () => {
    await openNative(harness);
    emit(harness, job());
    await answerRemoval(harness, log);
    const reply = sendRuntimeMessage(harness, { type: 'dm.status' }, ownPage('popup.html'));
    await answerNative(harness, 'host.ping', { ok: true });
    const status = await reply;
    assert.deepEqual(status.leftInBrowser.map((note) => note.reason),
      ['The site refused Inbound (HTTP 403), so your browser downloads it.']);
    assert.equal(log.notes.length, 1, 'said once');
  });
});
