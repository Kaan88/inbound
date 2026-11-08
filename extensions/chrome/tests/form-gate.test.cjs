const assert = require('node:assert/strict');
const fs = require('node:fs');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
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

const { FormGate } = loadTs('form-gate.ts');

function deferred() {
  let resolve, reject;
  const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

const tick = () => new Promise(resolve => setImmediate(resolve));

test('EXT.B2 gate: a pick holds Send and Browse, and its end releases both', async () => {
  const draws = [];
  const gate = new FormGate(() => draws.push({ canSend: gate.canSend, canPick: gate.canPick }));
  const pick = deferred();
  const running = gate.pick(() => pick.promise);
  assert.deepEqual(draws, [{ canSend: false, canPick: false }]);
  assert.equal(await gate.send(async () => assert.fail('a send must not start while the dialog is open')), false);
  assert.equal(await gate.pick(async () => assert.fail('a second pick must not start')), false);
  pick.resolve();
  assert.equal(await running, true);
  assert.deepEqual(draws.at(-1), { canSend: true, canPick: true });
  assert.equal(gate.picking, false);
  assert.equal(gate.sending, false);
});

test('EXT.B2 gate: a send holds Browse; a failed send clears only its own flag', async () => {
  const gate = new FormGate(() => undefined);
  const send = deferred();
  const running = gate.send(() => send.promise);
  assert.equal(gate.sending, true);
  assert.equal(gate.canPick, false);
  assert.equal(await gate.pick(async () => assert.fail('Browse must not open the dialog during a send')), false);
  assert.equal(gate.picking, false);
  send.reject(new Error('host gone'));
  await assert.rejects(running, /host gone/);
  assert.equal(gate.sending, false);
  assert.equal(gate.canSend, true);
  assert.equal(gate.canPick, true);
});

test('EXT.B2 gate: a failed pick releases Send', async () => {
  const gate = new FormGate(() => undefined);
  await assert.rejects(gate.pick(async () => { throw new Error('picker failed'); }), /picker failed/);
  assert.equal(gate.canSend, true);
  assert.equal(gate.canPick, true);
});

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

async function openFolderWindow(handlers = {}) {
  const page = installPage('?id=h1', {
    'dm.getHandoff': async () => ({ ok: true, held: false, handoff: { id: 'h1', url: 'https://example.test/a.zip' }, defaults: { destination: 'D:\\Default' } }),
    ...handlers,
  });
  loadTs('handoff.ts');
  await page.domReady();
  return page;
}

test('EXT.B2 folder window: a send that fails while Browse was tried leaves Send and Browse usable', async () => {
  const send = deferred();
  const page = await openFolderWindow({ 'dm.confirmHandoff': () => send.promise, 'dm.pickFolder': async () => ({ ok: true, path: 'Z:\\Wrong' }) });
  try {
    const confirm = page.el('confirm'), browse = page.el('browseDestination');
    assert.equal(confirm.disabled, false);
    const sending = confirm.click();
    assert.equal(confirm.disabled, true);
    assert.equal(browse.disabled, true, 'Browse is held while the send is in flight');
    assert.equal(await browse.click(), 'ignored');
    send.resolve({ ok: false, error: 'The app is not running.' });
    await sending;
    assert.equal(confirm.disabled, false, 'a failed send gives Send back');
    assert.equal(browse.disabled, false);
    assert.equal(page.el('error').textContent, 'The app is not running.');
    assert.deepEqual(page.sent, ['dm.getHandoff', 'dm.confirmHandoff']);
  } finally {
    page.restore();
  }
});

test('EXT.B2 folder window: Send does nothing while the dialog is open, and the chosen folder is sent', async () => {
  const pick = deferred();
  let confirmed;
  const page = await openFolderWindow({
    'dm.pickFolder': () => pick.promise,
    'dm.confirmHandoff': async message => { confirmed = message; return { ok: true, ack: { accepted: true, job: { fileName: 'a.zip' } } }; },
  });
  try {
    const confirm = page.el('confirm'), browse = page.el('browseDestination');
    const picking = browse.click();
    assert.equal(confirm.disabled, true, 'Send is held while the app\'s dialog is open');
    assert.equal(browse.disabled, true);
    assert.equal(await confirm.click(), 'ignored');
    await page.key({ key: 'Enter', target: page.el('fileName') });
    assert.equal(page.sent.includes('dm.confirmHandoff'), false);
    pick.resolve({ ok: true, path: 'E:\\Chosen' });
    await picking;
    assert.equal(confirm.disabled, false);
    assert.equal(browse.disabled, false);
    await confirm.click();
    assert.equal(confirmed.destination, 'E:\\Chosen');
    assert.equal(confirm.disabled, true, 'after success Send stays off; the window closes');
    assert.equal(browse.disabled, false);
  } finally {
    page.restore();
  }
});

test('EXT.B2 folder window: a cancelled or failed pick gives Send back', async () => {
  const page = await openFolderWindow({ 'dm.pickFolder': async () => { throw new Error('picker gone'); } });
  try {
    await page.el('browseDestination').click();
    assert.equal(page.el('confirm').disabled, false);
    assert.equal(page.el('browseDestination').disabled, false);
    assert.equal(page.el('destination').value, 'D:\\Default');
    assert.equal(page.el('error').textContent, 'picker gone');
  } finally {
    page.restore();
  }
});

test('EXT.B2 folder window: a cancelled pick on a valid request gives Send and Browse back', async () => {
  const page = await openFolderWindow({ 'dm.pickFolder': async () => ({ ok: true, cancelled: true }) });
  try {
    await page.el('browseDestination').click();
    assert.equal(page.el('confirm').disabled, false);
    assert.equal(page.el('browseDestination').disabled, false);
    assert.equal(page.el('destination').value, 'D:\\Default');
  } finally {
    page.restore();
  }
});

test('EXT.B2 folder window: Keep in browser and Escape do nothing while a send is in flight', async () => {
  const send = deferred();
  const page = installPage('?id=h1', {
    'dm.getHandoff': async () => ({ ok: true, held: true, handoff: { id: 'h1', url: 'https://example.test/a.zip' }, defaults: { destination: 'D:\\Default' } }),
    'dm.confirmHandoff': () => send.promise,
    'dm.keepInBrowser': async () => ({ ok: true }),
  });
  // A held request opens a keep-alive port with a 20 s ping timer; no port here, so the test can exit.
  global.chrome.runtime.connect = () => { throw new Error('no port in this test'); };
  try {
    loadTs('handoff.ts');
    await page.domReady();
    const keep = page.el('keepInBrowser');
    assert.equal(keep.disabled, false);
    const sending = page.el('confirm').click();
    assert.equal(keep.disabled, true, 'Keep is held while the send is in flight');
    await keep.click();
    await page.key({ key: 'Escape', target: page.el('fileName') });
    assert.equal(page.sent.includes('dm.keepInBrowser'), false);
    assert.equal(page.closed, false);
    send.resolve({ ok: false, error: 'The app is not running.' });
    await sending;
    assert.equal(keep.disabled, false, 'a failed send gives Keep back');
  } finally {
    page.restore();
  }
});

test('EXT.B2 folder window: the This PC / Server choice is held while the folder dialog is open', async () => {
  const pick = deferred();
  const page = installPage('?id=h1', {
    'dm.getHandoff': async () => ({ ok: true, held: false, handoff: { id: 'h1', url: 'https://example.test/a.zip' },
      defaults: { destination: 'D:\\Default' }, target: 'local',
      targets: [{ id: 'local', label: 'This PC', chooser: 'dialog', defaultFolder: 'D:\\Default', favorites: [] },
        { id: 'server', label: 'Server', chooser: 'list', defaultFolder: '/downloads', favorites: [] }] }),
    'dm.pickFolder': () => pick.promise,
  });
  try {
    loadTs('handoff.ts');
    await page.domReady();
    const radios = page.el('targetChoice').children.flatMap(label => label.children).filter(node => node.type === 'radio');
    assert.equal(radios.length, 2);
    assert.ok(radios.every(radio => !radio.disabled));
    const picking = page.el('browseDestination').click();
    assert.ok(radios.every(radio => radio.disabled), 'no target switch while the folder dialog is open');
    pick.resolve({ ok: true, path: 'E:\\Chosen' });
    await picking;
    assert.ok(radios.every(radio => !radio.disabled));
  } finally {
    page.restore();
  }
});

test('folder window: Download and Choose... wait for the request, so an early click cannot go to This PC instead of the server', async () => {
  const handoff = deferred();
  let confirmed;
  const page = installPage('?id=h1', {
    'dm.getHandoff': () => handoff.promise,
    'dm.pickFolder': async () => assert.fail('no dialog before the request has loaded'),
    'dm.confirmHandoff': async message => { confirmed = message; return { ok: false, error: 'rejected' }; },
  });
  try {
    loadTs('handoff.ts');
    const ready = page.domReady();
    const confirm = page.el('confirm'), browse = page.el('browseDestination');
    assert.equal(confirm.disabled, true);
    assert.equal(browse.disabled, true);
    assert.equal(await confirm.click(), 'ignored');
    assert.equal(await browse.click(), 'ignored');
    await page.key({ key: 'Enter', target: page.el('fileName') });
    // Even a click that reaches the handlers does nothing yet.
    await confirm.dispatch('click');
    await browse.dispatch('click');
    assert.deepEqual(page.sent, ['dm.getHandoff']);

    handoff.resolve({ ok: true, held: false, handoff: { id: 'h1', url: 'https://example.test/a.zip' }, defaults: { destination: '/downloads' },
      target: 'server', targets: [{ id: 'local', label: 'This PC', chooser: 'dialog', defaultFolder: 'D:\\Default', favorites: [] },
        { id: 'server', label: 'Server', chooser: 'list', defaultFolder: '/downloads', favorites: [] }] });
    await ready;
    assert.equal(confirm.disabled, false);
    assert.equal(browse.disabled, false);
    await confirm.click();
    assert.equal(confirmed.target, 'server');
  } finally {
    page.restore();
  }
});

test('EXT.B2 folder window: an expired request keeps Send off after a pick', async () => {
  const page = installPage('?id=h1', {
    'dm.getHandoff': async () => ({ ok: false, error: 'expired' }),
    'dm.pickFolder': async () => ({ ok: true, cancelled: true }),
  });
  try {
    loadTs('handoff.ts');
    await page.domReady();
    assert.equal(page.el('confirm').disabled, true);
    await page.el('browseDestination').click();
    assert.equal(page.el('confirm').disabled, true);
    assert.equal(page.el('browseDestination').disabled, false);
  } finally {
    page.restore();
  }
});

async function openBatchWindow(handlers = {}) {
  const page = installPage('?tabId=5', {
    'dm.pageLinks': async () => ({ ok: true, pageUrl: 'https://example.test/', links: [{ url: 'https://example.test/a.zip', text: 'a', kind: 'file', extension: 'zip', host: 'example.test' }] }),
    ...handlers,
  });
  loadTs('batch.ts');
  await tick();
  await tick();
  return page;
}

test('EXT.B2 batch window: Send is held while the dialog is open and released after', async () => {
  const pick = deferred();
  let confirmed;
  const page = await openBatchWindow({
    'dm.pickFolder': () => pick.promise,
    'dm.confirmBatch': async message => { confirmed = message; return { ok: false, error: 'rejected' }; },
  });
  try {
    const confirm = page.el('confirm'), browse = page.el('browseDestination');
    assert.equal(confirm.disabled, false, 'one link is selected');
    const picking = browse.click();
    assert.equal(confirm.disabled, true);
    assert.equal(await confirm.click(), 'ignored');
    pick.resolve({ ok: true, path: 'E:\\Batch' });
    await picking;
    assert.equal(confirm.disabled, false);
    await confirm.click();
    assert.equal(confirmed.destination, 'E:\\Batch');
    assert.equal(confirm.disabled, false, 'a failed send gives Send back');
    assert.equal(browse.disabled, false);
    assert.equal(page.el('cancel').disabled, false);
  } finally {
    page.restore();
  }
});

test('EXT.B2 batch window: Browse and Cancel are held during a send, and a failed send releases them', async () => {
  const send = deferred();
  const page = await openBatchWindow({ 'dm.confirmBatch': () => send.promise, 'dm.pickFolder': async () => ({ ok: true, path: 'Z:\\Wrong' }) });
  try {
    const confirm = page.el('confirm'), browse = page.el('browseDestination');
    const sending = confirm.click();
    assert.equal(confirm.disabled, true);
    assert.equal(browse.disabled, true);
    assert.equal(page.el('cancel').disabled, true);
    assert.equal(await browse.click(), 'ignored');
    send.reject(new Error('worker stopped'));
    await sending;
    assert.equal(confirm.disabled, false);
    assert.equal(browse.disabled, false);
    assert.equal(page.el('cancel').disabled, false);
    assert.equal(page.el('error').textContent, 'worker stopped');
    assert.equal(page.sent.includes('dm.pickFolder'), false);
  } finally {
    page.restore();
  }
});

const BATCH_TARGETS = {
  ok: true, target: 'local',
  targets: [{ id: 'local', label: 'This PC', chooser: 'dialog', defaultFolder: 'D:\\Default', favorites: [] },
    { id: 'server', label: 'Server', chooser: 'list', defaultFolder: '/downloads', favorites: [] }],
};

test('Download All: Save in shows the default folder, and an untouched default is sent as "" so the app decides', async () => {
  let confirmed;
  const page = await openBatchWindow({
    'dm.targets': async () => ({ ok: true, target: 'local', targets: [BATCH_TARGETS.targets[0]] }),
    'dm.confirmBatch': async message => { confirmed = message; return { ok: false, error: 'rejected' }; },
  });
  try {
    assert.equal(page.el('destination').value, 'D:\\Default');
    await page.el('confirm').click();
    assert.equal(confirmed.destination, '');
    assert.equal(confirmed.target, 'local');
    page.el('destination').value = 'E:\\Typed';
    await page.el('confirm').click();
    assert.equal(confirmed.destination, 'E:\\Typed', 'a changed folder is sent');
  } finally {
    page.restore();
  }
});

test('Download All: a folder typed before the late target list arrives is kept, and a later switch still shows the default', async () => {
  const targets = deferred();
  let confirmed;
  const page = await openBatchWindow({
    'dm.targets': () => targets.promise,
    'dm.confirmBatch': async message => { confirmed = message; return { ok: false, error: 'rejected' }; },
  });
  try {
    page.el('destination').value = 'E:\Typed';
    targets.resolve(BATCH_TARGETS);
    await tick();
    await tick();
    assert.equal(page.el('destination').value, 'E:\Typed', 'the late list does not replace the typed folder');
    await page.el('confirm').click();
    assert.equal(confirmed.destination, 'E:\Typed');

    const radios = page.el('targetChoice').children.flatMap(label => label.children).filter(node => node.type === 'radio');
    const server = radios.find(radio => radio.value === 'server');
    server.checked = true;
    await server.dispatch('change');
    assert.equal(page.el('destination').value, '/downloads');
  } finally {
    page.restore();
  }
});

test('EXT.B2 Download All: a target list that arrives during a This PC pick does not switch to the server under it', async () => {
  const targets = deferred();
  const pick = deferred();
  let confirmed;
  const page = await openBatchWindow({
    'dm.targets': () => targets.promise,
    'dm.pickFolder': () => pick.promise,
    'dm.confirmBatch': async message => { confirmed = message; return { ok: false, error: 'rejected' }; },
  });
  try {
    const picking = page.el('browseDestination').click();
    // The server is the default target, and its list arrives while the desktop app's dialog is open.
    targets.resolve({ ...BATCH_TARGETS, target: 'server' });
    await tick();
    await tick();
    pick.resolve({ ok: true, path: 'E:\\Picked' });
    await picking;
    assert.equal(page.el('destination').value, 'E:\\Picked');
    const radios = page.el('targetChoice').children.flatMap(label => label.children).filter(node => node.type === 'radio');
    assert.equal(radios.length, 2, 'the choice is shown once the pick ends');
    assert.ok(radios.every(radio => !radio.disabled));
    assert.equal(radios.find(radio => radio.checked)?.value, 'local');
    await page.el('confirm').click();
    assert.equal(confirmed.target, 'local', 'a This PC folder is never sent to the server');
    assert.equal(confirmed.destination, 'E:\\Picked');
  } finally {
    page.restore();
  }
});

test('Download All: switching target shows that target\'s default, and the radios are held during a pick', async () => {
  const pick = deferred();
  let confirmed;
  const page = await openBatchWindow({
    'dm.targets': async () => BATCH_TARGETS,
    'dm.pickFolder': () => pick.promise,
    'dm.confirmBatch': async message => { confirmed = message; return { ok: false, error: 'rejected' }; },
  });
  try {
    const radios = page.el('targetChoice').children.flatMap(label => label.children).filter(node => node.type === 'radio');
    assert.equal(radios.length, 2);
    assert.equal(page.el('destination').value, 'D:\\Default');
    const picking = page.el('browseDestination').click();
    assert.ok(radios.every(radio => radio.disabled), 'no target switch while the folder dialog is open (EXT.B2)');
    pick.resolve({ ok: true, cancelled: true });
    await picking;
    assert.ok(radios.every(radio => !radio.disabled));

    const server = radios.find(radio => radio.value === 'server');
    server.checked = true;
    await server.dispatch('change');
    assert.equal(page.el('destination').value, '/downloads');
    await page.el('confirm').click();
    assert.equal(confirmed.target, 'server');
    assert.equal(confirmed.destination, '', 'the server decides its default too');

    const local = radios.find(radio => radio.value === 'local');
    local.checked = true;
    await local.dispatch('change');
    assert.equal(page.el('destination').value, 'D:\\Default', 'a folder for one target is not kept for the other');
  } finally {
    page.restore();
  }
});
