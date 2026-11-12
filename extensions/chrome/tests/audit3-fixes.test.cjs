// Audit 3 (Host, Server, NativeHost, extension): the popup's list question, one request id per folder-window
// download, and paused downloads a stopped worker left behind.
const assert = require('node:assert/strict');
const test = require('node:test');
const {
  makeChrome, sendRuntimeMessage, answerNative, settle, startDownload, withBackground, capturingChrome, catching, ownPage,
} = require('./harness.cjs');

test('M3/M5: the popup asks for the newest 20 only, never retries, and a question still waiting answers the next one', async () => {
  const harness = makeChrome(catching, { id: 'seed' });
  await withBackground(harness, async () => {
    const first = sendRuntimeMessage(harness, { type: 'dm.jobs' }, ownPage('popup.html'));
    await settle(() => harness.nativeMessages.some(message => message.type === 'jobs.query'));
    const second = sendRuntimeMessage(harness, { type: 'dm.jobs' }, ownPage('popup.html'));
    await new Promise(resolve => setTimeout(resolve, 10));
    const queries = harness.nativeMessages.filter(message => message.type === 'jobs.query');
    assert.equal(queries.length, 1, 'one question while one is waiting');
    assert.deepEqual(queries[0].payload, { limit: 20 });
    await answerNative(harness, 'jobs.query', { jobs: [{ id: 'j1', fileName: 'a.zip', state: 'completed', receivedBytes: 1 }] });
    const [one, two] = await Promise.all([first, second]);
    assert.equal(one.ok, true);
    assert.deepEqual(one.jobs.map(job => job.id), ['j1']);
    assert.deepEqual(two, one);

    // The next poll asks again.
    const third = sendRuntimeMessage(harness, { type: 'dm.jobs' }, ownPage('popup.html'));
    await answerNative(harness, 'jobs.query', { jobs: [] });
    assert.equal((await third).ok, true);
    assert.equal(harness.nativeMessages.filter(message => message.type === 'jobs.query').length, 2);
    assert.equal(harness.nativePorts.length, 1, 'the shared connection was never dropped');
  });
});

test('L12: Download clicked again after a refusal sends the same request id', async () => {
  const pending = { id: 'h1', url: 'https://file.test/a.zip', userAgent: 'x', suggestedFileName: 'a.zip',
    isAutoIntercept: false, createdAt: Date.now() };
  const harness = makeChrome(catching, pending);
  await withBackground(harness, async () => {
    const first = sendRuntimeMessage(harness, { type: 'dm.confirmHandoff', id: 'h1', destination: 'Q:\\Gone', startMode: 'now' }, ownPage('handoff.html'));
    const sent1 = await answerNative(harness, 'handoff.request', { accepted: false, error: 'That folder does not exist.' });
    assert.equal((await first).ack.accepted, false);
    const second = sendRuntimeMessage(harness, { type: 'dm.confirmHandoff', id: 'h1', destination: '', startMode: 'now' }, ownPage('handoff.html'));
    const sent2 = await answerNative(harness, 'handoff.request', { accepted: true });
    assert.equal((await second).ack.accepted, true);
    assert.ok(sent1.payload.requestId);
    assert.equal(sent2.payload.requestId, sent1.payload.requestId);
  });
});

test('L12: a download paused for sending is recorded until it is settled', async () => {
  const { harness } = capturingChrome(catching);
  await withBackground(harness, async () => {
    void startDownload(harness, { id: 31, url: 'https://file.test/b.zip', filename: 'b.zip' });
    await settle(() => harness.nativeMessages.some(message => message.type === 'handoff.request'));
    assert.deepEqual(Object.keys(harness.session.pausedForHandoff ?? {}), ['31']);
    await answerNative(harness, 'handoff.request', { accepted: true });
    await settle(() => Object.keys(harness.session.pausedForHandoff ?? {}).length === 0);
    assert.deepEqual(harness.session.pausedForHandoff, {});
  });
});

test('L12: a worker start tells the user about a download a stopped worker left paused, and resumes nothing', async () => {
  const { harness, log } = capturingChrome(catching);
  harness.session.pausedForHandoff = { 41: { fileName: 'left.zip', at: Date.now() }, 42: { fileName: 'gone.zip', at: Date.now() } };
  harness.chrome.downloads.search = async ({ id }) => id === 41 ? [{ id, state: 'in_progress', paused: true }] : [];
  await withBackground(harness, async () => {
    await settle(() => log.notes.length > 0);
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.equal(log.notes.length, 1);
    assert.match(log.notes[0].message, /left\.zip: The extension stopped while sending this/);
    assert.deepEqual(log.resumed, []);
    assert.deepEqual(harness.session.pausedForHandoff, {});
  });
});
