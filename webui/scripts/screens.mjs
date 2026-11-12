// Walks every web UI screen (the shell that follows the desktop window) against a running test server and saves screenshots at desktop and phone width, in the
// dark and the light theme. Test data only: start a server with a temp data folder (see webui/README.md), never the
// real one. Sets that server's password on first run.
//
//   INBOUND_URL            the server (default http://127.0.0.1:18732)
//   INBOUND_TEST_SERVER    tools/TestServer started with --torbox (default http://127.0.0.1:18731)
//   INBOUND_FILES          a plain web server with holiday-photos.7z (password "screens") and documents.zip, optional
//   INBOUND_DOWNLOADS      the folder downloads go to; inside DOWNLOADMANAGER_ROOTS
//   INBOUND_TORRENT        a small .torrent file to upload, optional
//   INBOUND_TORRENT_NAME   its name, to add it stopped and open its Files tab (T3), optional
//   INBOUND_MOCK_KEY       the mock TorBox key the TestServer prints (an invented one), optional
//   INBOUND_TEST_PASSWORD  the throwaway password to set or sign in with
//   INBOUND_OUT            where screenshots go (default ../artifacts/webui)
//   INBOUND_VARIANTS       a regex of the variants to walk (desktop-dark, desktop-light, phone-dark, phone-light), optional

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

const allVariants = [
  { name: 'desktop-dark', viewport: { width: 1360, height: 820 }, colorScheme: 'dark' },
  { name: 'desktop-light', viewport: { width: 1360, height: 820 }, colorScheme: 'light' },
  { name: 'phone-dark', viewport: { width: 390, height: 844 }, colorScheme: 'dark', isMobile: true, hasTouch: true },
  { name: 'phone-light', viewport: { width: 390, height: 844 }, colorScheme: 'light', isMobile: true, hasTouch: true },
];
// INBOUND_VARIANTS=regex keeps only the matching variants (dark, say); the first one left runs the end-to-end checks.
const variants = process.env.INBOUND_VARIANTS ? allVariants.filter((v) => new RegExp(process.env.INBOUND_VARIANTS).test(v.name)) : allVariants;

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
    const rects = [...document.querySelectorAll('[role="toolbar"] .toolbtn')].filter((el) => !el.closest('[inert]')).map((el) => ({ name: el.textContent.trim(), r: el.getBoundingClientRect() })).filter((x) => x.r.width > 0);
    const found = [];
    for (let i = 1; i < rects.length; i++) if (rects[i].r.left < rects[i - 1].r.right - 1 && Math.abs(rects[i].r.top - rects[i - 1].r.top) < 4) found.push(`${rects[i - 1].name} / ${rects[i].name}`);
    return found;
  });
  for (const text of crowded) problems.push(`${variant.name}/${name}: toolbar buttons overlap: ${text}`);
  // The bar folds what doesn't fit into More: More stays inside the window and nothing is scrolled out of sight (U-WU-2).
  const bar = await page.evaluate(() => {
    const toolbar = document.querySelector('[role="toolbar"]');
    if (!toolbar) return null;
    const more = [...toolbar.querySelectorAll('.toolbtn')].find((el) => el.textContent.trim() === 'More' && !el.closest('[inert]'));
    return { scroll: toolbar.scrollWidth - toolbar.clientWidth, moreRight: more ? more.getBoundingClientRect().right : 0, width: window.innerWidth };
  });
  if (bar && bar.moreRight > bar.width + 1) problems.push(`${variant.name}/${name}: the toolbar's More is off the screen (right edge ${Math.round(bar.moreRight)})`);
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
  if (phone(variant)) await page.getByRole('button', { name: 'Menu', exact: true }).click();
}

/** The Add menu (the arrow beside Add URL in the side menu). */
async function addMenu(page, variant) {
  await menu(page, variant);
  await page.getByRole('button', { name: 'More ways to add' }).click();
  await page.getByRole('menu').waitFor();
}

