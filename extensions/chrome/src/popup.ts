import { connectionProblem } from "./connection-problem";
import { actionsFor, deleteAsksAboutFile, deleteFileFor, latestJobsByTarget, type JobAction, type JobSummary } from "./job-list";

interface Settings {
  enabled: boolean;
  autoIntercept: boolean;
  startMode: "now" | "later";
  notifications: boolean;
}

interface Status {
  ok: boolean;
  extensionVersion?: string;
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
  server?: { address: string; name?: string; connected: boolean; lastError: string } | null;
  /** Where a caught download goes: "server" only while one is paired. */
  automaticTarget?: "local" | "server";
}

// Set from the status: with a server paired, every download is labelled with where it is.
let serverAddress: string | null = null;

const statusText = document.getElementById("statusText") as HTMLSpanElement;
const statusDot = document.getElementById("statusDot") as HTMLSpanElement;
const statusLabel = document.getElementById("statusLabel") as HTMLSpanElement;
const statusDetail = document.getElementById("statusDetail") as HTMLSpanElement;
const jobsList = document.getElementById("jobs") as HTMLUListElement;
const errorBox = document.getElementById("error") as HTMLParagraphElement;
const getApp = document.getElementById("getApp") as HTMLAnchorElement;

function showError(message: string): void {
  errorBox.textContent = message;
  errorBox.classList.remove("hidden");
}

