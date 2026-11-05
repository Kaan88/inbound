interface Settings {
  enabled: boolean;
  autoIntercept: boolean;
  startMode: "now" | "later";
  defaultDestination: string;
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
  bypassUntil?: number;
  settings?: Settings;
  jobs?: JobSummary[];
}

interface MediaVariant {
  url: string;
  label: string;
  bandwidth?: number;
}

interface MediaItem {
  url: string;
  kind: string;
  label: string;
  variants?: MediaVariant[];
}

interface MediaResponse {
  ok?: boolean;
  tabId?: number;
  pageUrl?: string;
  items?: MediaItem[];
  error?: string;
}

const pageTabId = Number(new URLSearchParams(location.search).get("tabId") ?? "");
const statusText = document.getElementById("statusText") as HTMLSpanElement;
const statusDot = document.getElementById("statusDot") as HTMLSpanElement;
const statusLabel = document.getElementById("statusLabel") as HTMLSpanElement;
const statusDetail = document.getElementById("statusDetail") as HTMLSpanElement;
const jobsList = document.getElementById("jobs") as HTMLUListElement;
const mediaCard = document.getElementById("mediaCard") as HTMLElement;
const mediaList = document.getElementById("media") as HTMLUListElement;
const errorBox = document.getElementById("error") as HTMLParagraphElement;

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

let bypassActive = false;

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
    if (job.failure?.message) {
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

function mediaButton(url: string, pageUrl: string): HTMLButtonElement {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "link-button";
  button.textContent = "Download";
  button.addEventListener("click", async () => {
    clearError();
    button.disabled = true;
    try {
      const result = (await chrome.runtime.sendMessage({
        type: "dm.queueMedia",
        url,
        pageUrl,
      })) as { ok?: boolean; error?: string };
      if (!result?.ok) {
        showError(result?.error || "Could not queue this media.");
      }
    } catch (error) {
      showError(error instanceof Error ? error.message : String(error));
    } finally {
      button.disabled = false;
    }
  });
  return button;
}

function renderMedia(items: MediaItem[], pageUrl: string): void {
  mediaList.replaceChildren();
  if (!items.length) {
    mediaCard.classList.add("hidden");
    return;
  }

  mediaCard.classList.remove("hidden");
  for (const item of items) {
    const row = document.createElement("li");
    const name = document.createElement("span");
    name.className = "name";
    name.textContent = item.label || item.kind;
    name.title = item.url;

    const kind = document.createElement("span");
    kind.className = "state";
    kind.textContent = item.kind;

    row.append(name, kind, mediaButton(item.url, pageUrl));
    mediaList.append(row);

    for (const variant of item.variants ?? []) {
      const variantRow = document.createElement("li");
      variantRow.className = "variant";

      const variantName = document.createElement("span");
      variantName.className = "name";
      variantName.textContent = variant.label;
      variantName.title = variant.url;

      const variantKind = document.createElement("span");
      variantKind.className = "state";
      variantKind.textContent = "quality";

      variantRow.append(variantName, variantKind, mediaButton(variant.url, pageUrl));
      mediaList.append(variantRow);
    }
  }
}

async function refreshMedia(): Promise<void> {
  try {
    const media = (await chrome.runtime.sendMessage({
      type: "dm.media",
      tabId: Number.isFinite(pageTabId) && pageTabId > 0 ? pageTabId : undefined,
    })) as MediaResponse;
    renderMedia(media?.items ?? [], media?.pageUrl ?? "");
  } catch {
    renderMedia([], "");
  }
}

async function refresh(): Promise<void> {
  clearError();
  try {
    const status = (await chrome.runtime.sendMessage({ type: "dm.status" })) as Status;
    if (!status?.ok) {
      throw new Error("The extension worker did not respond.");
    }

    const enabled = status.settings?.enabled ?? status.enabled ?? false;
    const connected = status.connected ?? false;
    statusDot.className = "dot";
    if (!enabled) {
      statusDot.classList.add("warn");
      statusLabel.textContent = "Download catching is off";
      statusDetail.textContent = "Turn it on in Settings.";
      statusText.textContent = "Disabled";
    } else if (connected) {
      statusDot.classList.add("ok");
      statusLabel.textContent = "Connected to the desktop app";
      statusDetail.textContent = status.settings?.autoIntercept ? "Browser downloads are caught automatically" : "Automatic catching is off; use the context menu";
      statusText.textContent = "Connected";
    } else {
      statusDot.classList.add("bad");
      statusLabel.textContent = "Desktop app not reachable";
      statusDetail.textContent = status.lastError || "Start Inbound Download Manager, or run Repair from its installer.";
      statusText.textContent = "Disconnected";
    }

    bypassActive = !!status.bypassUntil && status.bypassUntil > Date.now();
    const bypassButton = document.getElementById("bypass") as HTMLButtonElement;
    bypassButton.textContent = bypassActive
      ? `Resume catching (${Math.ceil((status.bypassUntil! - Date.now()) / 60000)} min left)`
      : "Pause catching for 5 min";
    bypassButton.title = bypassActive
      ? "Downloads currently stay in the browser. Click to resume catching now."
      : "Let the browser handle downloads for the next 5 minutes.";

    renderJobs(status.jobs ?? []);
    await refreshMedia();
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

  (document.getElementById("refreshMedia") as HTMLButtonElement).addEventListener("click", async () => {
    try {
      await chrome.runtime.sendMessage({ type: "dm.rescan" });
      await new Promise((resolve) => setTimeout(resolve, 700));
      await refreshMedia();
    } catch (error) {
      showError(error instanceof Error ? error.message : String(error));
    }
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

  (document.getElementById("bypass") as HTMLButtonElement).addEventListener("click", async () => {
    clearError();
    try {
      const result = (await chrome.runtime.sendMessage({ type: "dm.bypass", cancel: bypassActive })) as { ok?: boolean; error?: string };
      if (!result?.ok) {
        showError(result?.error || "Could not change download catching.");
      }
      await refresh();
    } catch (error) {
      showError(error instanceof Error ? error.message : String(error));
    }
  });

  // Job events are reflected by the 1 s jobs poll above; a full refresh per
  // event would refetch media playlists on every progress tick.
});
