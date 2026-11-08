const assert = require('node:assert/strict');
const fs = require('node:fs');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
const ts = require('typescript');

const source = path.join(__dirname, '..', 'src', 'pick-destination.ts');
const compiled = ts.transpileModule(fs.readFileSync(source, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 },
}).outputText;
const loaded = new Module(source, module);
loaded.filename = source;
loaded.paths = Module._nodeModulePaths(path.dirname(source));
loaded._compile(compiled, source);
const { pickDestinationFolder } = loaded.exports;

function controls(value = '') {
  const input = { value };
  const button = { disabled: false };
  const errors = [];
  return { input, button, errors, showError: message => errors.push(message) };
}

test('picker applies a chosen folder and restores the Browse control', async () => {
  const previousChrome = global.chrome;
  let request;
  global.chrome = { runtime: { async sendMessage(message) {
    request = message;
    return { ok: true, cancelled: false, path: 'E:\\Chosen' };
  } } };
  try {
    const state = controls('D:\\Starting');
    await pickDestinationFolder(state.input, state.button, state.showError);
    assert.deepEqual(request, { type: 'dm.pickFolder', initialPath: 'D:\\Starting' });
    assert.equal(state.input.value, 'E:\\Chosen');
    assert.equal(state.button.disabled, false);
    assert.deepEqual(state.errors, []);
  } finally {
    global.chrome = previousChrome;
  }
});

test('cancelling keeps the current one-off destination unchanged', async () => {
  const previousChrome = global.chrome;
  global.chrome = { runtime: { async sendMessage() { return { ok: true, cancelled: true }; } } };
  try {
    const state = controls('D:\\Keep\\This');
    await pickDestinationFolder(state.input, state.button, state.showError);
    assert.equal(state.input.value, 'D:\\Keep\\This');
    assert.equal(state.button.disabled, false);
    assert.deepEqual(state.errors, []);
  } finally {
    global.chrome = previousChrome;
  }
});

test('picker errors preserve the existing destination and report through inline error handler', async () => {
  const previousChrome = global.chrome;
  global.chrome = { runtime: { async sendMessage() {
    return { ok: false, cancelled: false, error: 'Host denied folder selection.' };
  } } };
  try {
    const state = controls('D:\\Keep\\This');
    await pickDestinationFolder(state.input, state.button, state.showError);
    assert.equal(state.input.value, 'D:\\Keep\\This');
    assert.deepEqual(state.errors, ['Host denied folder selection.']);
    assert.equal(state.button.disabled, false);
  } finally {
    global.chrome = previousChrome;
  }
});

test('Browse stays disabled until a pending picker request settles', async () => {
  const previousChrome = global.chrome;
  let finish;
  global.chrome = { runtime: { sendMessage: () => new Promise(resolve => { finish = resolve; }) } };
  try {
    const state = controls();
    const pending = pickDestinationFolder(state.input, state.button, state.showError);
    assert.equal(state.button.disabled, true);
    finish({ ok: true, cancelled: false, path: 'F:\\Picked' });
    await pending;
    assert.equal(state.button.disabled, false);
    assert.equal(state.input.value, 'F:\\Picked');
  } finally {
    global.chrome = previousChrome;
  }
});

test('EXT.B2: Send waits while the app\'s folder dialog is open (onBusy brackets the request)', async () => {
  const previousChrome = global.chrome;
  let finish;
  const events = [];
  global.chrome = { runtime: { sendMessage: () => { events.push('request'); return new Promise(resolve => { finish = resolve; }); } } };
  try {
    const state = controls('D:\\Start');
    const pending = pickDestinationFolder(state.input, state.button, state.showError, busy => events.push(busy ? 'busy' : 'idle'));
    assert.deepEqual(events, ['busy', 'request']);
    finish({ ok: true, cancelled: true });
    await pending;
    assert.deepEqual(events, ['busy', 'request', 'idle']);
    events.length = 0;
    global.chrome = { runtime: { async sendMessage() { throw new Error('gone'); } } };
    await pickDestinationFolder(state.input, state.button, state.showError, busy => events.push(busy ? 'busy' : 'idle'));
    assert.deepEqual(events, ['busy', 'idle'], 'an error still releases Send');
  } finally {
    global.chrome = previousChrome;
  }
});

test('EXT.B2: the folder window and the batch window hold Send while choosing a folder', () => {
  const read = name => fs.readFileSync(path.join(__dirname, '..', 'src', name), 'utf8');
  const call = 'pickDestinationFolder(destinationInput, browseDestinationButton, showError, (busy) =>';
  const handoff = read('handoff.ts');
  assert.ok(handoff.includes(call), 'handoff.ts passes onBusy');
  assert.ok(handoff.includes('confirmDisabledBeforePick = confirmButton.disabled;'), 'handoff.ts remembers Send');
  assert.ok(handoff.includes('confirmButton.disabled = confirmDisabledBeforePick;'), 'handoff.ts restores Send');
  const batch = read('batch.ts');
  assert.ok(batch.includes(call), 'batch.ts passes onBusy');
  assert.ok(batch.includes('confirmButton.disabled = picking || selected.size === 0;'), 'batch.ts holds Send while picking');
  assert.ok(batch.includes('if (selected.size === 0 || picking) {'), 'a Send click while picking does nothing');
});
