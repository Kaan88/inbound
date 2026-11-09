// Makes the Chrome Web Store images from the real extension pages (EXT.R8 §7), deterministic from fixed data:
//   npm run screens            (in extensions/chrome; from Bash or a normal terminal, never the PowerShell tool)
//
// It builds dist, serves it on 127.0.0.1, and opens each page in Playwright's Chromium with tests/fixtures/chrome-stub.js
// answering the extension APIs from tests/fixtures/screens-data.json: no native host, no network, no browser profile.
// The clock is fixed, motion reduced, the scale 1:1. It fails on a console or page error, on sideways overflow, and when
// Segoe UI Variable is missing (so it runs on Windows).
//
// Output:
//   docs/chrome-store/images/         the store set: 1280x800 and 440x280, 24-bit RGB PNG without alpha
//   artifacts/extension-screens/      the QA set (not committed): every state, dark (and light with SCREENS_LIGHT=1)
// Screenshot 5 (the desktop window) frames a capture of the real app, a test instance in dark mode with the same four
// downloads (docs/chrome-store/LISTING.md); without SCREENS_APP_CAPTURE it is left as it is.
//   SCREENS_APP_CAPTURE=path  that capture (a PNG at 1:1, 984 px wide): screenshot 5 is made from it
//
//   SCREENS_ONLY=regex      only the shots whose name matches
//   SCREENS_NO_QA=1         the store set only
//   SCREENS_LIGHT=1         the QA set in light too

