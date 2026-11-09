// Options (EXT.R8 §3.6): the desktop app's Options window. Pages on the left, chosen by the address's #hash; OK / Cancel /
// Apply at the bottom. Only what the user changed here is saved, laid over the settings stored now, so an older tab
// or a change in the popup is never undone (U-EX-6), and closing with unsaved changes asks first. The two settings
// "enabled" and "autoIntercept" show as one box, which writes both (U-EX-5).

import { connectionProblem } from "./connection-problem";
import { DEFAULT_EXCLUDED_EXTENSIONS, DEFAULT_SETTINGS, type Settings } from "./capture-rules";
import { normalizeServerAddress } from "./transport";
import { confirmDialog } from "./ui/dialog";
import { fillIcons } from "./ui/icon";
import { applyTheme } from "./ui/theme";

interface ServerStatus {
  address: string;
  name?: string;
  pairedAt?: number;
  connected: boolean;
  lastError: string;
}

type FormValues = Pick<Settings, "enabled" | "autoIntercept" | "notifications" | "startMode" | "destinationMode" |
  "defaultTarget" | "interceptedExtensions" | "excludedExtensions" | "excludedHosts" | "theme">;

export const PAGES = ["general", "catching", "server", "site-access", "connection", "about"] as const;
export type PageId = typeof PAGES[number];

let pairedServer: ServerStatus | null = null;
// The settings as stored when this page last read or saved them: what "changed here" is measured against.
let stored: Settings = { ...DEFAULT_SETTINGS };
let loaded = false;
let saving = false;

const ALL_SITES = ["http://*/*", "https://*/*"];
const SAVE_TIMEOUT_MS = 10000;

/** The tests' shorter wait; never set in a browser. */
function saveTimeoutMs(): number {
  const override = (globalThis as { __inboundSaveTimeoutMs?: unknown }).__inboundSaveTimeoutMs;
  return typeof override === "number" ? override : SAVE_TIMEOUT_MS;
}

function byId<T extends HTMLElement>(id: string): T {
  return document.getElementById(id) as T;
}

export function splitList(value: string): string[] {
  return [...new Set(value
    .split(/[\s,;]+/)
    .map((item) => item.trim().toLowerCase().replace(/^\./, ""))
    .filter(Boolean))];
}

/** Entries the rules could never match, with the page they are on, or null (an invalid entry blocks OK and Apply). */
export function invalidEntry(values: Pick<FormValues, "interceptedExtensions" | "excludedExtensions" | "excludedHosts">):
  { page: PageId; message: string } | null {
  for (const [list, name] of [[values.interceptedExtensions, "Catch only these file types"], [values.excludedExtensions, "Never catch these file types"]] as const) {
    const bad = list.find((item) => !/^[a-z0-9]{1,10}$/.test(item));
    if (bad) return { page: "catching", message: `"${bad}" in ${name} is not a file type: use letters and digits only, such as zip.` };
  }
  const badHost = values.excludedHosts.find((item) => !/^(\*\.)?[a-z0-9-]+(\.[a-z0-9-]+)*(:\d{1,5})?$/.test(item));
  if (badHost) return { page: "catching", message: `"${badHost}" in Never catch downloads from these sites is not a site name, such as example.com.` };
  return null;
}

function destinationModeValue(): "default" | "ask" {
  const checked = document.querySelector<HTMLInputElement>('input[name="destinationMode"]:checked');
  return checked?.value === "ask" ? "ask" : "default";
}

function collect(): FormValues {
  const autoSend = byId<HTMLInputElement>("autoSend").checked;
  const wasOn = stored.enabled && stored.autoIntercept;
  return {
    // The one box: unchanged, both keys stay as stored; changed, both follow it.
    enabled: autoSend === wasOn ? stored.enabled : autoSend,
    autoIntercept: autoSend === wasOn ? stored.autoIntercept : autoSend,
    notifications: byId<HTMLInputElement>("notifications").checked,
    startMode: byId<HTMLSelectElement>("startMode").value === "later" ? "later" : "now",
    destinationMode: destinationModeValue(),
    defaultTarget: byId<HTMLSelectElement>("defaultTarget").value === "server" && pairedServer ? "server" : "local",
    interceptedExtensions: splitList(byId<HTMLTextAreaElement>("interceptedExtensions").value),
    excludedExtensions: splitList(byId<HTMLTextAreaElement>("excludedExtensions").value),
    excludedHosts: splitList(byId<HTMLTextAreaElement>("excludedHosts").value),
    theme: (["light", "dark"].includes(byId<HTMLSelectElement>("theme").value) ? byId<HTMLSelectElement>("theme").value : "system") as Settings["theme"],
  };
}

