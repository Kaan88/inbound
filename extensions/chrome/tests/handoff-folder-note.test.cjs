// Report 3, M1 follow-up: the desktop app saves browser downloads only in its own folders and in ones picked with its
// folder dialog. When it uses its default folder instead it says so (HandoffAck.note), and the extension tells the
// user, even with notifications off; Options confirms a typed folder in that dialog on Apply.
const assert = require('node:assert/strict');
const test = require('node:test');
const {
  answerNative, capturingChrome, catching, makeChrome, ownPage, sendRuntimeMessage, startDownload, withBackground,
} = require('./harness.cjs');
const { installDom, flush } = require('./dom-harness.cjs');
const data = require('./fixtures/screens-data.json');

const NOTE = 'The folder the browser asked for is not one of the app\'s folders, so the download went to the default folder.';

test('an automatic download the app put in its default folder says so, with notifications off too', async () => {
  const { harness, log } = capturingChrome({ ...catching, notifications: false, browserFolder: 'E:\\Typed' });
  await withBackground(harness, async () => {
    startDownload(harness, { id: 7, url: 'https://host.test/files/a.zip', filename: 'a.zip' });
    const request = await answerNative(harness, 'handoff.request', { accepted: true, note: NOTE });
    assert.equal(request.payload.destinationDirectory, 'E:\\Typed');
    for (let i = 0; i < 20 && log.notes.length === 0; i++) await new Promise((resolve) => setTimeout(resolve, 5));
    assert.equal(log.notes.length, 1, 'only the folder note: "Sent to" follows the notifications setting');
    assert.equal(log.notes[0].title, 'Saved in the default folder');
    assert.match(log.notes[0].message, /a\.zip: The folder the browser asked for/);
  });
});

test('an accepted download with no note gives no folder notification', async () => {
  const { harness, log } = capturingChrome({ ...catching, notifications: false, browserFolder: 'E:\\Picked' });
  await withBackground(harness, async () => {
    startDownload(harness, { id: 8, url: 'https://host.test/files/b.zip', filename: 'b.zip' });
    await answerNative(harness, 'handoff.request', { accepted: true });
    await new Promise((resolve) => setTimeout(resolve, 30));
    assert.equal(log.notes.length, 0);
  });
});

test('Last used folder does not remember a folder the app did not use', async () => {
  const pending = { id: 'h1', url: 'https://site.test/a.iso', userAgent: 'test', isAutoIntercept: false, createdAt: Date.now() };
  const harness = makeChrome({ ...catching, askStartsOn: 'last' }, pending);
  harness.chrome.notifications.create = () => {};
  await withBackground(harness, async () => {
    const response = sendRuntimeMessage(harness, { type: 'dm.confirmHandoff', id: 'h1', destination: 'E:\\Typed', startMode: 'now', target: 'local' },
      ownPage('handoff.html'));
    await answerNative(harness, 'handoff.request', { accepted: true, note: NOTE });
    assert.equal((await response).ack.accepted, true);
    assert.deepEqual(harness.values.lastFolders, {}, 'the default folder, which is where it went');

    const batch = sendRuntimeMessage(harness, { type: 'dm.confirmBatch', pageUrl: 'https://site.test/', destination: 'E:\\Batch',
      startMode: 'now', target: 'local', urls: [{ url: 'https://site.test/b.zip' }] }, ownPage('batch.html'));
    await answerNative(harness, 'handoff.request', { accepted: true, note: NOTE });
    const answer = await batch;
    assert.equal(answer.accepted, 1);
    assert.equal(answer.results[0].note, NOTE);
    assert.deepEqual(harness.values.lastFolders, {});
  });
});

// ---- Options ------------------------------------------------------------------------------------------------------

async function openOptions(handlers) {
  const saves = [];
  const picks = [];
  const page = installDom('options.html', { handlers: {
    'dm.status': async () => ({ ok: true, settings: { ...data.settings }, connected: true, server: null, extensionVersion: '0.6.0' }),
    'dm.saveSettings': async (message) => { saves.push(message); return { ok: true, settings: { ...data.settings, ...message.changes } }; },
    'dm.pickFolder': async (message) => { picks.push(message); return handlers.pick(message); },
  } });
  page.load('options.ts');
  await page.ready();
  await flush(10);
  return { page, saves, picks, doc: page.document };
}

function typeFolder(page, doc, folder) {
  const radio = doc.querySelector('input[name=browserFolderMode][value=own]');
  radio.checked = true;
  radio.dispatchEvent(new page.window.Event('change', { bubbles: true }));
  const field = doc.getElementById('browserFolder');
  field.value = folder;
  field.dispatchEvent(new page.window.Event('input', { bubbles: true }));
}

test('Options: a typed browser folder is confirmed in the app\'s folder dialog on Apply, and the picked one is saved', async () => {
  const { page, saves, picks, doc } = await openOptions({ pick: async () => ({ ok: true, path: 'E:\\Typed Folder' }) });
  try {
    typeFolder(page, doc, 'e:\\typed folder');
    doc.getElementById('apply').click();
    await flush();
    assert.deepEqual(picks.map((pick) => pick.initialPath), ['e:\\typed folder']);
    assert.deepEqual(saves[0].changes, { browserFolder: 'E:\\Typed Folder' });
    assert.equal(doc.getElementById('browserFolder').value, 'E:\\Typed Folder');
  } finally { page.restore(); }
});

test('Options: cancelling that dialog saves nothing and says why', async () => {
  const { page, saves, picks, doc } = await openOptions({ pick: async () => ({ ok: true, cancelled: true }) });
  try {
    typeFolder(page, doc, 'E:\\Typed');
    doc.getElementById('apply').click();
    await flush();
    assert.equal(picks.length, 1);
    assert.equal(saves.length, 0);
    assert.match(doc.getElementById('saveResult').textContent, /only in folders picked in its folder dialog/);
  } finally { page.restore(); }
});

test('Options: a folder from Choose… is not asked for again', async () => {
  const { page, saves, picks, doc } = await openOptions({ pick: async () => ({ ok: true, path: 'E:\\Chosen' }) });
  try {
    typeFolder(page, doc, '');
    doc.getElementById('chooseBrowserFolder').click();
    await flush();
    doc.getElementById('apply').click();
    await flush();
    assert.equal(picks.length, 1, 'Choose… only');
    assert.deepEqual(saves[0].changes, { browserFolder: 'E:\\Chosen' });
  } finally { page.restore(); }
});
