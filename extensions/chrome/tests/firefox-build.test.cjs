const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { pathToFileURL } = require('node:url');

const root = path.join(__dirname, '..');
const chromeManifest = JSON.parse(fs.readFileSync(path.join(root, 'manifest.json'), 'utf8'));

test('EXT.E1: the Firefox manifest is the Chrome one with its own ID, an event page and no key', async () => {
  const { firefoxManifest, FIREFOX_EXTENSION_ID, FIREFOX_UPDATE_URL } = await import(pathToFileURL(path.join(root, 'scripts', 'build.mjs')).href);
  const manifest = firefoxManifest(chromeManifest);
  assert.equal(FIREFOX_EXTENSION_ID, 'inbound-bridge@kaan88');
  assert.equal(manifest.browser_specific_settings.gecko.id, FIREFOX_EXTENSION_ID);
  // Self-distributed: Firefox updates it from the file on main (an https address is required).
  assert.equal(manifest.browser_specific_settings.gecko.update_url, FIREFOX_UPDATE_URL);
  assert.match(FIREFOX_UPDATE_URL, /^https:\/\/raw\.githubusercontent\.com\/Kaan88\/inbound\/main\//);
  assert.equal('update_url' in (chromeManifest.browser_specific_settings?.gecko ?? {}), false);
  assert.deepEqual(manifest.background, { scripts: ['background.js'] });
  assert.equal('key' in manifest, false);
  assert.equal('minimum_chrome_version' in manifest, false);
  // Same rights as in Chrome, plus blocking webRequest to catch a download before Firefox's Save dialog (EXT.B5).
  assert.deepEqual(manifest.permissions, [...chromeManifest.permissions, 'webRequest', 'webRequestBlocking']);
  assert.equal(chromeManifest.permissions.includes('webRequestBlocking'), false);
  assert.deepEqual(manifest.host_permissions, chromeManifest.host_permissions);
  assert.equal(manifest.version, chromeManifest.version);
  // The Chrome manifest itself is untouched.
  assert.ok(chromeManifest.key);
  assert.equal(chromeManifest.background.service_worker, 'background.js');
});

test('EXT.B6: Firefox extension pages do not upgrade http:// to https://, so a server at a LAN address can be paired', async () => {
  const { firefoxManifest, FIREFOX_EXTENSION_PAGES_CSP } = await import(pathToFileURL(path.join(root, 'scripts', 'build.mjs')).href);
  const manifest = firefoxManifest(chromeManifest);
  const csp = manifest.content_security_policy?.extension_pages;
  assert.equal(csp, FIREFOX_EXTENSION_PAGES_CSP);
  // Firefox's MV3 default has upgrade-insecure-requests; an explicit policy replaces that default.
  assert.doesNotMatch(csp, /upgrade-insecure-requests/);
  // Scripts still come only from the extension itself: no remote code, no eval.
  assert.match(csp, /(^|;)\s*script-src 'self'\s*(;|$)/);
  assert.doesNotMatch(csp, /unsafe-eval|unsafe-inline|https?:|\*/);
  // Chrome and Edge keep their own default, which has no upgrade.
  assert.equal('content_security_policy' in chromeManifest, false);
});

test('EXT.B6: the built Firefox manifest carries that CSP, and the Chrome build has none', () => {
  const { execFileSync } = require('node:child_process');
  // Inside the package (ignored): the build's esbuild child may not be allowed to write to the temp folder.
  const out = fs.mkdtempSync(path.join(root, 'test-build-'));
  try {
    const build = path.join(root, 'scripts', 'build.mjs');
    execFileSync(process.execPath, [build, '--target', 'firefox', '--outdir', path.join(out, 'firefox')], { cwd: root, stdio: 'pipe' });
    execFileSync(process.execPath, [build, '--target', 'chrome', '--outdir', path.join(out, 'chrome')], { cwd: root, stdio: 'pipe' });
    const firefox = JSON.parse(fs.readFileSync(path.join(out, 'firefox', 'manifest.json'), 'utf8'));
    assert.deepEqual(firefox.content_security_policy, { extension_pages: "script-src 'self'" });
    assert.equal(firefox.browser_specific_settings.gecko.id, 'inbound-bridge@kaan88');
    const chrome = JSON.parse(fs.readFileSync(path.join(out, 'chrome', 'manifest.json'), 'utf8'));
    assert.equal('content_security_policy' in chrome, false);
    assert.ok(fs.existsSync(path.join(out, 'firefox', 'background.js')));
    // EXT.R8: both builds carry the shared look (the web UI's tokens, kit and theme script) and the extension's layout,
    // and every page links them, theme.js first so the first paint has the chosen theme.
    for (const target of ['chrome', 'firefox']) {
      for (const file of ['tokens.css', 'ui-kit.css', 'theme.js', 'extension.css']) {
        assert.ok(fs.existsSync(path.join(out, target, file)), `${target}/${file}`);
      }
      assert.equal(fs.readFileSync(path.join(out, target, 'tokens.css'), 'utf8'),
        fs.readFileSync(path.join(root, '..', '..', 'webui', 'src', 'tokens.css'), 'utf8'), 'copied as it is');
      for (const page of ['popup.html', 'options.html', 'handoff.html', 'batch.html']) {
        const html = fs.readFileSync(path.join(out, target, page), 'utf8');
        assert.match(html, /<script src="theme\.js"><\/script>\s*<link rel="stylesheet" href="tokens\.css" \/>\s*<link rel="stylesheet" href="ui-kit\.css" \/>\s*<link rel="stylesheet" href="extension\.css" \/>/, page);
        assert.doesNotMatch(html, /<script>[^<]/, `${page}: no inline script (MV3)`);
        assert.doesNotMatch(html, /style="[^"]*(#|rgb)/, `${page}: no colour outside the tokens`);
      }
    }
  } finally {
    fs.rmSync(out, { recursive: true, force: true });
  }
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