import { chromium } from "@playwright/test";
import { execFileSync } from "node:child_process";
import { createServer } from "node:http";
import { mkdirSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import path from "node:path";
import zlib from "node:zlib";

const root = path.resolve(import.meta.dirname, "..");
const repo = path.resolve(root, "..", "..");
const dist = path.join(root, "dist");
const storeOut = path.join(repo, "docs", "chrome-store", "images");
const qaOut = path.join(repo, "artifacts", "extension-screens");
const data = JSON.parse(readFileSync(path.join(root, "tests", "fixtures", "screens-data.json"), "utf8"));
const stub = readFileSync(path.join(root, "tests", "fixtures", "chrome-stub.js"), "utf8");
const only = process.env.SCREENS_ONLY ? new RegExp(process.env.SCREENS_ONLY) : null;
const appCapture = process.env.SCREENS_APP_CAPTURE ? path.resolve(process.env.SCREENS_APP_CAPTURE) : "";
const FIXED_TIME = "2026-11-09T10:00:00";

// ---- 24-bit RGB PNG ---------------------------------------------------------------------------------------------

/** Decodes an 8-bit RGB or RGBA PNG (any filters) to RGB rows. */
function decodePng(buffer) {
  let offset = 8;
  let width = 0, height = 0, colorType = 0;
  const idat = [];
  while (offset < buffer.length) {
    const length = buffer.readUInt32BE(offset);
    const type = buffer.toString("latin1", offset + 4, offset + 8);
    const body = buffer.subarray(offset + 8, offset + 8 + length);
    if (type === "IHDR") {
      width = body.readUInt32BE(0);
      height = body.readUInt32BE(4);
      if (body[8] !== 8 || body[12] !== 0) throw new Error("Only 8-bit, non-interlaced PNGs are expected.");
      colorType = body[9];
    } else if (type === "IDAT") {
      idat.push(body);
    }
    offset += 12 + length;
  }
  const channels = colorType === 6 ? 4 : colorType === 2 ? 3 : 0;
  if (!channels) throw new Error(`Unexpected PNG colour type ${colorType}.`);
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const stride = width * channels;
  const rgb = Buffer.alloc(width * height * 3);
  let previous = Buffer.alloc(stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = Buffer.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
    for (let x = 0; x < stride; x++) {
      const left = x >= channels ? line[x - channels] : 0;
      const up = previous[x];
      const upLeft = x >= channels ? previous[x - channels] : 0;
      let add = 0;
      if (filter === 1) add = left;
      else if (filter === 2) add = up;
      else if (filter === 3) add = (left + up) >> 1;
      else if (filter === 4) {
        const p = left + up - upLeft;
        const pa = Math.abs(p - left), pb = Math.abs(p - up), pc = Math.abs(p - upLeft);
        add = pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
      }
      line[x] = (line[x] + add) & 0xff;
    }
    for (let x = 0; x < width; x++) {
      if (channels === 4 && line[x * 4 + 3] !== 255) throw new Error("A store image must be opaque.");
      line.copy(rgb, (y * width + x) * 3, x * channels, x * channels + 3);
    }
    previous = line;
  }
  return { width, height, rgb };
}

function chunk(type, body) {
  const head = Buffer.alloc(8);
  head.writeUInt32BE(body.length, 0);
  head.write(type, 4, "latin1");
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(zlib.crc32(Buffer.concat([head.subarray(4), body])) >>> 0, 0);
  return Buffer.concat([head, body, crc]);
}

/** Writes RGB pixels as a PNG of colour type 2 (no alpha), filter 0 on every row, so equal pixels give equal bytes. */
function encodeRgbPng({ width, height, rgb }) {
  const rows = Buffer.alloc((width * 3 + 1) * height);
  for (let y = 0; y < height; y++) rgb.copy(rows, y * (width * 3 + 1) + 1, y * width * 3, (y + 1) * width * 3);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", ihdr),
    chunk("IDAT", zlib.deflateSync(rows, { level: 9 })),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

/** Checks a written store image: PNG colour type 2 at the exact size. */
function checkStoreImage(file, width, height) {
  const buffer = readFileSync(file);
  const ok = buffer.readUInt32BE(16) === width && buffer.readUInt32BE(20) === height && buffer[24] === 8 && buffer[25] === 2;
  if (!ok) throw new Error(`${path.basename(file)} is not a ${width}x${height} 24-bit RGB PNG.`);
}

// ---- A local server for dist, the frame and the stub ---------------------------------------------------------------

const types = { ".html": "text/html", ".js": "text/javascript", ".css": "text/css", ".png": "image/png", ".json": "application/json" };

function serve() {
  const server = createServer((request, response) => {
    const url = new URL(request.url ?? "/", "http://127.0.0.1");
    let file;
    if (url.pathname === "/__capture/app.png" && appCapture) {
      response.writeHead(200, { "content-type": "image/png", "cache-control": "no-store" });
      response.end(readFileSync(appCapture));
      return;
    }
    if (url.pathname.startsWith("/__screens/")) file = path.join(root, "scripts", path.basename(url.pathname));
    else file = path.join(dist, path.normalize(decodeURIComponent(url.pathname)).replace(/^([/\\])+/, ""));
    if (!file.startsWith(root) || !existsSync(file)) {
      response.writeHead(404).end();
      return;
    }
    response.writeHead(200, { "content-type": types[path.extname(file)] ?? "application/octet-stream", "cache-control": "no-store" });
    response.end(readFileSync(file));
  });
  return new Promise((resolve) => server.listen(0, "127.0.0.1", () => resolve(server)));
}

// ---- Shots ------------------------------------------------------------------------------------------------------

const problems = [];

/** One page in a fresh context with the stub, the fixed clock and 1:1 pixels. */
async function open(browser, { url, viewport, theme, scenario }) {
  const context = await browser.newContext({ viewport, colorScheme: theme, reducedMotion: "reduce", deviceScaleFactor: 1 });
  await context.addInitScript({ content: `window.__inboundScreens = ${JSON.stringify({ data, scenario })};` });
  await context.addInitScript({ content: stub });
  const page = await context.newPage();
  await page.clock.setFixedTime(new Date(FIXED_TIME));
  page.on("pageerror", (error) => problems.push(`${url}: page error ${error.message}`));
  page.on("console", (message) => { if (message.type() === "error") problems.push(`${url}: console ${message.text()}`); });
  await page.goto(`${base}/${url}`);
  return { context, page };
}

/** Waits for what a page draws last, checks fonts and sideways overflow inside it. */
async function settle(frame, ready, label) {
  await frame.waitForSelector(ready, { state: "attached", timeout: 10000 });
  await frame.evaluate(() => document.fonts.ready);
  const fonts = await frame.evaluate(() => document.fonts.check('13px "Segoe UI Variable Text"'));
  if (!fonts) throw new Error('Segoe UI Variable Text is not available: run this on Windows 11.');
  const overflow = await frame.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  if (overflow > 0) problems.push(`${label}: ${overflow}px sideways overflow`);
  await new Promise((resolve) => setTimeout(resolve, 250));
}

/** A store image: the frame page with a caption and the extension page. */
async function storeShot(browser, shot) {
  const params = new URLSearchParams({ kind: shot.kind ?? "shot", caption: shot.caption ?? "", sub: shot.sub ?? "", page: shot.page ?? "",
    w: String(shot.width), h: shot.height ? String(shot.height) : "auto", scale: String(shot.scale ?? 1) });
  if (shot.image) params.set("image", shot.image);
  if (shot.compact) params.set("compact", "1");
  if (shot.captionWidth) params.set("captionWidth", String(shot.captionWidth));
  if (shot.top !== undefined) params.set("top", String(shot.top));
  if (shot.clip !== undefined) params.set("clip", String(shot.clip));
  const size = shot.kind === "promo" ? { width: 440, height: 280 } : { width: 1280, height: 800 };
  const { context, page } = await open(browser, { url: `__screens/store-frame.html?${params}`, viewport: size, theme: shot.theme, scenario: shot.scenario });
  if (shot.image) {
    // A capture at 1:1: no page inside to wait for, only the picture and the caption's font.
    await page.waitForFunction(() => document.querySelector(".surface img")?.complete && document.querySelector(".surface img").naturalWidth > 0);
    await settle(page, ".caption h1", shot.file);
    await page.evaluate(() => window.fitFrame());
    await new Promise((resolve) => setTimeout(resolve, 150));
    const png = encodeRgbPng(decodePng(await page.screenshot({ type: "png" })));
    const file = path.join(storeOut, shot.file);
    writeFileSync(file, png);
    checkStoreImage(file, size.width, size.height);
    await context.close();
    console.log(`store  ${shot.file}`);
    return;
  }
  const frame = await (await page.waitForSelector("iframe")).contentFrame();
  await settle(frame, shot.ready, shot.file);
  if (shot.act) await shot.act(frame);
  await page.evaluate(() => window.fitFrame());
  await settle(frame, shot.ready, shot.file);
  const png = encodeRgbPng(decodePng(await page.screenshot({ type: "png" })));
  const file = path.join(storeOut, shot.file);
  writeFileSync(file, png);
  checkStoreImage(file, size.width, size.height);
  await context.close();
  console.log(`store  ${shot.file}`);
}

/** A QA capture: one page at 1:1 in one theme. */
async function qaShot(browser, shot, theme) {
  const { context, page } = await open(browser, { url: shot.page, viewport: shot.viewport, theme, scenario: shot.scenario });
  await settle(page, shot.ready, shot.name);
  if (shot.act) await shot.act(page);
  await new Promise((resolve) => setTimeout(resolve, 150));
  const name = `${shot.name}.${theme}.png`;
  writeFileSync(path.join(qaOut, name), await page.screenshot({ type: "png", fullPage: shot.fullPage ?? false }));
  await context.close();
}

const PAIRED = { paired: true };
const popupReady = ".job, .empty-state:not(.hidden)";

const STORE = [
  { file: "screenshot-1-popup.png", theme: "dark", page: "popup.html", width: 380, scale: 1.5, ready: ".job .job-name",
    caption: "Your downloads, one click away", sub: "This PC and your own server side by side: stop, resume, retry or delete without leaving the page.",
    scenario: PAIRED },
  { file: "screenshot-2-ask.png", theme: "light", page: "popup.html", width: 380, scale: 1.5, ready: "body.asking #askConfirm:not([disabled])",
    caption: "Choose where each download goes", sub: "Ask where to save opens under the toolbar icon: the name, This PC or your server, the folder.",
    scenario: { paired: true, ask: true },
    // The primary takes the focus so Enter presses it; the store image shows the form without the focus ring.
    act: (frame) => frame.evaluate(() => (document.activeElement)?.blur?.()) },
  { file: "screenshot-3-batch.png", theme: "dark", page: "batch.html?tabId=5", width: 720, height: 640, ready: ".link-row",
    captionWidth: 440, caption: "Download all links on a page", sub: "Filter by name, type or site, pick the files and send them as one package.",
    scenario: PAIRED },
  { file: "screenshot-4-options.png", theme: "light", page: "options.html#general", width: 760, height: 600, ready: "body[data-settings-loaded=true]",
    captionWidth: 400, caption: "Your rules, your folders", sub: "Send downloads automatically or ask each time, pick file types and sites, pair your own server.",
    scenario: PAIRED },
  { file: "screenshot-5-app.png", theme: "dark", image: "__capture/app.png", width: 984, compact: true, captionWidth: 256,
    caption: "Everything in the desktop app", sub: "The same downloads in the Windows app, with queues, the speed graph and every detail." },
  { file: "promo-small-440x280.png", kind: "promo", theme: "dark", page: "popup.html", width: 380, top: 40, clip: 114, ready: ".job .job-name",
    scenario: {} },
];

const POPUP = { width: 380, height: 600 };
const WINDOW = { width: 520, height: 600 };
const QA = [
  { name: "popup-list", page: "popup.html", viewport: POPUP, ready: popupReady, scenario: {} },
  { name: "popup-paired", page: "popup.html", viewport: POPUP, ready: popupReady, scenario: PAIRED },
  { name: "popup-empty", page: "popup.html", viewport: POPUP, ready: ".empty-state:not(.hidden)", scenario: { empty: true } },
  { name: "popup-auto-off", page: "popup.html", viewport: POPUP, ready: popupReady, scenario: { autoOff: true } },
  { name: "popup-paused", page: "popup.html", viewport: POPUP, ready: popupReady, scenario: { paused: true } },
  { name: "popup-left", page: "popup.html", viewport: POPUP, ready: ".notice-action", scenario: { left: true },
    act: (page) => page.click(".notice-action") },
  { name: "popup-missing", page: "popup.html", viewport: POPUP, ready: ".empty-state:not(.hidden)", scenario: { localError: "missing" } },
  { name: "popup-stopped", page: "popup.html", viewport: POPUP, ready: ".empty-state:not(.hidden)", scenario: { localError: "stopped" } },
  { name: "popup-forbidden", page: "popup.html", viewport: POPUP, ready: ".empty-state:not(.hidden)", scenario: { localError: "forbidden" } },
  { name: "popup-server-down", page: "popup.html", viewport: POPUP, ready: popupReady, scenario: { paired: true, serverDown: true, target: "server" } },
  { name: "popup-server-only", page: "popup.html", viewport: POPUP, ready: popupReady, scenario: { paired: true, localError: "missing" } },
  { name: "popup-delete", page: "popup.html", viewport: POPUP, ready: ".job .job-name", scenario: PAIRED,
    act: (page) => page.click('.job-actions [data-action="remove"] >> nth=3') },
  { name: "popup-switcher", page: "popup.html", viewport: POPUP, ready: ".job .job-name", scenario: PAIRED,
    act: (page) => page.click("#targetSwitch") },
  { name: "popup-ask-local", page: "popup.html", viewport: POPUP, ready: "body.asking #askConfirm:not([disabled])", scenario: { paired: true, ask: true, askCount: 2 } },
  { name: "popup-ask-server", page: "popup.html", viewport: POPUP, ready: "body.asking #askConfirm:not([disabled])", scenario: { paired: true, ask: true, target: "server" } },
  { name: "popup-ask-folders", page: "popup.html", viewport: POPUP, ready: "body.asking #askConfirm:not([disabled])", scenario: { paired: true, ask: true, target: "server" },
    act: async (page) => { await page.click("#askBrowse"); await page.waitForSelector(".folder-entry"); } },
  { name: "popup-ask-server-down", page: "popup.html", viewport: POPUP, ready: "body.asking #askConfirm:not([disabled])",
    scenario: { paired: true, ask: true, target: "server", serverDown: true } },
  { name: "handoff", page: "handoff.html?id=ask-1", viewport: WINDOW, ready: "#confirm:not([disabled])", scenario: {} },
  { name: "handoff-server", page: "handoff.html?id=ask-1", viewport: WINDOW, ready: "#confirm:not([disabled])", scenario: { paired: true, target: "server" } },
  { name: "handoff-later", page: "handoff.html?id=ask-1", viewport: WINDOW, ready: "#confirm:not([disabled])", scenario: { startMode: "later" } },
  { name: "handoff-long-folder", page: "handoff.html?id=ask-1", viewport: WINDOW, ready: "#confirm:not([disabled])", scenario: { longFolder: true } },
  { name: "popup-ask-long-folder", page: "popup.html", viewport: POPUP, ready: "body.asking #askConfirm:not([disabled])",
    scenario: { ask: true, longFolder: true } },
  { name: "batch-long-folder", page: "batch.html?tabId=5", viewport: { width: 720, height: 640 }, ready: ".link-row", scenario: { longFolder: true } },
  { name: "handoff-unavailable", page: "handoff.html", viewport: WINDOW, ready: "#error:not(.hidden)", scenario: {} },
  { name: "batch", page: "batch.html?tabId=5", viewport: { width: 720, height: 640 }, ready: ".link-row", scenario: PAIRED },
  { name: "batch-empty", page: "batch.html?tabId=5", viewport: { width: 720, height: 640 }, ready: ".link-list .empty", scenario: { noLinks: true } },
  { name: "batch-scanning", page: "batch.html?tabId=5", viewport: { width: 720, height: 640 }, ready: ".link-list .empty", scenario: { linksPending: true } },
  { name: "batch-not-web", page: "batch.html?tabId=5", viewport: { width: 720, height: 640 }, ready: "#error:not(.hidden)", scenario: { linksError: "notWeb" } },
  ...["general", "catching", "server", "connection", "about"].map((name) => ({
    name: `options-${name}`, page: `options.html#${name}`, viewport: { width: 1000, height: 760 }, ready: "body[data-settings-loaded=true]", scenario: PAIRED,
  })),
  { name: "options-narrow", page: "options.html#general", viewport: { width: 600, height: 760 }, ready: "body[data-settings-loaded=true]", scenario: {} },
  { name: "options-unpair", page: "options.html#server", viewport: { width: 1000, height: 760 }, ready: "body[data-settings-loaded=true]", scenario: PAIRED,
    act: (page) => page.click("#unpairServer") },
];

// The owner walks dark mode only for now (light checks deferred); SCREENS_LIGHT=1 adds the light QA set.
const QA_THEMES = process.env.SCREENS_LIGHT ? ["dark", "light"] : ["dark"];

let base = "";
const server = await serve();
base = `http://127.0.0.1:${server.address().port}`;
try {
  execFileSync(process.execPath, [path.join(root, "scripts", "build.mjs")], { cwd: root, stdio: "inherit" });
  mkdirSync(storeOut, { recursive: true });
  const browser = await chromium.launch();
  try {
    for (const shot of STORE) {
      if (only && !only.test(shot.file)) continue;
      if (shot.image && !appCapture) continue;
      await storeShot(browser, shot);
    }
    if (!process.env.SCREENS_NO_QA) {
      mkdirSync(qaOut, { recursive: true });
      for (const shot of QA) {
        if (only && !only.test(shot.name)) continue;
        for (const theme of QA_THEMES) await qaShot(browser, shot, theme);
      }
      console.log(`qa     ${QA.length * QA_THEMES.length} captures in ${qaOut}`);
    }
  } finally {
    await browser.close();
  }
} finally {
  server.close();
}

if (problems.length) {
  console.error(problems.join("\n"));
  process.exitCode = 1;
} else {
  console.log("No console errors, page errors or sideways overflow.");
}
