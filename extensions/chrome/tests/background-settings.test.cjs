const assert = require('node:assert/strict');
const fs = require('node:fs');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
const esbuild = require('esbuild');

function event() {
  const listeners = [];
  return {
    addListener(listener) { listeners.push(listener); },
    listeners,
  };
}

function makeChrome(initialSettings, pending, pages = new Map()) {
  const values = { settings: structuredClone(initialSettings) };
  const session = { [`handoff.${pending.id}`]: pending };
  const runtimeOnMessage = event();
  const nativeOnMessage = event();
  const nativeMessages = [];
  const nativePorts = [];
  const contextMenuClicked = event();
  const downloadsCreated = event();
  const pausedDownloads = [];
  const tabMessages = [];
  let pageMessageCount = 0;
  const runtimeOnConnect = event();
  const runtime = {
    id: 'test-extension',
    lastError: undefined,
    onMessage: runtimeOnMessage,
    onConnect: runtimeOnConnect,
    onInstalled: event(),
    onStartup: event(),
    getManifest: () => ({ version: '0.0.0-test' }),
    getURL: value => `chrome-extension://test/${value}`,
    connectNative: () => {
      const portIndex = nativePorts.length;
      const port = {
        onMessage: nativeOnMessage,
        onDisconnect: event(),
        // Which connection carried it, so a test can tell the shared one (0) from one-off ones.
        postMessage(message) { message.portIndex = portIndex; nativeMessages.push(message); },
        disconnected: false,
        disconnect() { this.disconnected = true; },
      };
      nativePorts.push(port);
      return port;
    },
  };
  const chrome = {
    runtime,
    storage: {
      local: {
        async get(key) { return { [key]: values[key] }; },
        async set(update) { Object.assign(values, structuredClone(update)); },
      },
      session: {
        async get(key) { return { [key]: session[key] }; },
        async set(update) { Object.assign(session, structuredClone(update)); },
        async remove(key) { delete session[key]; },
      },
    },
    contextMenus: {
      onClicked: contextMenuClicked,
      async removeAll() {},
      create() {},
    },
    downloads: {
      onCreated: event(),
      onDeterminingFilename: downloadsCreated,
      onChanged: event(),
      onErased: event(),
      async search() { return []; },
      async pause(id) { pausedDownloads.push(id); throw new Error('mock pause'); },
    },
    tabs: {
      async query() { return [...pages.keys()].map(id => ({ id })); },
      async sendMessage(id, message) {
        pageMessageCount++;
        tabMessages.push({ id, message });
        const listener = pages.get(id);
        if (!listener) throw new Error('No content script in this tab.');
        return listener(message);
      },
    },
    windows: { async create() {} },
    notifications: { create() {} },
    cookies: { async getAll() { return []; } },
  };
  return {
    chrome, values, session, runtimeOnMessage, runtimeOnConnect, nativeOnMessage, nativeMessages, nativePorts, contextMenuClicked,
    downloadsCreated, pausedDownloads, tabMessages,
    get pageMessageCount() { return pageMessageCount; },
  };
}

async function startBackground(source) {
  const bundled = esbuild.buildSync({
    entryPoints: [source],
    bundle: true,
    write: false,
    platform: 'node',
    format: 'cjs',
    target: 'node20',
  }).outputFiles[0].text;
  const loaded = new Module(source, module);
  loaded.filename = source;
  loaded.paths = Module._nodeModulePaths(path.dirname(source));
  loaded._compile(bundled, source);
  await new Promise(resolve => setImmediate(resolve));
}

function sendRuntimeMessage(harness, message,
  sender = { id: 'test-extension', url: 'chrome-extension://test/options.html', tab: { id: 4 }, frameId: 0 }) {
  return new Promise((resolve, reject) => {
    const handled = harness.runtimeOnMessage.listeners[0](message, sender, resolve);
    if (handled !== true) reject(new Error('Runtime listener did not keep the response channel open.'));
  });
}

