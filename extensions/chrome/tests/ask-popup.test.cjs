// EXT.R7: "ask where to save" in the toolbar popup. The background opens the popup (action.openPopup) for a caught
// download that needs a folder, queues several, gives what still waits back to the browser when the popup closes,
// and falls back to the folder window whenever the popup can't be opened. Right-click downloads keep the window.
const assert = require('node:assert/strict');
const test = require('node:test');
const {
  answerNative, capturingChrome, catching, event, ownPage, sendRuntimeMessage, settle, withBackground,
} = require('./harness.cjs');

const asking = { ...catching, destinationMode: 'ask' };

/** A browser whose toolbar popup can be opened: openPopup's calls, and a front window that is focused or not. */
function withPopup(settings = asking, { focused = true, openPopup } = {}) {
  const made = capturingChrome(settings);
  const calls = [];
  made.harness.chrome.action.openPopup = openPopup ?? (async (options) => { calls.push(options); });
  made.harness.chrome.windows.getLastFocused = async (query) => ({ id: 7, type: 'normal', focused, query });
  return { ...made, openPopupCalls: calls };
}

/** The popup's port to the worker, as popup.ts opens it. close() is the popup closing. */
function openAskPort(harness, url = 'chrome-extension://test/popup.html', senderId = 'test-extension') {
  const posted = [];
  const port = { name: 'dm.ask', sender: { id: senderId, url }, onMessage: event(), onDisconnect: event(),
    disconnected: false, disconnect() { this.disconnected = true; }, postMessage(message) { posted.push(message); } };
  for (const listener of harness.runtimeOnConnect.listeners) listener(port);
  return { port, posted, close() { for (const listener of port.onDisconnect.listeners) listener(port); } };
}

/** The folder window's port, as handoff.ts opens it. */
function openHandoffPort(harness, id) {
  const port = { name: `dm.handoff:${id}`, sender: { id: 'test-extension', url: `chrome-extension://test/handoff.html?id=${id}` },
    onMessage: event(), onDisconnect: event(), disconnected: false, disconnect() { this.disconnected = true; }, postMessage() {} };
  for (const listener of harness.runtimeOnConnect.listeners) listener(port);
  return { port, close() { for (const listener of port.onDisconnect.listeners) listener(port); } };
}

function catchDownload(harness, id, name, answers) {
  return harness.downloadsCreated.listeners[0](
    { id, url: `https://site.test/${name}`, filename: name, referrer: '', mime: '', totalBytes: 2048, fileSize: 2048 },
    () => { answers.push(id); });
}

const queue = async (harness) => (await sendRuntimeMessage(harness, { type: 'dm.askQueue' }, ownPage('popup.html'))).items;

test('EXT.R7: a caught download opens the toolbar popup in the window in front, not a window, and waits there', async () => {
  const { harness, log, openPopupCalls } = withPopup();
  await withBackground(harness, async () => {
    const answers = [];
    assert.equal(catchDownload(harness, 21, 'a.iso', answers), true);
    await settle(() => openPopupCalls.length);
    assert.deepEqual(openPopupCalls, [{ windowId: 7 }]);
    assert.deepEqual(log.windows, [], 'no folder window');
    assert.deepEqual(log.paused, [21]);

    const popup = openAskPort(harness);
    assert.equal(popup.port.disconnected, false);
    const [item] = await queue(harness);
    assert.equal(item.fileName, 'a.iso');
    assert.equal(item.totalBytes, 2048);
    assert.equal(harness.session.askQueue.length, 1, 'kept for a worker restart');

    // The popup asks for the request as the folder window does, and sends it the same way.
    const info = sendRuntimeMessage(harness, { type: 'dm.getHandoff', id: item.id }, ownPage('popup.html'));
    await answerNative(harness, 'settings.downloadFolders', { ok: true, defaultFolder: 'D:\\Downloads', favorites: [] });
    assert.equal((await info).held, true);
    const confirmed = sendRuntimeMessage(harness, { type: 'dm.confirmHandoff', id: item.id, destination: '', startMode: 'now', target: 'local' }, ownPage('popup.html'));
    await answerNative(harness, 'handoff.request', { accepted: true });
    assert.equal((await confirmed).ack.accepted, true);
    assert.deepEqual(log.cancelled, [21]);
    assert.deepEqual(await queue(harness), []);
    assert.ok(popup.posted.some(message => message.type === 'dm.askChanged' && message.waiting === 0), 'the popup is told');

    // Closing the popup now gives nothing back: nothing waits.
    popup.close();
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.deepEqual(log.resumed, []);
    assert.deepEqual(answers, []);
  });
});

