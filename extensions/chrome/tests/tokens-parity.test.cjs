// EXT.R8 §8: the shared tokens (webui/src/tokens.css, copied into the extension) stay equal to the desktop app's
// App.xaml in both themes and both dark selectors, and the colour pairs the pages use keep their contrast.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const repo = path.join(__dirname, '..', '..', '..');
const xaml = fs.readFileSync(path.join(repo, 'src', 'DownloadManager.Desktop.WinUI', 'App.xaml'), 'utf8');
const css = fs.readFileSync(path.join(repo, 'webui', 'src', 'tokens.css'), 'utf8');

function brushes(theme) {
  const block = new RegExp(`<ResourceDictionary x:Key="${theme}">([\\s\\S]*?)</ResourceDictionary>`).exec(xaml)[1];
  return Object.fromEntries([...block.matchAll(/x:Key="(\w+)" Color="(#[0-9A-Fa-f]{6})"/g)].map(m => [m[1], m[2].toLowerCase()]));
}
const accent = Object.fromEntries([...xaml.matchAll(/<Color x:Key="(SystemAccentColor\w*)">(#[0-9A-Fa-f]{6})<\/Color>/g)].map(m => [m[1], m[2].toLowerCase()]));

/** The custom properties of one CSS block, found by its selector text. */
function block(selector) {
  const start = css.indexOf(selector);
  assert.ok(start >= 0, `tokens.css has ${selector}`);
  const open = start + selector.length - 1;
  let depth = 0, end = open;
  for (; end < css.length; end++) {
    if (css[end] === '{') depth++;
    else if (css[end] === '}' && --depth === 0) break;
  }
  return Object.fromEntries([...css.slice(open, end).matchAll(/(--[\w-]+):\s*([^;]+);/g)].map(m => [m[1], m[2].trim().toLowerCase()]));
}

const MAP = {
  '--chrome': 'InboundChromeBrush', '--surface': 'InboundSurfaceBrush', '--divider': 'InboundDividerBrush',
  '--muted': 'InboundSecondaryTextBrush', '--progress': 'InboundProgressFillBrush', '--track': 'InboundProgressTrackBrush',
  '--error': 'InboundErrorTextBrush', '--highlight': 'InboundHighlightBrush',
  '--icon-start': 'InboundIconStartBrush', '--icon-pause': 'InboundIconPauseBrush', '--icon-danger': 'InboundIconDangerBrush',
  '--icon-info': 'InboundIconInfoBrush', '--icon-neutral': 'InboundIconNeutralBrush', '--icon-media': 'InboundIconMediaBrush',
  '--status-active': 'InboundStatusActiveBrush', '--status-done': 'InboundStatusDoneBrush', '--status-failed': 'InboundStatusFailedBrush',
  '--status-waiting': 'InboundStatusWaitingBrush', '--status-neutral': 'InboundStatusNeutralBrush',
};
const ACCENT = {
  light: { '--accent': 'SystemAccentColor', '--accent-hover': 'SystemAccentColorDark1' },
  dark: { '--accent': 'SystemAccentColorLight2', '--accent-hover': 'SystemAccentColorLight3' },
};

const light = block(':root {');
const darkSystem = block(':root:not([data-theme="light"]) {');
const darkChosen = block(':root[data-theme="dark"] {');

test('every mapped token equals App.xaml, light and dark, under both dark selectors', () => {
  const xamlLight = brushes('Light');
  const xamlDark = brushes('Dark');
  for (const [token, brush] of Object.entries(MAP)) {
    assert.equal(light[token], xamlLight[brush], `light ${token}`);
    assert.equal(darkSystem[token], xamlDark[brush], `dark (system) ${token}`);
    assert.equal(darkChosen[token], xamlDark[brush], `dark (chosen) ${token}`);
  }
  for (const [token, key] of Object.entries(ACCENT.light)) assert.equal(light[token], accent[key], `light ${token}`);
  for (const [token, key] of Object.entries(ACCENT.dark)) {
    assert.equal(darkSystem[token], accent[key], `dark ${token}`);
    assert.equal(darkChosen[token], accent[key], `dark ${token}`);
  }
  // The two dark blocks are the same, token for token.
  assert.deepEqual(darkSystem, darkChosen);
});

function luminance(hex) {
  const [r, g, b] = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16) / 255)
    .map(c => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
function contrast(a, b) {
  const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p);
  return (x + 0.05) / (y + 0.05);
}

test('the colour pairs of the pages meet WCAG 2 (text 4.5:1, controls and bars 3:1)', () => {
  const pairs = [
    ['--muted', '--surface', 4.5], ['--muted', '--chrome', 4.5], ['--muted', '--highlight', 4.5],
    ['--status-waiting', '--highlight', 4.5], ['--status-active', '--selected', 4.5], ['--status-done', '--surface', 4.5],
    ['--status-failed', '--surface', 4.5], ['--error', '--surface', 4.5], ['--accent-text', '--accent', 4.5],
    ['--progress', '--track', 3], ['--muted', '--surface', 3],
    // The notice bar (its blue is --highlight): the error notice's text, and the glyphs the notices show.
    ['--status-failed', '--highlight', 4.5], ['--status-waiting', '--highlight', 3], ['--icon-danger', '--highlight', 3],
    ['--icon-info', '--highlight', 3],
  ];
  for (const [name, tokens] of [['light', light], ['dark', darkSystem]]) {
    for (const [fore, back, minimum] of pairs) {
      const ratio = contrast(tokens[fore], tokens[back]);
      assert.ok(ratio >= minimum, `${name}: ${fore} on ${back} is ${ratio.toFixed(2)}:1, under ${minimum}`);
    }
  }
  // The field's line is --control-stroke, which is --muted (the divider alone is 1.43:1 in dark).
  assert.equal(light['--control-stroke'], 'var(--muted)');
});

test('the badge colours read with white text (OFF on the waiting amber, a waiting count on the accent)', () => {
  assert.ok(contrast('#ffffff', '#8a5a00') >= 4.5);
  assert.ok(contrast('#ffffff', '#1f6fc5') >= 4.5);
  const background = fs.readFileSync(path.join(__dirname, '..', 'src', 'background.ts'), 'utf8');
  assert.match(background, /"#1f6fc5" : serverDown \? "#b3261e" : "#8a5a00"/);
});

test('the notice bar uses the colours checked above: the error text and the pause glyph', () => {
  const kit = fs.readFileSync(path.join(repo, 'webui', 'src', 'ui-kit.css'), 'utf8');
  const own = fs.readFileSync(path.join(__dirname, '..', 'extension.css'), 'utf8');
  assert.match(kit, /\.notice\.notice-error \{ color: var\(--status-failed\); \}/);
  assert.match(own, /\.notice-glyph\.tint-pause \{ color: var\(--status-waiting\); \}/);
  // --error itself on the notice's blue would fail in dark (4.27:1), and the icon amber in light (2.95:1).
  assert.ok(contrast(darkSystem['--error'], darkSystem['--highlight']) < 4.5);
  assert.ok(contrast(light['--icon-pause'], light['--highlight']) < 3);
});
