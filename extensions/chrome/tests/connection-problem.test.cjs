const assert = require('node:assert/strict');
const fs = require('node:fs');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
const ts = require('typescript');
const source = path.join(__dirname, '..', 'src', 'connection-problem.ts');
const loaded = new Module(source, module);
loaded.filename = source;
loaded.paths = Module._nodeModulePaths(path.dirname(source));
loaded._compile(ts.transpileModule(fs.readFileSync(source, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 },
}).outputText, source);
const { connectionProblem, readableError } = loaded.exports;

const FORBIDDEN = 'Access to the specified native messaging host is forbidden.';
const FOLDER_ID = 'eahieadhhlcbhbcdbjhocjakceigdlmf';

test('a copy whose ID came from its folder is told so, not told to update the app', () => {
  const message = connectionProblem(FORBIDDEN, FOLDER_ID);
  assert.ok(message.includes(FOLDER_ID));
  assert.match(message, /new ID from its folder/);
  assert.doesNotMatch(message, /^Update/);
});

for (const id of ['olhbcdbjdnfoiinllnlpandggadfabfm', 'ifoagddcenmjpbklkhghbdmgplldcdci', 'pknddmneejadnnngffojnoecojhdfalc']) {
  test(`an accepted ID refused by the app means the app is too old (${id})`, () => {
    assert.match(connectionProblem(FORBIDDEN, id), /^Update the Inbound Download Manager desktop app/);
  });
}

test('the extension\'s own ID is used when none is passed', () => {
  const old = global.chrome;
  global.chrome = { runtime: { id: FOLDER_ID } };
  try {
    assert.ok(readableError(FORBIDDEN).includes(FOLDER_ID));
  } finally { global.chrome = old; }
});

test('a missing app and a stopped app keep their own messages', () => {
  assert.match(connectionProblem('Specified native messaging host not found.', FOLDER_ID), /not installed/);
  assert.match(connectionProblem('Native host has exited.', FOLDER_ID), /^Start Inbound/);
});

test('the message fits a notification, which is cut at 300 characters', () => {
  assert.ok(`${connectionProblem(FORBIDDEN, FOLDER_ID)} This download stays in Chrome.`.length <= 300);
});
