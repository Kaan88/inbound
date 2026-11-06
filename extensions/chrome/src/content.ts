/// <reference types="chrome" />
import { KeepInBrowserToken, consumeKeepInBrowser, normalizeDownloadUrl } from "./keep-in-browser";

interface PageLink {
  url: string;
  text: string;
  kind: string;
  extension: string;
  host: string;
}

let deleteHeld = false;
const captureGestures: KeepInBrowserToken[] = [];
let watchingDelete = false;
let toldAutoSendLive = false;
function onKeyDown(event: KeyboardEvent): void {
  if (event.isTrusted && event.key === "Delete") deleteHeld = true;
}
function onKeyUp(event: KeyboardEvent): void {
  if (event.isTrusted && event.key === "Delete") deleteHeld = false;
}
function releaseDelete(): void {
  deleteHeld = false;
}
function onVisibilityChange(): void {
  if (document.hidden) deleteHeld = false;
}
// Capture before the page's click handler initiates navigation or a download.
function rememberCaptureGesture(event: MouseEvent): void {
  if (!event.isTrusted) return;
  captureGestures.length = 0;
  if (!deleteHeld) return;
  const anchor = event.composedPath().find(node => node instanceof HTMLAnchorElement) as HTMLAnchorElement | undefined;
  const url = normalizeDownloadUrl(anchor?.href ?? "");
  if (url) captureGestures.push({ url, expires: Date.now() + 15000 });
}
// Holding Delete keeps a download out of automatic sending, so nothing listens unless that is on.
function watchDelete(on: boolean): void {
  if (on === watchingDelete) return;
  watchingDelete = on;
  const documentListeners = [["keydown", onKeyDown], ["keyup", onKeyUp], ["visibilitychange", onVisibilityChange],
    ["click", rememberCaptureGesture], ["auxclick", rememberCaptureGesture]] as [string, EventListener][];
  for (const [name, listener] of documentListeners) {
    if (on) document.addEventListener(name, listener, true);
    else document.removeEventListener(name, listener, true);
  }
  if (on) window.addEventListener("blur", releaseDelete);
  else {
    window.removeEventListener("blur", releaseDelete);
    deleteHeld = false;
    captureGestures.length = 0;
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

  for (const source of Array.from(
    document.querySelectorAll("video[src], audio[src], video source[src], audio source[src]"),
  )) {
    addLink(source.getAttribute("src"), source.tagName.toLowerCase(), source);
  }

  return [...found.values()];
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "dm.consumeKeepInBrowser") {
    sendResponse({ keep: consumeKeepInBrowser(captureGestures, String(message.url ?? ""), Date.now()) });
    return false;
  }

  if (message?.type === "dm.autoSendChanged") {
    toldAutoSendLive = true;
    watchDelete(message.autoSend === true);
    sendResponse({ ok: true });
    return false;
  }

  if (message?.type === "dm.collectLinks") {
    sendResponse({ ok: true, pageUrl: location.href, links: collectLinks() });
    return true;
  }

  return false;
});

void chrome.runtime.sendMessage({ type: "dm.getAutoSend" })
  .then(response => {
    // Ignore an initial read that races a newer live Options change.
    if (!toldAutoSendLive) watchDelete(response?.autoSend === true);
  })
  .catch(() => undefined);
