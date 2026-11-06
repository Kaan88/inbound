import { connectionProblem } from "./connection-problem";

interface Settings {
  enabled: boolean;
  autoIntercept: boolean;
  startMode: "now" | "later";
  interceptedExtensions: string[];
  excludedExtensions: string[];
  excludedHosts: string[];
  notifications: boolean;
}

const DEFAULT_SETTINGS: Settings = {
  enabled: true,
  autoIntercept: false,
  startMode: "now",
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
    interceptedExtensions: splitList(byId<HTMLTextAreaElement>("interceptedExtensions").value),
    excludedExtensions: splitList(byId<HTMLTextAreaElement>("excludedExtensions").value),
    excludedHosts: splitList(byId<HTMLTextAreaElement>("excludedHosts").value),
  };
}

// Settings opens by itself on first install, and reading the settings can take a few seconds if the
// app has to start. A tick made before then would be overwritten, so the form waits.
function setFormEnabled(enabled: boolean): void {
  for (const field of document.querySelectorAll<HTMLInputElement>("input, select, textarea, #save")) {
    field.disabled = !enabled;
  }
}

async function load(): Promise<void> {
  setFormEnabled(false);
  try {
    const status = (await chrome.runtime.sendMessage({ type: "dm.status" })) as {
      ok?: boolean;
      settings?: Settings;
      connected?: boolean;
      lastError?: string;
    };
    fill({ ...DEFAULT_SETTINGS, ...(status?.settings ?? {}) });
    setFormEnabled(true);
    byId<HTMLParagraphElement>("testResult").textContent = status?.connected
      ? "Connected to the desktop app."
      : connectionProblem(status?.lastError);
    byId<HTMLAnchorElement>("getApp").classList.toggle("hidden", status?.connected === true);
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
      result.textContent = response?.ok ? "Connected to the desktop app." : (response?.error || connectionProblem(undefined));
      byId<HTMLAnchorElement>("getApp").classList.toggle("hidden", response?.ok === true);
    } catch (error) {
      result.textContent = connectionProblem(error instanceof Error ? error.message : String(error));
      byId<HTMLAnchorElement>("getApp").classList.remove("hidden");
    }
  });

  byId<HTMLButtonElement>("openApp").addEventListener("click", async () => {
    const result = byId<HTMLParagraphElement>("testResult");
    try {
      const response = (await chrome.runtime.sendMessage({ type: "dm.openApp" })) as {
        ok?: boolean;
        error?: string;
      };
      result.textContent = response?.ok ? "Opening Inbound Download Manager…" : (response?.error || "Could not open the app.");
    } catch (error) {
      result.textContent = error instanceof Error ? error.message : String(error);
    }
  });

});
