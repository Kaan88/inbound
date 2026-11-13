// The toolbar popup (EXT.R8, docs/plans/extension-design.md §3.1-3.2): the desktop window in small. A header of icon
// buttons, notices only when there is something to say, one list of downloads that alone scrolls, with constant row
// heights and per-row buttons, and a status bar with "Send to", the counts and the connection. The list is updated in
// place every second, keyed by place and job id, so focus and an open menu survive the poll.

import { AskPanel } from "./ask-panel";
import { connectionProblem, isAppMissing } from "./connection-problem";
import { renderPopupRows, type PopupListView } from "./popup-list";
import { confirmDialog, dialogOpen } from "./ui/dialog";
import { fillIcons, icon } from "./ui/icon";
import { menuOpen, openMenu } from "./ui/menu";
import { applyTheme, followThemeChanges, type ThemeChoice } from "./ui/theme";
import {
  badFileName, deleteAsksAboutFile, deleteFileFor, opensFile, REDOWNLOAD_NOTE, rowMenuEntries, type JobAction, type JobCommand, type JobSummary,
  type RowMenuChoice,
} from "./job-list";

interface Settings {
  enabled: boolean;
  autoIntercept: boolean;
  startMode: "now" | "later";
  notifications: boolean;
  theme?: ThemeChoice;
  /** "ask": a caught download asks where to save, in this popup or a folder window. */
  destinationMode?: string;
}

interface ServerInfo { address: string; name?: string; displayName?: string; connected: boolean; lastError: string }

interface Status {
  ok: boolean;
  enabled?: boolean;
  autoIntercept?: boolean;
  automaticSending?: boolean;
  connected?: boolean;
  lastError?: string;
  pauseUntil?: number;
  settings?: Settings;
  leftInBrowser?: Array<{ fileName: string; reason: string; at: number }>;
  jobs?: JobSummary[];
  /** The desktop app is not installed here and a server is paired: the server is the only place. */
  localMissing?: boolean;
  server?: ServerInfo | null;
  /** Where a caught download goes: "server" only while one is paired. */
  automaticTarget?: "local" | "server";
}

interface JobsAnswer {
  ok?: boolean;
  jobs?: JobSummary[];
  groups?: Array<{ target: "local" | "server"; ok: boolean; error?: string }>;
}

const GET_APP_URL = "https://github.com/Kaan88/inbound/releases/latest";
const LEFT_HIDDEN_KEY = "inbound.leftHidden";

function byId<T extends HTMLElement>(id: string): T {
  return document.getElementById(id) as T;
}

let status: Status | null = null;
let jobs: JobSummary[] = [];
let groupsOk: Partial<Record<"local" | "server", boolean>> = {};
let leftOpen = false;
let checking = true;

/**
 * The server's name as the user knows it (EXT.R9): the name Settings or the server gave it ("Home NAS"), else the
 * host of its address ("home-server").
 */
