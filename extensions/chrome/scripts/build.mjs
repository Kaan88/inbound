import { build, context } from "esbuild";
import { copyFile, mkdir, readFile, rm } from "node:fs/promises";
import { existsSync } from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "..");
const dist = path.join(root, "dist");
const watch = process.argv.includes("--watch");

const staticFiles = [
  "manifest.json",
  "popup.html",
  "options.html",
  "handoff.html",
  "styles.css",
];

async function copyStatic() {
  for (const file of staticFiles) {
    await copyFile(path.join(root, file), path.join(dist, file));
  }
  for (const size of [16, 32, 48, 128]) {
    await copyFile(path.join(root, "icons", `icon${size}.png`), path.join(dist, "icons", `icon${size}.png`));
  }
}

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
  },
  bundle: true,
  format: "iife",
  target: "chrome109",
  outdir: dist,
  sourcemap: false,
  minify: false,
  logLevel: "info",
};

if (watch) {
  const ctx = await context(options);
  await ctx.watch();
  console.log("Watching Chrome extension sources...");
} else {
  await build(options);
  const manifest = JSON.parse(await readFile(path.join(dist, "manifest.json"), "utf8"));
  if (!existsSync(path.join(dist, "background.js"))) {
    throw new Error("background.js was not produced");
  }
  console.log(`Built ${manifest.name} ${manifest.version} -> ${dist}`);
}
