// SITES.11 R0: screenshots of the web UI's Sites screens as built, to set beside the agreed mockup
// (docs/plans/sites-mockups/compare/<screen>-built.png). Point it only at a throwaway test server.
//   INBOUND_URL            the test server (default http://127.0.0.1:18742)
//   INBOUND_TEST_PASSWORD  the throwaway password to set or sign in with
//   INBOUND_OUT            output folder (default ../docs/plans/sites-mockups/compare)
//   INBOUND_STEP           'explore' prints the page's accessibility tree after opening the first site
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

const base = process.env.INBOUND_URL ?? 'http://127.0.0.1:18742';
if (!/^http:\/\/(127\.0\.0\.1|localhost)(:\d+)?$/.test(base)) throw new Error('INBOUND_URL must be a loopback test server.');
const password = process.env.INBOUND_TEST_PASSWORD;
if (!password) throw new Error('Set INBOUND_TEST_PASSWORD to a throwaway password for the test server.');
const out = resolve(process.env.INBOUND_OUT ?? new URL('../../docs/plans/sites-mockups/compare', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
mkdirSync(out, { recursive: true });

const browser = await chromium.launch();
const errors = [];
async function newPage(width, height, mobile = false) {
  const context = await browser.newContext({ viewport: { width, height }, colorScheme: 'dark', isMobile: mobile, hasTouch: mobile });
  const page = await context.newPage();
  page.on('console', (m) => { if (m.type() === 'error') errors.push(`console: ${m.text()}`); });
  page.on('pageerror', (e) => errors.push(`page: ${e.message}`));
  return { context, page };
}
const shot = async (page, name) => { await page.waitForTimeout(700); await page.screenshot({ path: `${out}/${name}-built.png` }); console.log(`saved ${name}`); };

// Sign in (or set the password on a first run).
const { page } = await newPage(1360, 800);
await page.goto(`${base}/`);
await page.waitForSelector('#password');
await page.fill('#password', password);
if (await page.locator('#repeat').count()) await page.fill('#repeat', password);
await page.getByRole('button', { name: /Set the password|Sign in/ }).click();
await page.waitForURL(`${base}/`);
await page.waitForSelector('nav[aria-label="Views"]');
const state = await page.context().storageState();

const openSite = async (p, name) => {
  await p.goto(`${base}/#/sites`);
  await p.waitForTimeout(1500);
  if (process.env.INBOUND_STEP === 'explore') { console.log(await p.locator('main').ariaSnapshot()); await p.screenshot({ path: `${out}/explore-sites.png` }); }
  await p.locator('main').getByText(name, { exact: true }).first().click();
  await p.waitForTimeout(2500);
};

// INBOUND_SITE_PASSWORD: enter the test sites' password again (site files copied from another instance are sealed to it).
if (process.env.INBOUND_SITE_PASSWORD) {
  for (const name of ['nas.home.lan', 'cloud.example.net', 'files.example.org', 'gdrive', 'b2-media']) {
    await page.goto(`${base}/#/sites`);
    await page.getByRole('button', { name: `Edit ${name}`, exact: true }).click();
    const box = page.locator('[role="dialog"] input[type="password"]').first();
    await box.waitFor({ timeout: 8000 });
    await box.fill(process.env.INBOUND_SITE_PASSWORD);
    if (process.env.INBOUND_STEP === 'explore' && name === 'nas.home.lan') console.log(await page.locator('[role="dialog"]').ariaSnapshot());
    await page.locator('[role="dialog"]').getByRole('button', { name: /^Save/ }).click();
    await page.waitForTimeout(1500);
    const left = page.locator('[role="dialog"]');
    console.log(`${name}: ` + (await left.count() ? 'NOT SAVED: ' + (await left.innerText()).split(/\r?\n/).filter((l) => /can|must|isn|outside|allowed|error|fail/i.test(l)).join(' | ') : 'saved'));
  }
}
await openSite(page, 'nas.home.lan');
if (process.env.INBOUND_STEP === 'explore') {
  console.log(await page.locator('body').ariaSnapshot());
  await page.screenshot({ path: `${out}/explore-site.png` });
  await browser.close();
  process.exit(0);
}

const steps = (process.env.INBOUND_STEPS ?? '').split(',').filter(Boolean);
for (const s of steps) {
  const [name, ...actions] = s.split('|');
  for (const a of actions) {
    const at = a.indexOf(':');
    const verb = at < 0 ? a : a.slice(0, at), arg = at < 0 ? '' : a.slice(at + 1);
    if (verb === 'click') await page.getByRole('button', { name: arg }).first().click();
    else if (verb === 'row') await page.getByText(arg, { exact: true }).first().click();
    else if (verb === 'tick') await page.getByRole('checkbox', { name: new RegExp(arg) }).first().check();
    else if (verb === 'site') await openSite(page, arg);
    else if (verb === 'wait') await page.waitForTimeout(Number(arg));
    else if (verb === 'esc') await page.keyboard.press('Escape');
    else if (verb === 'tree') console.log(await page.locator('body').ariaSnapshot());
    else throw new Error(`unknown step ${a}`);
  }
  if (name !== '-') await shot(page, name);
}

if (process.env.INBOUND_PHONE) {
  const { context, page: phone } = await newPage(390, 844, true);
  await context.addCookies(state.cookies);
  await phone.goto(`${base}/`);
  await phone.waitForSelector('nav[aria-label="Views"], [aria-label="Views"]', { timeout: 15000 }).catch(() => undefined);
  await openSite(phone, 'nas.home.lan');
  await shot(phone, 'phone');
  if (process.env.INBOUND_STEP === 'phone-explore') console.log(await phone.locator('main').ariaSnapshot());
  await phone.getByRole('checkbox', { name: 'Select ubuntu-26.04-desktop-amd64.iso' }).check();
  await shot(phone, 'phone-sheets');
}

for (const e of errors) console.log(e);
await browser.close();
if (errors.length) process.exitCode = 1;
