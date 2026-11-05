/// <reference types="chrome" />
import { CaptureBypass, consumeCaptureBypass, normalizeDownloadUrl } from "./capture-bypass";

interface MediaCandidate {
  url: string;
  kind: string;
  label: string;
}

interface PageLink {
  url: string;
  text: string;
  kind: string;
  extension: string;
  host: string;
}

const seen = new Map<string, MediaCandidate>();
let deleteHeld = false;
let mediaDetectionEnabled = false;
let mediaDetectionGeneration = 0;
let observer: MutationObserver | null = null;
let timer: ReturnType<typeof setTimeout> | null = null;
const captureGestures: CaptureBypass[] = [];
document.addEventListener("keydown", event => {
  if (event.isTrusted && event.key === "Delete") deleteHeld = true;
}, true);
document.addEventListener("keyup", event => {
  if (event.isTrusted && event.key === "Delete") deleteHeld = false;
}, true);
window.addEventListener("blur", () => { deleteHeld = false; });
document.addEventListener("visibilitychange", () => {
  if (document.hidden) deleteHeld = false;
});
// Capture before the page's click handler initiates navigation or a download.
function rememberCaptureGesture(event: MouseEvent): void {
  if (!event.isTrusted) return;
  captureGestures.length = 0;
  if (!deleteHeld) return;
  const anchor = event.composedPath().find(node => node instanceof HTMLAnchorElement) as HTMLAnchorElement | undefined;
  const url = normalizeDownloadUrl(anchor?.href ?? "");
  if (url) captureGestures.push({ url, expires: Date.now() + 15000 });
}
document.addEventListener("click", rememberCaptureGesture, true);
document.addEventListener("auxclick", rememberCaptureGesture, true);
function kindFor(url: string, tag: string): string {
  const lower = url.toLowerCase();
  if (lower.includes(".m3u8")) {
    return "hls";
  }
  if (lower.includes(".mpd")) {
    return "dash";
  }
  if (tag === "audio") {
    return "audio";
  }
  if (tag === "video") {
    return "video";
  }
  return "media";
}

function add(url: string | null | undefined, tag: string, label: string): void {
  if (!url) {
    return;
  }

  try {
    const resolved = new URL(url, location.href);
    if (resolved.protocol !== "http:" && resolved.protocol !== "https:") {
      return;
    }

    const absolute = resolved.href;
    if (!seen.has(absolute)) {
      seen.set(absolute, { url: absolute, kind: kindFor(absolute, tag), label });
    }
  } catch {
    // ignore malformed sources
  }
}

