const assert = require('node:assert/strict');
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
  const badgeUpdates = [];
  const tabMessages = [];
  let pageMessageCount = 0;
  const runtime = {
    id: 'test-extension',
    lastError: undefined,
    onMessage: runtimeOnMessage,
    onInstalled: event(),
    onStartup: event(),
    getManifest: () => ({ version: '0.0.0-test' }),
    getURL: value => `chrome-extension://test/${value}`,
    connectNative: () => {
      const port = {
        onMessage: nativeOnMessage,
        onDisconnect: event(),
        postMessage(message) { nativeMessages.push(message); },
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
      onRemoved: event(),
      onUpdated: event(),
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
    action: {
      async setBadgeText(update) { badgeUpdates.push(update); },
      async setBadgeBackgroundColor() {},
    },
    cookies: { async getAll() { return []; } },
  };
  return {
    chrome, values, session, runtimeOnMessage, nativeOnMessage, nativeMessages, nativePorts, contextMenuClicked,
    downloadsCreated, pausedDownloads, badgeUpdates, tabMessages,
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

    const handoffInfo = await sendRuntimeMessage(harness, { type: 'dm.getHandoff', id: pending.id });
    assert.equal(handoffInfo.defaults.destination, '');

    const response = sendRuntimeMessage(harness, {
      type: 'dm.confirmHandoff',
      id: pending.id,
      destination: 'D:\\This\\Download\\Only',
      startMode: 'later',
    });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(harness.nativeMessages.length, 1);
    assert.equal(harness.nativeMessages[0].payload.destinationDirectory, 'D:\\This\\Download\\Only');
    harness.nativeOnMessage.listeners[0]({
      type: 'handoff.ack',
      replyTo: harness.nativeMessages[0].id,
      payload: { accepted: true },
    });
    assert.equal((await response).ack.accepted, true);
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
    await sendRuntimeMessage(harness, { type: 'dm.getHandoff', id: pendingHandoff.id });
    const rejectedPage = await sendRuntimeMessage(harness, { type: 'dm.pickFolder' }, {
      url: 'https://example.test/', tab: { id: 4 }, frameId: 0,
    });
    const rejectedForgedUrl = await sendRuntimeMessage(harness, { type: 'dm.pickFolder' }, {
      id: 'other-extension', url: harness.chrome.runtime.getURL('handoff.html'),
    });
    assert.equal(rejectedPage.ok, false);
    assert.equal(rejectedForgedUrl.ok, false);
    assert.equal(harness.nativeMessages.length, 0);

    const handoff = sendRuntimeMessage(harness, {
      type: 'dm.confirmHandoff', id: pendingHandoff.id,
      fileName: 'archive.zip', destination: '', startMode: 'now',
    });
    await new Promise(resolve => setTimeout(resolve, 50));
    assert.ok(harness.nativeMessages.length >= 1, 'regular handoff should open the shared native connection');
    const handoffRequest = harness.nativeMessages[0];
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
    assert.ok(harness.nativeMessages.length >= 2, 'folder picker should use its own native connection');
    const request = harness.nativeMessages[1];
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

test('media detection defaults off and can be enabled then disabled', async () => {
  const settings = {
    enabled: true,
    autoIntercept: true,
    startMode: 'now',
    interceptedExtensions: [],
    excludedExtensions: [],
    excludedHosts: [],
    notifications: false,
  };
  let pageMediaEnabled = null;
  const page = message => {
    if (message?.type === 'dm.mediaDetectionChanged') {
      pageMediaEnabled = message.enabled;
      return { ok: true, enabled: pageMediaEnabled };
    }
    return undefined;
  };
  const harness = makeChrome(settings, {
    id: 'unused', url: '', userAgent: '', isAutoIntercept: false, createdAt: 0,
  }, new Map([[4, page]]));
  const oldChrome = global.chrome;
  global.chrome = harness.chrome;
  try {
    await startBackground(path.join(__dirname, '..', 'src', 'background.ts'));
    const status = await sendRuntimeMessage(harness, { type: 'dm.status' });
    assert.equal(status.settings.mediaDetectionEnabled, false);

    const stale = await sendRuntimeMessage(harness, {
      type: 'dm.mediaDetected', pageUrl: 'https://example.test/',
      items: [{ url: 'https://example.test/video.mp4', kind: 'video', label: 'video' }],
    });
    assert.equal(stale.disabled, true);
    assert.equal(stale.count, 0);
    assert.deepEqual((await sendRuntimeMessage(harness, { type: 'dm.media' })).items, []);

    const enabled = await sendRuntimeMessage(harness, {
      type: 'dm.saveSettings', settings: { ...settings, mediaDetectionEnabled: true },
    });
    assert.equal(enabled.settings.mediaDetectionEnabled, true);
    assert.equal(pageMediaEnabled, true);

    const detected = await sendRuntimeMessage(harness, {
      type: 'dm.mediaDetected', pageUrl: 'https://example.test/',
      items: [{ url: 'https://example.test/video.mp4', kind: 'video', label: 'video' }],
    });
    assert.equal(detected.count, 1);
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.media' })).items.length, 1);

    const disabled = await sendRuntimeMessage(harness, {
      type: 'dm.saveSettings', settings: { ...settings, mediaDetectionEnabled: false },
    });
    assert.equal(disabled.settings.mediaDetectionEnabled, false);
    assert.equal(pageMediaEnabled, false);
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.media' })).items.length, 0);
    const finalBadge = harness.badgeUpdates.at(-1);
    assert.equal(finalBadge.tabId, 4);
    assert.equal(finalBadge.text, '');

    const rejected = await sendRuntimeMessage(harness, {
      type: 'dm.mediaDetected', pageUrl: 'https://example.test/',
      items: [{ url: 'https://example.test/late.mp4', kind: 'video', label: 'late' }],
    });
    assert.equal(rejected.disabled, true);
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.media' })).items.length, 0);
  } finally {
    global.chrome = oldChrome;
  }
});

test('latest media option wins when tab synchronization overlaps', async () => {
  const settings = {
    enabled: true, autoIntercept: true, startMode: 'now',
    interceptedExtensions: [], excludedExtensions: [], excludedHosts: [], notifications: false,
    mediaDetectionEnabled: false,
  };
  let pageMediaEnabled = false;
  let releaseEnable;
  let markEnableStarted;
  const enableGate = new Promise(resolve => { releaseEnable = resolve; });
  const enableStarted = new Promise(resolve => { markEnableStarted = resolve; });
  const page = async message => {
    if (message?.type !== 'dm.mediaDetectionChanged') return undefined;
    if (message.enabled === true) {
      markEnableStarted();
      await enableGate;
    }
    pageMediaEnabled = message.enabled === true;
    return { ok: true, enabled: pageMediaEnabled };
  };
  const harness = makeChrome(settings, {
    id: 'unused', url: '', userAgent: '', isAutoIntercept: false, createdAt: 0,
  }, new Map([[4, page]]));
  const oldChrome = global.chrome;
  global.chrome = harness.chrome;
  try {
    await startBackground(path.join(__dirname, '..', 'src', 'background.ts'));
    const enabling = sendRuntimeMessage(harness, {
      type: 'dm.saveSettings', settings: { ...settings, mediaDetectionEnabled: true },
    });
    await enableStarted;

    const disabling = sendRuntimeMessage(harness, {
      type: 'dm.saveSettings', settings: { ...settings, mediaDetectionEnabled: false },
    });
    while (harness.values.settings.mediaDetectionEnabled !== false)
      await new Promise(resolve => setImmediate(resolve));

    releaseEnable();
    await Promise.all([enabling, disabling]);
    assert.equal(harness.values.settings.mediaDetectionEnabled, false);
    assert.equal(pageMediaEnabled, false);
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
  const harness = makeChrome({ ...ruleSettings, mediaDetectionEnabled: true }, { id: 'seed' }, pages);
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

    const media = await sendRuntimeMessage(harness, { type: 'dm.mediaDetected', pageUrl: 'https://page.test/', items: [
      { url: 'https://www.youtube.com/watch?v=1', kind: 'video' }, { url: 'https://file.test/a.mp4', kind: 'video' },
    ] });
    assert.equal(media.count, 1);
  });
});

test('pages can reach only what the content script needs', async () => {
  const harness = makeChrome({ ...ruleSettings, autoIntercept: false, mediaDetectionEnabled: true }, { id: 'seed' });
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

    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.getMediaDetectionEnabled' }, page)).enabled, true);

    const saved = await sendRuntimeMessage(harness, { type: 'dm.saveSettings', settings: { ...ruleSettings } },
      { url: 'chrome-extension://test/options.html' });
    assert.equal(saved.ok, true);
    assert.equal(harness.values.settings.autoIntercept, true);
  });
});

