// The background worker under Node, with a fake chrome.* (shared by the background test files).
const Module = require('node:module');
const path = require('node:path');
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


const backgroundSource = path.join(__dirname, '..', 'src', 'background.ts');
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

module.exports = {
  event, makeChrome, startBackground, sendRuntimeMessage, answerNative, backgroundSource,
  settle, startDownload, withBackground, capturingChrome, catching, ownPage,
};