export function serverName(server: ServerInfo | null | undefined): string {
  if (!server) return "Server";
  if (server.displayName?.trim()) return server.displayName.trim();
  try { return new URL(server.address).hostname || "Server"; } catch { return server.address.replace(/^https?:\/\//, "") || "Server"; }
}

function serverOnly(): boolean {
  return status?.localMissing === true && !!status.server;
}

// ---- Messages -------------------------------------------------------------------------------------------------

function say(message: string): void {
  byId("live").textContent = message;
}

function showError(message: string): void {
  byId("errorText").textContent = message;
  byId("error").classList.remove("hidden");
}

function clearError(): void {
  byId("errorText").textContent = "";
  byId("error").classList.add("hidden");
}

async function send<T = { ok?: boolean; error?: string }>(message: Record<string, unknown>): Promise<T | undefined> {
  try {
    return await chrome.runtime.sendMessage(message) as T;
  } catch (error) {
    showError(error instanceof Error ? error.message : String(error));
    return undefined;
  }
}

// ---- Notices --------------------------------------------------------------------------------------------------

function notice(options: { glyph: string; tint: string; text: string; action?: { label: string; run: () => void }; error?: boolean }): HTMLElement {
  const bar = document.createElement("div");
  bar.className = `notice${options.error ? " notice-error" : ""}`;
  bar.setAttribute("role", "status");
  const glyph = document.createElement("span");
  glyph.className = `notice-glyph ${options.tint}`;
  glyph.append(icon(options.glyph, 16));
  const text = document.createElement("span");
  text.className = "notice-text";
  text.textContent = options.text;
  bar.append(glyph, text);
  if (options.action) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "btn notice-action";
    button.textContent = options.action.label;
    button.addEventListener("click", options.action.run);
    bar.append(button);
  }
  return bar;
}

function hiddenLeftNotes(): Set<number> {
  try { return new Set(JSON.parse(localStorage.getItem(LEFT_HIDDEN_KEY) ?? "[]") as number[]); } catch { return new Set(); }
}

function hideLeftNote(at: number): void {
  const hidden = hiddenLeftNotes();
  hidden.add(at);
  try { localStorage.setItem(LEFT_HIDDEN_KEY, JSON.stringify([...hidden].slice(-20))); } catch { /* shown again next time */ }
  renderNotices();
}

function renderNotices(): void {
  const host = byId("notices");
  const bars: HTMLElement[] = [];
  if (status && status.ok) {
    const enabled = status.settings?.enabled ?? status.enabled ?? false;
    const automatic = status.automaticSending ?? (enabled && !!status.settings?.autoIntercept);
    const pauseLeft = status.pauseUntil && status.pauseUntil > Date.now() ? status.pauseUntil - Date.now() : 0;
    if (!automatic) {
      bars.push(notice({
        glyph: "alert", tint: "tint-pause", text: "Downloads are not sent automatically.",
        action: { label: "Turn on", run: () => void openOptions("general") },
      }));
    } else if (pauseLeft > 0) {
      bars.push(notice({
        glyph: "pause", tint: "tint-pause",
        text: `Sending paused, ${Math.ceil(pauseLeft / 60000)} min left. Downloads stay in the browser.`,
        action: { label: "Resume now", run: () => void togglePause() },
      }));
    }
    if (status.server && !status.server.connected) {
      // U-EX-4: where caught downloads go is not answering, so they stay in the browser: said where it is seen.
      const goesThere = automatic && pauseLeft === 0 && status.automaticTarget === "server";
      // With "ask where to save" a caught download is asked about, and the form then starts on This PC (U-EX-11).
      const asks = status.settings?.destinationMode === "ask" && !status.localMissing;
      const after = !goesThere ? "" : asks
        ? " When you start a download you'll be asked, with This PC chosen."
        : " Downloads you start stay in the browser.";
      bars.push(notice({
        glyph: "cloud", tint: "tint-danger",
        text: `${serverName(status.server)} isn't answering.${after}`,
        action: { label: "Open web UI", run: () => openServer() },
      }));
    }
    const hidden = hiddenLeftNotes();
    // Only recent ones: an old note would explain a download nobody is wondering about any more.
    const recent = (status.leftInBrowser ?? []).filter((note) => Date.now() - note.at < 30 * 60 * 1000 && !hidden.has(note.at)).slice(0, 3);
    if (recent.length > 0) {
      const bar = notice({
        glyph: "info", tint: "tint-info",
        text: recent.length === 1 ? "1 download stayed in the browser" : `${recent.length} downloads stayed in the browser`,
        action: { label: leftOpen ? "Hide" : "Show", run: () => { leftOpen = !leftOpen; renderNotices(); } },
      });
      const wrap = document.createElement("div");
      wrap.className = "notice-group";
      wrap.append(bar);
      if (leftOpen) {
        const list = document.createElement("ul");
        list.className = "left-list";
        for (const note of recent) {
          const item = document.createElement("li");
          const name = document.createElement("strong");
          name.textContent = note.fileName;
          const reason = document.createElement("span");
          reason.className = "left-reason";
          reason.append(name, ` stayed in the browser: ${note.reason}`);
          const close = document.createElement("button");
          close.type = "button";
          close.className = "btn-icon tint-neutral";
          close.title = "Hide";
          close.setAttribute("aria-label", `Hide the note about ${note.fileName}`);
          close.append(icon("close", 14));
          close.addEventListener("click", () => hideLeftNote(note.at));
          item.append(reason, close);
          list.append(item);
        }
        wrap.append(list);
      }
      bars.push(wrap);
    }
  } else if (status && !status.ok) {
    bars.push(notice({ glyph: "alert", tint: "tint-danger", text: "The extension is restarting. Close and reopen this popup.", error: true }));
  }
  host.replaceChildren(...bars);
}

// ---- The list -------------------------------------------------------------------------------------------------

function listView(): PopupListView {
  const paired = !!status?.server && !serverOnly();
  return {
    paired,
    serverName: serverName(status?.server),
    groupsOk: {
      local: groupsOk.local ?? (status?.connected !== false),
      server: groupsOk.server ?? (status?.server?.connected !== false),
    },
  };
}

function emptyState(glyph: string, tint: string, title: string, lines: string[], action?: { label: string; primary?: boolean; run: () => void }, mono?: string): HTMLElement[] {
  const mark = document.createElement("span");
  mark.className = `empty-glyph ${tint}`;
  mark.append(icon(glyph, 32));
  const heading = document.createElement("strong");
  heading.textContent = title;
  const parts: HTMLElement[] = [mark, heading];
  for (const line of lines) {
    const text = document.createElement("p");
    text.className = "hint";
    text.textContent = line;
    parts.push(text);
  }
  if (mono) {
    const code = document.createElement("code");
    code.textContent = mono;
    parts.push(code);
  }
  if (action) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = action.primary ? "btn-primary" : "btn";
    button.textContent = action.label;
    button.addEventListener("click", action.run);
    parts.push(button);
  }
  return parts;
}

