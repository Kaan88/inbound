import { build, context } from "esbuild";
import { copyFile, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "..");
const watch = process.argv.includes("--watch");
const targetIndex = process.argv.indexOf("--target");
const target = targetIndex >= 0 ? process.argv[targetIndex + 1] : "chrome";
if (target !== "chrome" && target !== "firefox") {
  throw new Error(`Unknown --target ${target}. Use chrome or firefox.`);
}
// Chrome and Edge share dist; Firefox gets its own folder, since its manifest differs. --outdir builds
// elsewhere (the tests check a real build without touching dist).
const outdirIndex = process.argv.indexOf("--outdir");
const dist = outdirIndex >= 0
  ? path.resolve(process.argv[outdirIndex + 1])
  : path.join(root, target === "firefox" ? "dist-firefox" : "dist");

// Firefox's add-on ID. The desktop app and the Firefox native-messaging manifest name the same one
// (BrowserBridge.FirefoxExtensionId).
export const FIREFOX_EXTENSION_ID = "inbound-bridge@kaan88";

// The Firefox add-on is self-distributed (unlisted on addons.mozilla.org): Setup ships the signed XPI and the
// GitHub release carries it. Firefox checks this file for newer versions; scripts/sign-extension-firefox.ps1
// writes it, and a release squash puts it on main, so it lists only XPIs already published.
export const FIREFOX_UPDATE_URL = "https://raw.githubusercontent.com/Kaan88/inbound/main/extensions/firefox-updates.json";

// The Firefox extension pages' CSP (see firefoxManifest): no upgrade-insecure-requests.
export const FIREFOX_EXTENSION_PAGES_CSP = "script-src 'self'";

const staticFiles = [
  "popup.html",
  "options.html",
  "handoff.html",
  "batch.html",
  "styles.css",
];

/**
 * The Firefox manifest from the Chrome one. Firefox runs an event page (background.scripts) where
 * Chrome runs a service worker, needs its own ID, and has no use for Chrome's "key" (which fixes the
 * unpacked Chrome ID) or minimum_chrome_version.
 */
export function firefoxManifest(chromeManifest) {
  const manifest = structuredClone(chromeManifest);
  delete manifest.key;
  delete manifest.minimum_chrome_version;
  manifest.background = { scripts: ["background.js"] };
  // EXT.B5: Firefox asks where to save before downloads.onCreated, so there downloads are caught from their
  // response headers (blocking webRequest, which Firefox keeps in MV3). Chrome has onDeterminingFilename.
  manifest.permissions = [...new Set([...(manifest.permissions ?? []), "webRequest", "webRequestBlocking"])];
  // EXT.B6: Firefox's default CSP for MV3 extension pages adds upgrade-insecure-requests, which turns the
  // background's fetch to a paired server at http://<LAN address> into https:// (loopback is exempt), so pairing
  // failed with a bare network error while Chrome and Edge, whose default has no upgrade, paired. This is that
  // default without the upgrade; scripts stay limited to the extension's own files.
  manifest.content_security_policy = { extension_pages: FIREFOX_EXTENSION_PAGES_CSP };
  manifest.browser_specific_settings = {
    gecko: {
      id: FIREFOX_EXTENSION_ID,
      // data_collection_permissions, which addons.mozilla.org asks every new add-on for, needs Firefox 140
      // (also an ESR); storage.session and MV3 host permissions as used here are older.
      strict_min_version: "140.0",
      update_url: FIREFOX_UPDATE_URL,
      // Nothing is sent to the developer; details go only to the desktop app on this computer or the
      // user's own paired server.
      data_collection_permissions: { required: ["none"] },
    },
    // Firefox for Android has the key from 142. The add-on is not built for Android (no native app there),
    // but addons.mozilla.org checks the Android minimum too.
    gecko_android: { strict_min_version: "142.0" },
  };
  return manifest;
}

async function copyStatic() {
  for (const file of staticFiles) {
    await copyFile(path.join(root, file), path.join(dist, file));
  }
  for (const size of [16, 32, 48, 128]) {
    await copyFile(path.join(root, "icons", `icon${size}.png`), path.join(dist, "icons", `icon${size}.png`));
  }
  if (target === "firefox") {
    const manifest = JSON.parse(await readFile(path.join(root, "manifest.json"), "utf8"));
    await writeFile(path.join(dist, "manifest.json"), JSON.stringify(firefoxManifest(manifest), null, 2) + "\n");
  } else {
    // Byte for byte: the store packager removes the key from this text and keeps the rest as built.
    await copyFile(path.join(root, "manifest.json"), path.join(dist, "manifest.json"));
  }
}

// Imported by the tests for firefoxManifest: build only when run as the script.
if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(import.meta.filename)) {
  await rm(dist, { recursive: true, force: true });
  await mkdir(path.join(dist, "icons"), { recursive: true });
  await copyStatic();

  const options = {
    entryPoints: {
      background: path.join(root, "src", "background.ts"),
      content: path.join(root, "src", "content.ts"),
      popup: path.join(root, "src", "popup.ts"),
      options: path.join(root, "src", "options.ts"),
      handoff: path.join(root, "src", "handoff.ts"),
      batch: path.join(root, "src", "batch.ts"),
    },
    bundle: true,
    format: "iife",
    target: target === "firefox" ? "firefox128" : "chrome109",
    outdir: dist,
    sourcemap: false,
    minify: false,
    logLevel: "info",
  };

  if (watch) {
    const ctx = await context(options);
    await ctx.watch();
    console.log(`Watching ${target} extension sources...`);
  } else {
    await build(options);
    const manifest = JSON.parse(await readFile(path.join(dist, "manifest.json"), "utf8"));
    if (!existsSync(path.join(dist, "background.js"))) {
      throw new Error("background.js was not produced");
    }
    console.log(`Built ${manifest.name} ${manifest.version} (${target}) -> ${dist}`);
  }
}
