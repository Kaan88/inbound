const assert = require('node:assert/strict');
const fs = require('node:fs');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
const ts = require('typescript');

const source = path.join(__dirname, '..', 'src', 'browser-download-cleanup.ts');
const compiled = ts.transpileModule(fs.readFileSync(source, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 },
}).outputText;
const loaded = new Module(source, module);
loaded.filename = source;
loaded.paths = Module._nodeModulePaths(path.dirname(source));
loaded._compile(compiled, source);
const { removeAcceptedBrowserDownload } = loaded.exports;

const settle = async () => {};
const item = (state = 'complete') => [{ id: 7, state }];

test('cancels before erasing a terminal browser entry', async () => {
  const calls = [];
  let present = true;
  const downloads = {
    cancel: async id => { calls.push(['cancel', id]); },
    search: async query => { calls.push(['search', query]); return present ? item() : []; },
    erase: async query => { calls.push(['erase', query]); present = false; },
  };

  assert.equal(await removeAcceptedBrowserDownload(downloads, 7, settle), true);
  assert.deepEqual(calls.map(call => call[0]), ['cancel', 'search', 'erase', 'search']);
  assert.deepEqual(calls.filter(call => call[0] === 'erase'), [['erase', { id: 7 }]]);
});

test('retries a transient cancel failure and never erases before cancel succeeds', async () => {
  const calls = [];
  let cancels = 0;
  let present = true;
  const downloads = {
    cancel: async id => {
      calls.push(['cancel', id]);
      if (++cancels === 1) throw new Error('transient cancel failure');
    },
    search: async query => { calls.push(['search', query]); return present ? item() : []; },
    erase: async query => { calls.push(['erase', query]); present = false; },
  };

  assert.equal(await removeAcceptedBrowserDownload(downloads, 7, settle), true);
  assert.deepEqual(calls.map(call => call[0]), ['cancel', 'cancel', 'search', 'erase', 'search']);
});

test('retries after erase failure and confirms removal with a fresh search', async () => {
  const calls = [];
  let erases = 0;
  let present = true;
  const downloads = {
    cancel: async id => { calls.push(['cancel', id]); },
    search: async query => { calls.push(['search', query]); return present ? item() : []; },
    erase: async query => {
      calls.push(['erase', query]);
      if (++erases === 1) throw new Error('transient erase failure');
      present = false;
    },
  };

  assert.equal(await removeAcceptedBrowserDownload(downloads, 7, settle), true);
  assert.deepEqual(calls.map(call => call[0]), ['cancel', 'search', 'erase', 'cancel', 'search', 'erase', 'search']);
});

test('does not erase an entry that remains in progress and stops after bounded retries', async () => {
  const calls = [];
  const downloads = {
    cancel: async id => { calls.push(['cancel', id]); },
    search: async query => { calls.push(['search', query]); return item('in_progress'); },
    erase: async query => { calls.push(['erase', query]); },
  };

  assert.equal(await removeAcceptedBrowserDownload(downloads, 7, settle), false);
  assert.equal(calls.filter(call => call[0] === 'cancel').length, 3);
  assert.equal(calls.some(call => call[0] === 'erase'), false);
});

test('returns false when erase reports success but the entry remains present', async () => {
  const calls = [];
  const downloads = {
    cancel: async id => { calls.push(['cancel', id]); },
    search: async query => { calls.push(['search', query]); return item(); },
    erase: async query => { calls.push(['erase', query]); },
  };

  assert.equal(await removeAcceptedBrowserDownload(downloads, 7, settle), false);
  assert.equal(calls.filter(call => call[0] === 'erase').length, 3);
  assert.equal(calls.filter(call => call[0] === 'search').length, 6);
});

test('treats an already-missing entry as removed after cancellation', async () => {
  const calls = [];
  const downloads = {
    cancel: async id => { calls.push(['cancel', id]); },
    search: async query => { calls.push(['search', query]); return []; },
    erase: async query => { calls.push(['erase', query]); },
  };

  assert.equal(await removeAcceptedBrowserDownload(downloads, 7, settle), true);
  assert.deepEqual(calls.map(call => call[0]), ['cancel', 'search']);
});
