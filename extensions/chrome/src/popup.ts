import { connectionProblem } from "./connection-problem";

interface Settings {
  enabled: boolean;
  autoIntercept: boolean;
  startMode: "now" | "later";
  notifications: boolean;
}

interface JobSummary {
  id: string;
  fileName: string;
  state: string;
  receivedBytes: number;
  totalBytes?: number;
  progress?: number;
  bytesPerSecond?: number;
  estimatedRemaining?: string | number | null;
  failure?: { message?: string };
}

interface Status {
  ok: boolean;
  extensionVersion?: string;
  enabled?: boolean;
  autoIntercept?: boolean;
  connected?: boolean;
  lastError?: string;
  pauseUntil?: number;
  settings?: Settings;
  jobs?: JobSummary[];
}

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

function renderJobs(jobs: JobSummary[]): void {
  jobsList.replaceChildren();
  if (!jobs.length) {
    const empty = document.createElement("li");
    empty.className = "empty";
    empty.textContent = "No recent downloads.";
    jobsList.append(empty);
    return;
  }

  for (const job of jobs.slice(0, 8)) {
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

    item.append(name, state, meta);

    if (typeof job.progress === "number" && job.progress > 0 && job.progress < 1) {
      const progress = document.createElement("div");
      progress.className = "progress";
      const fill = document.createElement("span");
      fill.style.width = `${Math.max(1, Math.min(100, job.progress * 100))}%`;
      progress.append(fill);
      item.append(progress);
    }

    jobsList.append(item);
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
    statusDot.className = "dot";
    if (!enabled) {
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
          ? "Catching is paused. Downloads stay in Chrome."
          : "Downloads that match your rules are sent automatically";
      statusText.textContent = "Connected";
    } else {
      statusDot.classList.add("bad");
      statusLabel.textContent = "Desktop app not reachable";
      statusDetail.textContent = connectionProblem(status.lastError);
      statusText.textContent = "Disconnected";
    }

    getApp.classList.toggle("hidden", !enabled || connected);
    pauseActive = !!status.pauseUntil && status.pauseUntil > Date.now();
    const pauseButton = document.getElementById("pauseCatching") as HTMLButtonElement;
    pauseButton.textContent = pauseActive
      ? `Resume catching (${Math.ceil((status.pauseUntil! - Date.now()) / 60000)} min left)`
      : "Pause catching for 5 min";
    pauseButton.title = pauseActive
      ? "Downloads currently stay in the browser. Click to resume catching now."
      : "Let the browser handle downloads for the next 5 minutes.";

    renderJobs(status.jobs ?? []);
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

  const liveTimer = window.setInterval(async () => {
    try {
      const result = (await chrome.runtime.sendMessage({ type: "dm.jobs" })) as {
        ok?: boolean;
        jobs?: JobSummary[];
      };
      if (Array.isArray(result?.jobs)) {
        renderJobs(result.jobs);
      }
    } catch {
      // The worker may be restarting; the manual Refresh button still works.
    }
  }, 1000);
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