/** Waits for the background to send a native message of this type and answers it. */
async function answerNative(harness, type, payload) {
  for (let attempt = 0; attempt < 400; attempt++) {
    const request = harness.nativeMessages.find(message => message.type === type && !message.answered);
    if (request) {
      request.answered = true;
      for (const listener of harness.nativeOnMessage.listeners) listener({ type: `${type}.reply`, replyTo: request.id, payload });
      return request;
    }
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  throw new Error(`No ${type} was sent.`);
}

test('right-click handoff retains its source frame and refreshes matching partition cookies on confirmation', async () => {
  const harness = makeChrome({ enabled: true, autoIntercept: true, startMode: 'now',
    interceptedExtensions: [], excludedExtensions: [], excludedHosts: [], notifications: false }, { id: 'seed' });
  const old = global.chrome;
  const queries = [];
  const key = { topLevelSite: 'https://page.test', hasCrossSiteAncestor: true };
  harness.chrome.cookies = {
    async getAllCookieStores() { return [{ id: 'source-store', tabIds: [4] }]; },
    async getPartitionKey(details) { assert.deepEqual(details, { tabId: 4, frameId: 7 }); return { partitionKey: key }; },
    async getAll(query) { queries.push(query); return query.partitionKey
      ? [{ name: 'cf_clearance', value: 'current-clearance', path: '/', partitionKey: key }]
      : [{ name: 'session', value: 'current-session', path: '/' }]; },
  };
  global.chrome = harness.chrome;
  try {
    await startBackground(path.join(__dirname, '..', 'src', 'background.ts'));
    harness.contextMenuClicked.listeners[0]({ linkUrl: 'https://file.test/download',
      pageUrl: 'https://page.test/', frameUrl: 'https://frame.test/list', frameId: 7 }, { id: 4 });
    for (let attempt = 0; attempt < 20 && Object.keys(harness.session).length < 2; attempt++)
      await new Promise(resolve => setImmediate(resolve));
    const pending = Object.entries(harness.session).find(([key]) => key !== 'handoff.seed')?.[1];
    assert.ok(pending);
    assert.equal(pending.referrer, 'https://frame.test/list');
    assert.equal(pending.pageUrl, 'https://page.test/');
    assert.deepEqual(pending.cookieContext, { storeId: 'source-store', partitionKey: key });
    assert.equal(queries.length, 0, 'cookies should be read fresh at confirmation');
    const confirmed = sendRuntimeMessage(harness, { type: 'dm.confirmHandoff', id: pending.id, destination: '', startMode: 'later' });
    for (let attempt = 0; attempt < 20 && !harness.nativeMessages.length; attempt++)
      await new Promise(resolve => setImmediate(resolve));
    const request = harness.nativeMessages[0];
    assert.equal(request.payload.referrer, 'https://frame.test/list');
    assert.equal(request.payload.headers.Cookie, 'session=current-session; cf_clearance=current-clearance');
    harness.nativeOnMessage.listeners[0]({ type: 'handoff.ack', replyTo: request.id, payload: { accepted: true } });
    assert.equal((await confirmed).ok, true);
  } finally { global.chrome = old; }
});

test('one-off handoff folder is used for that download only', async () => {
  const initialSettings = {
    enabled: true,
    autoIntercept: true,
    startMode: 'now',
    interceptedExtensions: [],
    excludedExtensions: [],
    excludedHosts: [],
    notifications: true,
  };
  const pending = {
    id: 'handoff-1',
    url: 'https://example.test/archive.zip',
    userAgent: 'test-agent',
    isAutoIntercept: false,
    createdAt: Date.now(),
  };
  const harness = makeChrome(initialSettings, pending);
  const oldChrome = global.chrome;
  global.chrome = harness.chrome;

  try {
    const source = path.join(__dirname, '..', 'src', 'background.ts');
    await startBackground(source);

    const folders = { ok: true, defaultFolder: 'D:\\Default', favorites: [{ name: 'Music', path: 'E:\\Music' }] };
    const handoffInfo = sendRuntimeMessage(harness, { type: 'dm.getHandoff', id: pending.id });
    await answerNative(harness, 'settings.downloadFolders', folders);
    const info = await handoffInfo;
    assert.equal(info.defaults.destination, 'D:\\Default');
    assert.deepEqual(info.defaults.favorites, [{ name: 'Music', path: 'E:\\Music' }]);
    assert.equal(info.held, false);

    const response = sendRuntimeMessage(harness, {
      type: 'dm.confirmHandoff',
      id: pending.id,
      destination: 'D:\\This\\Download\\Only',
      startMode: 'later',
    });
    const request = await answerNative(harness, 'handoff.request', { accepted: true });
    assert.equal(request.payload.destinationDirectory, 'D:\\This\\Download\\Only');
    assert.equal((await response).ack.accepted, true);

    // UI.R3: the next download's window starts at the default folder again, not the one-off choice.
    harness.session['handoff.handoff-2'] = { ...pending, id: 'handoff-2', url: 'https://example.test/next.zip' };
    const nextInfo = sendRuntimeMessage(harness, { type: 'dm.getHandoff', id: 'handoff-2' });
    await answerNative(harness, 'settings.downloadFolders', folders);
    assert.equal((await nextInfo).defaults.destination, 'D:\\Default');
    assert.equal(JSON.stringify(harness.values.settings).includes('This\\Download'), false,
      'a one-off folder is never stored in the extension settings');
    assert.equal(harness.values.settings.startMode, 'later');
  } finally {
    global.chrome = oldChrome;
  }
});

test('folder picker rejects page senders and forwards extension requests to the native host', async () => {
  const settings = {
    enabled: true, autoIntercept: true, startMode: 'now',
    interceptedExtensions: [], excludedExtensions: [], excludedHosts: [], notifications: false,
  };
  const pendingHandoff = {
    id: 'picker-source', url: 'https://example.test/archive.zip', userAgent: 'test',
    isAutoIntercept: false, createdAt: Date.now(),
  };
  const harness = makeChrome(settings, pendingHandoff);
  const oldChrome = global.chrome;
  global.chrome = harness.chrome;
  try {
    await startBackground(path.join(__dirname, '..', 'src', 'background.ts'));
    const info = sendRuntimeMessage(harness, { type: 'dm.getHandoff', id: pendingHandoff.id });
    await answerNative(harness, 'settings.downloadFolders', { ok: true, defaultFolder: '', favorites: [] });
    await info;
    const before = harness.nativeMessages.length;
    const rejectedPage = await sendRuntimeMessage(harness, { type: 'dm.pickFolder' }, {
      url: 'https://example.test/', tab: { id: 4 }, frameId: 0,
    });
    const rejectedForgedUrl = await sendRuntimeMessage(harness, { type: 'dm.pickFolder' }, {
      id: 'other-extension', url: harness.chrome.runtime.getURL('handoff.html'),
    });
    assert.equal(rejectedPage.ok, false);
    assert.equal(rejectedForgedUrl.ok, false);
    assert.equal(harness.nativeMessages.length, before);

    const handoff = sendRuntimeMessage(harness, {
      type: 'dm.confirmHandoff', id: pendingHandoff.id,
      fileName: 'archive.zip', destination: '', startMode: 'now',
    });
    await new Promise(resolve => setTimeout(resolve, 50));
    assert.ok(harness.nativeMessages.length >= before + 1, 'regular handoff should use the shared native connection');
    const handoffRequest = harness.nativeMessages[before];
    harness.nativeOnMessage.listeners[0]({
      type: 'handoff.ack', replyTo: handoffRequest.id, payload: { accepted: true },
    });
    assert.equal((await handoff).ack.accepted, true);
    assert.equal(harness.nativePorts.length, 1);

    const picked = sendRuntimeMessage(harness, {
      type: 'dm.pickFolder', initialPath: 'D:\\Existing\\Folder',
    }, {
      id: harness.chrome.runtime.id,
      url: `${harness.chrome.runtime.getURL('handoff.html')}?id=test`,
    });
    await new Promise(resolve => setTimeout(resolve, 50));
    assert.ok(harness.nativeMessages.length >= before + 2, 'folder picker should use its own native connection');
    const request = harness.nativeMessages[before + 1];
    assert.equal(harness.nativePorts.length, 2);
    assert.notEqual(harness.nativePorts[0], harness.nativePorts[1]);
    assert.equal(request.type, 'host.pickFolder');
    assert.deepEqual(request.payload, { initialPath: 'D:\\Existing\\Folder' });
    for (const listener of harness.nativeOnMessage.listeners) listener({
      type: 'host.folderPicked', replyTo: request.id,
      payload: { ok: true, cancelled: true },
    });
    assert.deepEqual(await picked, { ok: true, cancelled: true });
    assert.equal(harness.nativePorts[1].disconnected, true);
  } finally {
    global.chrome = oldChrome;
  }
});

test('page scripts are told whenever automatic sending changes', async () => {
  const settings = { enabled: true, autoIntercept: false, startMode: 'now',
    interceptedExtensions: [], excludedExtensions: [], excludedHosts: [], notifications: false };
  let pageAutoSend = null;
  const page = message => {
    if (message?.type === 'dm.autoSendChanged') pageAutoSend = message.autoSend;
    return { ok: true };
  };
  const harness = makeChrome(settings, {
    id: 'unused', url: '', userAgent: '', isAutoIntercept: false, createdAt: 0,
  }, new Map([[4, page]]));
  const oldChrome = global.chrome;
  global.chrome = harness.chrome;
  try {
    await startBackground(path.join(__dirname, '..', 'src', 'background.ts'));
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.status' })).settings.autoIntercept, false);

    await sendRuntimeMessage(harness, { type: 'dm.saveSettings', settings: { ...settings, autoIntercept: true } });
    assert.equal(pageAutoSend, true);

    await sendRuntimeMessage(harness, { type: 'dm.saveSettings', settings: { ...settings, autoIntercept: false } });
    assert.equal(pageAutoSend, false);

    await sendRuntimeMessage(harness, { type: 'dm.saveSettings', settings: { ...settings, autoIntercept: true } });
    assert.equal(pageAutoSend, true);
    await sendRuntimeMessage(harness, { type: 'dm.saveSettings', settings: { ...settings, enabled: false, autoIntercept: true } });
    assert.equal(pageAutoSend, false);
  } finally {
    global.chrome = oldChrome;
  }
});

test('latest automatic sending choice wins when tab synchronization overlaps', async () => {
  const settings = { enabled: true, autoIntercept: false, startMode: 'now',
    interceptedExtensions: [], excludedExtensions: [], excludedHosts: [], notifications: false };
  let pageAutoSend = false;
  let releaseEnable;
  let markEnableStarted;
  const enableGate = new Promise(resolve => { releaseEnable = resolve; });
  const enableStarted = new Promise(resolve => { markEnableStarted = resolve; });
  const page = async message => {
    if (message?.type !== 'dm.autoSendChanged') return undefined;
    if (message.autoSend === true) {
      markEnableStarted();
      await enableGate;
    }
    pageAutoSend = message.autoSend === true;
    return { ok: true };
  };
  const harness = makeChrome(settings, {
    id: 'unused', url: '', userAgent: '', isAutoIntercept: false, createdAt: 0,
  }, new Map([[4, page]]));
  const oldChrome = global.chrome;
  global.chrome = harness.chrome;
  try {
    await startBackground(path.join(__dirname, '..', 'src', 'background.ts'));
    const enabling = sendRuntimeMessage(harness, {
      type: 'dm.saveSettings', settings: { ...settings, autoIntercept: true },
    });
    await enableStarted;

    const disabling = sendRuntimeMessage(harness, {
      type: 'dm.saveSettings', settings: { ...settings, autoIntercept: false },
    });
    while (harness.values.settings.autoIntercept !== false)
      await new Promise(resolve => setImmediate(resolve));

    releaseEnable();
    await Promise.all([enabling, disabling]);
    assert.equal(harness.values.settings.autoIntercept, false);
    assert.equal(pageAutoSend, false);
  } finally {
    releaseEnable();
    global.chrome = oldChrome;
  }
});

test('download without a Delete gesture is paused for normal bridge capture', async () => {
  const settings = {
    enabled: true, autoIntercept: true, startMode: 'now',
    interceptedExtensions: [], excludedExtensions: [], excludedHosts: [], notifications: false,
  };
  const pages = new Map([[4, () => ({ keep: false })]]);
  const harness = makeChrome(settings, { id: 'unused', url: '', userAgent: '', isAutoIntercept: false, createdAt: 0 }, pages);
  const oldChrome = global.chrome;
  global.chrome = harness.chrome;
  try {
    const source = path.join(__dirname, '..', 'src', 'background.ts');
    await startBackground(source);
    harness.downloadsCreated.listeners[0]({
      id: 18, url: 'https://example.test/file.zip', finalUrl: 'https://example.test/file.zip',
      filename: 'file.zip', mime: 'application/zip', totalBytes: 1,
    });
    while (harness.pageMessageCount < 1) await new Promise(resolve => setImmediate(resolve));
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(harness.pausedDownloads, [18]);
  } finally {
    global.chrome = oldChrome;
  }
});

test('one Delete gesture keeps one matching download and the next is captured', async () => {
  const settings = {
    enabled: true, autoIntercept: true, startMode: 'now',
    interceptedExtensions: [], excludedExtensions: [], excludedHosts: [], notifications: false,
  };
  const url = 'https://example.test/file.zip';
  const gestures = [{ url, expires: Date.now() + 15000 }];
  const page = message => {
    if (message?.type !== 'dm.consumeKeepInBrowser') return undefined;
    const index = gestures.findIndex(item => item.expires > Date.now() && item.url === message.url);
    if (index < 0) return { keep: false };
    gestures.splice(index, 1);
    return { keep: true };
  };
  const harness = makeChrome(settings, { id: 'unused', url: '', userAgent: '', isAutoIntercept: false, createdAt: 0 }, new Map([[4, page]]));
  const oldChrome = global.chrome;
  global.chrome = harness.chrome;
  try {
    await startBackground(path.join(__dirname, '..', 'src', 'background.ts'));
    harness.downloadsCreated.listeners[0]({ id: 21, url, finalUrl: url, filename: 'file.zip', mime: 'application/zip', totalBytes: 1 });
    while (harness.pageMessageCount < 1) await new Promise(resolve => setImmediate(resolve));
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(harness.pausedDownloads, []);

    harness.downloadsCreated.listeners[0]({ id: 22, url, finalUrl: url, filename: 'file.zip', mime: 'application/zip', totalBytes: 1 });
    while (harness.pageMessageCount < 2) await new Promise(resolve => setImmediate(resolve));
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(harness.pausedDownloads, [22]);
  } finally {
    global.chrome = oldChrome;
  }
});

test('gesture in a content script survives a fresh service-worker load', async () => {
  const settings = {
    enabled: true, autoIntercept: true, startMode: 'now',
    interceptedExtensions: [], excludedExtensions: [], excludedHosts: [], notifications: false,
  };
  const url = 'https://example.test/file.zip';
  const gestures = [{ url, expires: Date.now() + 15000 }];
  const pages = new Map([[4, message => {
    if (message?.type !== 'dm.consumeKeepInBrowser') return undefined;
    const index = gestures.findIndex(item => item.expires > Date.now() && item.url === message.url);
    if (index < 0) return { keep: false };
    gestures.splice(index, 1);
    return { keep: true };
  }]]);
  const harness = makeChrome(settings, { id: 'unused', url: '', userAgent: '', isAutoIntercept: false, createdAt: 0 }, pages);
  const oldChrome = global.chrome;
  try {
    // Replace the background's event registry while preserving the page-side script state.
    global.chrome = harness.chrome;
    await startBackground(path.join(__dirname, '..', 'src', 'background.ts'));
    const reloaded = makeChrome(settings, { id: 'unused', url: '', userAgent: '', isAutoIntercept: false, createdAt: 0 }, pages);
    global.chrome = reloaded.chrome;
    await startBackground(path.join(__dirname, '..', 'src', 'background.ts'));
    reloaded.downloadsCreated.listeners[0]({ id: 31, url, finalUrl: url, filename: 'file.zip', mime: 'application/zip', totalBytes: 1 });
    while (reloaded.pageMessageCount < 1) await new Promise(resolve => setImmediate(resolve));
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(reloaded.pausedDownloads, []);
  } finally {
    global.chrome = oldChrome;
  }
});

test('restricted or unloaded pages fall back to normal bridge capture', async () => {
  const settings = {
    enabled: true, autoIntercept: true, startMode: 'now',
    interceptedExtensions: [], excludedExtensions: [], excludedHosts: [], notifications: false,
  };
  const pages = new Map([[4, null]]);
  const harness = makeChrome(settings, { id: 'unused', url: '', userAgent: '', isAutoIntercept: false, createdAt: 0 }, pages);
  const oldChrome = global.chrome;
  global.chrome = harness.chrome;
  try {
    await startBackground(path.join(__dirname, '..', 'src', 'background.ts'));
    harness.downloadsCreated.listeners[0]({ id: 41, url: 'https://example.test/file.zip', finalUrl: 'https://example.test/file.zip', filename: 'file.zip', mime: 'application/zip', totalBytes: 1 });
    while (harness.pageMessageCount < 1) await new Promise(resolve => setImmediate(resolve));
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(harness.pausedDownloads, [41]);
  } finally {
    global.chrome = oldChrome;
  }
});

test('file-name hook holds the download and answers when the browser keeps it', async () => {
  const settings = {
    enabled: true, autoIntercept: true, startMode: 'now',
    interceptedExtensions: ['zip'], excludedExtensions: [], excludedHosts: [], notifications: false,
  };
  const harness = makeChrome(settings, { id: 'unused', url: '', userAgent: '', isAutoIntercept: false, createdAt: 0 }, new Map());
  const oldChrome = global.chrome;
  global.chrome = harness.chrome;
  try {
    await startBackground(path.join(__dirname, '..', 'src', 'background.ts'));
    let answered = 0;
    const held = harness.downloadsCreated.listeners[0](
      { id: 51, url: 'https://example.test/page.html', finalUrl: 'https://example.test/page.html', filename: 'page.html', mime: 'text/html', totalBytes: 1 },
      () => { answered++; });
    assert.equal(held, true, 'listener must return true so the browser waits for the name');
    for (let i = 0; i < 50 && answered === 0; i++) await new Promise(resolve => setTimeout(resolve, 10));
    assert.equal(answered, 1, 'a download left to the browser gets exactly one suggestion');
    assert.deepEqual(harness.pausedDownloads, []);
  } finally {
    global.chrome = oldChrome;
  }
});

const backgroundSource = path.join(__dirname, '..', 'src', 'background.ts');
const ruleSettings = { enabled: true, autoIntercept: true, startMode: 'now',
  interceptedExtensions: [], excludedExtensions: [], excludedHosts: [], notifications: false };
const settle = async (done) => {
  for (let attempt = 0; attempt < 40 && !done(); attempt++) await new Promise(resolve => setImmediate(resolve));
};
const startDownload = (harness, item) => new Promise(resolve => {
  harness.downloadsCreated.listeners[0]({ id: 1, filename: 'a.zip', referrer: '', mime: '', totalBytes: 1, fileSize: 1, ...item }, resolve);
});
async function withBackground(harness, run) {
  const old = global.chrome;
  global.chrome = harness.chrome;
  try {
    await startBackground(backgroundSource);
    await run();
  } finally { global.chrome = old; }
}

test('a fresh install opens Settings and catches nothing until the user turns it on', async () => {
  const harness = makeChrome(undefined, { id: 'seed' });
  let opened = 0;
  harness.chrome.runtime.openOptionsPage = async () => { opened++; };
  await withBackground(harness, async () => {
    harness.chrome.runtime.onInstalled.listeners[0]({ reason: 'install' });
    assert.equal(opened, 1);
    await startDownload(harness, { url: 'https://file.test/a.zip' });
    assert.deepEqual(harness.pausedDownloads, []);
    assert.equal(harness.nativeMessages.length, 0);
    assert.equal(harness.values.settings, undefined);
  });
});

test('an upgrade from before catching was a choice keeps it on, once', async () => {
  const relied = makeChrome(undefined, { id: 'seed' });
  await withBackground(relied, async () => {
    relied.chrome.runtime.onInstalled.listeners[0]({ reason: 'update', previousVersion: '0.3.0' });
    await settle(() => relied.values.settings);
    assert.equal(relied.values.settings.autoIntercept, true);
  });

  // Someone who chose is not overruled.
  const chose = makeChrome({ ...ruleSettings, autoIntercept: false }, { id: 'seed' });
  await withBackground(chose, async () => {
    chose.chrome.runtime.onInstalled.listeners[0]({ reason: 'update', previousVersion: '0.3.0' });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(chose.values.settings.autoIntercept, false);
  });

  // Updating from a version that already asks changes nothing.
  const current = makeChrome(undefined, { id: 'seed' });
  await withBackground(current, async () => {
    current.chrome.runtime.onInstalled.listeners[0]({ reason: 'update', previousVersion: '0.4.0' });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(current.values.settings, undefined);
  });
});

test('YouTube stays out of every way of sending a download', async () => {
  const pages = new Map([[4, () => ({ ok: true, pageUrl: 'https://page.test/', links: [
    { url: 'https://www.youtube.com/watch?v=1', text: 'video' },
    { url: 'https://i.ytimg.com/vi/1/hq.jpg', text: 'thumbnail' },
    { url: 'https://file.test/a.zip', text: 'archive' },
  ] })]]);
  const harness = makeChrome(ruleSettings, { id: 'seed' }, pages);
  let windowsOpened = 0;
  harness.chrome.windows.create = async () => { windowsOpened++; };
  await withBackground(harness, async () => {
    harness.contextMenuClicked.listeners[0]({ linkUrl: 'https://www.youtube.com/watch?v=1', pageUrl: 'https://www.youtube.com/' }, { id: 4 });
    harness.contextMenuClicked.listeners[0]({ linkUrl: 'https://file.test/a.zip', pageUrl: 'https://m.youtube.com/watch?v=1' }, { id: 4 });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(windowsOpened, 0);

    await startDownload(harness, { url: 'https://rr1.googlevideo.com/videoplayback' });
    await startDownload(harness, { url: 'https://file.test/b.zip', referrer: 'https://www.youtube.com/watch?v=1' });
    assert.deepEqual(harness.pausedDownloads, []);
    await startDownload(harness, { url: 'https://file.test/c.zip' });
    assert.equal(harness.pausedDownloads.length, 1, 'an ordinary download is still caught');

    const links = await sendRuntimeMessage(harness, { type: 'dm.pageLinks', tabId: 4 });
    assert.deepEqual(links.links.map(link => link.url), ['https://file.test/a.zip']);
  });
});

test('pages can reach only what the content script needs', async () => {
  const harness = makeChrome({ ...ruleSettings, autoIntercept: false }, { id: 'seed' });
  const page = { tab: { id: 4 }, frameId: 0, url: 'https://evil.test/' };
  await withBackground(harness, async () => {
    for (const message of [
      { type: 'dm.saveSettings', settings: { autoIntercept: true } },
      { type: 'dm.confirmBatch', urls: ['https://bank.test/statement.pdf'] },
      { type: 'dm.getHandoff', id: 'seed' },
      { type: 'dm.jobs' },
    ]) {
      assert.equal((await sendRuntimeMessage(harness, message, page)).ok, false, message.type);
    }
    assert.equal(harness.values.settings.autoIntercept, false);

    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.getAutoSend' }, page)).autoSend, false);

    const saved = await sendRuntimeMessage(harness, { type: 'dm.saveSettings', settings: { ...ruleSettings } },
      { id: 'test-extension', url: 'chrome-extension://test/options.html' });
    assert.equal(saved.ok, true);
    assert.equal(harness.values.settings.autoIntercept, true);
  });
});

test('an excluded site stays out however the download got there', async () => {
  const harness = makeChrome({ ...ruleSettings, excludedHosts: ['bank.test'] }, { id: 'seed' });
  await withBackground(harness, async () => {
    await startDownload(harness, { url: 'https://bank.test/a.zip', finalUrl: 'https://cdn.test/a.zip' });
    await startDownload(harness, { url: 'https://cdn.test/b.zip', referrer: 'https://bank.test/statements' });
    await startDownload(harness, { url: 'https://cdn.test/c.zip', finalUrl: 'https://files.bank.test/c.zip' });
    assert.deepEqual(harness.pausedDownloads, []);
    await startDownload(harness, { url: 'https://cdn.test/d.zip' });
    assert.equal(harness.pausedDownloads.length, 1);
  });
});

test('Download All on a YouTube page lists nothing', async () => {
  const pages = new Map([[4, () => ({ ok: true, pageUrl: 'https://www.youtube.com/watch?v=1', links: [
    { url: 'https://file.test/a.zip', text: 'archive' },
  ] })]]);
  const harness = makeChrome(ruleSettings, { id: 'seed' }, pages);
  await withBackground(harness, async () => {
    assert.deepEqual((await sendRuntimeMessage(harness, { type: 'dm.pageLinks', tabId: 4 })).links, []);
  });
});

test('page scripts learn whether automatic sending is on, and a sender with no address gets nothing else', async () => {
  const harness = makeChrome({ ...ruleSettings, autoIntercept: false }, { id: 'seed' });
  await withBackground(harness, async () => {
    const page = { id: 'test-extension', tab: { id: 4 }, url: 'https://page.test/' };
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.getAutoSend' }, page)).autoSend, false);
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.saveSettings', settings: {} }, { tab: { id: 4 } })).ok, false);
  });
});

test('a missing desktop app is explained in plain words wherever it shows', async () => {
  const harness = makeChrome(ruleSettings, { id: 'seed' });
  harness.chrome.runtime.connectNative = () => { throw new Error('Specified native messaging host not found.'); };
  await withBackground(harness, async () => {
    const opened = await sendRuntimeMessage(harness, { type: 'dm.openApp' });
    assert.equal(opened.ok, false);
    assert.match(opened.error, /needs the Inbound Download Manager desktop app/);
    assert.doesNotMatch(opened.error, /native/i);
  });
});

test('turning the connection off also turns off automatic sending', async () => {
  const harness = makeChrome({ ...ruleSettings, enabled: false }, { id: 'seed' });
  await withBackground(harness, async () => {
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.getAutoSend' })).autoSend, false);
  });
});

test('a media setting stored by an older copy is ignored, and the media requests are gone', async () => {
  const harness = makeChrome({ ...ruleSettings, autoIntercept: false, mediaDetectionEnabled: true }, { id: 'seed' });
  await withBackground(harness, async () => {
    assert.deepEqual(await sendRuntimeMessage(harness, { type: 'dm.getAutoSend' }), { ok: true, autoSend: false });
    for (const type of ['dm.getMediaDetectionEnabled', 'dm.mediaDetected', 'dm.media', 'dm.queueMedia', 'dm.rescan']) {
      assert.deepEqual(await sendRuntimeMessage(harness, { type }), { ok: false, error: 'Unknown message.' }, type);
    }
  });
});

test('the extension makes no network requests of its own', () => {
  const sourceDir = path.join(__dirname, '..', 'src');
  for (const name of fs.readdirSync(sourceDir)) {
    assert.doesNotMatch(fs.readFileSync(path.join(sourceDir, name), 'utf8'),
      /\bfetch\s*\(|XMLHttpRequest|WebSocket|EventSource|sendBeacon/, name);
  }
});

// ---- EXT.B1, EXT.R1, EXT.R2, EXT.E1 ------------------------------------------------------------

/** A harness whose downloads can really be paused, resumed, cancelled and erased. */
function capturingChrome(initialSettings) {
  const harness = makeChrome(initialSettings, { id: 'seed' });
  const log = { paused: [], resumed: [], cancelled: [], erased: [], notes: [], windows: [], badge: [], downloaded: [] };
  const gone = new Set();
  Object.assign(harness.chrome.downloads, {
    async pause(id) { log.paused.push(id); },
    async resume(id) { log.resumed.push(id); },
    async cancel(id) { log.cancelled.push(id); gone.add(id); },
    async erase({ id }) { log.erased.push(id); },
    async search({ id }) { return gone.has(id) ? [] : [{ id, state: 'in_progress' }]; },
    async download(options) { log.downloaded.push(options); return 100 + log.downloaded.length; },
  });
  harness.chrome.notifications.create = (options) => { log.notes.push(options); };
  harness.chrome.windows = {
    async create(options) { log.windows.push(options); return { id: 900 + log.windows.length }; },
    onRemoved: event(),
  };
  harness.chrome.action = {
    async setBadgeText(details) { log.badge.push(details.text); },
    async setBadgeBackgroundColor() {},
    async setTitle() {},
  };
  return { harness, log };
}

const catching = { enabled: true, autoIntercept: true, startMode: 'now', destinationMode: 'default',
  interceptedExtensions: [], excludedExtensions: ['html', 'jpg'], excludedHosts: [], notifications: true, rulesVersion: 2 };
const ownPage = (page) => ({ id: 'test-extension', url: `chrome-extension://test/${page}` });

test('EXT.B1: a .flac from a file host is caught with the default rules, cookies of its final address included', async () => {
  const { harness, log } = capturingChrome(catching);
  harness.chrome.cookies = { async getAll(query) {
    return query.url.includes('/files/') ? [{ name: 'file_token', value: 't', path: '/files', domain: 'host.test' }]
      : [{ name: 'session', value: 's', path: '/', domain: 'host.test' }];
  } };
  await withBackground(harness, async () => {
    const answered = startDownload(harness, { id: 7, url: 'https://host.test/dl?id=9', finalUrl: 'https://host.test/files/track.flac', filename: 'track.flac' });
    const request = await answerNative(harness, 'handoff.request', { accepted: true });
    assert.equal(request.payload.suggestedFileName, 'track.flac');
    assert.equal(request.payload.finalUrl, 'https://host.test/files/track.flac');
    assert.equal(request.payload.headers.Cookie, 'file_token=t; session=s');
    await settle(() => log.cancelled.length);
    assert.deepEqual(log.paused, [7]);
    assert.deepEqual(log.cancelled, [7]);
    // Accepted: the browser is never told a file name.
    const raced = await Promise.race([answered.then(() => 'answered'), new Promise(resolve => setTimeout(() => resolve('held'), 50))]);
    assert.equal(raced, 'held');
  });
});

test('EXT.B1: a copy still on the old allow-list catches a .flac after the update', async () => {
  const legacy = { enabled: true, autoIntercept: true, startMode: 'now', notifications: false, excludedExtensions: [], excludedHosts: [],
    interceptedExtensions: ['3gp', '7z', 'aac', 'ace', 'aif', 'apk', 'arj', 'asf', 'avi', 'bin', 'bz2', 'exe', 'gz', 'gzip', 'img', 'iso', 'lzh',
      'm4a', 'm4v', 'mkv', 'mov', 'mp3', 'mpa', 'mpe', 'mpeg', 'mpg', 'msi', 'msu', 'ogg', 'ogv', 'plj', 'pps', 'ppt', 'qt', 'ra', 'rar', 'rm',
      'rmvb', 'sea', 'sit', 'sitx', 'tar', 'wav', 'wma', 'wmv', 'z', 'zip'] };
  const { harness, log } = capturingChrome(legacy);
  await withBackground(harness, async () => {
    void startDownload(harness, { id: 3, url: 'https://host.test/track.flac', filename: 'track.flac' });
    await answerNative(harness, 'handoff.request', { accepted: true });
    assert.deepEqual(log.paused, [3]);
    assert.deepEqual(harness.values.settings.interceptedExtensions, []);
    assert.equal(harness.values.settings.rulesVersion, 2);
  });
});

test('a download left by a rule says why; default exclusions and blob: downloads stay quiet', async () => {
  const { harness, log } = capturingChrome({ ...catching, excludedHosts: ['bank.test'] });
  await withBackground(harness, async () => {
    await startDownload(harness, { id: 1, url: 'https://bank.test/statement.pdf', filename: 'statement.pdf' });
    assert.equal(log.notes.length, 1);
    assert.equal(log.notes[0].title, 'Left in your browser');
    assert.match(log.notes[0].message, /statement\.pdf: bank\.test is on your list of sites never to catch/);

    await startDownload(harness, { id: 2, url: 'https://site.test/photo.jpg', filename: 'photo.jpg' });
    await startDownload(harness, { id: 3, url: 'blob:https://site.test/77', filename: 'made-by-page.zip' });
    assert.equal(log.notes.length, 1, 'a default exclusion and a blob: download raise no notification');
    assert.deepEqual(log.paused, []);

    const status = await sendRuntimeMessage(harness, { type: 'dm.status' });
    assert.deepEqual(status.leftInBrowser.map(note => note.fileName), ['photo.jpg', 'statement.pdf']);
    assert.match(status.leftInBrowser[0].reason, /\.jpg files are on your list/);
  });
});

test('automatic sending off: the badge says OFF, and the first download of the session says why', async () => {
  const { harness, log } = capturingChrome({ ...catching, autoIntercept: false });
  await withBackground(harness, async () => {
    assert.equal(log.badge.at(-1), 'OFF');
    await startDownload(harness, { id: 1, url: 'https://site.test/a.zip', filename: 'a.zip' });
    await startDownload(harness, { id: 2, url: 'https://site.test/b.zip', filename: 'b.zip' });
    assert.equal(log.notes.length, 1);
    assert.match(log.notes[0].message, /a\.zip: Automatic sending is off/);
    const status = await sendRuntimeMessage(harness, { type: 'dm.status' });
    assert.equal(status.automaticSending, false);
    assert.equal(status.leftInBrowser.length, 2);

    await sendRuntimeMessage(harness, { type: 'dm.saveSettings', settings: { ...catching } });
    assert.equal(log.badge.at(-1), '');
    await sendRuntimeMessage(harness, { type: 'dm.pauseCatching' });
    assert.equal(log.badge.at(-1), 'OFF');
  });
});

test('EXT.R1: "ask each time" holds the download until the folder window answers', async () => {
  const { harness, log } = capturingChrome({ ...catching, destinationMode: 'ask' });
  await withBackground(harness, async () => {
    let answered = 0;
    const handled = harness.downloadsCreated.listeners[0](
      { id: 21, url: 'https://site.test/a.iso', filename: 'a.iso', referrer: '', mime: '', totalBytes: 10, fileSize: 10 }, () => { answered++; });
    assert.equal(handled, true);
    await settle(() => log.windows.length);
    assert.deepEqual(log.paused, [21]);
    assert.match(log.windows[0].url, /handoff\.html\?id=/);
    const id = decodeURIComponent(log.windows[0].url.split('id=')[1]);

    const info = sendRuntimeMessage(harness, { type: 'dm.getHandoff', id }, ownPage('handoff.html'));
    await answerNative(harness, 'settings.downloadFolders', { ok: true, defaultFolder: 'D:\\Downloads', favorites: [] });
    const shown = await info;
    assert.equal(shown.held, true);
    assert.equal(shown.defaults.destination, 'D:\\Downloads');

    const confirmed = sendRuntimeMessage(harness, { type: 'dm.confirmHandoff', id, destination: 'E:\\Other', startMode: 'now' }, ownPage('handoff.html'));
    const request = await answerNative(harness, 'handoff.request', { accepted: true });
    assert.equal(request.payload.isAutoIntercept, true);
    assert.equal(request.payload.destinationDirectory, 'E:\\Other');
    assert.equal((await confirmed).ack.accepted, true);
    assert.deepEqual(log.cancelled, [21]);
    assert.deepEqual(log.resumed, []);
    assert.equal(answered, 0);
  });
});

test('EXT.R1: Keep in browser, or closing the window, gives the held download back', async () => {
  const { harness, log } = capturingChrome({ ...catching, destinationMode: 'ask' });
  await withBackground(harness, async () => {
    let answered = 0;
    harness.downloadsCreated.listeners[0]({ id: 31, url: 'https://site.test/a.iso', filename: 'a.iso' }, () => { answered++; });
    await settle(() => log.windows.length);
    const id = decodeURIComponent(log.windows[0].url.split('id=')[1]);
    await sendRuntimeMessage(harness, { type: 'dm.keepInBrowser', id }, ownPage('handoff.html'));
    assert.deepEqual(log.resumed, [31]);
    assert.equal(answered, 1);
    assert.equal(harness.session[`handoff.${id}`], undefined);

    harness.downloadsCreated.listeners[0]({ id: 32, url: 'https://site.test/b.iso', filename: 'b.iso' }, () => { answered++; });
    await settle(() => log.windows.length === 2);
    const second = decodeURIComponent(log.windows[1].url.split('id=')[1]);
    const window = openHandoffPort(harness, second);
    assert.equal(window.port.disconnected, false);
    window.close();
    await settle(() => log.resumed.length === 2);
    assert.deepEqual(log.resumed, [31, 32]);
    assert.equal(answered, 2);
    assert.deepEqual(log.cancelled, []);
  });
});

/** The folder window's port to the worker, as handoff.ts opens it. close() is the window closing. */
function openHandoffPort(harness, id, url = `chrome-extension://test/handoff.html?id=${encodeURIComponent(id)}`, senderId = 'test-extension') {
  const port = { name: `dm.handoff:${id}`, sender: { id: senderId, url }, onMessage: event(), onDisconnect: event(),
    disconnected: false, disconnect() { this.disconnected = true; }, postMessage() {} };
  for (const listener of harness.runtimeOnConnect.listeners) listener(port);
  return { port, close() { for (const listener of port.onDisconnect.listeners) listener(port); } };
}

test('closing a browser window wakes nothing: no windows.onRemoved listener, and only the handoff page may hold a port', async () => {
  const { harness, log } = capturingChrome({ ...catching, destinationMode: 'ask' });
  await withBackground(harness, async () => {
    assert.equal(harness.chrome.windows.onRemoved.listeners.length, 0);
    harness.downloadsCreated.listeners[0]({ id: 41, url: 'https://site.test/a.iso', filename: 'a.iso' }, () => {});
    await settle(() => log.windows.length);
    const id = decodeURIComponent(log.windows[0].url.split('id=')[1]);
    // Another page of this extension, or another extension, naming the same request is turned away,
    // and its port closing gives nothing back.
    for (const forged of [openHandoffPort(harness, id, 'chrome-extension://test/popup.html'),
      openHandoffPort(harness, id, 'chrome-extension://test/handoff.html', 'other-extension')]) {
      assert.equal(forged.port.disconnected, true);
      assert.equal(forged.port.onDisconnect.listeners.length, 0);
    }
    assert.deepEqual(log.resumed, []);
    assert.ok(harness.session[`handoff.${id}`]);
  });
});

test('EXT.R1: a worker that restarted with the folder window open starts the download again in the browser', async () => {
  const { harness, log } = capturingChrome({ ...catching, destinationMode: 'ask' });
  // Left by the worker before it restarted: the browser download is paused and its name callback gone.
  harness.session['handoff.lost'] = { id: 'lost', url: 'https://site.test/a.iso', suggestedFileName: 'a.iso', userAgent: 'x',
    isAutoIntercept: true, createdAt: Date.now(), browserDownloadId: 61 };
  await withBackground(harness, async () => {
    openHandoffPort(harness, 'lost').close();
    await settle(() => log.downloaded.length);
    assert.deepEqual(log.cancelled, [61]);
    assert.deepEqual(log.erased, [61]);
    assert.deepEqual(log.downloaded, [{ url: 'https://site.test/a.iso', filename: 'a.iso' }]);
    assert.equal(harness.session['handoff.lost'], undefined);
    // The new browser download is let through at once, not caught again.
    let answered = 0;
    const result = harness.downloadsCreated.listeners[0]({ id: 62, url: 'https://site.test/a.iso', filename: 'a.iso' }, () => { answered++; });
    assert.notEqual(result, true);
    assert.equal(answered, 1);
    assert.deepEqual(log.paused, []);
    // Only once: the same address later is caught as usual.
    assert.equal(harness.downloadsCreated.listeners[0]({ id: 63, url: 'https://site.test/a.iso', filename: 'a.iso' }, () => {}), true);
  });
});

test('EXT.R1: closing the folder window while the request is being sent waits for the answer', async () => {
  const { harness, log } = capturingChrome({ ...catching, destinationMode: 'ask' });
  await withBackground(harness, async () => {
    for (const [downloadId, accepted] of [[71, true], [72, false]]) {
      let answered = 0;
      harness.downloadsCreated.listeners[0]({ id: downloadId, url: `https://site.test/${downloadId}.iso`, filename: 'a.iso' }, () => { answered++; });
      await settle(() => log.windows.length === downloadId - 70);
      const id = decodeURIComponent(log.windows.at(-1).url.split('id=')[1]);
      const window = openHandoffPort(harness, id);
      const confirmed = sendRuntimeMessage(harness, { type: 'dm.confirmHandoff', id, destination: '', startMode: 'now' }, ownPage('handoff.html'));
      await settle(() => harness.nativeMessages.some(message => message.type === 'handoff.request' && !message.answered));
      window.close();
      await new Promise(resolve => setTimeout(resolve, 10));
      assert.equal(log.resumed.includes(downloadId), false, 'nothing is given back while the app may still take it');
      await answerNative(harness, 'handoff.request', { accepted, error: accepted ? undefined : 'No.' });
      await confirmed;
      if (accepted) {
        assert.ok(log.cancelled.includes(downloadId));
        assert.equal(log.resumed.includes(downloadId), false);
        assert.equal(answered, 0);
      } else {
        // Nobody is left to choose: the browser keeps it.
        await settle(() => log.resumed.includes(downloadId));
        assert.ok(log.resumed.includes(downloadId));
        assert.equal(answered, 1);
      }
    }
  });
});

test('EXT.R2: pause, resume, retry and delete reach the app, deleting the file only when asked', async () => {
  const { harness } = capturingChrome(catching);
  const job = '6f1c3c2e-8a4b-4c7d-9e2f-1a2b3c4d5e6f';
  await withBackground(harness, async () => {
    const cases = [
      ['pause', true, 'job.pause', false], ['resume', false, 'job.resume', false], ['retry', false, 'job.retry', false],
      ['remove', false, 'job.remove', false], ['remove', true, 'job.remove', true]];
    for (const [action, deleteFile, type, expectedDelete] of cases) {
      const reply = sendRuntimeMessage(harness, { type: 'dm.jobAction', action, jobId: job, deleteFile }, ownPage('popup.html'));
      const request = await answerNative(harness, type, { ok: true, job: { id: job } });
      assert.deepEqual(request.payload, { jobId: job, deleteFile: expectedDelete }, `${action} ${deleteFile}`);
      assert.equal((await reply).ok, true);
    }
    const before = harness.nativeMessages.length;
    for (const message of [{ action: 'shutdown', jobId: job }, { action: 'remove', jobId: 'not-a-job' }, { action: 'constructor', jobId: job }]) {
      assert.equal((await sendRuntimeMessage(harness, { type: 'dm.jobAction', ...message }, ownPage('popup.html'))).ok, false);
    }
    // A web page cannot ask at all.
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.jobAction', action: 'remove', jobId: job, deleteFile: true },
      { id: 'test-extension', url: 'https://evil.test/', tab: { id: 4 } })).ok, false);
    assert.equal(harness.nativeMessages.length, before);

    // The app's refusal comes back as an error.
    const refused = sendRuntimeMessage(harness, { type: 'dm.jobAction', action: 'pause', jobId: job }, ownPage('popup.html'));
    await answerNative(harness, 'job.pause', { ok: false, error: 'Job not found.' });
    assert.deepEqual(await refused, { ok: false, error: 'Job not found.' });
  });
});

