// EXT.B5: which responses Firefox would save, and so are caught before its Save dialog.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
const ts = require('typescript');

const source = path.join(__dirname, '..', 'src', 'response-download.ts');
const compiled = ts.transpileModule(fs.readFileSync(source, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 },
}).outputText;
const loaded = new Module(source, module);
loaded.filename = source;
loaded.paths = Module._nodeModulePaths(path.dirname(source));
loaded._compile(compiled, source);
const { responseDownload, contentDispositionFileName } = loaded.exports;

const response = (headers, extra = {}) => ({
  url: 'https://files.test/get?id=7', method: 'GET', statusCode: 200, type: 'main_frame',
  responseHeaders: Object.entries(headers).map(([name, value]) => ({ name, value })), ...extra,
});

test('an attachment is a download, whatever its type, with its name and size', () => {
  assert.deepEqual(responseDownload(response({ 'Content-Disposition': 'attachment; filename="report.pdf"', 'Content-Type': 'application/pdf', 'Content-Length': '2048' })),
    { fileName: 'report.pdf', mime: 'application/pdf', totalBytes: 2048 });
  assert.equal(responseDownload(response({ 'content-disposition': 'Attachment', 'content-type': 'text/plain' })).fileName, '');
});

test('a type Firefox cannot show is a download; one it shows is left to it', () => {
  for (const mime of ['application/zip', 'application/octet-stream', 'application/x-7z-compressed', 'video/x-matroska', 'image/tiff', 'application/x-msdownload; charset=binary']) {
    assert.ok(responseDownload(response({ 'Content-Type': mime })), mime);
  }
  for (const mime of ['text/html; charset=utf-8', 'text/plain', 'image/png', 'image/svg+xml', 'application/pdf', 'application/json', 'video/mp4', 'audio/mpeg', 'application/rss+xml', 'multipart/x-mixed-replace']) {
    assert.equal(responseDownload(response({ 'Content-Type': mime })), null, mime);
  }
  // No type: Firefox sniffs it, and a page taken from the tab by mistake would be lost.
  assert.equal(responseDownload(response({})), null);
  // An inline disposition does not stop a type Firefox can't show from being saved.
  assert.equal(responseDownload(response({ 'Content-Disposition': 'inline; filename=a.zip', 'Content-Type': 'application/zip' })).fileName, 'a.zip');
});

test('only a GET answer to a page or frame load counts', () => {
  const zip = { 'Content-Type': 'application/zip' };
  assert.equal(responseDownload(response(zip, { method: 'POST' })), null);
  assert.equal(responseDownload(response(zip, { type: 'xmlhttprequest' })), null);
  assert.ok(responseDownload(response(zip, { type: 'sub_frame' })));
  for (const statusCode of [204, 301, 302, 304, 403, 404, 500]) assert.equal(responseDownload(response(zip, { statusCode })), null, String(statusCode));
  // A partial answer is a download, but its length is not the file's.
  assert.equal(responseDownload(response({ ...zip, 'Content-Length': '10' }, { statusCode: 206 })).totalBytes, undefined);
  assert.equal(responseDownload(response(zip, { url: 'ftp://files.test/a.zip' })), null);
  // A compressed answer's length is not the file's.
  assert.equal(responseDownload(response({ ...zip, 'Content-Length': '10', 'Content-Encoding': 'gzip' })).totalBytes, undefined);
  assert.equal(responseDownload(response({ ...zip, 'Content-Length': '10', 'Content-Encoding': 'identity' })).totalBytes, 10);
});

test('Content-Disposition names: filename* first, quotes and escapes, never a path', () => {
  assert.equal(contentDispositionFileName("attachment; filename=\"plain.zip\"; filename*=UTF-8''%C3%BCber%20alles.zip"), 'über alles.zip');
  assert.equal(contentDispositionFileName('attachment; filename="say \\"hi\\".txt"'), 'say "hi".txt');
  assert.equal(contentDispositionFileName('attachment; filename=bare.tar.gz; size=3'), 'bare.tar.gz');
  assert.equal(contentDispositionFileName('attachment; filename="..\\..\\evil.exe"'), 'evil.exe');
  assert.equal(contentDispositionFileName('attachment; filename="/etc/passwd"'), 'passwd');
  assert.equal(contentDispositionFileName('attachment; filename=".."'), '');
  assert.equal(contentDispositionFileName("attachment; filename*=UTF-8''%E0%A4%A"), '');
  assert.equal(contentDispositionFileName('attachment'), '');
});
