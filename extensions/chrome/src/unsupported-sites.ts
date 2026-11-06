// The Chrome Web Store does not allow helping people download from YouTube. These sites stay out of
// every way of sending a download, so that does not depend on how a site happens to serve its video.
const HOSTS = ["youtube.com", "youtu.be", "youtube-nocookie.com", "googlevideo.com", "ytimg.com"];

export const UNSUPPORTED_SITE_MESSAGE = "YouTube is not supported.";

export function isUnsupportedHost(host: string): boolean {
  const name = host.toLowerCase();
  return HOSTS.some((site) => name === site || name.endsWith(`.${site}`));
}

export function isUnsupportedUrl(url: unknown): boolean {
  if (typeof url !== "string") return false;
  try {
    return isUnsupportedHost(new URL(url).hostname);
  } catch {
    return false;
  }
}