test('EXT.R2: the popup gets the newest 20 of all downloads', async () => {
  const { harness } = capturingChrome(catching);
  await withBackground(harness, async () => {
    const reply = sendRuntimeMessage(harness, { type: 'dm.jobs' }, ownPage('popup.html'));
    const jobs = Array.from({ length: 25 }, (_, index) => ({ id: `id-${index}`, fileName: `f${index}`, state: 'completed', receivedBytes: 1,
      createdAt: new Date(Date.UTC(2026, 0, 1, index)).toISOString() }));
    await answerNative(harness, 'jobs.query', { jobs });
    const shown = (await reply).jobs;
    assert.equal(shown.length, 20);
    assert.equal(shown[0].fileName, 'f24');
  });
});

test('EXT.E1: without onDeterminingFilename (Firefox) a download is caught from onCreated', async () => {
  const { harness, log } = capturingChrome(catching);
  delete harness.chrome.downloads.onDeterminingFilename;
  await withBackground(harness, async () => {
    assert.equal(harness.chrome.downloads.onCreated.listeners.length, 1);
    harness.chrome.downloads.onCreated.listeners[0]({ id: 5, url: 'https://site.test/book.epub', filename: 'C:\\Users\\me\\Downloads\\book.epub',
      referrer: '', mime: '', totalBytes: 1, fileSize: 1 });
    const request = await answerNative(harness, 'handoff.request', { accepted: true });
    assert.equal(request.payload.suggestedFileName, 'book.epub');
    await settle(() => log.cancelled.length);
    assert.deepEqual(log.paused, [5]);
    assert.deepEqual(log.cancelled, [5]);
  });
});