async function addItem(page, variant, label) {
  await addMenu(page, variant);
  await page.getByRole('menuitem', { name: new RegExp(`^${label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}…?$`) }).click();
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

async function tool(page, name) {
  const button = toolbar(page).getByRole('button', { name, exact: true });
  if (await button.count()) {
    await button.click();
    return;
  }
  await openMore(page);
  await page.getByRole('menuitem', { name, exact: true }).click();
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
  await tool(page, 'Add URL');
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
  await tool(p, 'Delete');
  await dialog(p).waitFor();
});
await capture('12-delete-completed-dialog', signedIn, async (p) => {
  await app(p, '#/all');
  await tool(p, 'Delete Completed');
  await dialog(p).waitFor();
});
await capture('13-cancel-package-dialog', signedIn, async (p) => {
  await app(p, '#/all');
  await p.getByRole('checkbox', { name: 'Select Holiday', exact: true }).check();
  await tool(p, 'Cancel Package');
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
  await tool(p, 'Add URL');
  await p.fill('#url-box', `${testServer}/file/manual.pdf?size=1048576`);
});
await capture('18-add-url-info', signedIn, async (p) => {
  await app(p, '#/all');
  await tool(p, 'Add URL');
  await p.fill('#url-box', `${testServer}/file/manual.pdf?size=1048576`);
  await dialog(p).getByRole('button', { name: 'Next' }).click();
  await dialog(p).getByRole('button', { name: 'Start download' }).waitFor();
  await p.waitForTimeout(1200);
});
await capture('19-add-url-duplicate', signedIn, async (p) => {
  await app(p, '#/all');
  await tool(p, 'Add URL');
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
  await tool(p, 'Extract archive…');
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
// T2/T3 torrents, when the server has the torrent worker (its hello offers `torrents`): Options > Torrents, and a
// torrent's details with the live tabs. The torrent is a magnet added stopped, so nothing connects to any peer.
async function ipc(page, type, payload = {}) {
  return page.evaluate(async ([t, body]) => {
    const response = await fetch('/api/ipc', { method: 'POST', headers: { 'Content-Type': 'application/json' }, credentials: 'same-origin',
      body: JSON.stringify({ v: 1, type: t, id: crypto.randomUUID(), payload: body }) });
    return response.json();
  }, [type, payload]);
}
const torrentName = 'Screens torrent';
let torrentsOffered = false;
await step('e2e: torrents on, one stopped magnet', async () => {
  const { context, page } = await newPage(variants[0], signedIn);
  try {
    await app(page, '#/all');
    torrentsOffered = (await ipc(page, 'torrent.network')).type === 'torrent.networkResult';
    if (!torrentsOffered) return;
    const settings = (await ipc(page, 'settings.get')).payload.settings;
    if (!settings.torrentsEnabled) {
      settings.torrentsEnabled = true;
      const saved = await ipc(page, 'settings.update', { settings });
      expect(saved.payload?.ok !== false, `torrents could not be turned on: ${saved.payload?.error}`);
    }
    const added = await ipc(page, 'torrent.add', { input: `magnet:?xt=urn:btih:${'5c'.repeat(20)}&dn=${encodeURIComponent(torrentName)}`, startMode: 'later' });
    expect(added.payload?.ok, `the magnet was not added: ${added.payload?.error}`);
  } finally {
    await context.close();
  }
});
if (torrentsOffered) {
  await capture(`${at++}-options-torrents`, signedIn, async (p, v) => {
    await app(p, '#/settings');
    await optionsPage(p, v, 'Torrents');
    await p.getByLabel('Stop seeding at ratio').waitFor();
  }, { fullPage: true });
  // WEB-T1/T2/T3: the Torrents view with Seeds, Peers, Up and Ratio on a desktop, the view chips and the line of what
  // the torrents move on a phone. The magnet added for later waits in its queue, so it is under Downloading.
  await capture(`${at++}-torrents-view`, signedIn, async (p) => {
    await app(p, '#/torrents');
    await rowNamed(p, torrentName).waitFor();
  });
  await capture(`${at++}-torrents-downloading`, signedIn, async (p) => {
    await app(p, '#/torrents-downloading');
    await rowNamed(p, torrentName).waitFor();
  });
  // A stopped torrent: the live tabs say so and ask the server nothing; T3's Overview and Options read the Host's facts.
  for (const [tab, ready] of [['Overview', /^Added from$/], ['Peers', /^Not running\./], ['Trackers', /^Not running\./], ['Pieces & Speed', /^Not running\./], ['Options', /^Stop seeding at$/]]) {
    await capture(`${at++}-torrent-details-${tab.toLowerCase().replace(/[^a-z]+/g, '-')}`, signedIn, async (p, v) => {
      await app(p, '#/all');
      const row = rowNamed(p, torrentName);
      if (v.hasTouch) await row.tap();
      else await row.dblclick();
      await dialog(p).waitFor();
      await p.getByRole('tab', { name: tab, exact: true }).click();
      await dialog(p).getByText(ready).first().waitFor();
    });
  }
  // T3: the row menu's Force Recheck, Announce Now and Set Limits…, which opens the Options tab.
  await capture(`${at++}-torrent-row-menu`, signedIn, async (p, v) => {
    await app(p, '#/all');
    await openRowMenu(p, v, rowNamed(p, torrentName));
    for (const item of ['Force Recheck', 'Announce Now', 'Set Limits…']) await p.getByRole('menuitem', { name: item }).waitFor();
  });
  await capture(`${at++}-torrent-set-limits`, signedIn, async (p, v) => {
    await app(p, '#/all');
    await openRowMenu(p, v, rowNamed(p, torrentName));
    await p.getByRole('menuitem', { name: 'Set Limits…' }).click();
    await dialog(p).getByText(/^Speed limits for this torrent$/).waitFor();
  });
}

// T3: a .torrent file with several files (INBOUND_TORRENT): the add dialog's file tree with priorities and the free space,
// then, added stopped, the details' Files tab. Nothing connects to a peer.
const torrentFile = process.env.INBOUND_TORRENT;
const filesTorrentName = process.env.INBOUND_TORRENT_NAME ?? '';
if (torrentsOffered && torrentFile) {
  await capture(`${at++}-torrent-add-priorities`, signedIn, async (p, v) => {
    await app(p, '#/all');
    await addItem(p, v, 'Import torrent file');
    await dialog(p).waitFor();
    await p.locator('input[type="file"]').setInputFiles(torrentFile);
    await p.getByRole('button', { name: /^Choose the files of/ }).click();
    const first = p.getByRole('combobox', { name: /^Priority of/ }).first();
    await first.selectOption('high');
    await p.getByLabel('Free space').waitFor();
  });
  if (filesTorrentName) {
    await step('e2e: a .torrent file added stopped', async () => {
      const { context, page } = await newPage(variants[0], signedIn);
      try {
        await app(page, '#/all');
        await addItem(page, variants[0], 'Import torrent file');
        await page.locator('input[type="file"]').setInputFiles(torrentFile);
        await page.getByRole('button', { name: /^Choose the files of/ }).waitFor();
        await page.selectOption('#torrent-start', 'later');
        await dialog(page).getByRole('button', { name: /^Add( \d+)?$/ }).click();
        await rowNamed(page, filesTorrentName).waitFor();
      } finally {
        await context.close();
      }
    });
    await capture(`${at++}-torrent-details-files-priorities`, signedIn, async (p, v) => {
      await app(p, '#/all');
      const row = rowNamed(p, filesTorrentName);
      if (v.hasTouch) await row.tap();
      else await row.dblclick();
      await dialog(p).waitFor();
      await p.getByRole('tab', { name: 'Files', exact: true }).click();
      await p.getByRole('combobox', { name: /^Priority of/ }).first().waitFor();
    });
  }
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
// D2d: a row opens a dialog (files, destination, Download), and its menu has the quick actions.
/** A My TorBox row whose files the provider lists ("3 files"), or the first row. */
async function itemRow(p) {
  await p.locator('.item-row').first().waitFor();
  const listed = p.locator('.item-row', { hasText: / \d+ files? / }).first();
  return (await listed.count()) ? listed : p.locator('.item-row').first();
}
await capture(`${at++}-my-torbox-item-dialog`, signedIn, async (p) => {
  await app(p, '#/provider');
  const row = (await itemRow(p)).locator(':scope > button').first();
  await row.click();
  await dialog(p).waitFor();
  await dialog(p).getByRole('button', { name: 'Download selected' }).waitFor();
  await p.waitForTimeout(800); // the file tree
});
await capture(`${at++}-my-torbox-row-menu`, signedIn, async (p, v) => {
  await app(p, '#/provider');
  const row = await itemRow(p);
  if (phone(v)) await row.getByRole('button', { name: /^Actions for / }).click();
  else await row.locator(':scope > button').first().click({ button: 'right' });
  await p.getByRole('menu').waitFor();
});
await capture(`${at++}-statistics`, signedIn, (p) => app(p, '#/statistics'), { fullPage: true });
// U-WU-1: a page reached from another host's switcher offers the way back and the hosts it lacks. The fragment is what
// the other host's link carries; no second server is needed to see this side.
const otherHost = 'http://127.0.0.1:18999';
const handoff = encodeURIComponent(JSON.stringify({ v: 1, f: otherHost, h: [{ n: 'This one', o: new URL(base).origin }, { n: 'Attic', o: 'http://192.168.1.30:8080' }] }));
await capture(`${at++}-host-switcher-back`, signedIn, async (p) => {
  await p.goto(`${base}/#inbound-hosts=${handoff}`);
  await p.waitForSelector('main');
  expect(!(await p.evaluate(() => location.hash.includes('inbound-hosts'))), 'the handoff stayed in the address');
  await p.getByRole('button', { name: /^Host: / }).click();
  // The link could be crafted: an origin this browser hasn't saved gets Add first, and Back only once it is added.
  expect(await p.getByRole('link', { name: /Back to 127\.0\.0\.1:18999/ }).count() === 0, 'Back offered for a host that is not saved');
  await p.getByRole('button', { name: 'Add 127.0.0.1:18999 to this list' }).click();
  await p.getByRole('link', { name: /Back to 127\.0\.0\.1:18999/ }).waitFor();
});
await capture(`${at++}-add-links-numbered`, signedIn, async (p, v) => {
  await app(p, '#/all');
  await addItem(p, v, 'Add links');
  await p.fill('#add-text', `${testServer}/file/img[1-3].jpg?size=1000`);
  await p.waitForTimeout(2500);
  expect(/3 of 3/.test(await dialog(p).innerText()), 'img[1-3].jpg did not become three lines');
});
await capture(`${at++}-folder-picker-outside`, signedIn, async (p, v) => {
  await app(p, '#/settings');
  await optionsPage(p, v, 'Save to');
  await p.getByRole('button', { name: 'Browse…' }).first().click();
  await dialog(p).waitFor();
  await p.waitForTimeout(800);
  const box = dialog(p).getByLabel('Folder path');
  await box.fill('C:\\Windows');
  await dialog(p).getByRole('button', { name: 'Check' }).click();
  await dialog(p).getByRole('alert').waitFor();
  expect(await box.inputValue() === 'C:\\Windows', 'the typed path was replaced');
});
await capture(`${at++}-options-invalid`, signedIn, async (p, v) => {
  await app(p, '#/settings');
  await optionsPage(p, v, 'Connection');
  await p.fill('#set-concurrent', '0');
  await p.fill('#set-connections', '999');
  await optionsPage(p, v, 'General');
  await p.getByRole('button', { name: 'Apply', exact: true }).click();
  await p.waitForTimeout(500);
  expect(await p.locator('[aria-invalid="true"]').count() === 2, 'the two bad boxes are not both marked');
  expect(/2 values need fixing/.test(await p.getByRole('alert').first().innerText()), 'the message does not count both problems');
});
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
  await tool(page, 'Stop');
  await page.waitForTimeout(1500);
  expect(/Paused|Stopped/.test(await video.innerText()), `Stop did not stop holiday-video.mkv: ${(await video.innerText()).replace(/\s+/g, ' ')}`);
  await video.click();
  await tool(page, 'Resume');
  await page.waitForTimeout(2500);
  expect(/Downloading|Connecting|Starting/.test(await video.innerText()), `Resume did not restart holiday-video.mkv: ${(await video.innerText()).replace(/\s+/g, ' ')}`);
});

await step('e2e: Delete without the file keeps setup-tool.exe on disk', async () => {
  await app(page, '#/finished');
  const setup = page.locator('[role="grid"] [role="row"]', { hasText: 'setup-tool.exe' });
  expect(await setup.count() > 0, 'setup-tool.exe is not finished');
  await setup.getByRole('checkbox').check();
  await tool(page, 'Delete');
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

// Review of D2d, items 1 and 2: a download that fails keeps the dialog open with the focus inside, so Escape works,
// and Shift+Tab from the panel stays inside. Never skipped: without the mock provider it fails and says why, and it
// opens items until one can still take a folder (one already on the list can't, after an earlier run's download).
await step('e2e: My TorBox dialog keeps the focus after a failed download', async () => {
  expect(mockKey, 'needs INBOUND_MOCK_KEY (tools/TestServer --torbox) to reach the failure path');
  await app(page, '#/provider');
  await itemRow(page);
  const rows = page.locator('.item-row');
  let folder = null;
  for (let i = 0, n = await rows.count(); i < n && !folder; i++) {
    await rows.nth(i).locator(':scope > button').first().click();
    await dialog(page).waitFor();
    const box = dialog(page).locator('#item-dest');
    if (await box.count() && await box.isEnabled()) folder = box;
    else {
      await page.keyboard.press('Escape');
      await dialog(page).waitFor({ state: 'detached' });
    }
  }
  expect(folder, 'every My TorBox item is already on the download list, so none can fail on its folder');
  await folder.fill('C:\\Windows\\Temp');
  await page.waitForFunction(() => {
    const button = [...document.querySelectorAll('[role="dialog"] button')].find((b) => b.textContent?.trim() === 'Download all');
    return button && !button.disabled;
  }, null, { timeout: 10000 });
  await dialog(page).getByRole('button', { name: 'Download all' }).click();
  await dialog(page).locator('.text-error[role="status"]').waitFor({ timeout: 10000 });
  expect(await page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]')), 'the focus left the dialog after the failed download');
  // The page keeps the box as typed; the next check wants the default folder again.
  await folder.fill('');
  await dialog(page).focus();
  await page.keyboard.press('Shift+Tab');
  expect(await page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]')), 'Shift+Tab from the panel left the dialog');
  await page.keyboard.press('Escape');
  await dialog(page).waitFor({ state: 'detached' });
});

// D2d: My TorBox's item dialog by keyboard (Enter opens, Tab stays inside, Escape closes, the row has the focus again),
// the row menu by Shift+F10, and Download selected from the dialog, which closes it and says so under the list.
if (mockKey) {
  await step('e2e: My TorBox item dialog and row menu by keyboard, then Download selected', async () => {
    await app(page, '#/provider');
    const row = (await itemRow(page)).locator(':scope > button').first();
    await row.focus();
    await page.keyboard.press('Enter');
    await dialog(page).waitFor();
    expect(await page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]')), 'the dialog did not take the focus');
    for (let i = 0; i < 12; i++) await page.keyboard.press('Tab');
    expect(await page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]')), 'Tab left the dialog');
    await page.keyboard.press('Escape');
    await dialog(page).waitFor({ state: 'detached' });
    expect(await row.evaluate((el) => el === document.activeElement), 'the focus did not come back to the row');

    await page.keyboard.press('Shift+F10');
    await page.getByRole('menu').waitFor();
    expect(await page.getByRole('menuitem', { name: /Copy name/ }).count() === 1, 'the row menu has no Copy name');
    await page.keyboard.press('Escape');
    await page.getByRole('menu').waitFor({ state: 'detached' });
    expect(await row.evaluate((el) => el === document.activeElement), 'the focus did not come back to the row after the menu');
    await page.keyboard.press('Shift+F10');
    await page.getByRole('menuitem', { name: /Choose files and download/ }).click();
    await dialog(page).waitFor();
    const selected = dialog(page).getByRole('button', { name: 'Download selected' });
    await page.waitForFunction(() => {
      const button = [...document.querySelectorAll('[role="dialog"] button')].find((b) => b.textContent?.trim() === 'Download selected');
      return button && !button.disabled;
    }, null, { timeout: 10000 });
    await selected.click();
    await dialog(page).waitFor({ state: 'detached', timeout: 10000 }).catch(async () => {
      throw new Error(`Download selected kept the dialog open: ${(await dialog(page).innerText()).replace(/\s+/g, ' ').slice(-300)}`);
    });
    expect(/Added '.+' to the download list|will download/.test(await page.locator('main').innerText()), 'Download selected did not say it was added');
    await page.screenshot({ path: resolve(out, 'e2e-my-torbox-added.desktop-dark.png') });
  });
}

// U-WU-6: Add URL's second step takes the focus, so Escape still closes it.
await step('e2e: Add URL step two has the focus and Escape closes it', async () => {
  await app(page, '#/all');
  await tool(page, 'Add URL');
  await page.fill('#url-box', `${testServer}/file/manual.pdf?size=1048576`);
  await page.keyboard.press('Enter');
  await dialog(page).getByRole('button', { name: 'Start download' }).waitFor();
  await page.waitForTimeout(300);
  expect(await page.evaluate(() => document.activeElement?.id) === 'file-name', `the focus is on ${await page.evaluate(() => document.activeElement?.tagName)}, not File name`);
  await page.keyboard.press('Escape');
  await dialog(page).waitFor({ state: 'detached' });
});

// U-WU-7: Back with a dialog open closes the dialog and leaves the view.
await step('e2e: Back closes the dialog and keeps the view', async () => {
  await app(page, '#/all');
  await app(page, '#/finished');
  // A download's own row (a package row's double-click opens it in place instead).
  const row = page.locator('[role="grid"] [role="row"]').filter({ hasNot: page.getByRole('button', { name: /^(Expand|Collapse)$/ }) }).first();
  await row.dblclick();
  await dialog(page).waitFor();
  await page.goBack();
  await dialog(page).waitFor({ state: 'detached' });
  expect(await page.evaluate(() => location.hash) === '#/finished', `Back changed the view to ${await page.evaluate(() => location.hash)}`);
});

// U-WU-7 on a phone (full-screen dialogs, touch): Back closes only the dialog and the view stays. A #fragment
// navigation while a dialog is open leaves it open; Back from there returns to the view, the next Back closes it.
await step('e2e: phone Back with a dialog open', async () => {
  const phoneVariant = variants.find(phone) ?? allVariants.find(phone);
  const { context, page: p } = await newPage(phoneVariant, signedIn);
  try {
    await app(p, '#/all');
    await app(p, '#/finished');
    await tool(p, 'Add URL');
    await dialog(p).waitFor();
    await p.goBack();
    await dialog(p).waitFor({ state: 'detached' });
    expect(await p.evaluate(() => location.hash) === '#/finished', `Back changed the view to ${await p.evaluate(() => location.hash)}`);

    await tool(p, 'Add URL');
    await dialog(p).waitFor();
    await p.evaluate(() => { location.hash = '#/all'; });
    await p.waitForTimeout(400);
    expect(await dialog(p).count() === 1, 'a #fragment navigation closed the dialog');
    await p.goBack();
    await p.waitForFunction(() => location.hash === '#/finished');
    await p.waitForTimeout(400);
    expect(await dialog(p).count() === 1, 'Back onto the dialog\'s own entry closed it');
    await p.goBack();
    await dialog(p).waitFor({ state: 'detached' });
    expect(await p.evaluate(() => location.hash) === '#/finished', `the last Back changed the view to ${await p.evaluate(() => location.hash)}`);
  } finally {
    await context.close();
  }
});

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
  await dialog(page).waitFor();
  expect(/Screens walk \(browser extension\) will lose access/.test(await dialog(page).innerText()), 'removing a paired app did not ask first');
  await dialog(page).getByRole('button', { name: 'Remove', exact: true }).click();
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

// U-WU-1 on the sign-in page: a link from a host this browser hasn't saved asks before it goes back (and adds it).
await step('e2e: sign-in Back to an unsaved host asks first', async () => {
  const { context, page: p } = await newPage(variants[0]);
  try {
    await p.route(`${otherHost}/**`, (route) => route.fulfill({ status: 200, contentType: 'text/html', body: '<title>other</title>' }));
    await p.goto(`${base}/signin/#inbound-hosts=${handoff}`);
    expect(await p.getByRole('link', { name: /Back to 127\.0\.0\.1:18999/ }).count() === 0, 'Back offered as a plain link for a host that is not saved');
    await p.getByRole('button', { name: /Back to 127\.0\.0\.1:18999/ }).click();
    await p.screenshot({ path: resolve(out, 'e2e-signin-back-asks.desktop-dark.png') });
    const group = p.getByRole('group', { name: /Go back to 127\.0\.0\.1:18999/ });
    await group.getByRole('button', { name: 'Cancel' }).click();
    expect(p.url().startsWith(base), 'Cancel left the page');
    await p.getByRole('button', { name: /Back to 127\.0\.0\.1:18999/ }).click();
    await group.getByRole('button', { name: 'Add it and go back' }).click();
    await p.waitForURL((url) => url.origin === otherHost);
    expect(p.url().includes('#inbound-hosts='), 'the way back lost the list it takes along');
    await p.goto(`${base}/signin/`);
    const saved = await p.evaluate(() => localStorage.getItem('inbound.hosts') ?? '');
    expect(saved.includes(otherHost), 'the host was not added');
  } finally {
    await context.close();
  }
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
