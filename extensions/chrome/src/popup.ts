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

const statusText = document.getElementById("statusText") as HTMLSpanElement;
const statusDot = document.getElementById("statusDot") as HTMLSpanElement;
const statusLabel = document.getElementById("statusLabel") as HTMLSpanElement;
const statusDetail = document.getElementById("statusDetail") as HTMLSpanElement;
const jobsList = document.getElementById("jobs") as HTMLUListElement;
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
    state.textContent = job.state;

    const meta = document.createElement("span");
    meta.className = "meta";
    const total = job.totalBytes && job.totalBytes > 0 ? formatBytes(job.totalBytes) : "unknown size";
    meta.textContent = `${formatBytes(job.receivedBytes)} / ${total}`;
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
      statusLabel.textContent = "Bridge disabled";
      statusDetail.textContent = "Enable it in Settings.";
      statusText.textContent = "Disabled";
    } else if (connected) {
      statusDot.classList.add("ok");
      statusLabel.textContent = "Native bridge connected";
      statusDetail.textContent = status.settings?.autoIntercept ? "Automatic handoff on" : "Automatic handoff off";
      statusText.textContent = "Connected";
    } else {
      statusDot.classList.add("bad");
      statusLabel.textContent = "Native bridge unavailable";
      statusDetail.textContent = status.lastError || "Check native-host registration.";
      statusText.textContent = "Disconnected";
    }

    const bypass = status.bypassUntil && status.bypassUntil > Date.now();
    if (bypass) {
      const bypassButton = document.getElementById("bypass") as HTMLButtonElement;
      bypassButton.textContent = `Bypassed (${Math.ceil((status.bypassUntil! - Date.now()) / 60000)} min)`;
    }

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

  (document.getElementById("options") as HTMLButtonElement).addEventListener("click", () => {
    void chrome.runtime.openOptionsPage();
  });

  (document.getElementById("bypass") as HTMLButtonElement).addEventListener("click", async () => {
    clearError();
    try {
      const result = (await chrome.runtime.sendMessage({ type: "dm.bypass" })) as { ok?: boolean; error?: string };
      if (!result?.ok) {
        showError(result?.error || "Could not enable bypass.");
      }
      await refresh();
    } catch (error) {
      showError(error instanceof Error ? error.message : String(error));
    }
  });

  chrome.runtime.onMessage.addListener((message: { type?: string }) => {
    if (message?.type === "dm.jobEvent") {
      void refresh();
    }
  });
});
