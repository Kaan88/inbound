const assert = require('node:assert/strict');
const fs = require('node:fs');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
const ts = require('typescript');
const source = path.join(__dirname, '..', 'src', 'browser-cookies.ts');
const loaded = new Module(source, module);
loaded.filename = source;
loaded.paths = Module._nodeModulePaths(path.dirname(source));
loaded._compile(ts.transpileModule(fs.readFileSync(source, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 },
}).outputText, source);
const { cookieContextForFrame, browserCookieHeaders } = loaded.exports;

test('context-menu cookie context uses the source tab store and exact frame partition', async () => {
  const old = global.chrome;
  let frame;
  global.chrome = { cookies: {
    async getAllCookieStores() { return [{ id: 'regular', tabIds: [1] }, { id: 'private', tabIds: [4] }]; },
    async getPartitionKey(details) { frame = details; return { partitionKey: { topLevelSite: 'https://example.test', hasCrossSiteAncestor: true } }; },
  } };
  try {
    assert.deepEqual(await cookieContextForFrame(4, 7), { storeId: 'private',
      partitionKey: { topLevelSite: 'https://example.test', hasCrossSiteAncestor: true } });
    assert.deepEqual(frame, { tabId: 4, frameId: 7 });
  } finally { global.chrome = old; }
});

test('cookie header combines only matching partition cookies with ordinary session cookies', async () => {
  const old = global.chrome;
  const queries = [];
  const key = { topLevelSite: 'https://example.test', hasCrossSiteAncestor: false };
  global.chrome = { cookies: { async getAll(query) {
    queries.push(query);
    return query.partitionKey ? [
      { name: 'cf_clearance', value: 'clearance', path: '/', partitionKey: key },
      { name: 'wrong', value: 'other-site', path: '/', partitionKey: { topLevelSite: 'https://other.test' } },
      { name: 'wrong-frame', value: 'cross-site', path: '/', partitionKey: { ...key, hasCrossSiteAncestor: true } },
    ] : [{ name: 'session', value: 'root', path: '/' }, { name: 'session', value: 'specific', path: '/download' }];
  } } };
  try {
    assert.deepEqual(await browserCookieHeaders('https://example.test/download/file', { storeId: 'private', partitionKey: key }),
      { Cookie: 'session=specific; session=root; cf_clearance=clearance' });
    assert.equal(queries.length, 2);
    assert.ok(queries.every(query => query.storeId === 'private'));
    assert.deepEqual(queries[1].partitionKey, key);
  } finally { global.chrome = old; }
});

test('missing source cookie store never falls back to another profile', async () => {
  const old = global.chrome;
  global.chrome = { cookies: { async getAllCookieStores() { return [{ id: 'regular', tabIds: [1] }]; },
    async getAll() { throw new Error('must not query another store'); } } };
  try { assert.deepEqual(await browserCookieHeaders('https://example.test', await cookieContextForFrame(4)), {}); }
  finally { global.chrome = old; }
});

test('older browsers retain unpartitioned session cookies', async () => {
  const old = global.chrome;
  global.chrome = { cookies: { async getAllCookieStores() { return [{ id: 'regular', tabIds: [4] }]; },
    async getAll() { return [{ name: 'session', value: 'saved', path: '/' }]; } } };
  try { assert.deepEqual(await browserCookieHeaders('https://example.test', await cookieContextForFrame(4)), { Cookie: 'session=saved' }); }
  finally { global.chrome = old; }
});