/** Firefox: no file-name hook, so downloads come from onCreated. */
function firefoxChrome(settings = catching) {
  const made = capturingChrome(settings);
  delete made.harness.chrome.downloads.onDeterminingFilename;
  return made;
}

const firefoxItem = (id, fileName) => ({ id, url: `https://site.test/${fileName}`, filename: fileName, referrer: '', mime: '', totalBytes: 1, fileSize: 1 });

test('EXT.E1: on Firefox a download that will not pause is left in the browser, never sent while it still arrives', async () => {
  const { harness, log } = firefoxChrome();
  harness.chrome.downloads.pause = async () => { throw new Error('cannot pause'); };
  await withBackground(harness, async () => {
    harness.chrome.downloads.onCreated.listeners[0](firefoxItem(8, 'big.iso'));
    await settle(() => log.notes.length);
    await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(harness.nativeMessages.some(message => message.type === 'handoff.request'), false);
    assert.match(log.notes[0].message, /big\.iso: The browser would not pause it/);

    // Already finished: said so, nothing sent.
    harness.chrome.downloads.search = async ({ id }) => [{ id, state: 'complete' }];
    harness.chrome.downloads.onCreated.listeners[0](firefoxItem(9, 'small.zip'));
    await settle(() => log.notes.length === 2);
    assert.match(log.notes[1].message, /small\.zip: It finished in the browser before it could be sent/);
    assert.equal(harness.nativeMessages.some(message => message.type === 'handoff.request'), false);
  });
});