test('a playlist on another site is requested without the user\'s cookies', async () => {
  const harness = makeChrome({ ...ruleSettings, mediaDetectionEnabled: true }, { id: 'seed' });
  const oldFetch = global.fetch;
  const requests = new Map();
  global.fetch = async (url, options) => { requests.set(url, options.credentials); return new Response('#EXTM3U'); };
  try {
    await withBackground(harness, async () => {
      await sendRuntimeMessage(harness, { type: 'dm.mediaDetected', pageUrl: 'https://video.test/watch', items: [
        { url: 'https://video.test/own.m3u8', kind: 'hls' }, { url: 'https://bank.test/x.m3u8', kind: 'hls' },
      ] });
      await sendRuntimeMessage(harness, { type: 'dm.media', tabId: 4 });
      assert.equal(requests.get('https://video.test/own.m3u8'), 'include');
      assert.equal(requests.get('https://bank.test/x.m3u8'), 'omit');
    });
  } finally { global.fetch = oldFetch; }
});

test('a playlist on the page\'s own site loses the cookies if it redirects', async () => {
  const harness = makeChrome({ ...ruleSettings, mediaDetectionEnabled: true }, { id: 'seed' });
  const oldFetch = global.fetch;
  const requests = [];
  global.fetch = async (url, options) => {
    requests.push({ credentials: options.credentials, redirect: options.redirect });
    return options.redirect === 'manual'
      ? { type: 'opaqueredirect', ok: false, status: 0 }
      : new Response('#EXTM3U');
  };
  try {
    await withBackground(harness, async () => {
      await sendRuntimeMessage(harness, { type: 'dm.mediaDetected', pageUrl: 'https://video.test/watch',
        items: [{ url: 'https://video.test/moved.m3u8', kind: 'hls' }] });
      await sendRuntimeMessage(harness, { type: 'dm.media', tabId: 4 });
      assert.deepEqual(requests, [{ credentials: 'include', redirect: 'manual' }, { credentials: 'omit', redirect: undefined }]);
    });
  } finally { global.fetch = oldFetch; }
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

test('Download All on a YouTube page lists nothing, and the panel cannot send its media', async () => {
  const pages = new Map([[4, () => ({ ok: true, pageUrl: 'https://www.youtube.com/watch?v=1', links: [
    { url: 'https://file.test/a.zip', text: 'archive' },
  ] })]]);
  const harness = makeChrome({ ...ruleSettings, mediaDetectionEnabled: true }, { id: 'seed' }, pages);
  await withBackground(harness, async () => {
    assert.deepEqual((await sendRuntimeMessage(harness, { type: 'dm.pageLinks', tabId: 4 })).links, []);
    const queued = await sendRuntimeMessage(harness, { type: 'dm.queueMedia', url: 'https://rr1.googlevideo.com/x', pageUrl: 'https://page.test/' });
    assert.equal(queued.ok, false);
  });
});

test('page scripts learn whether automatic sending is on, and a sender with no address gets nothing else', async () => {
  const harness = makeChrome({ ...ruleSettings, autoIntercept: false }, { id: 'seed' });
  await withBackground(harness, async () => {
    const page = { id: 'test-extension', tab: { id: 4 }, url: 'https://page.test/' };
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.getMediaDetectionEnabled' }, page)).autoSend, false);
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

test('turning the connection off also turns off media detection', async () => {
  const harness = makeChrome({ ...ruleSettings, enabled: false, mediaDetectionEnabled: true }, { id: 'seed' });
  await withBackground(harness, async () => {
    assert.equal((await sendRuntimeMessage(harness, { type: 'dm.getMediaDetectionEnabled' })).enabled, false);
  });
});
