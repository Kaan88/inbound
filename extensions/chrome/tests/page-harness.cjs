// Fake extension pages for the page tests: src/*.ts compiled afresh per test, a fake DOM and a fake chrome.runtime.
const fs = require('node:fs');
const Module = require('node:module');
const path = require('node:path');
const ts = require('typescript');

const srcDir = path.join(__dirname, '..', 'src');

// Compiles a src/*.ts file and its relative imports afresh, so each page test gets its own module state.
function loadTs(file) {
  const cache = new Map();
  function load(source) {
    if (cache.has(source)) return cache.get(source).exports;
    const loaded = new Module(source, module);
    loaded.filename = source;
    loaded.paths = Module._nodeModulePaths(path.dirname(source));
    cache.set(source, loaded);
    const parentRequire = loaded.require.bind(loaded);
    loaded.require = id => id.startsWith('./') ? load(path.join(path.dirname(source), `${id.slice(2)}.ts`)) : parentRequire(id);
    loaded._compile(ts.transpileModule(fs.readFileSync(source, 'utf8'), {
      compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 },
    }).outputText, source);
    return loaded.exports;
  }
  return load(path.join(srcDir, file));
}

function deferred() {
  let resolve, reject;
  const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

const tick = () => new Promise(resolve => setImmediate(resolve));

// A fake page: elements by id, listeners, and clicks that do nothing on a disabled button, as in a browser.
function fakeElement(id) {
  const listeners = new Map();
  const classes = new Set();
  const element = {
    id, value: '', disabled: false, checked: false, indeterminate: false, textContent: '', title: '', className: '', type: '',
    children: [], placeholder: '', attributes: {},
    // A select's first option ("Pick a favorite folder..."), which the folder window keeps when it lists favorites.
    options: [{ value: '', textContent: '' }],
    setAttribute: (name, value) => { element.attributes[name] = String(value); },
    getAttribute: name => element.attributes[name] ?? null,
    removeAttribute: name => { delete element.attributes[name]; },
    classList: {
      add: name => classes.add(name),
      remove: name => classes.delete(name),
      toggle: (name, force) => ((force ?? !classes.has(name)) ? classes.add(name) : classes.delete(name)),
      contains: name => classes.has(name),
    },
    addEventListener: (type, listener) => listeners.set(type, [...(listeners.get(type) ?? []), listener]),
    append: (...nodes) => element.children.push(...nodes),
    replaceChildren: (...nodes) => { element.children = nodes; },
    dispatch: (type, event = {}) => Promise.all((listeners.get(type) ?? []).map(listener => listener({ target: element, ...event }))),
    click: () => (element.disabled ? Promise.resolve('ignored') : element.dispatch('click')),
  };
  return element;
}

function installPage(search, handlers) {
  const elements = new Map();
  const documentListeners = new Map();
  const sent = [];
  const saved = {};
  for (const name of ['chrome', 'document', 'location', 'window', 'screen', 'outerHeight', 'innerHeight']) saved[name] = global[name];
  global.location = { search };
  global.window = { close: () => { page.closed = true; } };
  global.document = {
    getElementById: id => { if (!elements.has(id)) elements.set(id, fakeElement(id)); return elements.get(id); },
    createElement: tag => fakeElement(tag),
    addEventListener: (type, listener) => documentListeners.set(type, [...(documentListeners.get(type) ?? []), listener]),
    documentElement: { scrollHeight: 400 },
  };
  global.chrome = {
    runtime: {
      sendMessage: async message => {
        sent.push(message.type);
        const handler = handlers[message.type];
        if (!handler) throw new Error(`unexpected message ${message.type}`);
        return handler(message);
      },
      connect: () => ({ postMessage() {}, onDisconnect: { addListener() {} } }),
    },
    // fitWindowToContent leaves a normal (non-popup) window alone.
    windows: { getCurrent: async () => ({ type: 'normal' }), update: async () => undefined },
  };
  const page = {
    closed: false,
    sent,
    el: id => global.document.getElementById(id),
    key: event => Promise.all((documentListeners.get('keydown') ?? []).map(listener => listener(event))),
    domReady: () => Promise.all((documentListeners.get('DOMContentLoaded') ?? []).map(listener => listener())),
    // The window stub stays: a success schedules window.close() after the test has ended.
    restore: () => Object.assign(global, saved, { window: { close() {} } }),
  };
  return page;
}

module.exports = { loadTs, deferred, tick, fakeElement, installPage };