test('EXT.E1: on Firefox the download is paused before the tabs are asked about a Delete gesture', async () => {
  const { harness, log } = firefoxChrome();
  const order = [];
  harness.chrome.downloads.pause = async (id) => { order.push('pause'); log.paused.push(id); };
  harness.chrome.tabs.query = async () => { order.push('tabs'); return [{ id: 4 }]; };
  harness.chrome.tabs.sendMessage = async () => ({ keep: true });
  await withBackground(harness, async () => {
    const before = order.length;
    harness.chrome.downloads.onCreated.listeners[0](firefoxItem(10, 'a.zip'));
    await settle(() => log.resumed.length);
    assert.deepEqual(order.slice(before, before + 2), ['pause', 'tabs']);
    // A Delete gesture still keeps it in the browser, which continues it.
    assert.deepEqual(log.resumed, [10]);
    assert.equal(harness.nativeMessages.some(message => message.type === 'handoff.request'), false);
  });
});

test('EXT.E1: on Firefox a refused download the browser cannot continue gets a note', async () => {
  const { harness, log } = firefoxChrome();
  harness.chrome.downloads.resume = async () => { throw new Error('Download cannot be resumed'); };
  harness.chrome.downloads.search = async ({ id }) => [{ id, state: 'interrupted', paused: false }];
  await withBackground(harness, async () => {
    harness.chrome.downloads.onCreated.listeners[0](firefoxItem(11, 'a.zip'));
    await answerNative(harness, 'handoff.request', { accepted: false, error: 'Disk full.' });
    await settle(() => log.notes.length);
    assert.match(log.notes.at(-1).message, /Disk full\. The browser could not continue it/);
    const status = await sendRuntimeMessage(harness, { type: 'dm.status' }, ownPage('popup.html'));
    assert.match(status.leftInBrowser[0].reason, /could not continue it/);
  });
});

