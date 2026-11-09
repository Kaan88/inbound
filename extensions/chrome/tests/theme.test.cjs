// EXT.R8: the Theme setting (Match the browser, Light, Dark). theme.js, the web UI's file copied into the build, puts the
// stored choice on the page before it draws; ui/theme.ts writes it there and to localStorage["inbound.theme"].
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { JSDOM } = require('jsdom');
const { installDom } = require('./dom-harness.cjs');

const themeJs = fs.readFileSync(path.join(__dirname, '..', '..', '..', 'webui', 'public', 'theme.js'), 'utf8');

function runThemeJs(stored) {
  const dom = new JSDOM('<!doctype html><html><head></head><body></body></html>', { url: 'http://extension.test/popup.html', runScripts: 'outside-only' });
  if (stored) dom.window.localStorage.setItem('inbound.theme', stored);
  dom.window.eval(themeJs);
  const theme = dom.window.document.documentElement.dataset.theme;
  dom.window.close();
  return theme;
}

test('theme.js puts a stored Light or Dark on the page before it draws, and follows the browser otherwise', () => {
  assert.equal(runThemeJs('dark'), 'dark');
  assert.equal(runThemeJs('light'), 'light');
  assert.equal(runThemeJs(undefined), undefined);
  assert.equal(runThemeJs('purple'), undefined);
});

test('applyTheme writes the page and localStorage; Match the browser clears both', () => {
  const page = installDom('popup.html');
  try {
    const { applyTheme } = page.load('ui/theme.ts');
    applyTheme('dark');
    assert.equal(page.document.documentElement.dataset.theme, 'dark');
    assert.equal(page.window.localStorage.getItem('inbound.theme'), 'dark');
    applyTheme('system');
    assert.equal(page.document.documentElement.dataset.theme, undefined);
    assert.equal(page.window.localStorage.getItem('inbound.theme'), null);
  } finally { page.restore(); }
});

test('a stored theme other than light or dark is read as Match the browser', () => {
  const { migrateSettings } = require('./page-harness.cjs').loadTs('capture-rules.ts');
  assert.equal(migrateSettings({ theme: 'purple' }).settings.theme, 'system');
  assert.equal(migrateSettings({ theme: 'dark' }).settings.theme, 'dark');
  assert.equal(migrateSettings(undefined).settings.theme, 'system');
});