test('EXT.R7: several downloads at once open the popup once and wait in order; closing it gives the rest back to the browser', async () => {
  const { harness, log, openPopupCalls } = withPopup();
  await withBackground(harness, async () => {
    const answers = [];
    catchDownload(harness, 31, 'one.zip', answers);
    catchDownload(harness, 32, 'two.zip', answers);
    await settle(() => log.paused.length === 2);
    const popup = openAskPort(harness);
    catchDownload(harness, 33, 'three.zip', answers);
    await settle(() => popup.posted.length);
    assert.equal(openPopupCalls.length, 1, 'one popup for all of them');
    assert.deepEqual((await queue(harness)).map(item => item.fileName), ['one.zip', 'two.zip', 'three.zip']);

    // Keep in browser for the first: it goes back at once, and the rest still wait.
    const [first] = await queue(harness);
    await sendRuntimeMessage(harness, { type: 'dm.keepInBrowser', id: first.id }, ownPage('popup.html'));
    assert.deepEqual(log.resumed, [31]);
    assert.deepEqual(answers, [31]);
    assert.deepEqual((await queue(harness)).map(item => item.fileName), ['two.zip', 'three.zip']);

    popup.close();
    await settle(() => log.resumed.length === 3);
    assert.deepEqual(log.resumed, [31, 32, 33]);
    assert.deepEqual(answers, [31, 32, 33]);
    assert.deepEqual(log.cancelled, []);
    assert.deepEqual(log.windows, []);
    assert.deepEqual(harness.session.askQueue, []);
  });
});

test('EXT.R7: a popup closed while its download is being sent waits for the answer', async () => {
  const { harness, log } = withPopup();
  await withBackground(harness, async () => {
    for (const [downloadId, accepted] of [[41, true], [42, false]]) {
      const answers = [];
      catchDownload(harness, downloadId, `${downloadId}.iso`, answers);
      await settle(() => log.paused.includes(downloadId));
      const popup = openAskPort(harness);
      const [item] = await queue(harness);
      const confirmed = sendRuntimeMessage(harness, { type: 'dm.confirmHandoff', id: item.id, destination: '', startMode: 'now' }, ownPage('popup.html'));
      await settle(() => harness.nativeMessages.some(message => message.type === 'handoff.request' && !message.answered));
      popup.close();
      await new Promise(resolve => setTimeout(resolve, 10));
      assert.equal(log.resumed.includes(downloadId), false, 'nothing is given back while the app may still take it');
      await answerNative(harness, 'handoff.request', { accepted, error: accepted ? undefined : 'No.' });
      await confirmed;
      if (accepted) {
        assert.ok(log.cancelled.includes(downloadId));
        assert.equal(log.resumed.includes(downloadId), false);
      } else {
        await settle(() => log.resumed.includes(downloadId));
        assert.deepEqual(answers, [downloadId], 'nobody is left to choose: the browser keeps it');
      }
    }
  });
});

for (const [name, options, opens] of [
  ['the browser has no action.openPopup (Chrome or Edge before 127)', { openPopup: null }, false],
  ['openPopup refuses (Firefox before 149 without a user gesture, or a menu in the way)', { openPopup: async () => { throw new Error('openPopup requires a user gesture'); } }, true],
  ['the browser is not the window in front', { focused: false }, false],
]) {
  test(`EXT.R7: when ${name}, the folder window asks instead`, async () => {
    const made = withPopup(asking, options);
    if (options.openPopup === null) delete made.harness.chrome.action.openPopup;
    const { harness, log, openPopupCalls } = made;
    await withBackground(harness, async () => {
      const answers = [];
      catchDownload(harness, 51, 'a.iso', answers);
      catchDownload(harness, 52, 'b.iso', answers);
      await settle(() => log.windows.length === 2);
      assert.deepEqual(log.windows.map(window => window.url.includes('handoff.html?id=')), [true, true]);
      assert.equal(log.windows.some(window => window.url.includes('pick=1')), false);
      assert.deepEqual(await queue(harness), [], 'nothing waits in the popup');
      if (!opens) assert.deepEqual(openPopupCalls, []);
      assert.deepEqual(log.resumed, []);
      // The window works as it always has: closing it gives the download back.
      const id = decodeURIComponent(log.windows[0].url.split('id=')[1]);
      openHandoffPort(harness, id).close();
      await settle(() => log.resumed.length);
      assert.deepEqual(log.resumed, [51]);
    });
  });
}

test('EXT.R7: a popup that opens but never connects hands its downloads to folder windows', async () => {
  const { harness, log, openPopupCalls } = withPopup();
  global.__inboundPopupConnectMs = 20;
  try {
    await withBackground(harness, async () => {
      catchDownload(harness, 61, 'a.iso', []);
      await settle(() => openPopupCalls.length);
      assert.deepEqual(log.windows, []);
      await new Promise(resolve => setTimeout(resolve, 80));
      assert.equal(log.windows.length, 1);
      assert.deepEqual(log.resumed, []);
    });
  } finally { delete global.__inboundPopupConnectMs; }
});

