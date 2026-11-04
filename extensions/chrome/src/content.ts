/// <reference types="chrome" />

interface MediaCandidate {
  url: string;
  kind: string;
  label: string;
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
  }
  return true;
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
