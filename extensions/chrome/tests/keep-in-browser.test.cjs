const assert = require('node:assert/strict');
const fs = require('node:fs');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
const ts = require('typescript');
const source = path.join(__dirname, '..', 'src', 'keep-in-browser.ts');
const loaded = new Module(source, module);
loaded._compile(ts.transpileModule(fs.readFileSync(source, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 },
}).outputText, source);
const { normalizeDownloadUrl, consumeKeepInBrowser } = loaded.exports;

test('held Delete link token keeps only the matching first transfer', () => {
  const tokens = [{ url: normalizeDownloadUrl('https://example.test/file.zip#download'), expires: 2000 }];
  assert.equal(consumeKeepInBrowser(tokens, 'https://other.test/file.zip', 1000), false);
  assert.equal(consumeKeepInBrowser(tokens, 'https://example.test/file.zip', 1000), true);
  assert.equal(consumeKeepInBrowser(tokens, 'https://example.test/file.zip', 1000), false);
});

test('expired gestures do not suppress later capture', () => {
  const tokens = [{ url: 'https://example.test/file.zip', expires: 1000 }];
  assert.equal(consumeKeepInBrowser(tokens, tokens[0].url, 1000), false);
});

test('redirect targets and different query strings cannot consume the original gesture', () => {
  const tokens = [{ url: 'https://example.test/file.zip?version=1', expires: 2000 }];
  assert.equal(consumeKeepInBrowser(tokens, 'https://cdn.test/file.zip?version=1', 1000), false);
  assert.equal(consumeKeepInBrowser(tokens, 'https://example.test/file.zip?version=2', 1000), false);
  assert.equal(consumeKeepInBrowser(tokens, tokens[0].url, 1000), true);
});

test('unsupported and malformed link URLs cannot keep a download in the browser', () => {
  for (const url of ['file:///x', 'javascript:void(0)', 'blob:https://example.test/id', 'bad url']) {
    assert.equal(normalizeDownloadUrl(url), '');
  }
});

const { restartInterruptedText, restartSavedWebPage, RESTART_SAVED_WEB_PAGE_TEXT } = loaded.exports;

test('Audit 3 M4 review: a restarted download\'s interruption gives its own reason, and only a server reason blames the site', () => {
  assert.equal(restartInterruptedText('USER_CANCELED'), undefined);
  assert.equal(restartInterruptedText('USER_SHUTDOWN'), undefined, 'the browser was closed; it offers to resume it');
  for (const code of ['SERVER_FORBIDDEN', 'SERVER_FAILED', 'SERVER_UNAUTHORIZED', 'SERVER_BAD_CONTENT']) {
    assert.match(restartInterruptedText(code), /site refused it/);
  }
  for (const code of ['FILE_NO_SPACE', 'FILE_ACCESS_DENIED', 'FILE_FAILED', 'NETWORK_FAILED', 'NETWORK_DISCONNECTED', 'CRASH', undefined, 'SOMETHING_NEW']) {
    const text = restartInterruptedText(code);
    assert.ok(text, `${code} is said`);
    assert.doesNotMatch(text, /site refused/, `${code} does not blame the site`);
  }
  assert.match(restartInterruptedText('FILE_NO_SPACE'), /disk is full/);
  assert.match(restartInterruptedText('NETWORK_FAILED'), /connection failed/);
});

test('Audit 3 M4 review: a restart that saved a web page in place of a file is recognised', () => {
  assert.equal(restartSavedWebPage('application/zip', 'text/html; charset=utf-8'), true);
  assert.equal(restartSavedWebPage('application/zip', 'application/xhtml+xml'), true);
  assert.equal(restartSavedWebPage('application/zip', 'application/zip'), false);
  assert.equal(restartSavedWebPage('text/html', 'text/html'), false, 'a page that was a page');
  assert.equal(restartSavedWebPage(undefined, 'text/html'), false, 'nothing known about the first response');
  assert.equal(restartSavedWebPage('application/zip', undefined), false);
  assert.match(RESTART_SAVED_WEB_PAGE_TEXT, /web page/);
});
