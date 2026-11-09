// A real DOM (jsdom) for the EXT.R8 page tests: an extension page's HTML, the browser globals the scripts use, and a
// chrome stub whose messages the test answers. Scripts are src/*.ts compiled afresh (page-harness loadTs).
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const { loadTs } = require('./page-harness.cjs');

const GLOBALS = ['window', 'document', 'location', 'localStorage', 'navigator', 'HTMLElement', 'Node', 'Element', 'Event',
  'KeyboardEvent', 'Option', 'getComputedStyle', 'chrome'];

function installDom(page, { hash = '', handlers = {}, storage = {} } = {}) {
  const html = fs.readFileSync(path.join(__dirname, '..', page), 'utf8')
    .replace(/<script[^>]*><\/script>/g, '');
  const dom = new JSDOM(html, { url: `http://extension.test/${page}${hash}`, pretendToBeVisual: true });
  // jsdom fires DOMContentLoaded itself, a moment after the page is made; ready() waits for that.
  let loaded = false;
  const whenLoaded = new Promise((resolve) => dom.window.document.addEventListener('DOMContentLoaded', () => { loaded = true; resolve(); }));
  const saved = Object.fromEntries(GLOBALS.map((name) => [name, Object.getOwnPropertyDescriptor(global, name)]));
  const sent = [];
  const storageListeners = [];
  const stored = { ...storage };
  const removedTabs = [];
  const chrome = {
    runtime: {
      id: 'test',
      getURL: (file) => `chrome-extension://test/${file}`,
      getManifest: () => ({ version: '0.6.0' }),
      sendMessage: async (message) => {
        sent.push(message);
        const handler = handlers[message.type];
        if (!handler) return { ok: true };
        return handler(message);
      },
      connect: () => ({ postMessage() {}, onMessage: { addListener() {} }, onDisconnect: { addListener() {} } }),
      openOptionsPage: async () => undefined,
    },
    storage: {
      local: { get: async () => ({ ...stored }), set: async (values) => Object.assign(stored, values) },
      onChanged: { addListener: (listener) => storageListeners.push(listener) },
    },
    permissions: { contains: async () => true, request: async () => true },
    tabs: { create: async () => ({}), getCurrent: async () => ({ id: 7 }), remove: async (id) => { removedTabs.push(id); } },
    windows: { getCurrent: async () => ({ type: 'normal' }), update: async () => undefined },
  };
  dom.window.chrome = chrome;
  const values = {
    window: dom.window, document: dom.window.document, location: dom.window.location, localStorage: dom.window.localStorage,
    navigator: dom.window.navigator, HTMLElement: dom.window.HTMLElement, Node: dom.window.Node, Element: dom.window.Element,
    Event: dom.window.Event, KeyboardEvent: dom.window.KeyboardEvent, Option: dom.window.Option,
    getComputedStyle: dom.window.getComputedStyle.bind(dom.window), chrome,
  };
  for (const [name, value] of Object.entries(values)) {
    Object.defineProperty(global, name, { value, configurable: true, writable: true });
  }
  return {
    dom, window: dom.window, document: dom.window.document, sent, stored, removedTabs,
    load: (file) => loadTs(file),
    ready: () => (loaded ? Promise.resolve() : whenLoaded),
    /** Another window stores settings (chrome.storage.onChanged). */
    storeElsewhere: (settings) => { stored.settings = settings; for (const listener of storageListeners) listener({ settings: { newValue: settings } }, 'local'); },
    restore: () => {
      for (const [name, descriptor] of Object.entries(saved)) {
        if (descriptor) Object.defineProperty(global, name, descriptor);
        else delete global[name];
      }
      dom.window.close();
    },
  };
}

const flush = async (times = 6) => { for (let i = 0; i < times; i++) await new Promise((resolve) => setImmediate(resolve)); };

module.exports = { installDom, flush };
