// EXT.R7: "ask where to save" in the toolbar popup. The background opens the popup (action.openPopup) for a caught
// download that needs a folder, queues several, gives what still waits back to the browser when the popup closes,
// and falls back to the folder window whenever the popup can't be opened. Right-click downloads keep the window.
const assert = require('node:assert/strict');
const test = require('node:test');
const {
  answerNative, capturingChrome, catching, event, ownPage, sendRuntimeMessage, settle, withBackground,
} = require('./harness.cjs');

const asking = { ...catching, destinationMode: 'ask' };
// A download counts as seen in the popup once it was there 500 ms; these tests close popups at once, so they
// count it as seen at once unless a test says otherwise (the D1 tests).
global.__inboundAskSeenMs = 0;

/**
 * A browser whose toolbar popup can be opened: openPopup's calls, and a front window that is focused or not.
 * windows: what windows.getAll answers (Firefox includes a focused folder window, type popup).
 */
function withPopup(settings = asking, { focused = true, openPopup, windows } = {}) {
  const made = capturingChrome(settings);
  const calls = [];
  made.harness.chrome.action.openPopup = openPopup ?? (async (options) => { calls.push(options); });
  made.harness.chrome.windows.getLastFocused = async (query) => (windows ?? [{ id: 7, type: 'normal', focused }]).find(w => w.focused) ?? { id: 7, type: 'normal', focused, query };
  made.harness.chrome.windows.getAll = async () => windows ?? [{ id: 7, type: 'normal', focused }];
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

test('EXT.R7: a popup that opens but never connects, and the browser cannot tell whether it is still there, hands its downloads to folder windows', async () => {
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

test('EXT.R7 (review 2, e2e D2): downloads caught after openPopup resolved but before the popup connected join it; openPopup is not called again', async () => {
  // Firefox refuses a second call while the first popup is loading.
  let open = false;
  const calls = [];
  const { harness, log } = withPopup(asking, { openPopup: async (options) => {
    calls.push(options);
    if (open) throw new Error('openPopup() cannot be called while another panel is open');
    open = true;
  } });
  global.__inboundPopupConnectMs = 60;
  try {
    await withBackground(harness, async () => {
      const answers = [];
      catchDownload(harness, 101, 'one.zip', answers);
      await settle(() => calls.length === 1 && harness.session.askQueue?.length === 1);
      await new Promise(resolve => setTimeout(resolve, 5)); // openPopup has resolved; the page is still loading
      catchDownload(harness, 102, 'two.zip', answers);
      catchDownload(harness, 103, 'three.zip', answers);
      await settle(() => log.paused.length === 3 && harness.session.askQueue?.length === 3);
      await new Promise(resolve => setTimeout(resolve, 10));
      assert.equal(calls.length, 1, 'no second openPopup while the first popup loads');
      assert.deepEqual(log.windows, [], 'nothing moved to windows');
      const popup = openAskPort(harness);
      assert.deepEqual((await queue(harness)).map(item => item.fileName), ['one.zip', 'two.zip', 'three.zip']);
      // Past the connect wait: the connected popup keeps them.
      await new Promise(resolve => setTimeout(resolve, 100));
      assert.deepEqual(log.windows, []);
      assert.deepEqual(log.resumed, []);
      assert.equal((await queue(harness)).length, 3);
        popup.close();
      await settle(() => log.resumed.length === 3);
      assert.deepEqual(answers, [101, 102, 103]);
    });
  } finally { delete global.__inboundPopupConnectMs; }
});

/** Runs with the browser's real seen time: a popup closed within 500 ms of showing a download never saw it. */
async function withSeenTime(run) {
  global.__inboundAskSeenMs = 500;
  try { await run(); } finally { global.__inboundAskSeenMs = 0; }
}

test('EXT.R7 (e2e D1): a download caught while the popup is closing opens the popup again instead of going to the browser', () => withSeenTime(async () => {
  const { harness, log, openPopupCalls } = withPopup();
  await withBackground(harness, async () => {
    const answers = [];
    // Open with nothing waiting (as after a Download), then losing the focus as the next download starts.
    const closing = openAskPort(harness);
    catchDownload(harness, 111, 'next.zip', answers);
    await settle(() => closing.posted.some(message => message.type === 'dm.askChanged' && message.waiting === 1));
    closing.close(); // closed at once: it never showed it
    await settle(() => openPopupCalls.length === 1);
    assert.deepEqual(log.resumed, [], 'not given to the browser');
    assert.deepEqual(log.windows, []);
    assert.deepEqual((await queue(harness)).map(item => item.fileName), ['next.zip']);

    // The popup that opened again shows it; closing that one after a while gives it back.
    const again = openAskPort(harness);
    await new Promise(resolve => setTimeout(resolve, 520));
    again.close();
    await settle(() => log.resumed.length === 1);
    assert.deepEqual(answers, [111]);
    assert.equal(openPopupCalls.length, 1);
  });
}));

test('EXT.R7: a download shown in the popup for a while goes back to the browser when the popup closes, without opening it again', () => withSeenTime(async () => {
  const { harness, log, openPopupCalls } = withPopup();
  await withBackground(harness, async () => {
    const answers = [];
    const popup = openAskPort(harness);
    catchDownload(harness, 112, 'seen.zip', answers);
    await settle(() => popup.posted.length);
    await new Promise(resolve => setTimeout(resolve, 520));
    popup.close();
    await settle(() => log.resumed.length === 1);
    assert.deepEqual(answers, [112]);
    assert.deepEqual(openPopupCalls, []);
  });
}));

test('EXT.R7 (e2e D1): the popup opens again only once for a download it never showed', () => withSeenTime(async () => {
  const { harness, log, openPopupCalls } = withPopup();
  await withBackground(harness, async () => {
    const answers = [];
    const first = openAskPort(harness);
    catchDownload(harness, 121, 'a.zip', answers);
    await settle(() => first.posted.length);
    first.close();
    await settle(() => openPopupCalls.length === 1);
    // The second popup closes without showing it either: now the browser keeps it.
    openAskPort(harness).close();
    await settle(() => log.resumed.length === 1);
    assert.equal(openPopupCalls.length, 1);
    assert.deepEqual(answers, [121]);
    assert.deepEqual(await queue(harness), []);
  });
}));

test('EXT.R7 (e2e D3, Firefox): a focused folder window (type popup) is not where the toolbar popup opens; the folder window asks', async () => {
  const { harness, log, openPopupCalls } = withPopup(asking, {
    windows: [{ id: 47, type: 'popup', focused: true }, { id: 7, type: 'normal', focused: false }],
  });
  await withBackground(harness, async () => {
    catchDownload(harness, 131, 'a.zip', []);
    await settle(() => log.windows.length === 1);
    assert.deepEqual(openPopupCalls, []);
    assert.match(log.windows[0].url, /handoff\.html\?id=/);
    assert.deepEqual(log.resumed, []);
  });
});

test('EXT.R7: the popup opens in the focused normal window among several', async () => {
  const { harness, openPopupCalls } = withPopup(asking, {
    windows: [{ id: 3, type: 'normal', focused: false }, { id: 9, type: 'normal', focused: true }],
  });
  await withBackground(harness, async () => {
    catchDownload(harness, 141, 'a.zip', []);
    await settle(() => openPopupCalls.length);
    assert.deepEqual(openPopupCalls, [{ windowId: 9 }]);
  });
});

for (const [name, setup, expectWindow] of [
  ['the browser says the popup is gone (runtime.getContexts, Chrome)', (chrome) => { chrome.runtime.getContexts = async () => []; }, false],
  ['the browser says the popup is gone (extension.getViews, Firefox)', (chrome) => { chrome.extension = { getViews: () => [] }; }, false],
  ['the popup page is still there but never connected', (chrome) => { chrome.runtime.getContexts = async () => [{ contextType: 'POPUP' }]; }, true],
]) {
  test(`EXT.R7 (review 3): a popup that never connects: when ${name}, ${expectWindow ? 'folder windows ask' : 'the browser keeps the downloads'}`, async () => {
    const { harness, log, openPopupCalls } = withPopup();
    setup(harness.chrome);
    global.__inboundPopupConnectMs = 20;
    try {
      await withBackground(harness, async () => {
        const answers = [];
        catchDownload(harness, 151, 'a.iso', answers);
        catchDownload(harness, 152, 'b.iso', answers);
        await settle(() => openPopupCalls.length && log.paused.length === 2);
        await new Promise(resolve => setTimeout(resolve, 100));
        if (expectWindow) {
          assert.equal(log.windows.length, 2);
          assert.deepEqual(log.resumed, []);
        } else {
          assert.deepEqual(log.windows, [], 'no folder windows for a popup the user closed');
          assert.deepEqual(log.resumed, [151, 152]);
          assert.deepEqual(answers, [151, 152]);
        }
        assert.deepEqual(await queue(harness), []);
      });
    } finally { delete global.__inboundPopupConnectMs; }
  });
}

test('EXT.R7 (review 1): Choose... from the popup carries its target, file name, folder and start mode to the window', async () => {
  const { harness, log } = withPopup();
  await withBackground(harness, async () => {
    catchDownload(harness, 161, 'a.iso', []);
    await settle(() => log.paused.length);
    openAskPort(harness);
    const [item] = await queue(harness);
    const draft = { target: 'local', fileName: 'renamed.iso', destination: 'E:\\Fav', startMode: 'later', extra: 'dropped' };
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.askInWindow', id: item.id, pick: true, draft }, ownPage('popup.html'))).ok, true);
    assert.match(log.windows[0].url, /pick=1$/);
    const info = sendRuntimeMessage(harness, { type: 'dm.getHandoff', id: item.id }, ownPage('handoff.html'));
    await answerNative(harness, 'settings.downloadFolders', { ok: true, defaultFolder: 'D:\\Downloads', favorites: [] });
    assert.deepEqual((await info).draft, { target: 'local', fileName: 'renamed.iso', destination: 'E:\\Fav', startMode: 'later' });
    assert.equal(harness.session[`handoff.${item.id}`].windowDraft.fileName, 'renamed.iso', 'kept for a worker restart');
  });
});

test('EXT.R7 (review 3, Chromium): openPopup refused after its page was there (closed while loading) gives the downloads back; refused without a page, the windows ask', async () => {
  for (const shownFirst of [true, false]) {
    let contexts = [];
    const { harness, log } = withPopup(asking, { openPopup: async () => {
      if (shownFirst) contexts = [{ contextType: 'POPUP' }];
      await new Promise(resolve => setTimeout(resolve, 60));
      contexts = [];
      throw new Error('Failed to open popup.');
    } });
    harness.chrome.runtime.getContexts = async () => contexts;
    await withBackground(harness, async () => {
      const answers = [];
      catchDownload(harness, 181, 'a.iso', answers);
      await new Promise(resolve => setTimeout(resolve, 150));
      if (shownFirst) {
        assert.deepEqual(log.windows, [], 'the user closed it: no folder window');
        assert.deepEqual(log.resumed, [181]);
        assert.deepEqual(answers, [181]);
      } else {
        assert.equal(log.windows.length, 1);
        assert.deepEqual(log.resumed, []);
      }
      assert.deepEqual(await queue(harness), []);
    });
  }
});

test('EXT.R7 (e2e D1, Firefox): the popup says it is closing before its port closes; a download caught after that is not counted as seen', () => withSeenTime(async () => {
  const { harness, log, openPopupCalls } = withPopup();
  await withBackground(harness, async () => {
    const answers = [];
    const popup = openAskPort(harness);
    catchDownload(harness, 191, 'shown.zip', answers);
    await settle(() => popup.posted.length);
    await new Promise(resolve => setTimeout(resolve, 520));
    // pagehide: the popup is gone now, although Firefox closes the port only about a second later.
    for (const listener of popup.port.onMessage.listeners) listener({ type: 'dm.askClosing' });
    await settle(() => log.resumed.length === 1);
    assert.deepEqual(answers, [191], 'shown long enough: back to the browser at once');
    assert.deepEqual(openPopupCalls, []);
    // A download caught now waits for a new popup, not for the closed one.
    catchDownload(harness, 192, 'late.zip', answers);
    await settle(() => openPopupCalls.length === 1);
    popup.close(); // the late disconnect changes nothing
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.deepEqual(log.resumed, [191]);
    assert.deepEqual((await queue(harness)).map(item => item.fileName), ['late.zip']);
  });
}));

test('EXT.R7: an openPopup call that never answers does not hold the queue: the folder windows ask', async () => {
  const { harness, log } = withPopup(asking, { openPopup: () => new Promise(() => {}) });
  global.__inboundPopupConnectMs = 20;
  try {
    await withBackground(harness, async () => {
      catchDownload(harness, 201, 'a.iso', []);
      await new Promise(resolve => setTimeout(resolve, 80));
      assert.equal(log.windows.length, 1);
      catchDownload(harness, 202, 'b.iso', []);
      await new Promise(resolve => setTimeout(resolve, 80));
      assert.equal(log.windows.length, 2, 'the next download is not stuck behind it either');
    });
  } finally { delete global.__inboundPopupConnectMs; }
});
