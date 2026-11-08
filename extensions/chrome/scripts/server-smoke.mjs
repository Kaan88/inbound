// A check of the extension's server transport (src/transport.ts) against a running Inbound server, the way
// the extension uses it: pair with a one-time code, then handoff, list, folder list, delete, a refused
// command, and a revoked token. Run it against a test server only; it signs in, pairs a browser named
// "Extension smoke test", adds one download (start later, to an address that answers nothing) and removes it,
// and removes the pairing again.
//
//   INBOUND_SERVER=http://127.0.0.1:18480 INBOUND_PASSWORD=... node scripts/server-smoke.mjs
//
// Prints one line per step and exits non-zero on the first failure. The password and the token are never printed.
import { buildSync } from "esbuild";
import { createRequire } from "node:module";
import path from "node:path";
import { writeFileSync, mkdtempSync, rmSync } from "node:fs";
import os from "node:os";

const root = path.resolve(import.meta.dirname, "..");
const address = (process.env.INBOUND_SERVER ?? "http://127.0.0.1:18480").replace(/\/+$/, "");
const password = process.env.INBOUND_PASSWORD;
if (!password) throw new Error("Set INBOUND_PASSWORD to the test server's password.");
// The unpacked extension's origin, as Chrome sends it (BrowserBridge.UnpackedExtensionId).
const EXTENSION_ID = "pknddmneejadnnngffojnoecojhdfalc";
const ORIGIN = `chrome-extension://${EXTENSION_ID}`;

const work = mkdtempSync(path.join(os.tmpdir(), "inbound-transport-"));
const bundle = path.join(work, "transport.cjs");
writeFileSync(bundle, buildSync({
  entryPoints: [path.join(root, "src", "transport.ts")], bundle: true, write: false, platform: "node", format: "cjs", target: "node20",
}).outputFiles[0].text);
const transport = createRequire(import.meta.url)(bundle);
rmSync(work, { recursive: true, force: true });

// What the browser adds to an extension's request: its own origin.
const extensionFetch = (url, init) => fetch(url, { ...init, headers: { ...init.headers, Origin: ORIGIN } });

function step(name, ok, detail = "") {
  console.log(`${ok ? "ok  " : "FAIL"} ${name}${detail ? ` (${detail})` : ""}`);
  if (!ok) process.exit(1);
}

// The web UI's part: sign in, show a pairing code.
const signIn = await fetch(`${address}/api/auth/signin`, {
  method: "POST", headers: { "Content-Type": "application/json", Origin: address }, body: JSON.stringify({ password }),
});
const cookie = (signIn.headers.get("set-cookie") ?? "").split(";")[0];
step("sign in to the web UI", signIn.status === 200 && cookie.startsWith("inbound_session="), `HTTP ${signIn.status}`);
const web = (method, pathName, body) => fetch(`${address}${pathName}`, {
  method, headers: { "Content-Type": "application/json", Cookie: cookie, Origin: address }, body: body ? JSON.stringify(body) : undefined,
});
const codeAnswer = await (await web("POST", "/api/auth/pairing/code", {})).json();
step("the web UI shows a pairing code", /^[A-Z2-9]{8}$/.test(codeAnswer.code ?? ""));

// The extension's part, through transport.ts.
const shown = `${codeAnswer.code.slice(0, 4).toLowerCase()}-${codeAnswer.code.slice(4)}`;
const pairing = await transport.pairWithServer(address, shown, "Extension smoke test", extensionFetch);
step("pair with the code as typed, checked with a ping", pairing.token.startsWith("ibt_") && pairing.address === address, `id ${pairing.id}`);
const server = new transport.ServerTransport(pairing, extensionFetch);

const folders = await server.send("settings.downloadFolders", {});
step("the server's default folder", folders.ok === true && typeof folders.defaultFolder === "string", folders.defaultFolder);
const roots = await server.send("folders.browse", { path: "" });
step("Choose… lists the server's folders", roots.ok === true && roots.roots.length > 0, roots.roots.join(", "));

const requestId = crypto.randomUUID();
const handoff = {
  requestId, extensionId: EXTENSION_ID, url: "http://127.0.0.1:9/extension-smoke.zip", pageUrl: "https://site.test/",
  referrer: "https://site.test/", userAgent: "smoke", suggestedFileName: "extension-smoke.zip", startMode: "later",
  isAutoIntercept: true, headers: { Cookie: "smoke=1" }, destinationDirectory: roots.roots[0],
};
const ack = await server.send("handoff.request", handoff, 15000);
step("a handoff is accepted", ack.accepted === true, `job ${ack.jobId}`);
const again = await server.send("handoff.request", handoff, 15000);
step("the same request again is the same download", again.duplicate === true && again.jobId === ack.jobId);

const snapshot = await server.send("jobs.query", {});
step("the popup's list has it, without the settings", snapshot.jobs.some((job) => job.id === ack.jobId) && !("settings" in snapshot));

const removed = await server.send("job.remove", { jobId: ack.jobId, deleteFile: false });
step("the popup's Delete removes it", removed.ok === true);

const refused = await server.request("job.add", { url: "http://127.0.0.1:9/x" }, 5000, "late");
step("a command outside the extension's role is refused", refused.type === "host.actionResult" && refused.payload.ok === false, refused.payload.message);

const revoke = await web("DELETE", `/api/auth/tokens/${pairing.id}`);
step("the web UI removes the pairing", revoke.status === 200);
const after = await server.send("host.ping", {}).catch((error) => error);
step("the removed token stops working, and says to pair again", after instanceof transport.ServerUnpairedError, after.message);
