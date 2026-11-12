// T1 guard (docs/plans/native-torrent-roadmap.md rule 5 and section 5; native-torrent-research.md, "Extension" and
// "Legal and store policy"): the extension never handles magnets or torrents. It catches no magnet, offers no
// "send torrent", and a .torrent file it catches is an ordinary file download. Torrents are the app's own business.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const Module = require('node:module');
const path = require('node:path');
const test = require('node:test');
const { pathToFileURL } = require('node:url');
const esbuild = require('esbuild');

const root = path.join(__dirname, '..');
const repo = path.join(root, '..', '..');
const WORDS = /torrent|magnet|btih|\bpeers?\b|\bseed(s|ing|er)?\b/i;

function filesUnder(dir, extensions) {
  const found = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) found.push(...filesUnder(full, extensions));
    else if (extensions.includes(path.extname(entry.name))) found.push(full);
  }
  return found;
}

function load(file) {
  const source = path.join(root, 'src', file);
  const bundled = esbuild.buildSync({
    entryPoints: [source], bundle: true, write: false, platform: 'node', format: 'cjs', target: 'node20',
  }).outputFiles[0].text;
  const loaded = new Module(source, module);
  loaded.filename = source;
  loaded.paths = Module._nodeModulePaths(path.dirname(source));
  loaded._compile(bundled, source);
  return loaded.exports;
}

test('T1: the extension source, pages, manifests and store listing say nothing of magnets or torrents', async () => {
  const files = [
    ...filesUnder(path.join(root, 'src'), ['.ts']),
    ...fs.readdirSync(root).filter(name => /\.(html|css|json)$/.test(name) && name !== 'package-lock.json').map(name => path.join(root, name)),
    path.join(root, 'scripts', 'build.mjs'),
    path.join(repo, 'docs', 'chrome-store', 'LISTING.md'),
  ];
  assert.ok(files.length > 20, 'the guard reads the extension');
  for (const file of files) {
    const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
    lines.forEach((line, index) => {
      assert.doesNotMatch(line, WORDS, `${path.relative(repo, file)}:${index + 1}`);
    });
  }

  const chromeManifest = JSON.parse(fs.readFileSync(path.join(root, 'manifest.json'), 'utf8'));
  const { firefoxManifest } = await import(pathToFileURL(path.join(root, 'scripts', 'build.mjs')).href);
  for (const manifest of [chromeManifest, firefoxManifest(chromeManifest)]) {
    // No protocol handler: a magnet link clicked in the browser is never the extension's.
    assert.equal('protocol_handlers' in manifest, false);
    assert.doesNotMatch(JSON.stringify(manifest), WORDS);
  }
});

test('T1: a built extension carries no torrent wording either, shared web UI code included', () => {
  // The popup bundles webui/src/lib/format.ts; torrent words there would reach the store build. Checked when built.
  for (const dir of ['dist', 'dist-firefox']) {
    const built = path.join(root, dir);
    if (!fs.existsSync(built)) continue;
    for (const file of filesUnder(built, ['.js', '.html', '.json'])) {
      assert.doesNotMatch(fs.readFileSync(file, 'utf8'), /torrent|magnet:|btih/i, path.relative(root, file));
    }
  }
});

test('T1: a .torrent file the browser downloads is caught like any other file, and a magnet never is', () => {
  const { DEFAULT_SETTINGS, decideCapture, isHttpUrl } = load('capture-rules.ts');
  const on = { ...DEFAULT_SETTINGS, autoIntercept: true };
  const decide = (fileName, mime, url = 'https://files.test/get?id=1') => decideCapture({ url, fileName, mime }, on);
  // The same answer as an unknown binary: the app gets the file itself, as an ordinary download.
  assert.deepEqual(decide('ubuntu.torrent', 'application/x-bittorrent'), decide('ubuntu.bin', 'application/octet-stream'));
  assert.deepEqual(decide('ubuntu.torrent', ''), decide('ubuntu.bin', ''));
  assert.equal(isHttpUrl('magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567'), false);
  assert.equal(decide('x', '', 'magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567').capture, false);
});

test('T1: Download All never collects a magnet link', async () => {
  const source = path.join(root, 'src', 'content.ts');
  const bundled = esbuild.buildSync({
    entryPoints: [source], bundle: true, write: false, platform: 'node', format: 'cjs', target: 'node20',
  }).outputFiles[0].text;
  const element = (tagName, src) => ({ tagName, textContent: '', getAttribute: name => (name === 'href' || name === 'src' ? src : null) });
  const elements = {
    'a[href]': [
      element('A', 'magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&tr=udp%3A%2F%2Ftracker.test%3A1337'),
      element('A', '/file.torrent'),
    ],
  };
  const names = ['document', 'window', 'location', 'chrome', 'HTMLAnchorElement'];
  const saved = names.map(name => Object.getOwnPropertyDescriptor(global, name));
  let onMessage;
  const globals = {
    document: { addEventListener() {}, removeEventListener() {}, hidden: false, querySelectorAll: selector => elements[selector] ?? [] },
    window: { addEventListener() {}, removeEventListener() {} },
    location: { href: 'https://page.test/list' },
    HTMLAnchorElement: class {},
    chrome: { runtime: {
      onMessage: { addListener(listener) { onMessage = listener; } },
      sendMessage() { return Promise.resolve({ ok: true, autoSend: false }); },
    } },
  };
  for (const name of names) Object.defineProperty(global, name, { value: globals[name], configurable: true, writable: true });
  try {
    const loaded = new Module(source, module);
    loaded.filename = source;
    loaded._compile(bundled, source);
    await new Promise(resolve => setImmediate(resolve));
    const response = await new Promise(resolve => onMessage({ type: 'dm.collectLinks' }, {}, resolve));
    // The .torrent file is a link like any other; the magnet is not a link the app downloads.
    assert.deepEqual(response.links.map(link => link.url), ['https://page.test/file.torrent']);
  } finally {
    names.forEach((name, index) => {
      if (saved[index]) Object.defineProperty(global, name, saved[index]);
      else delete global[name];
    });
  }
});