function scan(): void {
  if (!mediaDetectionEnabled) return;

  for (const video of Array.from(document.querySelectorAll("video"))) {
    add(video.currentSrc, "video", video.getAttribute("title") || "video");
    add(video.getAttribute("src"), "video", video.getAttribute("title") || "video");
    for (const source of Array.from(video.querySelectorAll("source"))) {
      add(source.getAttribute("src"), "video", source.getAttribute("type") || "video");
    }
  }

  for (const audio of Array.from(document.querySelectorAll("audio"))) {
    add(audio.currentSrc, "audio", audio.getAttribute("title") || "audio");
    add(audio.getAttribute("src"), "audio", audio.getAttribute("title") || "audio");
    for (const source of Array.from(audio.querySelectorAll("source"))) {
      add(source.getAttribute("src"), "audio", source.getAttribute("type") || "audio");
    }
  }

  for (const anchor of Array.from(document.querySelectorAll("a[href]"))) {
    const href = anchor.getAttribute("href") ?? "";
    if (/\.(m3u8|mpd)(\?|#|$)/i.test(href)) {
      add(href, "anchor", anchor.textContent?.trim().slice(0, 80) || "stream");
    }
  }

  updatePanel();
  send();
}

let panelHost: HTMLDivElement | null = null;
let panelRoot: ShadowRoot | null = null;

const PANEL_STYLE = `
  :host { all: initial; }
  .wrap { display: flex; flex-direction: column; align-items: flex-end; font: 12px/1.4 "Segoe UI", system-ui, sans-serif; }
  #dm-toggle { border: 0; border-radius: 999px; background: #185abc; color: #fff; padding: 7px 12px;
    font: 600 12px/1 "Segoe UI", system-ui, sans-serif; cursor: pointer; box-shadow: 0 4px 14px rgba(0,0,0,.25); }
  .panel { margin-top: 6px; width: 300px; max-height: 340px; overflow: auto; background: #fff; color: #172033;
    border: 1px solid #dbe3f0; border-radius: 10px; box-shadow: 0 10px 28px rgba(0,0,0,.28); padding: 8px; }
  .head { display: flex; align-items: center; justify-content: space-between; padding: 2px 2px 6px; }
  .head strong { font-size: 12px; }
  .close { border: 0; background: transparent; color: #627089; font-size: 15px; cursor: pointer; padding: 0 4px; }
  .row { display: grid; grid-template-columns: 1fr auto; gap: 2px 8px; padding: 6px 2px; border-top: 1px solid #eef3fb; }
  .name { font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .kind { color: #627089; font-size: 11px; }
  .dl { grid-column: 2; grid-row: 1 / 3; align-self: center; border: 1px solid #dbe3f0; background: #eef3fb;
    color: #185abc; border-radius: 6px; padding: 4px 8px; font: inherit; cursor: pointer; }
  @media (prefers-color-scheme: dark) {
    .panel { background: #1a202b; color: #e8edf6; border-color: #303b4c; }
    .row { border-color: #303b4c; }
    .kind { color: #a0aec2; }
    .dl { background: #222b39; border-color: #303b4c; color: #6ea8ff; }
    .close { color: #a0aec2; }
  }
`;

function ensurePanel(): ShadowRoot {
  if (panelRoot) {
    return panelRoot;
  }

  const host = document.createElement("div");
  host.id = "dm-media-panel-host";
  host.style.cssText = "position:fixed;top:14px;right:14px;z-index:2147483647;";
  const root = host.attachShadow({ mode: "open" });
  root.innerHTML = `<style>${PANEL_STYLE}</style>
    <div class="wrap">
      <button id="dm-toggle" type="button">Media</button>
      <div id="dm-panel" class="panel" hidden>
        <div class="head"><strong>Media on this page</strong><button id="dm-close" class="close" type="button">×</button></div>
        <div id="dm-list"></div>
      </div>
    </div>`;
  document.documentElement.append(host);
  const toggle = root.getElementById("dm-toggle") as HTMLButtonElement;
  const panel = root.getElementById("dm-panel") as HTMLDivElement;
  toggle.addEventListener("click", () => {
    panel.hidden = !panel.hidden;
  });
  (root.getElementById("dm-close") as HTMLButtonElement).addEventListener("click", () => {
    panel.hidden = true;
  });
  panelHost = host;
  panelRoot = root;
  return root;
}

function updatePanel(): void {
  const items = [...seen.values()];
  if (items.length === 0) {
    panelHost?.remove();
    panelHost = null;
    panelRoot = null;
    return;
  }

  const root = ensurePanel();
  (root.getElementById("dm-toggle") as HTMLButtonElement).textContent = `Media (${items.length})`;
  const list = root.getElementById("dm-list") as HTMLDivElement;
  list.replaceChildren();
  for (const item of items) {
    const row = document.createElement("div");
    row.className = "row";

    const name = document.createElement("span");
    name.className = "name";
    name.textContent = item.label || item.kind;
    name.title = item.url;

    const kind = document.createElement("span");
    kind.className = "kind";
    kind.textContent = item.kind;

    const button = document.createElement("button");
    button.type = "button";
    button.className = "dl";
    button.textContent = "Download";
    button.addEventListener("click", async () => {
      button.disabled = true;
      button.textContent = "Sending...";
      try {
        const result = (await chrome.runtime.sendMessage({
          type: "dm.queueMedia",
          url: item.url,
          pageUrl: location.href,
        })) as { ok?: boolean; error?: string };
        button.textContent = result?.ok ? "Sent" : "Retry";
        button.title = result?.error ?? "";
      } catch (error) {
        button.textContent = "Retry";
        button.title = error instanceof Error ? error.message : String(error);
      } finally {
        button.disabled = false;
      }
    });

    row.append(name, kind, button);
    list.append(row);
  }
}

function removeMediaPanel(): void {
  seen.clear();
  panelHost?.remove();
  document.getElementById("dm-media-panel-host")?.remove();
  panelHost = null;
  panelRoot = null;
}

function send(): void {
  if (!mediaDetectionEnabled || seen.size === 0) {
    return;
  }

  try {
    chrome.runtime.sendMessage({
      type: "dm.mediaDetected",
      pageUrl: location.href,
      items: [...seen.values()],
    }).catch(() => undefined);
  } catch {
    // The service worker may be restarting.
  }
}

function extensionFor(url: URL): string {
  const name = url.pathname.split("/").pop() ?? "";
  const dot = name.lastIndexOf(".");
  if (dot <= 0 || dot >= name.length - 1) {
    return "";
  }

  return name.slice(dot + 1).toLowerCase().slice(0, 16);
}

function linkLabel(element: Element, url: URL): string {
  const text = (element.textContent ?? "").replace(/\s+/g, " ").trim();
  if (text) {
    return text.slice(0, 160);
  }

  const title = (element.getAttribute("title") ?? "").trim();
  if (title) {
    return title.slice(0, 160);
  }

  const alt = (element.getAttribute("alt") ?? "").trim();
  if (alt) {
    return alt.slice(0, 160);
  }

  const name = url.pathname.split("/").pop() ?? "";
  try {
    return decodeURIComponent(name || url.hostname).slice(0, 160);
  } catch {
    return (name || url.hostname).slice(0, 160);
  }
}

function collectLinks(): PageLink[] {
  const found = new Map<string, PageLink>();
  const addLink = (raw: string | null | undefined, kind: string, element: Element): void => {
    if (!raw) {
      return;
    }

    try {
      const url = new URL(raw, location.href);
      if (url.protocol !== "http:" && url.protocol !== "https:") {
        return;
      }

      url.hash = "";
      const absolute = url.href;
      if (found.has(absolute)) {
        return;
      }

      found.set(absolute, {
        url: absolute,
        text: linkLabel(element, url),
        kind,
        extension: extensionFor(url),
        host: url.host,
      });
    } catch {
      // ignore malformed links
    }
  };

  for (const anchor of Array.from(document.querySelectorAll("a[href]"))) {
    const href = anchor.getAttribute("href") ?? "";
    if (!href || href.startsWith("#")) {
      continue;
    }

    addLink(href, "link", anchor);
  }

  for (const image of Array.from(document.querySelectorAll("img[src]"))) {
    addLink(image.getAttribute("src"), "image", image);
  }

  for (const media of Array.from(
    document.querySelectorAll("video[src], audio[src], video source[src], audio source[src]"),
  )) {
    addLink(media.getAttribute("src"), media.tagName.toLowerCase(), media);
  }

  return [...found.values()];
}

function scheduleScan(): void {
  if (!mediaDetectionEnabled) return;
  if (timer) {
    clearTimeout(timer);
  }

  timer = setTimeout(() => {
    timer = null;
    if (mediaDetectionEnabled) scan();
  }, 500);
}

function setMediaDetectionEnabled(enabled: boolean): void {
  mediaDetectionGeneration++;
  if (enabled === mediaDetectionEnabled) {
    if (!enabled) removeMediaPanel();
    return;
  }
  mediaDetectionEnabled = enabled;
  if (enabled) {
    observer ??= new MutationObserver(scheduleScan);
    observer.observe(document.documentElement, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ["src"],
    });
    for (const event of ["loadedmetadata", "durationchange", "play"]) {
      document.addEventListener(event, scheduleScan, true);
    }
    scan();
    return;
  }

  if (timer) {
    clearTimeout(timer);
    timer = null;
  }
  observer?.disconnect();
  for (const event of ["loadedmetadata", "durationchange", "play"]) {
    document.removeEventListener(event, scheduleScan, true);
  }
  removeMediaPanel();
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "dm.consumeCaptureBypass") {
    sendResponse({ bypass: consumeCaptureBypass(captureGestures, String(message.url ?? ""), Date.now()) });
    return false;
  }
  if (message?.type === "dm.scan") {
    if (mediaDetectionEnabled) scan();
    sendResponse({ ok: true, disabled: !mediaDetectionEnabled });
    return true;
  }

  if (message?.type === "dm.mediaDetectionChanged") {
    setMediaDetectionEnabled(message.enabled === true);
    sendResponse({ ok: true, enabled: mediaDetectionEnabled });
    return false;
  }

  if (message?.type === "dm.collectLinks") {
    sendResponse({ ok: true, pageUrl: location.href, links: collectLinks() });
    return true;
  }

  return false;
});

const initialMediaDetectionGeneration = mediaDetectionGeneration;
void chrome.runtime.sendMessage({ type: "dm.getMediaDetectionEnabled" })
  .then(response => {
    // Ignore an initial read that races a newer live Options change.
    if (mediaDetectionGeneration === initialMediaDetectionGeneration)
      setMediaDetectionEnabled(response?.enabled === true);
  })
  .catch(() => undefined);