function same(a: unknown, b: unknown): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

/** What the user changed on this page, by setting name; empty when nothing was. */
export function changesFrom(form: FormValues, base: Settings): Partial<Settings> {
  const changes: Partial<Settings> = {};
  for (const key of Object.keys(form) as Array<keyof FormValues>) {
    if (!same(form[key], base[key])) (changes as Record<string, unknown>)[key] = form[key];
  }
  return changes;
}

function fillField(key: keyof FormValues, settings: Settings): void {
  switch (key) {
    case "enabled":
    case "autoIntercept":
      byId<HTMLInputElement>("autoSend").checked = settings.enabled && settings.autoIntercept;
      break;
    case "notifications": byId<HTMLInputElement>("notifications").checked = settings.notifications; break;
    case "startMode": byId<HTMLSelectElement>("startMode").value = settings.startMode; break;
    case "destinationMode":
      for (const radio of Array.from(document.querySelectorAll<HTMLInputElement>('input[name="destinationMode"]'))) {
        radio.checked = radio.value === (settings.destinationMode === "ask" ? "ask" : "default");
      }
      break;
    case "defaultTarget":
      byId<HTMLSelectElement>("defaultTarget").value = settings.defaultTarget === "server" && pairedServer ? "server" : "local";
      break;
    case "interceptedExtensions":
    case "excludedExtensions":
    case "excludedHosts":
      byId<HTMLTextAreaElement>(key).value = settings[key].join(", ");
      break;
    case "theme": byId<HTMLSelectElement>("theme").value = settings.theme ?? "system"; break;
  }
}

const FORM_KEYS: Array<keyof FormValues> = ["enabled", "autoIntercept", "notifications", "startMode", "destinationMode",
  "defaultTarget", "interceptedExtensions", "excludedExtensions", "excludedHosts", "theme"];

function fill(settings: Settings): void {
  for (const key of FORM_KEYS) fillField(key, settings);
}

function dirty(): boolean {
  return loaded && Object.keys(changesFrom(collect(), stored)).length > 0;
}

function renderFooter(): void {
  byId<HTMLButtonElement>("apply").disabled = !loaded || saving || !dirty();
  byId<HTMLButtonElement>("ok").disabled = !loaded || saving;
}

function footerMessage(text: string, error = false): void {
  const line = byId<HTMLParagraphElement>("saveResult");
  line.textContent = text;
  line.classList.toggle("error", error);
}

// ---- Pages ----------------------------------------------------------------------------------------------------

