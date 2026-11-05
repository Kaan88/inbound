const assert = require('node:assert/strict');
const fs = require('node:fs');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
const ts = require('typescript');
const source = path.join(__dirname, '..', 'src', 'capture-bypass.ts');
const loaded = new Module(source, module);
loaded._compile(ts.transpileModule(fs.readFileSync(source, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 },
}).outputText, source);
const { normalizeDownloadUrl, consumeCaptureBypass } = loaded.exports;

test('held Delete link token bypasses only the matching first transfer', () => {
  const tokens = [{ url: normalizeDownloadUrl('https://example.test/file.zip#download'), expires: 2000 }];
  assert.equal(consumeCaptureBypass(tokens, 'https://other.test/file.zip', 1000), false);
  assert.equal(consumeCaptureBypass(tokens, 'https://example.test/file.zip', 1000), true);
  assert.equal(consumeCaptureBypass(tokens, 'https://example.test/file.zip', 1000), false);
});

test('expired gestures do not suppress later capture', () => {
  const tokens = [{ url: 'https://example.test/file.zip', expires: 1000 }];
  assert.equal(consumeCaptureBypass(tokens, tokens[0].url, 1000), false);
});

test('redirect targets and different query strings cannot consume the original gesture', () => {
  const tokens = [{ url: 'https://example.test/file.zip?version=1', expires: 2000 }];
  assert.equal(consumeCaptureBypass(tokens, 'https://cdn.test/file.zip?version=1', 1000), false);
  assert.equal(consumeCaptureBypass(tokens, 'https://example.test/file.zip?version=2', 1000), false);
  assert.equal(consumeCaptureBypass(tokens, tokens[0].url, 1000), true);
});

test('unsupported and malformed link URLs cannot create a bypass', () => {
  for (const url of ['file:///x', 'javascript:void(0)', 'blob:https://example.test/id', 'bad url']) {
    assert.equal(normalizeDownloadUrl(url), '');
  }
});
