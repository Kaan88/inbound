// Walks every web UI screen (the shell that follows the desktop window) against a running test server and saves screenshots at desktop and phone width, in the
// dark and the light theme. Test data only: start a server with a temp data folder (see webui/README.md), never the
// real one. Sets that server's password on first run.
//
//   INBOUND_URL            the server (default http://127.0.0.1:18732)
//   INBOUND_TEST_SERVER    tools/TestServer started with --torbox (default http://127.0.0.1:18731)
//   INBOUND_FILES          a plain web server with holiday-photos.7z (password "screens") and documents.zip, optional
//   INBOUND_DOWNLOADS      the folder downloads go to; inside DOWNLOADMANAGER_ROOTS
//   INBOUND_TORRENT        a small .torrent file to upload, optional
//   INBOUND_MOCK_KEY       the mock TorBox key the TestServer prints (an invented one), optional
//   INBOUND_TEST_PASSWORD  the throwaway password to set or sign in with
//   INBOUND_OUT            where screenshots go (default ../artifacts/webui)

import { chromium } from '@playwright/test';
import { mkdirSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';

const base = process.env.INBOUND_URL ?? 'http://127.0.0.1:18732';
const testServer = process.env.INBOUND_TEST_SERVER ?? 'http://127.0.0.1:18731';
const files = process.env.INBOUND_FILES ?? '';
const downloads = process.env.INBOUND_DOWNLOADS ?? '';
const mockKey = process.env.INBOUND_MOCK_KEY ?? '';
const password = process.env.INBOUND_TEST_PASSWORD;
const out = resolve(process.env.INBOUND_OUT ?? resolve(import.meta.dirname, '../../artifacts/webui'));
if (!password) throw new Error('Set INBOUND_TEST_PASSWORD to a throwaway password for the test server.');
mkdirSync(out, { recursive: true });

const variants = [
  { name: 'desktop-dark', viewport: { width: 1360, height: 820 }, colorScheme: 'dark' },
  { name: 'desktop-light', viewport: { width: 1360, height: 820 }, colorScheme: 'light' },
  { name: 'phone-dark', viewport: { width: 390, height: 844 }, colorScheme: 'dark', isMobile: true, hasTouch: true },
  { name: 'phone-light', viewport: { width: 390, height: 844 }, colorScheme: 'light', isMobile: true, hasTouch: true },
];

// INBOUND_ONLY=regex walks only the screens (or, with a name starting "e2e:", the end-to-end checks) whose name matches,
// to try one fix quickly; the screens' state then must come from an earlier run.
const only = process.env.INBOUND_ONLY ? new RegExp(process.env.INBOUND_ONLY) : null;
const browser = await chromium.launch();
const problems = [];
const steps = [];

/** One checked step: a screen in one variant, or one end-to-end check. A throw is a failed step. */
async function step(name, run) {
  if (only && /^e2e:/.test(name) && !only.test(name)) return;
  try {
    await run();
    steps.push({ name, ok: true });
  } catch (error) {
    const text = error.message.split('\n')[0];
    steps.push({ name, ok: false, text });
    problems.push(`step ${name}: ${text}`);
  }
}

/** A failed expectation inside a step. */
function expect(condition, text) {
  if (!condition) throw new Error(text);
}

async function newPage(variant, storageState) {
  const context = await browser.newContext({
    viewport: variant.viewport, colorScheme: variant.colorScheme, isMobile: variant.isMobile ?? false,
    hasTouch: variant.hasTouch ?? false, deviceScaleFactor: 1, storageState,
  });
  const page = await context.newPage();
  page.setDefaultTimeout(10000);
  page.on('pageerror', (error) => problems.push(`${variant.name}: page error ${error.message}`));
  page.on('console', (message) => {
    if (message.type() === 'error') problems.push(`${variant.name}: console ${message.text()} (${page.url()})`);
  });
  return { context, page };
}

async function settle(page) {
  await page.waitForLoadState('networkidle').catch(() => undefined);
  await page.waitForTimeout(400);
}

async function overflow(page, name, variant) {
  const wide = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  if (wide > 1) problems.push(`${variant.name}/${name}: the page scrolls sideways by ${wide}px`);
  // A dialog or the main area wider than the screen is the same fault, hidden by overflow-x.
  const box = await page.evaluate(() => {
    const found = [];
    for (const el of document.querySelectorAll('[role="dialog"], main')) {
      const r = el.getBoundingClientRect();
      if (r.right > window.innerWidth + 1 || el.scrollWidth - el.clientWidth > 1) found.push(`${el.getAttribute('role') ?? el.tagName} is ${Math.round(el.scrollWidth - el.clientWidth)}px wider than it shows, right edge ${Math.round(r.right)}`);
    }
    return found;
  });
  for (const text of box) problems.push(`${variant.name}/${name}: ${text}`);
  // Toolbar buttons side by side must not overlap (they used to shrink into one another).
  const crowded = await page.evaluate(() => {
    const rects = [...document.querySelectorAll('[role="toolbar"] .toolbtn')].map((el) => ({ name: el.textContent.trim(), r: el.getBoundingClientRect() })).filter((x) => x.r.width > 0);
    const found = [];
    for (let i = 1; i < rects.length; i++) if (rects[i].r.left < rects[i - 1].r.right - 1 && Math.abs(rects[i].r.top - rects[i - 1].r.top) < 4) found.push(`${rects[i - 1].name} / ${rects[i].name}`);
    return found;
  });
  for (const text of crowded) problems.push(`${variant.name}/${name}: toolbar buttons overlap: ${text}`);
}

/** One screen in every variant: open() gets the page to the state to capture. */
async function capture(name, storageState, open, { fullPage = false } = {}) {
  if (only && !only.test(name)) return;
  for (const variant of variants) {
    const { context, page } = await newPage(variant, storageState);
    await step(`${name} ${variant.name}`, async () => {
      try {
        await open(page, variant);
        await settle(page);
        await overflow(page, name, variant);
        await page.screenshot({ path: resolve(out, `${name}.${variant.name}.png`), fullPage });
      } catch (error) {
        await page.screenshot({ path: resolve(out, `${name}.${variant.name}.failed.png`) }).catch(() => undefined);
        throw error;
      }
    });
    await context.close();
  }
}

async function app(page, hash) {
  await page.goto(`${base}/${hash}`);
  await page.waitForSelector('main', { timeout: 15000 });
  await page.waitForFunction(() => !document.querySelector('main')?.textContent?.includes('Connecting'), null, { timeout: 15000 });
}

const phone = (variant) => variant.viewport.width < 768;

/** The side menu on a phone is a drawer behind the Menu button. */
async function menu(page, variant) {
  if (phone(variant)) await page.getByRole('button', { name: 'Menu' }).click();
}

/** The Add menu (the arrow beside Add URL in the side menu). */
async function addMenu(page, variant) {
  await menu(page, variant);
  await page.getByRole('button', { name: 'More ways to add' }).click();
  await page.getByRole('menu').waitFor();
}

async function addItem(page, variant, label) {
  await addMenu(page, variant);
  await page.getByRole('menuitem', { name: label, exact: true }).click();
}

const toolbar = (page) => page.getByRole('toolbar', { name: 'Download actions' });
const dialog = (page) => page.getByRole('dialog');

/** The toolbar's More button, which scrolls into view first on a phone: a scroll after the menu opens closes it, as it should. */
async function openMore(page) {
  const more = toolbar(page).getByRole('button', { name: 'More' });
  await more.scrollIntoViewIfNeeded();
  await page.waitForTimeout(400);
  await more.click();
  await page.getByRole('menu').waitFor();
}

async function expandAll(page) {
  for (let i = 0; i < 5; i++) {
    const expand = page.getByRole('button', { name: 'Expand', exact: true }).first();
    if (!(await expand.count())) break;
    await expand.click();
  }
}

/** The list row whose name is exactly this (a text match would also take holiday-video.mkv for Holiday). */
const rowNamed = (page, name) => page.locator('[role="grid"] [role="row"]').filter({ has: page.getByText(name, { exact: true }) }).first();

/** The row menu: a right-click with a mouse, the row's own button on a touch screen. */
async function openRowMenu(page, variant, row) {
  // Scroll first and let it settle: a scroll after the menu opens closes it, as it should.
  await row.scrollIntoViewIfNeeded();
  await page.waitForTimeout(400);
  if (variant.hasTouch) await row.getByRole('button', { name: /^Actions for/ }).click();
  else await row.click({ button: 'right' });
  await page.getByRole('menu').waitFor();
}

/** Add URL for one address: the address step, then the info step, then Start download. */
async function addOne(page, address) {
  await toolbar(page).getByRole('button', { name: 'Add URL' }).click();
  await page.fill('#url-box', address);
  await dialog(page).getByRole('button', { name: /^(Next|Try again)$/ }).click();
  const start = dialog(page).getByRole('button', { name: 'Start download' });
  const add = dialog(page).getByRole('button', { name: /^Add( \d+)?$/ });
  await Promise.race([start.waitFor({ timeout: 8000 }), add.waitFor({ timeout: 8000 })]).catch(() => undefined);
  if (await start.count()) await start.click();
  else if (await add.count() && await add.isEnabled()) await add.click();
  else await dialog(page).getByRole('button', { name: 'Cancel' }).first().click();
  await dialog(page).waitFor({ state: 'detached', timeout: 8000 }).catch(() => undefined);
}

/** The Options page by name: a list on a desktop, a menu on a phone. */
async function optionsPage(page, variant, label) {
  if (phone(variant)) await page.getByLabel('Options page', { exact: true }).selectOption({ label });
  else await page.getByRole('navigation', { name: 'Options pages' }).getByRole('button', { name: label, exact: true }).click();
}

const apply = async (page) => {
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  await page.waitForTimeout(900);
};

// ---- Sign-in -----------------------------------------------------------------------------------------------------

const status = await (await fetch(`${base}/api/auth/status`)).json();
if (status.setupNeeded) {
  await capture('01-setup', undefined, async (page) => {
    await page.goto(`${base}/`);
    await page.waitForSelector('#password');
  });
}

const { context: main, page } = await newPage(variants[0]);
let signedIn;
await step('sign in (or set the password)', async () => {
  await page.goto(`${base}/`);
  await page.waitForSelector('#password');
  await page.fill('#password', password);
  if (await page.locator('#repeat').count()) await page.fill('#repeat', password);
  await page.getByRole('button', { name: /Set the password|Sign in/ }).click();
  await page.waitForURL(`${base}/`);
  await page.waitForSelector('nav[aria-label="Views"]');
  signedIn = await main.storageState();
});
if (!signedIn) throw new Error('Could not sign in; nothing else can be walked.');

await capture('02-signin', undefined, async (p) => {
  await p.goto(`${base}/signin/`);
  await p.waitForSelector('#password');
});

// ---- Seed: settings, the mock provider key, downloads ------------------------------------------------------------

await step('seed: Options (download folder, local network, extraction)', async () => {
  await app(page, '#/settings');
  await optionsPage(page, variants[0], 'Save to');
  if (downloads) await page.fill('#set-folder', downloads);
  await optionsPage(page, variants[0], 'Connection');
  await page.getByLabel('Allow downloads from addresses on the local network').check();
  await optionsPage(page, variants[0], 'Extraction');
  await page.getByLabel('Extract archives automatically after they finish downloading').check();
  await apply(page);
  expect(!(await page.getByRole('alert').count()), `Options did not save: ${await page.getByRole('alert').first().innerText().catch(() => '')}`);
});

if (mockKey) {
  await step('seed: provider key', async () => {
    await app(page, '#/settings');
    await optionsPage(page, variants[0], 'TorBox account');
    await page.locator('input[id^="provider-key-"]').fill(mockKey);
    await page.getByRole('button', { name: 'Save key' }).click();
    await page.waitForTimeout(2500);
    expect(/Key saved/.test(await page.locator('main').innerText()), 'the TorBox key was not accepted');
  });
}

await app(page, '#/all');
const listed = await page.evaluate(() => document.querySelectorAll('[role="grid"] [role="row"]').length);
if (listed === 0) {
  await step('seed: Add links (a package of three)', async () => {
    await addItem(page, variants[0], 'Add links');
    await page.fill('#add-text', [
      `${testServer}/slow/holiday-video.mkv?size=1073741824&bps=4096`,
      `${testServer}/file/report.pdf?size=2097152`,
      `${testServer}/file/album.flac?size=6291456`,
    ].join('\n'));
    await page.waitForTimeout(2500);
    await page.fill('#add-package', 'Holiday');
    await dialog(page).getByRole('button', { name: /^Add( \d+)?$/ }).click();
    await dialog(page).waitFor({ state: 'detached', timeout: 10000 });
  });

  const singles = [`${testServer}/file/setup-tool.exe?size=1048576`, `${testServer}/gone/missing.iso`];
  if (files) singles.push(`${files}/holiday-photos.7z`, `${files}/documents.zip`);
  if (mockKey) singles.push('magnet:?xt=urn:btih:5b1e7c3d9a0f42c18e6d7b3a2f9c0e1d4a6b8c7f&dn=Example+Linux+ISO');
  for (const line of singles) {
    await step(`seed: Add URL ${line.replace(/\?.*/, '').replace(/^.*\//, '')}`, async () => {
      await addOne(page, line);
      await page.waitForTimeout(800);
    });
  }
  await page.waitForTimeout(4000);
}

// ---- Screens -----------------------------------------------------------------------------------------------------

await capture('03-list-all', signedIn, (p) => app(p, '#/all'));
await capture('04-list-package-selected', signedIn, async (p) => {
  await app(p, '#/all');
  await expandAll(p);
  const boxes = p.getByRole('checkbox', { name: /^Select (?!all)/ });
  expect(await boxes.count() > 2, 'the list has too few rows to select');
  await boxes.nth(1).check();
  await boxes.nth(2).check();
});
await capture('05-list-search', signedIn, async (p) => {
  await app(p, '#/all');
  await p.fill('#list-search', 'holiday');
});
await capture('06-row-menu', signedIn, async (p, v) => {
  await app(p, '#/all');
  await expandAll(p);
  await openRowMenu(p, v, p.locator('[role="grid"] [role="row"]', { hasText: 'report.pdf' }).first());
});
await capture('07-row-menu-package', signedIn, async (p, v) => {
  await app(p, '#/all');
  await openRowMenu(p, v, rowNamed(p, 'Holiday'));
});
await capture('08-package-progress-details', signedIn, async (p, v) => {
  await app(p, '#/all');
  await openRowMenu(p, v, rowNamed(p, 'Holiday'));
  await p.getByRole('menuitem', { name: /Progress Details/ }).click();
  await dialog(p).waitFor();
});
await capture('09-toolbar-more-menu', signedIn, async (p) => {
  await app(p, '#/all');
  await openMore(p);
});
await capture('10-speed-limit-dialog', signedIn, async (p) => {
  await app(p, '#/all');
  await openMore(p);
  await p.getByRole('menuitem', { name: /^Speed Limit/ }).click();
  await p.getByRole('menu').getByText('Other…', { exact: true }).click();
  await dialog(p).waitFor();
});
await capture('11-delete-dialog', signedIn, async (p) => {
  await app(p, '#/finished');
  const boxes = p.getByRole('checkbox', { name: /^Select (?!all)/ });
  expect(await boxes.count() > 0, 'nothing finished to delete');
  await boxes.first().check();
  await toolbar(p).getByRole('button', { name: 'Delete', exact: true }).click();
  await dialog(p).waitFor();
});
await capture('12-delete-completed-dialog', signedIn, async (p) => {
  await app(p, '#/all');
  await toolbar(p).getByRole('button', { name: 'Delete Completed' }).click();
  await dialog(p).waitFor();
});
await capture('13-cancel-package-dialog', signedIn, async (p) => {
  await app(p, '#/all');
  await p.getByRole('checkbox', { name: 'Select Holiday', exact: true }).check();
  await toolbar(p).getByRole('button', { name: 'Cancel Package' }).click();
  await dialog(p).waitFor();
});
await capture('14-job-details', signedIn, async (p, v) => {
  await app(p, '#/all');
  await expandAll(p);
  // A tap opens it on a phone; a double-click with a mouse.
  const row = p.locator('[role="grid"] [role="row"]', { hasText: 'holiday-video' }).last();
  if (v.hasTouch) await row.tap();
  else await row.dblclick();
  await dialog(p).waitFor();
});
await capture('15-job-details-speed', signedIn, async (p, v) => {
  await app(p, '#/all');
  await expandAll(p);
  const row = p.locator('[role="grid"] [role="row"]', { hasText: 'holiday-video' }).last();
  if (v.hasTouch) await row.tap();
  else await row.dblclick();
  await dialog(p).waitFor();
  await p.getByRole('tab', { name: 'Speed limiter' }).click();
});
await capture('16-add-menu', signedIn, async (p, v) => {
  await app(p, '#/all');
  await addMenu(p, v);
});
await capture('17-add-url-address', signedIn, async (p) => {
  await app(p, '#/all');
  await toolbar(p).getByRole('button', { name: 'Add URL' }).click();
  await p.fill('#url-box', `${testServer}/file/manual.pdf?size=1048576`);
});
await capture('18-add-url-info', signedIn, async (p) => {
  await app(p, '#/all');
  await toolbar(p).getByRole('button', { name: 'Add URL' }).click();
  await p.fill('#url-box', `${testServer}/file/manual.pdf?size=1048576`);
  await dialog(p).getByRole('button', { name: 'Next' }).click();
  await dialog(p).getByRole('button', { name: 'Start download' }).waitFor();
  await p.waitForTimeout(1200);
});
await capture('19-add-url-duplicate', signedIn, async (p) => {
  await app(p, '#/all');
  await toolbar(p).getByRole('button', { name: 'Add URL' }).click();
  await p.fill('#url-box', `${testServer}/file/report.pdf?size=2097152`);
  await dialog(p).getByRole('button', { name: 'Next' }).click();
  // The list is asked when Start download is pressed, as on the desktop.
  await dialog(p).getByRole('button', { name: 'Start download' }).click();
  await dialog(p).getByRole('button', { name: 'Download again' }).waitFor();
});
await capture('20-add-links', signedIn, async (p, v) => {
  await app(p, '#/all');
  await addItem(p, v, 'Add links');
  await p.fill('#add-text', [
    `${testServer}/file/manual.pdf?size=1048576`,
    `${testServer}/file/second.pdf?size=1048576`,
    'magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Sample',
    'https://not-a-real-hoster.invalid/abc',
  ].join('\n'));
  await p.waitForTimeout(3000);
});
await capture('21-add-magnet', signedIn, async (p, v) => {
  await app(p, '#/all');
  await addItem(p, v, 'Add magnet');
  await p.fill('#add-text', 'magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Sample');
  await p.waitForTimeout(2500);
});
await capture('22-import-torrent', signedIn, async (p, v) => {
  await app(p, '#/all');
  await addItem(p, v, 'Import torrent file');
  await dialog(p).waitFor();
});
await capture('23-import-download-list', signedIn, async (p, v) => {
  await app(p, '#/all');
  await addItem(p, v, 'Import download list');
  await dialog(p).waitFor();
});
await capture('24-extract-archive-from-disk', signedIn, async (p) => {
  await app(p, '#/all');
  await toolbar(p).getByRole('button', { name: 'Extract archive…' }).click();
  await dialog(p).waitFor();
});
await capture('25-waiting-at-provider', signedIn, async (p) => {
  await app(p, '#/remote');
  const actions = p.getByRole('button', { name: 'Actions', exact: true }).first();
  if (await actions.count()) {
    await actions.click();
    await dialog(p).waitFor();
  }
});
await capture('26-extractions', signedIn, (p) => app(p, '#/extractions'));
await capture('27-archive-password', signedIn, async (p) => {
  await app(p, '#/all');
  // The notice bar above the list: the row's key button is in a column a phone does not show.
  const button = p.getByRole('button', { name: 'Enter Password…' }).first();
  expect(await button.count() > 0, 'nothing asks for an archive password');
  await button.click();
  await dialog(p).waitFor();
});
await capture('28-queues', signedIn, (p) => app(p, '#/queues'), { fullPage: true });
await capture('29-queue-schedule-editing', signedIn, async (p) => {
  await app(p, '#/queues');
  await p.getByRole('button', { name: 'New schedule' }).first().click();
}, { fullPage: true });
await capture('30-queue-new-dialog', signedIn, async (p) => {
  await app(p, '#/queues');
  await p.getByRole('button', { name: 'New queue' }).click();
  await dialog(p).waitFor();
});
await capture('31-queue-files-dialog', signedIn, async (p) => {
  await app(p, '#/queues');
  await p.getByRole('button', { name: 'Files', exact: true }).first().click();
  await dialog(p).waitFor();
});
const optionPages = ['General', 'Save to', 'Downloads', 'Connection', 'Proxy', 'Site logins', 'Updates', 'TorBox account', 'Extraction', 'Sign-in and browsers'];
let at = 32;
for (const label of optionPages) {
  await capture(`${at++}-options-${label.toLowerCase().replace(/[^a-z]+/g, '-')}`, signedIn, async (p, v) => {
    await app(p, '#/settings');
    await optionsPage(p, v, label);
    if (label === 'Sign-in and browsers') await p.getByRole('button', { name: 'Pair a browser' }).click();
    if (label === 'Site logins') await p.getByRole('button', { name: 'Add site login' }).click();
  });
}
await capture(`${at++}-options-folder-picker`, signedIn, async (p, v) => {
  await app(p, '#/settings');
  await optionsPage(p, v, 'Save to');
  await p.getByRole('button', { name: 'Browse…' }).first().click();
  await dialog(p).waitFor();
});
await capture(`${at++}-options-unsaved-changes`, signedIn, async (p, v) => {
  await app(p, '#/settings');
  await optionsPage(p, v, 'Connection');
  await p.fill('#set-concurrent', '5'); // not the server's own 3: that is no change
  await p.waitForTimeout(300);
  await p.evaluate(() => { location.hash = '#/all'; });
  await dialog(p).waitFor();
});
for (const index of [0, 1, 2, 3]) {
  await capture(`${at++}-my-torbox-tab-${index + 1}`, signedIn, async (p) => {
    await app(p, '#/provider');
    const tabs = p.getByRole('tab');
    expect(await tabs.count() > index, 'My TorBox has no such tab');
    await tabs.nth(index).click();
    await p.waitForTimeout(1500);
  });
}
await capture(`${at++}-statistics`, signedIn, (p) => app(p, '#/statistics'), { fullPage: true });
await capture(`${at++}-menu-open`, signedIn, async (p, v) => {
  await app(p, '#/all');
  await menu(p, v);
});
await capture(`${at++}-empty-view`, signedIn, (p) => app(p, '#/failed'));

// ---- End to end: actions that change the server, after every capture that needs the state before them -----------

// The test archive's password: it was made for this walk (webui/README.md), not a real one.
const archivePassword = process.env.INBOUND_ARCHIVE_PASSWORD ?? 'screens';
await step('e2e: archive password extracts holiday-photos.7z', async () => {
  await app(page, '#/all');
  const enter = page.getByRole('button', { name: 'Enter Password…' }).first();
  expect(await enter.count() > 0, 'nothing asks for the archive password');
  await enter.click();
  await page.fill('#archive-password', archivePassword);
  await dialog(page).getByRole('button', { name: /Save|Extract/ }).last().click();
  await page.waitForTimeout(4500);
  const text = await page.locator('[role="grid"]').innerText();
  expect(/holiday-photos[\s\S]*Extraction completed/.test(text), 'the password did not extract holiday-photos.7z');
});
await capture(`${at++}-after-archive-password`, signedIn, (p) => app(p, '#/extractions'));

// Stop and resume the running download from the toolbar, then delete a finished one without its file.
await step('e2e: Stop and Resume holiday-video.mkv', async () => {
  await app(page, '#/all');
  await expandAll(page);
  const video = page.locator('[role="grid"] [role="row"]', { hasText: 'holiday-video' }).last();
  expect(/Downloading/.test(await video.innerText()), `holiday-video.mkv is not downloading: ${(await video.innerText()).replace(/\s+/g, ' ')}`);
  await video.click();
  await toolbar(page).getByRole('button', { name: 'Stop', exact: true }).click();
  await page.waitForTimeout(1500);
  expect(/Paused|Stopped/.test(await video.innerText()), `Stop did not stop holiday-video.mkv: ${(await video.innerText()).replace(/\s+/g, ' ')}`);
  await video.click();
  await toolbar(page).getByRole('button', { name: 'Resume', exact: true }).click();
  await page.waitForTimeout(2500);
  expect(/Downloading|Connecting|Starting/.test(await video.innerText()), `Resume did not restart holiday-video.mkv: ${(await video.innerText()).replace(/\s+/g, ' ')}`);
});

await step('e2e: Delete without the file keeps setup-tool.exe on disk', async () => {
  await app(page, '#/finished');
  const setup = page.locator('[role="grid"] [role="row"]', { hasText: 'setup-tool.exe' });
  expect(await setup.count() > 0, 'setup-tool.exe is not finished');
  await setup.getByRole('checkbox').check();
  await toolbar(page).getByRole('button', { name: 'Delete', exact: true }).click();
  const box = dialog(page).getByRole('checkbox');
  if (await box.count()) expect(!(await box.isChecked()), 'Delete from disk starts ticked');
  await dialog(page).getByRole('button', { name: 'Delete', exact: true }).click();
  await page.waitForTimeout(1500);
  expect(!(await page.locator('[role="grid"] [role="row"]', { hasText: 'setup-tool.exe' }).count()), 'Delete left setup-tool.exe in the list');
  if (downloads) {
    const present = readdirSync(downloads, { recursive: true }).some((f) => String(f).endsWith('setup-tool.exe'));
    expect(present, 'Delete without "Delete from disk" removed setup-tool.exe from the disk');
  }
});

// A .torrent file uploaded from this browser, its files listed from the file itself, sent to the (mock) provider.
const torrent = process.env.INBOUND_TORRENT;
if (torrent && mockKey) {
  await step('e2e: torrent upload reaches the provider', async () => {
    await app(page, '#/all');
    await addItem(page, variants[0], 'Import torrent file');
    await page.locator('input[type="file"]').setInputFiles(torrent);
    await page.waitForTimeout(2500);
    await page.screenshot({ path: resolve(out, 'e2e-torrent-files.desktop-dark.png') });
    await dialog(page).getByRole('button', { name: /^Add( \d+)?$/ }).click();
    await page.waitForTimeout(2500);
    // The provider lists the files inside the torrent, and asks which to take.
    const choose = dialog(page).getByRole('button', { name: 'Add', exact: true });
    if (await dialog(page).getByText(/Choose files/).count() && await choose.count()) {
      await page.screenshot({ path: resolve(out, 'e2e-torrent-choose-files.desktop-dark.png') });
      await choose.click();
      await page.waitForTimeout(2500);
    }
    await page.screenshot({ path: resolve(out, 'e2e-torrent-after-add.desktop-dark.png') });
    // Not cached at the mock provider: a dialog may ask before anything is fetched there.
    const close = dialog(page).getByRole('button', { name: /^Close$/ }).last();
    if (await dialog(page).count() && await close.count()) await close.click();
    await app(page, '#/remote');
    expect(/torrent-(?!5b1e7c3d)\w+[\s\S]*Waiting for TorBox/.test(await page.locator('main').innerText()), 'the uploaded torrent is not waiting at the provider');
  });
}

// A site login and a proxy with passwords: the server keeps them and says only that one is saved. One draft, Apply.
await step('e2e: site login and proxy password are saved and not shown', async () => {
  await app(page, '#/settings');
  await optionsPage(page, variants[0], 'Site logins');
  await page.getByRole('button', { name: 'Add site login' }).click();
  await page.locator('[id^="login-site-"]').last().fill('files.example.test');
  await page.locator('[id^="login-user-"]').last().fill('tester');
  await page.locator('[id^="login-pass-"]').last().fill('login-test-only');
  await optionsPage(page, variants[0], 'Proxy');
  await page.getByLabel('Use this proxy server:').check();
  await page.fill('#proxy-address', '127.0.0.1:3128');
  await page.fill('#proxy-user', 'tester');
  await page.fill('#proxy-pass', 'proxy-test-only');
  await apply(page);
  expect(!(await page.getByRole('alert').count()), `Apply failed: ${await page.getByRole('alert').first().innerText().catch(() => '')}`);
  expect(/saved/.test(await page.locator('main').innerText()), 'the proxy password is not reported as saved');
  await page.screenshot({ path: resolve(out, 'e2e-proxy-saved.desktop-dark.png') });
  await optionsPage(page, variants[0], 'Site logins');
  expect(/Password \(saved/.test(await page.locator('main').innerText()), 'the site login password is not reported as saved');
  expect(!/login-test-only|proxy-test-only/.test(await page.content()), 'a saved password is in the page');
  // Back to no proxy and no login, so the rest of the walk does not go through them.
  await optionsPage(page, variants[0], 'Proxy');
  await page.getByLabel('No proxy').check();
  await optionsPage(page, variants[0], 'Site logins');
  await page.getByRole('button', { name: 'Remove', exact: true }).first().click();
  await apply(page);
});

await step('e2e: Cancel drops a draft (after the question)', async () => {
  await app(page, '#/settings');
  await optionsPage(page, variants[0], 'Connection');
  const before = await page.inputValue('#set-concurrent');
  await page.fill('#set-concurrent', '7');
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await dialog(page).getByRole('button', { name: 'Leave without saving' }).click();
  await page.waitForTimeout(500);
  await app(page, '#/settings');
  await optionsPage(page, variants[0], 'Connection');
  expect(await page.inputValue('#set-concurrent') === before, 'Cancel kept the draft');
});

await step('e2e: Back with unsaved Options changes asks first', async () => {
  await app(page, '#/all');
  await page.getByRole('link', { name: 'Options', exact: true }).first().click();
  await optionsPage(page, variants[0], 'Connection');
  await page.fill('#set-concurrent', '6');
  await page.goBack();
  await dialog(page).waitFor();
  expect(await page.evaluate(() => location.hash) === '#/settings', 'the address left Options before the question was answered');
  await dialog(page).getByRole('button', { name: 'Stay' }).click();
  expect(await page.inputValue('#set-concurrent') === '6', 'Stay lost the draft');
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await dialog(page).getByRole('button', { name: 'Leave without saving' }).click();
});

// Pairing: the code shown here, used once on /api/pair as an extension would, then the paired browser is listed.
await step('e2e: pair a browser, limited token, revoke', async () => {
  await app(page, '#/settings');
  await optionsPage(page, variants[0], 'Sign-in and browsers');
  await page.getByRole('button', { name: 'Pair a browser' }).click();
  const code = (await page.getByLabel('Pairing code').innerText()).replace(/\s/g, '');
  const paired = await (await fetch(`${base}/api/pair`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', Origin: 'chrome-extension://pknddmneejadnnngffojnoecojhdfalc' },
    body: JSON.stringify({ code, name: 'Screens walk' }),
  })).json();
  expect(paired.ok && paired.token, `pairing failed: ${paired.error}`);
  const reply = await (await fetch(`${base}/api/ipc`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${paired.token}`, Origin: 'chrome-extension://pknddmneejadnnngffojnoecojhdfalc' },
    body: JSON.stringify({ v: 1, type: 'settings.get', id: 'screens-settings', payload: {} }),
  })).json();
  expect(reply.type === 'host.actionResult' && reply.payload?.ok === false, 'a paired token could read the settings');
  await page.getByRole('button', { name: 'Done' }).click();
  await page.waitForTimeout(800);
  expect(/Screens walk/.test(await page.locator('main').innerText()), 'the paired browser is not listed');
  await page.screenshot({ path: resolve(out, 'e2e-paired-browser.desktop-dark.png') });
  await page.locator('li', { hasText: 'Screens walk' }).getByRole('button', { name: 'Remove' }).click();
  await page.waitForTimeout(800);
  const after = await fetch(`${base}/api/ipc`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${paired.token}` },
    body: JSON.stringify({ v: 1, type: 'jobs.list', id: 'screens-list', payload: {} }),
  });
  expect(after.status === 401, `a removed token still answered ${after.status}`);
});

// Sign out: the page goes to sign-in, and the old cookie no longer opens the app.
await step('e2e: sign out', async () => {
  await app(page, '#/all');
  await page.getByRole('button', { name: 'Sign out' }).click();
  // Two navigations to the sign-in page can race (the button's and the closed socket's), so look at where the page is, not at one navigation.
  await page.waitForFunction(() => location.pathname.startsWith('/signin'));
});

await main.close();
await browser.close();

const failed = steps.filter((s) => !s.ok);
console.log(`Screenshots in ${out}`);
console.log(`Steps: ${steps.length - failed.length} passed, ${failed.length} failed of ${steps.length}`);
for (const s of failed) console.log(`  FAILED ${s.name}: ${s.text}`);
const others = problems.filter((p) => !p.startsWith('step '));
if (others.length) {
  console.log(`${others.length} problem(s) seen (console, page errors, sideways scroll):`);
  for (const problem of [...new Set(others)]) console.log(`  ${problem}`);
}
if (failed.length || others.length) process.exitCode = 1;
