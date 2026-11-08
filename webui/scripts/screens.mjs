// Walks every web UI screen against a running test server and saves screenshots at desktop and phone width, in the
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
import { mkdirSync } from 'node:fs';
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

const browser = await chromium.launch();
const problems = [];

async function newPage(variant, storageState) {
  const context = await browser.newContext({
    viewport: variant.viewport, colorScheme: variant.colorScheme, isMobile: variant.isMobile ?? false,
    hasTouch: variant.hasTouch ?? false, deviceScaleFactor: 1, storageState,
  });
  const page = await context.newPage();
  page.on('pageerror', (error) => problems.push(`${variant.name}: page error ${error.message}`));
  page.on('console', (message) => {
    if (message.type() === 'error') problems.push(`${variant.name}: console ${message.text()}`);
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
}

/** One screen in every variant: open() gets the page to the state to capture. */
async function capture(name, storageState, open, { fullPage = false } = {}) {
  for (const variant of variants) {
    const { context, page } = await newPage(variant, storageState);
    try {
      await open(page, variant);
      await settle(page);
      await overflow(page, name, variant);
      await page.screenshot({ path: resolve(out, `${name}.${variant.name}.png`), fullPage });
    } catch (error) {
      problems.push(`${variant.name}/${name}: ${error.message.split('\n')[0]}`);
      await page.screenshot({ path: resolve(out, `${name}.${variant.name}.failed.png`) }).catch(() => undefined);
    } finally {
      await context.close();
    }
  }
}

async function app(page, hash) {
  await page.goto(`${base}/${hash}`);
  await page.waitForSelector('main', { timeout: 15000 });
  await page.waitForFunction(() => !document.querySelector('main')?.textContent?.includes('Connecting'), null, { timeout: 15000 });
}

async function menu(page, variant) {
  if (variant.viewport.width < 768) await page.getByRole('button', { name: 'Menu' }).click();
}

// ---- Sign-in -----------------------------------------------------------------------------------------------------

const status = await (await fetch(`${base}/api/auth/status`)).json();
if (status.setupNeeded) {
  await capture('01-setup', undefined, async (page) => {
    await page.goto(`${base}/`);
    await page.waitForSelector('#password');
  });
}

const { context: main, page } = await newPage(variants[0]);
await page.goto(`${base}/`);
await page.waitForSelector('#password');
await page.fill('#password', password);
if (await page.locator('#repeat').count()) await page.fill('#repeat', password);
await page.getByRole('button', { name: /Set the password|Sign in/ }).click();
await page.waitForURL(`${base}/`);
await page.waitForSelector('nav[aria-label="Views"]');
const signedIn = await main.storageState();

await capture('02-signin', undefined, async (p) => {
  await p.goto(`${base}/signin/`);
  await p.waitForSelector('#password');
});

// ---- Seed: settings, the mock provider key, downloads ------------------------------------------------------------

await app(page, '#/settings');
if (downloads) await page.fill('#set-folder', downloads);
await page.getByLabel('Allow downloads from addresses on the local network').check();
await page.getByRole('button', { name: 'Save', exact: true }).click();
await page.waitForTimeout(800);
await page.getByRole('button', { name: 'Extraction', exact: true }).click();
await page.getByLabel('Extract archives when their downloads finish').check();
await page.getByRole('button', { name: 'Save', exact: true }).click();
await page.waitForTimeout(800);

if (mockKey) {
  await app(page, '#/provider');
  await page.fill('#provider-key', mockKey);
  await page.getByRole('button', { name: 'Save key' }).click();
  await page.waitForTimeout(2500);
}

await app(page, '#/all');
const listed = await page.evaluate(() => document.querySelectorAll('[role="grid"] [role="row"]').length);
if (listed === 0) {
  await app(page, '#/all');
  await page.getByRole('toolbar').getByRole('button', { name: 'Add links' }).click();
  const lines = [
    `${testServer}/slow/holiday-video.mkv?size=419430400&bps=196608`,
    `${testServer}/file/report.pdf?size=2097152`,
    `${testServer}/file/album.flac?size=6291456`,
  ];
  await page.fill('#add-text', lines.join('\n'));
  await page.getByRole('button', { name: /Check links/ }).click();
  await page.waitForTimeout(2000);
  await page.fill('#add-package', 'Holiday');
  await page.getByRole('button', { name: /^Add/ }).last().click();
  await page.waitForTimeout(1500);

  const singles = [`${testServer}/file/setup-tool.exe?size=1048576`, `${testServer}/gone/missing.iso`];
  if (files) singles.push(`${files}/holiday-photos.7z`, `${files}/documents.zip`);
  if (mockKey) singles.push('magnet:?xt=urn:btih:5b1e7c3d9a0f42c18e6d7b3a2f9c0e1d4a6b8c7f&dn=Example+Linux+ISO');
  for (const line of singles) {
    await page.getByRole('toolbar').getByRole('button', { name: 'Add links' }).click();
    await page.fill('#add-text', line);
    await page.getByRole('button', { name: /Check links/ }).click();
    await page.waitForTimeout(1500);
    const add = page.getByRole('button', { name: /^Add( \d+)?$/ });
    if (await add.isEnabled()) await add.click();
    else await page.getByRole('button', { name: 'Cancel' }).click();
    await page.waitForTimeout(1000);
  }
  await page.waitForTimeout(4000);
}

// ---- Screens -----------------------------------------------------------------------------------------------------

await capture('03-list-all', signedIn, (p) => app(p, '#/all'));
await capture('04-list-package-selected', signedIn, async (p) => {
  await app(p, '#/all');
  const expand = p.getByRole('button', { name: 'Expand' }).first();
  if (await expand.count()) await expand.click();
  const boxes = p.getByRole('checkbox', { name: 'Select', exact: true });
  if ((await boxes.count()) > 2) {
    await boxes.nth(1).check();
    await boxes.nth(2).check();
  }
});
await capture('05-delete-dialog', signedIn, async (p) => {
  await app(p, '#/finished');
  const boxes = p.getByRole('checkbox', { name: 'Select', exact: true });
  if (await boxes.count()) await boxes.first().check();
  await p.getByRole('toolbar').getByRole('button', { name: 'Delete', exact: true }).click();
  await p.waitForSelector('[role="dialog"]');
});
await capture('06-job-details', signedIn, async (p, v) => {
  await app(p, '#/all');
  const expand = p.getByRole('button', { name: 'Expand' }).first();
  if (await expand.count()) await expand.click();
  // A tap opens it on a phone; a double-click with a mouse.
  const row = p.locator('[role="grid"] [role="row"]', { hasText: 'holiday-video' }).last();
  if (v.hasTouch) await row.tap();
  else await row.dblclick();
  await p.waitForSelector('[role="dialog"]');
});
await capture('07-add-links', signedIn, async (p) => {
  await app(p, '#/all');
  await p.getByRole('toolbar').getByRole('button', { name: 'Add links' }).click();
  await p.fill('#add-text', [
    `${testServer}/file/manual.pdf?size=1048576`,
    'magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Sample',
    'https://not-a-real-hoster.invalid/abc',
  ].join('\n'));
  await p.getByRole('button', { name: /Check links/ }).click();
  await p.waitForTimeout(2500);
});
await capture('08-add-torrent', signedIn, async (p) => {
  await app(p, '#/all');
  await p.getByRole('toolbar').getByRole('button', { name: 'Add links' }).click();
  await p.getByRole('tab', { name: 'Torrent file' }).click();
});
await capture('09-waiting-at-provider', signedIn, async (p, v) => {
  await app(p, '#/remote');
  const actions = p.getByRole('button', { name: 'Actions' }).first();
  if (await actions.count()) await actions.click();
});
await capture('10-extractions', signedIn, (p) => app(p, '#/extractions'));
await capture('11-archive-password', signedIn, async (p) => {
  await app(p, '#/all');
  const button = p.getByRole('button', { name: 'Enter password' });
  if (await button.count()) {
    await button.click();
    await p.waitForSelector('[role="dialog"]');
  }
});
await capture('12-queues', signedIn, (p) => app(p, '#/queues'), { fullPage: true });
await capture('12b-queue-and-schedule-editing', signedIn, async (p) => {
  await app(p, '#/queues');
  await p.getByRole('button', { name: 'Edit', exact: true }).first().click();
  await p.getByRole('button', { name: 'Add schedule' }).click();
}, { fullPage: true });
for (const [index, label] of [['13', 'Downloads'], ['14', 'Favorite folders'], ['15', 'Site logins'], ['16', 'Proxy'], ['17', 'Extraction'], ['18', 'Programs and updates'], ['19', 'Sign-in and browsers']]) {
  await capture(`${index}-settings-${label.toLowerCase().replace(/[^a-z]+/g, '-')}`, signedIn, async (p, v) => {
    await app(p, '#/settings');
    if (v.viewport.width < 768) await p.getByLabel('Settings page', { exact: true }).selectOption({ label });
    else await p.getByRole('button', { name: label, exact: true }).click();
    if (label === 'Sign-in and browsers') await p.getByRole('button', { name: 'Pair a browser' }).click();
    if (label === 'Site logins') await p.getByRole('button', { name: 'Add login' }).click();
  });
}
await capture('20-folder-picker', signedIn, async (p) => {
  await app(p, '#/settings');
  await p.getByRole('button', { name: 'Browse…' }).first().click();
  await p.waitForSelector('[role="dialog"]');
});
for (const [index, tab] of [['21', 'Account'], ['22', 'In my account'], ['23', 'Hoster status']]) {
  await capture(`${index}-provider-${tab.toLowerCase().replace(/[^a-z]+/g, '-')}`, signedIn, async (p) => {
    await app(p, '#/provider');
    await p.getByRole('tab', { name: tab, exact: true }).click();
    await p.waitForTimeout(1500);
  });
}
await capture('24-menu-open', signedIn, async (p, v) => {
  await app(p, '#/all');
  await menu(p, v);
});

// ---- End to end: actions that change the server, after every capture that needs the state before them -----------

// The test archive's password: it was made for this walk (webui/README.md), not a real one.
const archivePassword = process.env.INBOUND_ARCHIVE_PASSWORD ?? 'screens';
await app(page, '#/all');
const enter = page.getByRole('button', { name: 'Enter password' });
if (await enter.count()) {
  await enter.click();
  await page.fill('#archive-password', archivePassword);
  await page.getByRole('button', { name: 'Save and extract' }).click();
  await page.waitForTimeout(4000);
  const text = await page.locator('[role="grid"]').innerText();
  if (!/holiday-photos[\s\S]*Extracted/.test(text)) problems.push('end to end: the password did not extract holiday-photos.7z');
}
await capture('25-after-archive-password', signedIn, (p) => app(p, '#/extractions'));

// Pause and resume the running download from the toolbar, then delete a finished one without its file.
await app(page, '#/active');
const expandActive = page.getByRole('button', { name: 'Expand' }).first();
if (await expandActive.count()) await expandActive.click();
const video = page.locator('[role="grid"] [role="row"]', { hasText: 'holiday-video' }).last();
if (await video.count()) {
  await video.click();
  await page.getByRole('toolbar').getByRole('button', { name: 'Pause' }).click();
  await page.waitForTimeout(1500);
  if (!/Paused/.test(await video.innerText())) problems.push('end to end: Pause did not pause holiday-video.mkv');
  await page.goto(`${base}/#/all`);
  await page.waitForSelector('[role="grid"]');
  const expandAll = page.getByRole('button', { name: 'Expand' }).first();
  if (await expandAll.count()) await expandAll.click();
  const again = page.locator('[role="grid"] [role="row"]', { hasText: 'holiday-video' }).last();
  await again.click();
  await page.getByRole('toolbar').getByRole('button', { name: 'Resume' }).click();
  await page.waitForTimeout(2500);
  if (!/Downloading|Connecting/.test(await again.innerText())) problems.push('end to end: Resume did not restart holiday-video.mkv');
}
await app(page, '#/finished');
const setup = page.locator('[role="grid"] [role="row"]', { hasText: 'setup-tool.exe' });
if (await setup.count()) {
  await setup.getByRole('checkbox', { name: 'Select', exact: true }).check();
  await page.getByRole('toolbar').getByRole('button', { name: 'Delete', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete', exact: true }).click();
  await page.waitForTimeout(1500);
  if (await page.locator('[role="grid"] [role="row"]', { hasText: 'setup-tool.exe' }).count()) problems.push('end to end: Delete left setup-tool.exe in the list');
}

// A .torrent file uploaded from this browser, its files listed from the file itself, sent to the (mock) provider.
const torrent = process.env.INBOUND_TORRENT;
if (torrent && mockKey) {
  await app(page, '#/all');
  await page.getByRole('toolbar').getByRole('button', { name: 'Add links' }).click();
  await page.getByRole('tab', { name: 'Torrent file' }).click();
  await page.locator('input[type="file"]').setInputFiles(torrent);
  await page.waitForTimeout(2500);
  await page.screenshot({ path: resolve(out, '26-add-torrent-files.desktop-dark.png') });
  await page.getByRole('button', { name: 'Add torrent' }).click();
  await page.waitForTimeout(3000);
  // Not cached at the mock provider: the dialog asks before anything is fetched there.
  if (await page.getByRole('dialog', { name: 'Added' }).count()) {
    await page.screenshot({ path: resolve(out, '26b-torrent-approval.desktop-dark.png') });
    await page.getByRole('dialog').getByRole('button', { name: 'Close' }).last().click();
  }
  await app(page, '#/remote');
  if (!/Sample Album/.test(await page.locator('main').innerText())) problems.push('end to end: the uploaded torrent is not waiting at the provider');
}

// A site login and a proxy with passwords: the server keeps them and says only that one is saved.
await app(page, '#/settings');
await page.getByRole('button', { name: 'Site logins', exact: true }).click();
await page.getByRole('button', { name: 'Add login' }).click();
await page.fill('#login-site', 'files.example.test');
await page.fill('#login-user', 'tester');
await page.fill('#login-pass', 'login-test-only');
await page.getByRole('button', { name: 'Save login' }).click();
await page.waitForTimeout(1000);
if (!/password saved/.test(await page.locator('main').innerText())) problems.push('end to end: the site login was not saved with a password');
await page.getByRole('button', { name: 'Proxy', exact: true }).click();
await page.getByLabel('Through this proxy').check();
await page.fill('#proxy-address', 'http://127.0.0.1:3128');
await page.fill('#proxy-user', 'tester');
await page.fill('#proxy-pass', 'proxy-test-only');
await page.getByRole('button', { name: 'Save', exact: true }).click();
await page.waitForTimeout(1000);
if (!(await page.getByLabel('Remove the saved password').count())) problems.push('end to end: the proxy password was not kept');
await page.screenshot({ path: resolve(out, '27-proxy-saved.desktop-dark.png') });
await page.getByLabel('Directly').check();
await page.getByRole('button', { name: 'Save', exact: true }).click();
await page.waitForTimeout(800);

// Pairing: the code shown here, used once on /api/pair as an extension would, then the paired browser is listed.
await page.getByRole('button', { name: 'Sign-in and browsers', exact: true }).click();
await page.getByRole('button', { name: 'Pair a browser' }).click();
const code = (await page.getByLabel('Pairing code').innerText()).replace(/\s/g, '');
const paired = await (await fetch(`${base}/api/pair`, {
  method: 'POST', headers: { 'Content-Type': 'application/json', Origin: 'chrome-extension://pknddmneejadnnngffojnoecojhdfalc' },
  body: JSON.stringify({ code, name: 'Screens walk' }),
})).json();
if (!paired.ok || !paired.token) problems.push(`end to end: pairing failed: ${paired.error}`);
else {
  const reply = await (await fetch(`${base}/api/ipc`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${paired.token}`, Origin: 'chrome-extension://pknddmneejadnnngffojnoecojhdfalc' },
    body: JSON.stringify({ v: 1, type: 'settings.get', id: 'screens-settings', payload: {} }),
  })).json();
  if (reply.type !== 'host.actionResult' || reply.payload?.ok !== false) problems.push('end to end: a paired token could read the settings');
  await page.getByRole('button', { name: 'Done' }).click();
  await page.waitForTimeout(800);
  if (!/Screens walk/.test(await page.locator('main').innerText())) problems.push('end to end: the paired browser is not listed');
  await page.screenshot({ path: resolve(out, '28-paired-browser.desktop-dark.png') });
  await page.locator('li', { hasText: 'Screens walk' }).getByRole('button', { name: 'Remove' }).click();
  await page.waitForTimeout(800);
  const after = await fetch(`${base}/api/ipc`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${paired.token}` },
    body: JSON.stringify({ v: 1, type: 'jobs.list', id: 'screens-list', payload: {} }),
  });
  if (after.status !== 401) problems.push(`end to end: a removed token still answered ${after.status}`);
}

// Sign out: the page goes to sign-in, and the old cookie no longer opens the app.
await app(page, '#/all');
await page.getByRole('button', { name: 'Sign out' }).click();
await page.waitForURL(/\/signin\//);

await main.close();
await browser.close();

console.log(`Screenshots in ${out}`);
if (problems.length) {
  console.log(`${problems.length} problem(s):`);
  for (const problem of problems) console.log(`  ${problem}`);
  process.exitCode = 1;
}
