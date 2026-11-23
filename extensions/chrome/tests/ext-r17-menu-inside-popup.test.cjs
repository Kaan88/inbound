// EXT.R17 (owner, 2026-11-17): the popup's row menu must fit without the popup growing. The menu goes below its point
// or row when the whole menu fits there, above it when it doesn't, is shifted to stay inside the popup on both axes,
// and is capped to the popup's height (and scrolls) rather than asking the browser for more room.
const assert = require('node:assert/strict');
const test = require('node:test');
const { installDom, flush } = require('./dom-harness.cjs');
const { loadTs } = require('./page-harness.cjs');

const { placeMenu } = loadTs('ui/menu.ts');

test('EXT.R17: the menu opens downward when there is room, upward when there is not', () => {
  const viewport = { width: 360, height: 200 };
  const EDGE = 4;
  // Under a right-click's point when the whole menu fits below it.
  assert.deepEqual(placeMenu({ width: 180, height: 60, viewport, at: { x: 40, y: 30 } }),
    { left: 40, top: 30, maxHeight: 200 - EDGE * 2 });
  // The point is low: the menu is put above it, its bottom at the point.
  assert.deepEqual(placeMenu({ width: 180, height: 60, viewport, at: { x: 40, y: 160 } }),
    { left: 40, top: 100, maxHeight: 200 - EDGE * 2 });

  // Opened from the keyboard on a row: flipped upward it sits above the row's top edge, not over the row.
  assert.equal(placeMenu({ width: 180, height: 60, viewport, at: { x: 40, y: 160 }, above: 130 }).top, 70);
  // From a row: under it, then above it.
  const row = { left: 20, top: 40, bottom: 70 };
  assert.equal(placeMenu({ width: 180, height: 60, viewport, anchor: row }).top, 72);
  const short = { width: 360, height: 110 };
  const lowRow = { left: 20, top: 60, bottom: 90 };
  // 92 + 40 > 106: above the row, not below it.
  assert.equal(placeMenu({ width: 180, height: 40, viewport: short, anchor: lowRow }).top, 60 - 2 - 40);
  // The status bar's switcher asks for upward even when there is room below.
  const lowAnchor = { left: 20, top: 100, bottom: 130 };
  assert.equal(placeMenu({ width: 180, height: 40, viewport, anchor: lowAnchor }).top, 132, 'under it normally');
  assert.equal(placeMenu({ width: 180, height: 40, viewport, anchor: lowAnchor, upward: true }).top, 100 - 2 - 40);
});

test('EXT.R17: the menu is shifted to stay inside the popup sideways and lengthwise', () => {
  const viewport = { width: 360, height: 200 };
  // Its right edge would pass the popup's: moved left by what sticks out.
  assert.equal(placeMenu({ width: 200, height: 60, viewport, at: { x: 300, y: 30 } }).left, 360 - 4 - 200);
  // Its left edge would pass the popup's: kept at the edge.
  assert.equal(placeMenu({ width: 200, height: 60, viewport, at: { x: -30, y: 30 } }).left, 4);
  // From a row near the right edge too.
  assert.equal(placeMenu({ width: 200, height: 60, viewport, anchor: { left: 340, top: 40, bottom: 70 } }).left, 156);
  // A point below the popup's own height (another window layout) is brought back in.
  const low = placeMenu({ width: 180, height: 60, viewport, at: { x: 40, y: 190 } });
  assert.ok(low.top >= 4 && low.top + 60 <= 200 - 4, `inside: ${low.top}`);
});

test('EXT.R17: a menu taller than the popup is capped to it and scrolls', () => {
  const viewport = { width: 360, height: 120 };
  const capped = placeMenu({ width: 180, height: 300, viewport, at: { x: 20, y: 20 } });
  assert.equal(capped.maxHeight, 120 - 8);
  assert.equal(capped.top, 4);
  assert.ok(capped.top + capped.maxHeight <= 120 - 4, 'never below the popup');
  // The same from a row.
  assert.equal(placeMenu({ width: 180, height: 300, viewport, anchor: { left: 20, top: 60, bottom: 90 } }).maxHeight, 112);
});

/**
 * jsdom lays nothing out: the popup's own size and the menu's wanted height are given per test. The menu is measured
 * from its class, so the code under test does the same measuring a browser would.
 */
function layout(page, { width = 360, height = 300, menuHeight = 180 } = {}) {
  Object.defineProperty(page.window, 'innerWidth', { configurable: true, value: width });
  Object.defineProperty(page.window, 'innerHeight', { configurable: true, value: height });
  const proto = page.window.HTMLElement.prototype;
  const realRect = proto.getBoundingClientRect;
  Object.defineProperty(proto, 'getBoundingClientRect', {
    configurable: true,
    writable: true,
    value() {
      if (!this.classList.contains('popup-menu')) return realRect.call(this);
      return { left: 0, top: 0, right: 200, bottom: menuHeight, width: 200, height: menuHeight };
    },
  });
  return {
    get menuHeight() { return menuHeight; },
    set menuHeight(value) { menuHeight = value; },
  };
}

const rowRect = (row, rect) => {
  Object.defineProperty(row, 'getBoundingClientRect', {
    configurable: true,
    value: () => ({ left: rect.left, top: rect.top, right: rect.right ?? rect.left + 300,
      bottom: rect.bottom, width: (rect.right ?? rect.left + 300) - rect.left, height: rect.bottom - rect.top }),
  });
};

const items = () => [{ label: 'One', onSelect() {} }, { label: 'Two', onSelect() {} }];

