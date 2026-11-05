const assert = require('node:assert/strict');
const fs = require('node:fs');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
const ts = require('typescript');

const source = path.join(__dirname, '..', 'src', 'download-ui.ts');
const compiled = ts.transpileModule(fs.readFileSync(source, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 },
}).outputText;
const loaded = new Module(source, module);
loaded.filename = source;
loaded._compile(compiled, source);
const { DownloadUiController } = loaded.exports;

function fake() {
  const calls = [];
  return { calls, api: { setUiOptions: async (o) => { calls.push(o.enabled); } } };
}

test('hides browser download UI while intercepting', async () => {
  const f = fake();
  const ui = new DownloadUiController(f.api);
  await ui.setIntercepting(true);
  assert.deepEqual(f.calls, [false]);
  await ui.setIntercepting(false);
  assert.deepEqual(f.calls, [false, true]);
});

test('shows UI while a browser-owned download is in progress', async () => {
  const f = fake();
  const ui = new DownloadUiController(f.api);
  await ui.setIntercepting(true);
  await ui.keepInBrowser(1);
  await ui.keepInBrowser(2);
  await ui.settled(1);
  assert.equal(ui.uiEnabled, true);
  await ui.settled(2);
  assert.deepEqual(f.calls, [false, true, false]);
  await ui.settled(99);
  assert.deepEqual(f.calls, [false, true, false]);
});

test('tolerates browsers without setUiOptions or permission', async () => {
  await new DownloadUiController({}).setIntercepting(true);
  const ui = new DownloadUiController({ setUiOptions: async () => { throw new Error('no permission'); } });
  await ui.setIntercepting(true);
  assert.equal(ui.uiEnabled, undefined);
});