function clearError(): void {
  errorBox.textContent = "";
  errorBox.classList.add("hidden");
}

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) {
    return "0 B";
  }
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let index = 0;
  while (value >= 1024 && index < units.length - 1) {
    value /= 1024;
    index++;
  }
  return `${value.toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

function isActiveState(state: string): boolean {
  return state === "connecting" || state === "downloading" ||
    state === "retrying" || state === "retryWaiting" ||
    state === "verifying" || state === "muxing";
}

function parseEtaSeconds(job: JobSummary): number | null {
  const value = job.estimatedRemaining;
  if (typeof value === "number" && Number.isFinite(value) && value > 0) {
    // Ticks are far larger than any plausible second count.
    return value > 1_000_000_000 ? value / 10_000_000 : value;
  }

  if (typeof value === "string" && value.trim().length > 0) {
    const text = value.trim();
    const iso = /^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:([\d.]+)S)?)?$/i.exec(text);
    if (iso) {
      const days = Number(iso[1] ?? 0);
      const hours = Number(iso[2] ?? 0);
      const minutes = Number(iso[3] ?? 0);
      const seconds = Number(iso[4] ?? 0);
      const total = days * 86_400 + hours * 3_600 + minutes * 60 + seconds;
      return total > 0 ? total : null;
    }

    const parts = text.split(":").map((part) => Number(part));
    if (parts.length >= 2 && parts.every((part) => Number.isFinite(part))) {
      let total = 0;
      for (const part of parts) {
        total = total * 60 + part;
      }
      return total > 0 ? total : null;
    }
  }

  const speed = job.bytesPerSecond ?? 0;
  if (speed > 0 && typeof job.totalBytes === "number" &&
      job.totalBytes > job.receivedBytes) {
    return (job.totalBytes - job.receivedBytes) / speed;
  }

  return null;
}

function formatEta(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds <= 0) {
    return "";
  }
  const total = Math.max(1, Math.round(seconds));
  if (total < 60) {
    return `${total}s`;
  }
  if (total < 3_600) {
    return `${Math.floor(total / 60)}m ${String(total % 60).padStart(2, "0")}s`;
  }
  return `${Math.floor(total / 3_600)}h ` +
    `${String(Math.floor((total % 3_600) / 60)).padStart(2, "0")}m`;
}

const STATE_LABELS: Record<string, string> = {
  queued: "Queued", connecting: "Connecting", downloading: "Downloading", retrying: "Retrying",
  retryWaiting: "Waiting to retry", verifying: "Verifying", muxing: "Merging", paused: "Paused",
  completed: "Completed", failed: "Failed", cancelled: "Cancelled", scheduled: "Scheduled",
};

function stateLabel(state: string): string {
  return STATE_LABELS[state] ?? state.replace(/([a-z])([A-Z])/g, "$1 $2").replace(/^./, c => c.toUpperCase());
}

let pauseActive = false;
// The list is redrawn every second, so a Delete being confirmed is remembered here: job id -> "delete the file too".
const confirmingDelete = new Map<string, boolean>();
let shownJobs: JobSummary[] = [];

const ACTION_LABELS: Record<JobAction, string> = { pause: "Pause", resume: "Resume", retry: "Retry", remove: "Delete" };

async function runJobAction(job: JobSummary, action: JobAction, deleteFile = false): Promise<void> {
  clearError();
  try {
    const result = (await chrome.runtime.sendMessage({
      type: "dm.jobAction", action, jobId: job.id, deleteFile, target: job.target ?? "local",
    })) as { ok?: boolean; error?: string };
    if (!result?.ok) showError(result?.error || "The app did not do that.");
  } catch (error) {
    showError(error instanceof Error ? error.message : String(error));
  }
  confirmingDelete.delete(job.id);
  await pollJobs();
}

function actionRow(job: JobSummary): HTMLDivElement {
  const row = document.createElement("div");
  row.className = "job-actions";
  const label = job.fileName || "this download";
  if (confirmingDelete.has(job.id)) {
    const option = document.createElement("label");
    if (deleteAsksAboutFile(job)) {
      const box = document.createElement("input");
      box.type = "checkbox";
      box.checked = confirmingDelete.get(job.id) === true;
      box.addEventListener("change", () => confirmingDelete.set(job.id, box.checked));
      option.append(box, "Delete the file too");
    } else {
      option.append("Partial data is removed too.");
    }
    const confirm = document.createElement("button");
    confirm.type = "button";
    confirm.textContent = "Delete";
    confirm.title = job.target === "server" ? `Remove ${label} from the server` : `Remove ${label} from Inbound`;
    confirm.addEventListener("click", () => void runJobAction(job, "remove", deleteFileFor(job, confirmingDelete.get(job.id) === true)));
    const cancel = document.createElement("button");
    cancel.type = "button";
    cancel.className = "quiet";
    cancel.textContent = "Cancel";
    cancel.addEventListener("click", () => { confirmingDelete.delete(job.id); renderJobs(shownJobs); });
    row.append(option, confirm, cancel);
    return row;
  }
  for (const action of actionsFor(job)) {
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = ACTION_LABELS[action];
    button.title = `${ACTION_LABELS[action]} ${label}`;
    button.addEventListener("click", () => {
      if (action === "remove") {
        // Delete asks first, with "Delete the file too" unticked.
        confirmingDelete.set(job.id, false);
        renderJobs(shownJobs);
      } else {
        void runJobAction(job, action);
      }
    });
    row.append(button);
  }
  return row;
}

function renderJobs(jobs: JobSummary[]): void {
  shownJobs = latestJobsByTarget(jobs);
  jobsList.replaceChildren();
  if (!shownJobs.length) {
    const empty = document.createElement("li");
    empty.className = "empty";
    empty.textContent = "No downloads.";
    jobsList.append(empty);
    return;
  }

  for (const job of shownJobs) {
    const item = document.createElement("li");
    const name = document.createElement("span");
    name.className = "name";
    name.textContent = job.fileName || job.id;
    name.title = job.fileName || job.id;

    const state = document.createElement("span");
    state.className = "state";
    state.textContent = stateLabel(job.state);
    state.dataset.state = job.state;

    const meta = document.createElement("span");
    meta.className = "meta";
    const total = job.totalBytes && job.totalBytes > 0 ? formatBytes(job.totalBytes) : "unknown size";
    const parts = [`${formatBytes(job.receivedBytes)} / ${total}`];
    if (isActiveState(job.state)) {
      const speed = job.bytesPerSecond ?? 0;
      if (speed > 0) {
        parts.push(`${formatBytes(speed)}/s`);
      }
      const eta = parseEtaSeconds(job);
      if (eta !== null) {
        parts.push(`ETA ${formatEta(eta)}`);
      }
    }

    meta.textContent = parts.join(" · ");
    // The state badge already says "Cancelled"; only real errors add detail.
    if (job.failure?.message && job.state !== "cancelled") {
      meta.textContent += ` — ${job.failure.message}`;
    }

    if (serverAddress) {
      // EXT.R1: which place the download is in, when there are two.
      const where = document.createElement("span");
      where.className = "target-tag";
      where.dataset.target = job.target ?? "local";
      where.textContent = job.target === "server" ? "Server" : "This PC";
      item.append(name, where, state, meta);
    } else {
      item.append(name, state, meta);
    }

    if (typeof job.progress === "number" && job.progress > 0 && job.progress < 1) {
      const progress = document.createElement("div");
      progress.className = "progress";
      const fill = document.createElement("span");
      fill.style.width = `${Math.max(1, Math.min(100, job.progress * 100))}%`;
      progress.append(fill);
      item.append(progress);
    }

    item.append(actionRow(job));
    jobsList.append(item);
  }
}

function renderServer(status: Status): void {
  const card = document.getElementById("serverCard") as HTMLElement;
  card.classList.toggle("hidden", !status.server);
  if (!status.server) return;
  const dot = document.getElementById("serverDot") as HTMLSpanElement;
  dot.className = `dot ${status.server.connected ? "ok" : "bad"}`;
  (document.getElementById("serverLabel") as HTMLSpanElement).textContent = status.server.connected
    ? "Connected to the server"
    : "Server not answering";
  (document.getElementById("serverDetail") as HTMLSpanElement).textContent = status.server.connected
    ? status.server.address.replace(/^https?:\/\//, "")
    : status.server.lastError || status.server.address;
}

/**
 * The This PC / Server switch (owner, 2026-11-08). Only with a server paired and the desktop app here: with one
 * place there is nothing to switch.
 */
function renderTarget(status: Status): void {
  const card = document.getElementById("targetCard") as HTMLElement;
  card.classList.toggle("hidden", !status.server || status.localMissing === true);
  const target = status.automaticTarget === "server" && status.server ? "server" : "local";
  for (const id of ["targetLocal", "targetServer"]) {
    const button = document.getElementById(id) as HTMLButtonElement;
    button.setAttribute("aria-pressed", String(button.dataset.target === target));
  }
}

async function setTarget(target: "local" | "server"): Promise<void> {
  clearError();
  try {
    const result = (await chrome.runtime.sendMessage({ type: "dm.setDefaultTarget", target })) as { ok?: boolean; error?: string };
    if (!result?.ok) showError(result?.error || "Could not change where downloads go.");
    await refresh();
  } catch (error) {
    showError(error instanceof Error ? error.message : String(error));
  }
}

function renderLeftInBrowser(notes: Status["leftInBrowser"]): void {
  const card = document.getElementById("leftCard") as HTMLElement;
  const list = document.getElementById("leftList") as HTMLUListElement;
  // Only recent ones: an old note would explain a download nobody is wondering about any more.
  const recent = (notes ?? []).filter((note) => Date.now() - note.at < 30 * 60 * 1000).slice(0, 3);
  card.classList.toggle("hidden", recent.length === 0);
  list.replaceChildren(...recent.map((note) => {
    const item = document.createElement("li");
    const name = document.createElement("span");
    name.className = "name";
    name.textContent = note.fileName;
    item.append(name, `: ${note.reason}`);
    return item;
  }));
}

async function pollJobs(): Promise<void> {
  try {
    const result = (await chrome.runtime.sendMessage({ type: "dm.jobs" })) as { ok?: boolean; jobs?: JobSummary[] };
    if (Array.isArray(result?.jobs)) {
      renderJobs(result.jobs);
    }
  } catch {
    // The worker may be restarting; the manual Refresh button still works.
  }
}

async function refresh(): Promise<void> {
  clearError();
  try {
    const status = (await chrome.runtime.sendMessage({ type: "dm.status" })) as Status;
    if (!status?.ok) {
      throw new Error("The extension is not responding. Close and reopen this popup.");
    }

    const enabled = status.settings?.enabled ?? status.enabled ?? false;
    const connected = status.connected ?? false;
    serverAddress = status.server?.address ?? null;
    renderServer(status);
    renderTarget(status);
    // Without the desktop app here, the paired server is the only place; its card says how it is.
    (document.getElementById("statusCard") as HTMLElement).classList.toggle("hidden", status.localMissing === true);
    statusDot.className = "dot";
    if (status.localMissing) {
      statusText.textContent = status.server?.connected ? "Server connected" : "Server not answering";
    } else if (!enabled) {
      statusDot.classList.add("warn");
      statusLabel.textContent = "Automatic sending is off";
      statusDetail.textContent = "Right-click a link to send it, or turn it on in Settings.";
      statusText.textContent = "Automatic off";
    } else if (connected) {
      statusDot.classList.add("ok");
      statusLabel.textContent = "Connected to the desktop app";
      statusDetail.textContent = !status.settings?.autoIntercept
        ? "Automatic sending is off. Right-click a link to send it, or turn it on in Settings."
        : status.pauseUntil && status.pauseUntil > Date.now()
          ? "Catching is paused. Downloads stay in your browser."
          : status.automaticTarget === "server" && status.server
            ? "Downloads that match your rules go to the server"
            : "Downloads that match your rules are sent automatically";
      statusText.textContent = "Connected";
    } else {
      statusDot.classList.add("bad");
      statusLabel.textContent = "Desktop app not reachable";
      statusDetail.textContent = connectionProblem(status.lastError);
      statusText.textContent = "Disconnected";
    }

    getApp.classList.toggle("hidden", !enabled || connected || status.localMissing === true);
    pauseActive = !!status.pauseUntil && status.pauseUntil > Date.now();

    // EXT.B1: whether downloads are caught automatically is always in view, with the way to turn it on.
    const autoState = document.getElementById("autoState") as HTMLSpanElement;
    const automatic = status.automaticSending ?? (enabled && !!status.settings?.autoIntercept);
    autoState.dataset.on = String(automatic && !pauseActive);
    autoState.textContent = !automatic
      ? "Automatic sending: off"
      : pauseActive ? "Automatic sending: paused" : "Automatic sending: on";
    (document.getElementById("autoTurnOn") as HTMLButtonElement).classList.toggle("hidden", automatic);
    renderLeftInBrowser(status.leftInBrowser);

    const pauseButton = document.getElementById("pauseCatching") as HTMLButtonElement;
    pauseButton.textContent = pauseActive
      ? `Resume catching (${Math.ceil((status.pauseUntil! - Date.now()) / 60000)} min left)`
      : "Pause catching for 5 min";
    pauseButton.title = pauseActive
      ? "Downloads currently stay in the browser. Click to resume catching now."
      : "Let the browser handle downloads for the next 5 minutes.";

    renderJobs(status.jobs ?? []);
    void pollJobs();
  } catch (error) {
    statusDot.className = "dot bad";
    statusLabel.textContent = "Extension not ready";
    statusDetail.textContent = "";
    statusText.textContent = "Error";
    showError(error instanceof Error ? error.message : String(error));
  }
}

document.addEventListener("DOMContentLoaded", () => {
  void refresh();

  // Only while the popup is open.
  const liveTimer = window.setInterval(() => void pollJobs(), 1000);
  window.addEventListener("unload", () => window.clearInterval(liveTimer));

  (document.getElementById("refresh") as HTMLButtonElement).addEventListener("click", () => {
    void refresh();
  });

  (document.getElementById("openApp") as HTMLButtonElement).addEventListener("click", async () => {
    clearError();
    try {
      const result = (await chrome.runtime.sendMessage({ type: "dm.openApp" })) as { ok?: boolean; error?: string };
      if (!result?.ok) {
        showError(result?.error || "Could not open the app.");
      }
    } catch (error) {
      showError(error instanceof Error ? error.message : String(error));
    }
  });

  (document.getElementById("downloadAll") as HTMLButtonElement).addEventListener("click", async () => {
    clearError();
    try {
      const result = (await chrome.runtime.sendMessage({ type: "dm.openBatch" })) as {
        ok?: boolean;
        error?: string;
      };
      if (!result?.ok) {
        showError(result?.error || "Could not scan this page.");
        return;
      }
      window.close();
    } catch (error) {
      showError(error instanceof Error ? error.message : String(error));
    }
  });

  (document.getElementById("openServer") as HTMLButtonElement).addEventListener("click", () => {
    if (serverAddress) void chrome.tabs.create({ url: `${serverAddress}/` });
  });

  for (const id of ["targetLocal", "targetServer"]) {
    const button = document.getElementById(id) as HTMLButtonElement;
    button.addEventListener("click", () => void setTarget(button.dataset.target === "server" ? "server" : "local"));
  }

  (document.getElementById("autoTurnOn") as HTMLButtonElement).addEventListener("click", () => {
    void chrome.runtime.openOptionsPage();
  });

  (document.getElementById("options") as HTMLButtonElement).addEventListener("click", () => {
    void chrome.runtime.openOptionsPage();
  });

  (document.getElementById("pauseCatching") as HTMLButtonElement).addEventListener("click", async () => {
    clearError();
    try {
      const result = (await chrome.runtime.sendMessage({ type: "dm.pauseCatching", cancel: pauseActive })) as { ok?: boolean; error?: string };
      if (!result?.ok) {
        showError(result?.error || "Could not change download catching.");
      }
      await refresh();
    } catch (error) {
      showError(error instanceof Error ? error.message : String(error));
    }
  });

  // Job events are reflected by the 1 s jobs poll above, not by a full refresh per event.
});