test('EXT.R2: popup actions use a connection of their own, so a slow delete cannot hold up a handoff', async () => {
  const { harness, log } = capturingChrome(catching);
  const job = '6f1c3c2e-8a4b-4c7d-9e2f-1a2b3c4d5e6f';
  await withBackground(harness, async () => {
    // The shared connection is open (a handoff used it).
    void startDownload(harness, { id: 12, url: 'https://site.test/first.zip', filename: 'first.zip' });
    const first = await answerNative(harness, 'handoff.request', { accepted: true });
    // A delete the app is slow to answer.
    const removing = sendRuntimeMessage(harness, { type: 'dm.jobAction', action: 'remove', jobId: job, deleteFile: true }, ownPage('popup.html'));
    await settle(() => harness.nativeMessages.some(message => message.type === 'job.remove'));
    const remove = harness.nativeMessages.find(message => message.type === 'job.remove');
    assert.notEqual(remove.portIndex, first.portIndex);
    // A handoff meanwhile goes out on the shared connection and is answered first.
    void startDownload(harness, { id: 13, url: 'https://site.test/second.zip', filename: 'second.zip' });
    const second = await answerNative(harness, 'handoff.request', { accepted: true });
    assert.equal(second.portIndex, first.portIndex);
    await settle(() => log.cancelled.length === 2);
    await answerNative(harness, 'job.remove', { ok: true, job: { id: job } });
    assert.equal((await removing).ok, true);
    assert.equal(harness.nativePorts[remove.portIndex].disconnected, true, 'the one-off connection is closed after its answer');
  });
});
