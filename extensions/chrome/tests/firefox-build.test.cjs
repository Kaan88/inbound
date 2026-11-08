const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { pathToFileURL } = require('node:url');

const root = path.join(__dirname, '..');
const chromeManifest = JSON.parse(fs.readFileSync(path.join(root, 'manifest.json'), 'utf8'));

test('EXT.E1: the Firefox manifest is the Chrome one with its own ID, an event page and no key', async () => {
  const { firefoxManifest, FIREFOX_EXTENSION_ID } = await import(pathToFileURL(path.join(root, 'scripts', 'build.mjs')).href);
  const manifest = firefoxManifest(chromeManifest);
  assert.equal(FIREFOX_EXTENSION_ID, 'inbound-bridge@kaan88');
  assert.equal(manifest.browser_specific_settings.gecko.id, FIREFOX_EXTENSION_ID);
  assert.deepEqual(manifest.background, { scripts: ['background.js'] });
  assert.equal('key' in manifest, false);
  assert.equal('minimum_chrome_version' in manifest, false);
  // Same rights as in Chrome, nothing more.
  assert.deepEqual(manifest.permissions, chromeManifest.permissions);
  assert.deepEqual(manifest.host_permissions, chromeManifest.host_permissions);
  assert.equal(manifest.version, chromeManifest.version);
  // The Chrome manifest itself is untouched.
  assert.ok(chromeManifest.key);
  assert.equal(chromeManifest.background.service_worker, 'background.js');
});

test('the desktop app trusts the same Firefox ID', () => {
  const bridge = fs.readFileSync(path.join(root, '..', '..', 'src', 'DownloadManager.Contracts', 'BrowserBridge.cs'), 'utf8');
  assert.match(bridge, /FirefoxExtensionId = "inbound-bridge@kaan88"/);
});

test('Firefox has no onDeterminingFilename, so the background never calls it unguarded', () => {
  const source = fs.readFileSync(path.join(root, 'src', 'background.ts'), 'utf8');
  const uses = source.split('\n').filter(line => line.includes('onDeterminingFilename.addListener'));
  assert.equal(uses.length, 1);
  assert.match(source, /if \(chrome\.downloads\.onDeterminingFilename\) \{/);
});