test('EXT.R7: right-click downloads keep the folder window, and the popup is not opened for them', async () => {
  const { harness, log, openPopupCalls } = withPopup();
  await withBackground(harness, async () => {
    await new Promise(resolve => harness.contextMenuClicked.listeners[0]({ linkUrl: 'https://site.test/menu.zip', pageUrl: 'https://site.test/' }, { id: 4, url: 'https://site.test/' }) || resolve());
    await settle(() => log.windows.length);
    assert.equal(log.windows.length, 1);
    assert.match(log.windows[0].url, /handoff\.html\?id=/);
    assert.deepEqual(openPopupCalls, []);
    assert.deepEqual(await queue(harness), []);
  });
});

test('EXT.R7: This PC\'s Choose... in the popup moves the waiting downloads to windows, the chosen one with its dialog', async () => {
  const { harness, log } = withPopup();
  await withBackground(harness, async () => {
    const answers = [];
    catchDownload(harness, 71, 'a.iso', answers);
    catchDownload(harness, 72, 'b.iso', answers);
    await settle(() => log.paused.length === 2);
    const popup = openAskPort(harness);
    const [, second] = await queue(harness);

    // Only from the popup.
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.askInWindow', id: second.id, pick: true }, ownPage('handoff.html'))).ok, false);
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.askQueue' }, ownPage('options.html'))).ok, false);

    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.askInWindow', id: second.id, pick: true }, ownPage('popup.html'))).ok, true);
    assert.equal(log.windows.length, 2);
    assert.match(log.windows[0].url, new RegExp(`id=${second.id}&pick=1$`));
    assert.doesNotMatch(log.windows[1].url, /pick=1/);
    assert.deepEqual(await queue(harness), []);
    // The folder dialog takes the focus and closes the popup: the downloads now belong to their windows.
    popup.close();
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.deepEqual(log.resumed, []);
    assert.deepEqual(answers, []);
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.askInWindow', id: second.id }, ownPage('popup.html'))).ok, false, 'no longer waiting');
  });
});

test('EXT.R7: only the extension\'s own popup may hold the ask port', async () => {
  const { harness, log } = withPopup();
  await withBackground(harness, async () => {
    catchDownload(harness, 81, 'a.iso', []);
    await settle(() => log.paused.length);
    for (const forged of [openAskPort(harness, 'chrome-extension://test/handoff.html'), openAskPort(harness, 'chrome-extension://test/popup.html', 'other-extension')]) {
      assert.equal(forged.port.disconnected, true);
      assert.equal(forged.port.onDisconnect.listeners.length, 0);
    }
    assert.equal((await queue(harness)).length, 1);
    assert.deepEqual(log.resumed, []);
  });
});

test('EXT.R7: after a worker restart the popup finds its downloads again, and closing it starts them again in the browser', async () => {
  const { harness, log } = withPopup();
  // Left by the worker before it restarted: the queue and the request, the browser's name callback gone.
  harness.session['handoff.lost'] = { id: 'lost', url: 'https://site.test/a.iso', suggestedFileName: 'a.iso', userAgent: 'x',
    isAutoIntercept: true, createdAt: Date.now(), browserDownloadId: 91 };
  harness.session.askQueue = ['lost'];
  await withBackground(harness, async () => {
    const popup = openAskPort(harness);
    assert.deepEqual((await queue(harness)).map(item => item.id), ['lost']);
    popup.close();
    await settle(() => log.downloaded.length);
    assert.deepEqual(log.cancelled, [91]);
    assert.deepEqual(log.downloaded, [{ url: 'https://site.test/a.iso', filename: 'a.iso' }]);
  });
});

test('EXT.R7 on Firefox: a download caught from its response waits in the popup; closing it starts the download again in Firefox', async () => {
  const made = withPopup();
  delete made.harness.chrome.downloads.onDeterminingFilename;
  const registered = [];
  made.harness.chrome.webRequest = { onHeadersReceived: { addListener(listener) { registered.push(listener); } } };
  const { harness, log, openPopupCalls } = made;
  await withBackground(harness, async () => {
    const answer = await registered[0]({
      url: 'https://site.test/get?id=9', method: 'GET', statusCode: 200, type: 'main_frame', originUrl: 'https://site.test/page',
      responseHeaders: [{ name: 'Content-Type', value: 'application/zip' }, { name: 'Content-Disposition', value: 'attachment; filename="ff.zip"' }],
    });
    assert.deepEqual(answer, { cancel: true }, 'Firefox drops the response; the popup holds the details');
    assert.equal(openPopupCalls.length, 1);
    assert.deepEqual(log.windows, []);
    const popup = openAskPort(harness);
    assert.deepEqual((await queue(harness)).map(item => item.fileName), ['ff.zip']);
    popup.close();
    await settle(() => log.downloaded.length);
    assert.deepEqual(log.downloaded, [{ url: 'https://site.test/get?id=9', filename: 'ff.zip' }]);
  });
});
