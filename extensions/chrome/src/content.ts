/// <reference types="chrome" />

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
let timer: ReturnType<typeof setTimeout> | null = null;

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

  send();
}

function send(): void {
  if (seen.size === 0) {
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
  if (timer) {
    clearTimeout(timer);
  }

  timer = setTimeout(() => {
    timer = null;
    scan();
  }, 500);
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "dm.scan") {
    scan();
    sendResponse({ ok: true });
    return true;
  }

  if (message?.type === "dm.collectLinks") {
    sendResponse({ ok: true, pageUrl: location.href, links: collectLinks() });
    return true;
  }

  return false;
});

scan();
const observer = new MutationObserver(scheduleScan);
observer.observe(document.documentElement, {
  childList: true,
  subtree: true,
  attributes: true,
  attributeFilter: ["src"],
});
for (const event of ["loadedmetadata", "durationchange", "play"]) {
  document.addEventListener(event, scheduleScan, true);
}