/** What the list area shows when this PC can't be used and no server is paired. */
function unavailableView(): HTMLElement[] | null {
  if (!status?.ok || status.server || status.connected !== false || checking) return null;
  const error = status.lastError;
  if (isAppMissing(error)) {
    return emptyState("plug", "tint-danger", "Inbound Download Manager isn't installed on this PC.", ["Downloads stay in the browser."],
      { label: "Get the app", primary: true, run: () => void chrome.tabs.create({ url: GET_APP_URL }) });
  }
  if (/forbidden/i.test(error ?? "")) {
    return emptyState("lock", "tint-danger", "Inbound doesn't accept this copy of the extension.", [connectionProblem(error)], undefined,
      chrome.runtime.id);
  }
  return emptyState("plug", "tint-danger", "Can't reach Inbound on this PC.", [connectionProblem(error)],
    { label: "Try again", run: () => void refresh() });
}

/**
 * The row that had the focus can go (deleted, or gone from the app): the focus moves to the row now in its place, or
 * the one before, or the header's first button, rather than to the page.
 */
function keepListFocus(render: () => void): void {
  const list = byId<HTMLUListElement>("jobs");
  const active = document.activeElement as HTMLElement | null;
  const row = active?.closest?.(".job") as HTMLElement | null;
  const at = row && list.contains(row) ? Array.from(list.querySelectorAll(".job")).indexOf(row) : -1;
  render();
  if (at < 0 || active!.isConnected) return;
  const rows = Array.from(list.querySelectorAll<HTMLElement>(".job:not(.skeleton)"));
  const next = rows[Math.min(at, rows.length - 1)] ?? byId<HTMLButtonElement>("downloadAll");
  try { next.focus(); } catch { /* not shown */ }
}

function renderList(): void {
  keepListFocus(drawList);
}

function drawList(): void {
  const list = byId<HTMLUListElement>("jobs");
  const empty = byId("emptyState");
  const unavailable = unavailableView();
  const blocked = unavailable !== null;
  for (const id of ["downloadAll", "pauseCatching", "openApp"]) byId<HTMLButtonElement>(id).disabled = blocked;
  if (unavailable) {
    list.replaceChildren();
    list.classList.add("hidden");
    empty.replaceChildren(...unavailable);
    empty.classList.remove("hidden");
    return;
  }
  const shown = renderPopupRows(list, jobs, listView(), {
    onAction: runJobAction, onOpen: openJob, onReveal: (job) => void revealJob(job), onMenu: openRowMenu,
  });
  if (shown === 0 && !checking) {
    list.classList.add("hidden");
    empty.replaceChildren(...emptyState("download", "tint-info", "No downloads yet",
      ["Downloads you send from this browser, and the app's own, appear here."],
      { label: "Download all links on this page", run: () => void downloadAll() }));
    empty.classList.remove("hidden");
  } else {
    list.classList.remove("hidden");
    empty.classList.add("hidden");
    empty.replaceChildren();
  }
}

function showSkeleton(): void {
  const list = byId<HTMLUListElement>("jobs");
  if (!checking || list.children.length > 0) return;
  for (let index = 0; index < 3; index++) {
    const row = document.createElement("li");
    row.className = "job skeleton";
    row.setAttribute("aria-hidden", "true");
    list.append(row);
  }
}

// ---- The status bar -------------------------------------------------------------------------------------------