test('EXT.R17: the menu is put inside the popup and the popup is never made taller for it', () => {
  const page = installDom('popup.html');
  const size = layout(page, { width: 360, height: 200, menuHeight: 180 });
  try {
    const { openMenu, closeMenu } = page.load('ui/menu.ts');
    const doc = page.document;
    const row = doc.createElement('li');
    doc.body.append(row);
    rowRect(row, { left: 8, top: 150, bottom: 180 });

    const root = doc.documentElement;
    const before = root.style.cssText;
    assert.equal(root.style.minHeight, '');

    // Right-click low in a short popup: above the point, inside the popup, capped to its height.
    openMenu(row, items(), { at: { x: 340, y: 190 } });
    const menu = doc.querySelector('.popup-menu');
    assert.ok(menu, 'the menu opened');
    assert.equal(menu.style.top, `${190 - 180}px`, 'above the point');
    assert.equal(menu.style.left, `${360 - 4 - 200}px`, 'shifted left, inside');
    assert.equal(menu.style.maxHeight, `${200 - 8}px`);
    assert.equal(menu.style.overflowY, 'auto', 'it scrolls rather than growing the popup');
    assert.equal(root.style.cssText, before, 'the root is not changed while the menu is open');
    assert.equal(root.style.minHeight, '', 'not a taller page');

    closeMenu();
    assert.equal(doc.querySelector('.popup-menu'), null);
    assert.equal(root.style.cssText, before, 'nor after it closes');

    // A popup shorter than the menu: capped to the popup.
    size.menuHeight = 300;
    openMenu(row, items(), { at: { x: 40, y: 60 } });
    assert.equal(doc.querySelector('.popup-menu').style.maxHeight, '192px');
    assert.equal(doc.querySelector('.popup-menu').style.top, '4px');
    assert.equal(root.style.minHeight, '');
    closeMenu();
  } finally { page.restore(); }
});

test('EXT.R17: room below the row menu keeps it under the row, still without growing the popup', () => {
  const page = installDom('popup.html');
  layout(page, { width: 360, height: 300, menuHeight: 120 });
  try {
    const { openMenu } = page.load('ui/menu.ts');
    const doc = page.document;
    const row = doc.createElement('li');
    doc.body.append(row);
    rowRect(row, { left: 8, top: 40, bottom: 70 });
    openMenu(row, items(), { at: { x: 40, y: 70 } });
    const menu = doc.querySelector('.popup-menu');
    assert.equal(menu.style.top, '70px', 'under the point');
    assert.equal(menu.style.left, '40px');
    assert.equal(menu.style.maxHeight, '292px');
    assert.equal(doc.documentElement.style.minHeight, '');
  } finally { page.restore(); }
});

test('EXT.R17: the row menu of the popup itself is placed inside it, the popup\'s height never asked to change', async () => {
  const job = { id: '6f1c3c2e-8a4b-4c7d-9e2f-1a2b3c4d5e6f', target: 'local', fileName: 'report.pdf', state: 'completed',
    receivedBytes: 4, totalBytes: 4, url: 'https://files.example.test/report.pdf', createdAt: '2026-11-10T10:00:00Z',
    canPause: false, canResume: false };
  const status = { ok: true, enabled: true, autoIntercept: true, automaticSending: true, connected: true, lastError: '',
    localMissing: false, server: null, automaticTarget: 'local', pauseUntil: 0, settings: { enabled: true, autoIntercept: true },
    leftInBrowser: [], jobs: [job] };
  const page = installDom('popup.html', { handlers: {
    'dm.status': async () => status,
    'dm.jobs': async () => ({ ok: true, jobs: [job] }),
    'dm.askQueue': async () => ({ ok: true, items: [] }),
  } });
  layout(page, { width: 360, height: 240, menuHeight: 100 });
  try {
    page.load('popup.ts');
    await page.ready();
    await flush(12);
    const doc = page.document;
    const row = [...doc.querySelectorAll('.job')].find((item) => item.textContent.includes('report.pdf'));
    assert.ok(row, 'the download is listed');
    // A row low in the popup. The keyboard's contextmenu has no point of its own, so the popup uses the row's.
    rowRect(row, { left: 8, top: 150, bottom: 180 });
    const opening = new page.window.MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 30, clientY: 220 });
    row.dispatchEvent(opening);
    assert.equal(opening.defaultPrevented, true, 'the browser\'s own menu stays shut');
    const menu = doc.querySelector('.popup-menu');
    assert.ok(menu, 'the row menu opened');
    // The row's bottom is 180; 180 + 100 does not fit in 240, so the menu goes above the row's top edge (150 - 100),
    // not over the row from its bottom edge (180 - 100).
    assert.equal(menu.style.top, '50px', 'above the row, inside the popup');
    assert.equal(menu.style.maxHeight, `${240 - 8}px`);
    assert.equal(doc.documentElement.style.minHeight, '', 'the popup is not made taller');
    assert.deepEqual([...menu.querySelectorAll('.menu-item')].map((item) => item.textContent),
      ['Show in app', 'Open containing folder', 'Copy download link', 'Redownload', 'Rename…', 'Delete…']);
    assert.ok(menu.contains(doc.activeElement), 'the menu has the focus');
    // Pressing outside closes it; the root is still untouched.
    row.dispatchEvent(new page.window.MouseEvent('pointerdown', { bubbles: true }));
    assert.equal(doc.querySelector('.popup-menu'), null);
    assert.equal(doc.documentElement.style.minHeight, '');
  } finally { page.restore(); }
});
