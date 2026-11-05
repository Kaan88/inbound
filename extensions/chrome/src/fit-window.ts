/// <reference types="chrome" />

// Chrome window dimensions include the frame; form layout uses the viewport.
export async function fitWindowToContent(): Promise<void> {
  const current = await chrome.windows.getCurrent();
  if (current.id === undefined || current.type !== "popup") return;
  const width = Math.min(current.width ?? 560, screen.availWidth);
  const frameHeight = Math.max(0, outerHeight - innerHeight);
  const height = Math.min(document.documentElement.scrollHeight + frameHeight + 8, screen.availHeight);
  const display = screen as Screen & { availLeft?: number; availTop?: number };
  const originX = display.availLeft ?? 0;
  const originY = display.availTop ?? 0;
  const left = Math.max(originX, Math.min(current.left ?? originX, originX + screen.availWidth - width));
  const top = Math.max(originY, Math.min(current.top ?? originY, originY + screen.availHeight - height));
  await chrome.windows.update(current.id, { width, height, left, top });
}