function renderStatusBar(): void {
  const switcher = byId<HTMLButtonElement>("targetSwitch");
  const counts = byId("counts");
  const connection = byId("connection");
  if (!status || checking) {
    connection.textContent = "Connecting…";
    connection.className = "status-connection";
    return;
  }
  const paired = !!status.server && !serverOnly();
  switcher.classList.toggle("hidden", !paired);
  if (paired) {
    const target = status.automaticTarget === "server" ? serverName(status.server) : "This PC";
    switcher.replaceChildren(`Send to: ${target}`, icon("chevron-up", 12));
    switcher.title = "Where sent downloads go";
  }
  const visible = jobs.length;
  const active = jobs.filter((job) => ["downloading", "connecting", "verifying", "retrying"].includes(job.state)).length;
  const queued = jobs.filter((job) => job.state === "queued").length;
  counts.textContent = visible === 0 ? "" : [
    `${visible} download${visible === 1 ? "" : "s"}`,
    active > 0 ? `${active} active` : "",
    queued > 0 ? `${queued} queued` : "",
  ].filter(Boolean).join(" · ");

  let words: string;
  let failed: boolean;
  if (serverOnly() || (status.server && status.automaticTarget === "server")) {
    failed = !status.server!.connected;
    words = failed ? "Server not answering" : "Server connected";
    connection.title = failed ? status.server!.lastError || `${serverName(status.server)} isn't answering.` : status.server!.address;
  } else {
    failed = status.connected !== true;
    words = failed ? "Not connected" : "Connected";
    connection.title = failed ? connectionProblem(status.lastError) : "Connected to Inbound on this PC";
  }
  connection.textContent = words;
  connection.className = `status-connection${failed ? " tone-failed" : ""}`;
}

function renderHeader(): void {
  const pauseButton = byId<HTMLButtonElement>("pauseCatching");
  const paused = !!status?.pauseUntil && status.pauseUntil > Date.now();
  pauseButton.replaceChildren(icon(paused ? "play" : "pause", 16));
  pauseButton.className = `btn-icon ${paused ? "tint-start" : "tint-pause"}`;
  const label = paused ? "Resume sending" : "Pause sending";
  pauseButton.setAttribute("aria-label", label);
  pauseButton.title = paused ? "Resume sending now" : "Let the browser keep downloads for 5 minutes";
  const openApp = byId<HTMLButtonElement>("openApp");
  const web = serverOnly();
  openApp.title = web ? "Open web UI" : "Open Inbound";
  openApp.setAttribute("aria-label", openApp.title);
}

function render(): void {
  renderHeader();
  renderNotices();
  renderList();
  renderStatusBar();
}

// ---- Actions --------------------------------------------------------------------------------------------------

const ACTION_WORDS: Record<JobAction, string> = { start: "Started", pause: "Stopped", resume: "Resumed", retry: "Retrying", remove: "Deleted" };

async function runJobAction(job: JobSummary, action: JobAction): Promise<void> {
  clearError();
  let deleteFile = false;
  if (action === "remove") {
    const asks = deleteAsksAboutFile(job);
    const answer = await confirmDialog({
      title: "Delete download?",
      message: `Remove '${job.fileName || "this download"}' from the list?`,
      checkbox: asks ? { label: "Delete from disk: the downloaded file" } : undefined,
      note: asks ? undefined : "Partial data is always removed.",
      confirmLabel: "Delete",
      danger: true,
    });
    if (!answer) return;
    deleteFile = deleteFileFor(job, answer.checked);
  }
  const result = await send({ type: "dm.jobAction", action, jobId: job.id, deleteFile, target: job.target ?? "local" });
  if (result && !result.ok) showError(result.error || "The app did not do that.");
  else if (result) say(`${ACTION_WORDS[action]}: ${job.fileName}`);
  await pollJobs();
}

// ---- The row menu (EXT.R11, GitHub #4) ---------------------------------------------------------------------------

async function sendCommand(job: JobSummary, command: JobCommand, extra: Record<string, unknown> = {}):
  Promise<{ ok?: boolean; error?: string; message?: string } | undefined> {
  clearError();
  return await send({ type: "dm.jobCommand", command, jobId: job.id, target: job.target ?? "local", ...extra });
}

async function revealJob(job: JobSummary): Promise<void> {
  const result = await sendCommand(job, "reveal");
  if (result && !result.ok) showError(result.error || "Could not open the folder.");
  else if (result) say(result.message || `Opened the folder of ${job.fileName}`);
}

