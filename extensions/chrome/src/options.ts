import { connectionProblem } from "./connection-problem";
import { DEFAULT_EXCLUDED_EXTENSIONS, DEFAULT_SETTINGS, type Settings } from "./capture-rules";

const ALL_SITES = ["http://*/*", "https://*/*"];

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
  byId<HTMLSelectElement>("destinationMode").value = settings.destinationMode === "ask" ? "ask" : "default";
  byId<HTMLTextAreaElement>("interceptedExtensions").value = settings.interceptedExtensions.join(", ");
  byId<HTMLTextAreaElement>("excludedExtensions").value = settings.excludedExtensions.join(", ");
  byId<HTMLTextAreaElement>("excludedHosts").value = settings.excludedHosts.join(", ");
  showAutomaticState(settings);
}

/** The saved state, in words, at the top of the page: it is what the toolbar badge reflects. */
function showAutomaticState(settings: Pick<Settings, "enabled" | "autoIntercept">): void {
  const banner = byId<HTMLParagraphElement>("autoBanner");
  const on = settings.enabled && settings.autoIntercept;
  banner.dataset.on = String(on);
  banner.textContent = on
    ? "Automatic sending is on: downloads that match the rules below go to the desktop app."
    : "Automatic sending is off: downloads stay in your browser unless you send them from the right-click menu. The toolbar icon shows OFF.";
}

function collect(): Settings {
  return {
    ...DEFAULT_SETTINGS,
    enabled: byId<HTMLInputElement>("enabled").checked,
    autoIntercept: byId<HTMLInputElement>("autoIntercept").checked,
    notifications: byId<HTMLInputElement>("notifications").checked,
    startMode: byId<HTMLSelectElement>("startMode").value === "later" ? "later" : "now",
    destinationMode: byId<HTMLSelectElement>("destinationMode").value === "ask" ? "ask" : "default",
    interceptedExtensions: splitList(byId<HTMLTextAreaElement>("interceptedExtensions").value),
    excludedExtensions: splitList(byId<HTMLTextAreaElement>("excludedExtensions").value),
    excludedHosts: splitList(byId<HTMLTextAreaElement>("excludedHosts").value),
  };
}

// Settings opens by itself on first install, and reading the settings can take a few seconds if the
// app has to start. A tick made before then would be overwritten, so the form waits.
function setFormEnabled(enabled: boolean): void {
  for (const field of document.querySelectorAll<HTMLInputElement>("input, select, textarea, #save, #resetExcluded")) {
    field.disabled = !enabled;
  }
}

/**
 * Firefox can install the extension without access to sites, and then cookies cannot be read and the
 * page script does not run. Chrome and Edge grant it at install, so the card stays hidden there.
 */
async function showSiteAccess(): Promise<void> {
  const card = byId<HTMLElement>("siteAccessCard");
  try {
    const granted = chrome.permissions?.contains ? await chrome.permissions.contains({ origins: ALL_SITES }) : true;
    card.classList.toggle("hidden", granted);
  } catch {
    card.classList.add("hidden");
  }
}

async function load(): Promise<void> {
  setFormEnabled(false);
  void showSiteAccess();
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
      })) as { ok?: boolean; error?: string; settings?: Settings };
      result.textContent = response?.ok ? "Saved." : (response?.error || "Save failed.");
      if (response?.ok && response.settings) showAutomaticState(response.settings);
    } catch (error) {
      result.textContent = error instanceof Error ? error.message : String(error);
    }
  });

  byId<HTMLButtonElement>("resetExcluded").addEventListener("click", () => {
    byId<HTMLTextAreaElement>("excludedExtensions").value = DEFAULT_EXCLUDED_EXTENSIONS.join(", ");
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
      const response = (await chrome.runtime.sendMessage({
        type: "dm.openApp",
      })) as {
        ok?: boolean;
        error?: string;
      };
      result.textContent = response?.ok ? "Opening Inbound Download Manager…" : (response?.error || "Could not open the app.");
    } catch (error) {
      result.textContent = error instanceof Error ? error.message : String(error);
    }
  });

});