function pageFromHash(): PageId {
  const hash = location.hash.replace(/^#/, "");
  if (hash === "privacy") return "about";
  return (PAGES as readonly string[]).includes(hash) ? hash as PageId : "general";
}

function showPage(page: PageId): void {
  for (const section of Array.from(document.querySelectorAll<HTMLElement>(".settings-page"))) {
    section.classList.toggle("hidden", section.dataset.page !== page);
  }
  for (const link of Array.from(document.querySelectorAll<HTMLAnchorElement>(".page-link"))) {
    if (link.dataset.page === page) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }
  if (location.hash === "#privacy") byId("privacy").scrollIntoView?.();
}

// ---- Server ---------------------------------------------------------------------------------------------------

/** The server page: the pairing form, or the paired server with its state. */
function showServer(status: ServerStatus | null): void {
  pairedServer = status;
  byId<HTMLElement>("serverUnpaired").classList.toggle("hidden", status !== null);
  byId<HTMLElement>("serverPaired").classList.toggle("hidden", status === null);
  const target = byId<HTMLSelectElement>("defaultTarget");
  target.disabled = status === null;
  if (!status) {
    target.value = "local";
    return;
  }
  const state = byId<HTMLParagraphElement>("serverState");
  state.dataset.on = String(status.connected);
  const where = status.address.replace(/^https?:\/\//, "");
  state.textContent = status.connected
    ? `Paired with the server at ${where}${status.name ? ` as "${status.name}"` : ""}. It is answering.`
    : `Paired with the server at ${where}. ${status.lastError || "It has not answered yet."}`;
}

function showCleartextNote(): void {
  const normalized = normalizeServerAddress(byId<HTMLInputElement>("serverAddress").value);
  byId<HTMLElement>("serverCleartext").classList.toggle("hidden", !(normalized.ok && normalized.cleartext));
}

/**
 * Firefox can install the extension without access to sites, and then cookies cannot be read and the
 * page script does not run. Chrome and Edge grant it at install, so there the page shows only after the
 * user has withdrawn it.
 */
async function showSiteAccess(): Promise<void> {
  const link = byId<HTMLElement>("siteAccessLink");
  try {
    const granted = chrome.permissions?.contains ? await chrome.permissions.contains({ origins: ALL_SITES }) : true;
    link.classList.toggle("hidden", granted);
  } catch {
    link.classList.add("hidden");
  }
}

// ---- Loading and saving ---------------------------------------------------------------------------------------

// Options opens by itself on first install, and reading the settings can take a few seconds if the
// app has to start. A tick made before then would be overwritten, so the form waits.
function setFormEnabled(enabled: boolean): void {
  for (const field of Array.from(document.querySelectorAll<HTMLInputElement>(".settings-page input, .settings-page select, .settings-page textarea, #resetExcluded"))) {
    field.disabled = !enabled;
  }
  if (enabled) byId<HTMLSelectElement>("defaultTarget").disabled = !pairedServer;
}

async function load(): Promise<void> {
  setFormEnabled(false);
  renderFooter();
  void showSiteAccess();
  try {
    const status = (await chrome.runtime.sendMessage({ type: "dm.status" })) as {
      ok?: boolean;
      settings?: Settings;
      connected?: boolean;
      lastError?: string;
      server?: ServerStatus | null;
      extensionVersion?: string;
    };
    showServer(status?.server ?? null);
    stored = { ...DEFAULT_SETTINGS, ...(status?.settings ?? {}) };
    fill(stored);
    applyTheme(stored.theme);
    loaded = true;
    setFormEnabled(true);
    byId("version").textContent = status?.extensionVersion ?? chrome.runtime.getManifest?.().version ?? "";
    byId<HTMLParagraphElement>("testResult").textContent = status?.connected
      ? "Connected to the desktop app."
      : connectionProblem(status?.lastError);
    byId<HTMLAnchorElement>("getApp").classList.toggle("hidden", status?.connected === true);
    document.body.dataset.settingsLoaded = "true";
  } catch (error) {
    footerMessage(`Could not read settings: ${error instanceof Error ? error.message : String(error)}`, true);
    document.body.dataset.settingsLoaded = "error";
  }
  renderFooter();
}

/** Saves what changed here. Returns whether everything is saved (nothing to save counts). */
async function apply(): Promise<boolean> {
  if (!loaded || saving) return false;
  const form = collect();
  const invalid = invalidEntry(form);
  if (invalid) {
    location.hash = invalid.page;
    showPage(invalid.page);
    footerMessage(invalid.message, true);
    return false;
  }
  const changes = changesFrom(form, stored);
  if (Object.keys(changes).length === 0) {
    footerMessage("");
    return true;
  }
  saving = true;
  renderFooter();
  footerMessage("Saving…");
  try {
    // U-EX-17: a save that gets no answer says so instead of staying on "Saving…".
    const response = await Promise.race([
      chrome.runtime.sendMessage({ type: "dm.saveSettings", changes }) as Promise<{ ok?: boolean; error?: string; settings?: Settings }>,
      new Promise<{ ok: false; error: string }>((resolve) => setTimeout(
        () => resolve({ ok: false, error: "The extension did not answer. Your changes are not saved; try Apply again." }), saveTimeoutMs())),
    ]);
    if (!response?.ok) {
      footerMessage(response?.error || "Save failed.", true);
      return false;
    }
    stored = { ...DEFAULT_SETTINGS, ...(response.settings ?? { ...stored, ...changes }) };
    fill(stored);
    applyTheme(stored.theme);
    footerMessage("Saved.");
    return true;
  } catch (error) {
    footerMessage(error instanceof Error ? error.message : String(error), true);
    return false;
  } finally {
    saving = false;
    renderFooter();
  }
}

/** Closes this Options tab (OK and Cancel), as the desktop's Options window closes. */
async function closeTab(): Promise<void> {
  try {
    const tab = await chrome.tabs.getCurrent();
    if (tab?.id !== undefined) { await chrome.tabs.remove(tab.id); return; }
  } catch { /* an embedded options page: nothing to close */ }
  window.close();
}

/** Another window saved settings while this page is open: fields not changed here show the new values. */
function storedElsewhere(next: Settings): void {
  if (saving) return;
  const form = collect();
  const changedHere = new Set(Object.keys(changesFrom(form, stored)));
  const previous = stored;
  stored = { ...DEFAULT_SETTINGS, ...next };
  let shown = false;
  for (const key of FORM_KEYS) {
    if (changedHere.has(key) || same(previous[key], stored[key])) continue;
    fillField(key, stored);
    shown = true;
  }
  if (shown) byId("elsewhereNote").classList.remove("hidden");
  applyTheme(stored.theme);
  renderFooter();
}

document.addEventListener("DOMContentLoaded", () => {
  fillIcons();
  showPage(pageFromHash());
  window.addEventListener("hashchange", () => showPage(pageFromHash()));
  void load();

  // Anything typed or ticked: Apply comes alive.
  for (const type of ["input", "change"]) {
    document.querySelector(".options-page")?.addEventListener(type, () => { footerMessage(""); renderFooter(); });
  }
  // U-EX-6: leaving with unsaved changes asks first (the browser's own "Leave site?").
  window.addEventListener("beforeunload", (event) => {
    if (!dirty()) return;
    event.preventDefault();
    event.returnValue = "";
  });
  chrome.storage?.onChanged?.addListener((changes, area) => {
    if (area === "local" && changes.settings?.newValue && loaded) storedElsewhere(changes.settings.newValue as Settings);
  });

  byId<HTMLButtonElement>("apply").addEventListener("click", () => void apply());
  byId<HTMLButtonElement>("ok").addEventListener("click", async () => {
    if (await apply()) await closeTab();
  });
  byId<HTMLButtonElement>("cancel").addEventListener("click", async () => {
    // Discard: put the stored values back first, so leaving does not ask.
    fill(stored);
    await closeTab();
  });

  byId<HTMLButtonElement>("resetExcluded").addEventListener("click", () => {
    byId<HTMLTextAreaElement>("excludedExtensions").value = DEFAULT_EXCLUDED_EXTENSIONS.join(", ");
    renderFooter();
  });

  byId<HTMLButtonElement>("grantSiteAccess").addEventListener("click", async () => {
    const result = byId<HTMLParagraphElement>("siteAccessResult");
    try {
      // Must run straight from the click: browsers only ask from a user gesture.
      const granted = await chrome.permissions.request({ origins: ALL_SITES });
      result.textContent = granted ? "Access allowed." : "Access was not allowed.";
    } catch (error) {
      result.textContent = error instanceof Error ? error.message : String(error);
    }
    await showSiteAccess();
  });

  byId<HTMLButtonElement>("test").addEventListener("click", async () => {
    const result = byId<HTMLParagraphElement>("testResult");
    result.textContent = "Checking…";
    try {
      const response = (await chrome.runtime.sendMessage({ type: "dm.ping" })) as { ok?: boolean; error?: string };
      result.textContent = response?.ok ? "Connected to the desktop app." : (response?.error || connectionProblem(undefined));
      byId<HTMLAnchorElement>("getApp").classList.toggle("hidden", response?.ok === true);
    } catch (error) {
      result.textContent = connectionProblem(error instanceof Error ? error.message : String(error));
      byId<HTMLAnchorElement>("getApp").classList.remove("hidden");
    }
  });

  byId<HTMLInputElement>("serverAddress").addEventListener("input", showCleartextNote);

  byId<HTMLButtonElement>("pairServer").addEventListener("click", async () => {
    const result = byId<HTMLParagraphElement>("serverResult");
    const normalized = normalizeServerAddress(byId<HTMLInputElement>("serverAddress").value);
    if (!normalized.ok) {
      result.textContent = normalized.error;
      return;
    }
    const button = byId<HTMLButtonElement>("pairServer");
    button.disabled = true;
    try {
      // Access to the server's address only, asked straight from the click (browsers ask only from a
      // user gesture). Chrome and Edge already have it with the access to all sites; Firefox may ask.
      const granted = chrome.permissions?.request ? await chrome.permissions.request({ origins: [normalized.pattern] }) : true;
      if (!granted) {
        result.textContent = "The browser was not allowed to reach that address, so the server can't be paired.";
        return;
      }
      result.textContent = "Pairing…";
      const response = (await chrome.runtime.sendMessage({
        type: "dm.pairServer",
        address: normalized.origin,
        code: byId<HTMLInputElement>("pairingCode").value,
      })) as { ok?: boolean; error?: string; server?: ServerStatus };
      if (!response?.ok || !response.server) {
        result.textContent = response?.error || "Pairing failed.";
        return;
      }
      byId<HTMLInputElement>("pairingCode").value = "";
      showServer(response.server);
      setFormEnabled(true);
      result.textContent = "Paired. Choose below where automatic downloads go, then Apply.";
    } catch (error) {
      result.textContent = error instanceof Error ? error.message : String(error);
    } finally {
      button.disabled = false;
    }
  });

  byId<HTMLButtonElement>("checkServer").addEventListener("click", async () => {
    const result = byId<HTMLParagraphElement>("serverResult");
    result.textContent = "Checking…";
    try {
      const response = (await chrome.runtime.sendMessage({ type: "dm.pingServer" })) as { ok?: boolean; error?: string; server?: ServerStatus };
      if (response?.server) showServer(response.server);
      result.textContent = response?.ok ? "The server is answering." : (response?.error || "The server did not answer.");
    } catch (error) {
      result.textContent = error instanceof Error ? error.message : String(error);
    }
  });

  byId<HTMLButtonElement>("openServer").addEventListener("click", () => {
    if (pairedServer) void chrome.tabs.create({ url: `${pairedServer.address}/` });
  });

  byId<HTMLButtonElement>("unpairServer").addEventListener("click", async () => {
    const result = byId<HTMLParagraphElement>("serverResult");
    // U-EX-13: getting back needs a new code from the server's web UI, so this asks first.
    const answer = await confirmDialog({
      title: "Unpair the server?",
      message: `Downloads go to this PC afterwards. To pair ${pairedServer ? pairedServer.address.replace(/^https?:\/\//, "") : "the server"} again you need a new pairing code from its web UI.`,
      confirmLabel: "Unpair",
      danger: true,
    });
    if (!answer) return;
    try {
      result.textContent = "Unpairing…";
      const response = (await chrome.runtime.sendMessage({ type: "dm.unpairServer" })) as { ok?: boolean; error?: string; revoked?: boolean; wasPaired?: boolean; settings?: Settings };
      if (!response?.ok) {
        result.textContent = response?.error || "Could not unpair.";
        return;
      }
      showServer(null);
      if (response.settings) storedElsewhere(response.settings);
      // The server removed the token itself (DELETE /api/pair/self), or it could not be reached in time.
      result.textContent = response.wasPaired === false
        ? "Not paired. Downloads go to this PC."
        : response.revoked
        ? "Unpaired. Downloads go to this PC. The server removed this browser's pairing too, so its token no longer works there."
        : "Unpaired. Downloads go to this PC. The server could not be told, so remove this browser from its Paired apps list too, so its token stops working there.";
    } catch (error) {
      result.textContent = error instanceof Error ? error.message : String(error);
    }
  });

  byId<HTMLButtonElement>("openApp").addEventListener("click", async () => {
    const result = byId<HTMLParagraphElement>("testResult");
    try {
      const response = (await chrome.runtime.sendMessage({ type: "dm.openApp" })) as { ok?: boolean; error?: string };
      result.textContent = response?.ok ? "Opening Inbound Download Manager…" : (response?.error || "Could not open the app.");
    } catch (error) {
      result.textContent = error instanceof Error ? error.message : String(error);
    }
  });
});