async function copyLink(job: JobSummary): Promise<void> {
  clearError();
  try {
    await navigator.clipboard.writeText(job.url ?? "");
    say(`Link copied: ${job.fileName}`);
  } catch {
    showError("Could not copy the link.");
  }
}

async function redownloadJob(job: JobSummary): Promise<void> {
  const answer = await confirmDialog({
    title: "Redownload?",
    message: `Download '${job.fileName || "this download"}' again?`,
    note: REDOWNLOAD_NOTE,
    confirmLabel: "Redownload",
  });
  if (!answer) return;
  const result = await sendCommand(job, "redownload");
  if (result && !result.ok) showError(result.error || "The app did not do that.");
  else if (result) say(`Redownloading: ${job.fileName}`);
  await pollJobs();
}

async function renameJob(job: JobSummary): Promise<void> {
  let name = job.fileName;
  for (;;) {
    const answer = await confirmDialog({
      title: "Rename file",
      message: "The file on disk is renamed in its folder.",
      input: { label: "New name", value: name },
      confirmLabel: "Rename",
    });
    if (!answer) return;
    name = (answer.value ?? "").trim();
    if (name === job.fileName) return;
    const bad = badFileName(name);
    if (bad) { showError(bad); continue; }
    break;
  }
  const result = await sendCommand(job, "rename", { newFileName: name });
  if (result && !result.ok) showError(result.error || "The app did not rename the file.");
  else if (result) say(`Renamed to ${name}`);
  await pollJobs();
}

/** EXT.R12: the app's main window at this download, its row highlighted for a moment. */
async function showInApp(job: JobSummary): Promise<void> {
  const result = await sendCommand(job, "show");
  if (result && !result.ok) showError(result.error || "Could not open Inbound.");
}

function runMenuChoice(job: JobSummary, choice: RowMenuChoice): void {
  if (choice === "show") void showInApp(job);
  else if (choice === "reveal") void revealJob(job);
  else if (choice === "copyLink") void copyLink(job);
  else if (choice === "redownload") void redownloadJob(job);
  else if (choice === "rename") void renameJob(job);
  else void runJobAction(job, "remove");
}

function openRowMenu(job: JobSummary, row: HTMLElement, at?: { x: number; y: number }): void {
  if (dialogOpen()) return;
  // From the keyboard: under the row's start, kept inside the popup like a right-click's.
  const rect = row.getBoundingClientRect();
  at ??= { x: rect.left + 24, y: rect.bottom - 4 };
  openMenu(row, rowMenuEntries(job).map((entry) => ({
    label: entry.label,
    disabled: !entry.enabled,
    title: entry.title,
    onSelect: () => runMenuChoice(job, entry.id),
  })), { at });
}

/** EXT.R13 (owner, 2026-11-13): a finished download's file opens, not the app; the app shows a program in its folder. */
async function openFile(job: JobSummary): Promise<void> {
  // A second double-click while the first is still being answered (Windows' warning may be up) is not sent again.
  if (opening.has(job.id)) return;
  opening.add(job.id);
  try {
    const result = await sendCommand(job, "open");
    if (result && !result.ok) showError(result.error || "Could not open the file.");
    else if (result) say(result.message || `Opened ${job.fileName}`);
  } finally {
    opening.delete(job.id);
  }
}

const opening = new Set<string>();

function openJob(job: JobSummary): void {
  if (opensFile(job)) void openFile(job);
  else if (job.target === "server") openServer();
  else void openApp();
}

function openServer(): void {
  if (status?.server) void chrome.tabs.create({ url: `${status.server.address}/` });
}

async function openApp(): Promise<void> {
  clearError();
  if (serverOnly()) { openServer(); return; }
  const result = await send({ type: "dm.openApp" });
  if (result && !result.ok) showError(result.error || "Could not open Inbound.");
}

async function openOptions(page?: string): Promise<void> {
  if (page) {
    await chrome.tabs.create({ url: chrome.runtime.getURL(`options.html#${page}`) });
    window.close();
  } else {
    await chrome.runtime.openOptionsPage();
  }
}

async function downloadAll(): Promise<void> {
  clearError();
  const result = await send({ type: "dm.openBatch" });
  if (!result) return;
  if (!result.ok) { showError(result.error || "Could not scan this page."); return; }
  window.close();
}

async function togglePause(): Promise<void> {
  clearError();
  const paused = !!status?.pauseUntil && status.pauseUntil > Date.now();
  const result = await send({ type: "dm.pauseCatching", cancel: paused });
  if (result && !result.ok) showError(result.error || "Could not change download sending.");
  else if (result) say(paused ? "Sending resumed" : "Sending paused for 5 minutes");
  await refresh();
}

