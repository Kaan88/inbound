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
