interface Settings {
  enabled: boolean;
  autoIntercept: boolean;
  startMode: "now" | "later";
  defaultDestination: string;
  interceptedExtensions: string[];
  excludedExtensions: string[];
  excludedHosts: string[];
  notifications: boolean;
}

const DEFAULT_SETTINGS: Settings = {
  enabled: true,
  autoIntercept: true,
  startMode: "now",
  defaultDestination: "",
  interceptedExtensions: [],
  excludedExtensions: [],
  excludedHosts: [],
  notifications: true,
};

function byId<T extends HTMLElement>(id: string): T {
  return document.getElementById(id) as T;
}

function splitList(value: string): string[] {
  return [...new Set(value
    .split(/[\s,;]+/)
    .map((item) => item.trim().toLowerCase().replace(/^\./, ""))
    .filter(Boolean))];
}

function fill(settings: Settings): void {
  byId<HTMLInputElement>("enabled").checked = settings.enabled;
  byId<HTMLInputElement>("autoIntercept").checked = settings.autoIntercept;
  byId<HTMLInputElement>("notifications").checked = settings.notifications;
  byId<HTMLSelectElement>("startMode").value = settings.startMode;
  byId<HTMLInputElement>("defaultDestination").value = settings.defaultDestination;
  byId<HTMLTextAreaElement>("interceptedExtensions").value = settings.interceptedExtensions.join(", ");
  byId<HTMLTextAreaElement>("excludedExtensions").value = settings.excludedExtensions.join(", ");
  byId<HTMLTextAreaElement>("excludedHosts").value = settings.excludedHosts.join(", ");
}

function collect(): Settings {
  return {
    enabled: byId<HTMLInputElement>("enabled").checked,
    autoIntercept: byId<HTMLInputElement>("autoIntercept").checked,
    notifications: byId<HTMLInputElement>("notifications").checked,
    startMode: byId<HTMLSelectElement>("startMode").value === "later" ? "later" : "now",
    defaultDestination: byId<HTMLInputElement>("defaultDestination").value.trim(),
    interceptedExtensions: splitList(byId<HTMLTextAreaElement>("interceptedExtensions").value),
    excludedExtensions: splitList(byId<HTMLTextAreaElement>("excludedExtensions").value),
    excludedHosts: splitList(byId<HTMLTextAreaElement>("excludedHosts").value),
  };
}

async function load(): Promise<void> {
  try {
    const status = (await chrome.runtime.sendMessage({ type: "dm.status" })) as {
      ok?: boolean;
      settings?: Settings;
      connected?: boolean;
      lastError?: string;
    };
    fill({ ...DEFAULT_SETTINGS, ...(status?.settings ?? {}) });
    byId<HTMLParagraphElement>("testResult").textContent = status?.connected
      ? "Native host connected."
      : `Native host not connected${status?.lastError ? `: ${status.lastError}` : "."}`;
    document.body.dataset.settingsLoaded = "true";
  } catch (error) {
    byId<HTMLParagraphElement>("testResult").textContent =
      `Could not read settings: ${error instanceof Error ? error.message : String(error)}`;
    document.body.dataset.settingsLoaded = "error";
  }
}

document.addEventListener("DOMContentLoaded", () => {
  void load();

  byId<HTMLButtonElement>("save").addEventListener("click", async () => {
    const result = byId<HTMLSpanElement>("saveResult");
    result.textContent = "Saving…";
    try {
      const response = (await chrome.runtime.sendMessage({
        type: "dm.saveSettings",
        settings: collect(),
      })) as { ok?: boolean; error?: string };
      result.textContent = response?.ok ? "Saved." : (response?.error || "Save failed.");
    } catch (error) {
      result.textContent = error instanceof Error ? error.message : String(error);
    }
  });

  byId<HTMLButtonElement>("test").addEventListener("click", async () => {
    const result = byId<HTMLParagraphElement>("testResult");
    result.textContent = "Testing…";
    try {
      const response = (await chrome.runtime.sendMessage({ type: "dm.ping" })) as {
        ok?: boolean;
        error?: string;
      };
      result.textContent = response?.ok
        ? "Native host responded."
        : `Native host failed: ${response?.error || "unknown error"}`;
    } catch (error) {
      result.textContent = `Native host failed: ${error instanceof Error ? error.message : String(error)}`;
    }
  });

  byId<HTMLButtonElement>("openApp").addEventListener("click", async () => {
    const result = byId<HTMLParagraphElement>("testResult");
    try {
      const response = (await chrome.runtime.sendMessage({ type: "dm.openApp" })) as {
        ok?: boolean;
        error?: string;
      };
      result.textContent = response?.ok ? "Opening Download Manager…" : (response?.error || "Could not open the app.");
    } catch (error) {
      result.textContent = error instanceof Error ? error.message : String(error);
    }
  });

  byId<HTMLButtonElement>("test").addEventListener("contextmenu", () => {
    // no-op: keep the standard browser menu available
  });
});
