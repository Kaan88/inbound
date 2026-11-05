const assert = require('node:assert/strict');
const fs = require('node:fs');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
const ts = require('typescript');

const source = path.join(__dirname, '..', 'src', 'download-filename.ts');
const compiled = ts.transpileModule(fs.readFileSync(source, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 },
}).outputText;
const loaded = new Module(source, module);
loaded.filename = source;
loaded.paths = Module._nodeModulePaths(path.dirname(source));
loaded._compile(compiled, source);
const { chooseDownloadFileName } = loaded.exports;

const original = 'https://github.com/ViRb3/wgcf/releases/download/v2.3.1/wgcf_2.3.1_windows_amd64.exe';
const uuidUrl = 'https://objects.githubusercontent.com/UUID94c4f025-1be4-420c-beb8-a6ccb65eec91';
const uuid = 'UUID94c4f025-1be4-420c-beb8-a6ccb65eec91';

test('prefers descriptive original filename when Chrome has not populated filename', () => {
  assert.equal(chooseDownloadFileName('', original, uuidUrl), 'wgcf_2.3.1_windows_amd64.exe');
});

test('prefers descriptive original filename over opaque UUID Chrome filename', () => {
  assert.equal(chooseDownloadFileName(uuid, original, uuidUrl), 'wgcf_2.3.1_windows_amd64.exe');
});

test('decodes percent-encoded URL filenames', () => {
  assert.equal(chooseDownloadFileName('', 'https://example.test/files/my%20tool.exe?token=x', uuidUrl), 'my tool.exe');
});

test('preserves an informative Chrome-provided filename', () => {
  assert.equal(chooseDownloadFileName('installer-final.exe', original, uuidUrl), 'installer-final.exe');
});

test('file-type filtering sees the descriptive original URL extension, not UUID', () => {
  const name = chooseDownloadFileName(uuid, original, uuidUrl);
  assert.equal(/\.([a-z0-9]{1,10})$/i.exec(name)?.[1]?.toLowerCase(), 'exe');
});

test('URL fallback excludes query strings', () => {
  assert.equal(chooseDownloadFileName('', 'https://example.test/download?file=archive.zip'), 'download');
});

test('encoded separators stay inside the URL filename', () => {
  assert.equal(chooseDownloadFileName('', 'https://example.test/files/a%2Fb%5Cc.zip'), 'a_b_c.zip');
});