async function setTarget(target: "local" | "server"): Promise<void> {
  clearError();
  const result = await send({ type: "dm.setDefaultTarget", target });
  if (result && !result.ok) showError(result.error || "Could not change where downloads go.");
  await refresh();
}

function openSwitcher(): void {
  if (!status?.server) return;
  const current = status.automaticTarget === "server" ? "server" : "local";
  openMenu(byId("targetSwitch"), [
    { label: "This PC", checked: current === "local", onSelect: () => void setTarget("local") },
    { label: serverName(status.server), checked: current === "server", onSelect: () => void setTarget("server") },
  ], { upward: true });
}

// ---- Loading --------------------------------------------------------------------------------------------------

async function pollJobs(): Promise<void> {
  if (dialogOpen()) return;
  try {
    const result = await chrome.runtime.sendMessage({ type: "dm.jobs" }) as JobsAnswer;
    if (Array.isArray(result?.jobs)) {
      jobs = result.jobs;
      groupsOk = Object.fromEntries((result.groups ?? []).map((group) => [group.target, group.ok]));
      renderList();
      if (!menuOpen()) renderStatusBar();
    }
  } catch {
    // The worker may be restarting; the next poll or a reopen asks again.
  }
}

async function refresh(): Promise<void> {
  try {
    const answer = await chrome.runtime.sendMessage({ type: "dm.status" }) as Status;
    status = answer?.ok ? answer : { ok: false };
    if (answer?.ok) {
      applyTheme(answer.settings?.theme);
      if (checking) jobs = answer.jobs ?? [];
    }
  } catch {
    status = { ok: false };
  }
  checking = false;
  render();
  void pollJobs();
}

/**
 * EXT.R7: a port to the background while the popup is open. Downloads caught with "ask where to save" wait here;
 * the port closing (the popup closed) gives what still waits back to the browser, and its pings keep the worker
 * alive meanwhile. A worker that restarted is connected to again.
 */
let askPort: chrome.runtime.Port | null = null;
let popupClosing = false;

function connectAsk(panel: AskPanel, reconnects = 0): void {
  if (popupClosing) return;
  let port: chrome.runtime.Port;
  try { port = chrome.runtime.connect({ name: "dm.ask" }); } catch { return; }
  askPort = port;
  const timer = window.setInterval(() => {
    try { port.postMessage({ type: "ping" }); } catch { /* reconnected below */ }
  }, 20000);
  port.onMessage.addListener(() => void panel.refresh());
  port.onDisconnect.addListener(() => {
    window.clearInterval(timer);
    if (askPort === port) askPort = null;
    if (!popupClosing && reconnects < 5) {
      connectAsk(panel, reconnects + 1);
      void panel.refresh();
    }
  });
}

document.addEventListener("DOMContentLoaded", () => {
  fillIcons();
  followThemeChanges();
  const ask = new AskPanel((waiting) => {
    document.body.classList.toggle("asking", waiting);
    // Back to the list: it shows the download just sent.
    if (!waiting) void refresh();
  });
  connectAsk(ask);
  // Closing: told at once, since Firefox reports the port closed only about a second later (see background.ts).
  window.addEventListener("pagehide", () => {
    popupClosing = true;
    try { askPort?.postMessage({ type: "dm.askClosing" }); } catch { /* the port closing says the same */ }
  });
  void ask.refresh();
  renderStatusBar();
  // Skeleton rows only if the first answer takes a while; a quick answer draws the list straight away.
  window.setTimeout(showSkeleton, 300);
  void refresh();

  // Only while the popup is open.
  const liveTimer = window.setInterval(() => void pollJobs(), 1000);
  const statusTimer = window.setInterval(() => { if (!dialogOpen() && !menuOpen()) void refresh(); }, 5000);
  // pagehide, not unload: Chromium refuses unload listeners in more and more pages.
  window.addEventListener("pagehide", () => { window.clearInterval(liveTimer); window.clearInterval(statusTimer); });

  byId("openApp").addEventListener("click", () => void openApp());
  byId("downloadAll").addEventListener("click", () => void downloadAll());
  byId("options").addEventListener("click", () => void openOptions());
  byId("pauseCatching").addEventListener("click", () => void togglePause());
  byId("targetSwitch").addEventListener("click", openSwitcher);
  byId("errorClose").addEventListener("click", clearError);
});
