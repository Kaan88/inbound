// EXT.R12: draws the toolbar icon for a state (toolbar-status.ts): the extension's own icon, smaller, inside a ring.
// Drawn with OffscreenCanvas, which Chrome's service worker and Firefox's event page have; where it is missing the
// icon stays as it is and only its tooltip changes.

import type { IconState } from "./toolbar-status";

/** Ring colours: the status colours, mid-tones that read on light and dark toolbars alike. */
export const RING_COLORS = {
  downloading: "#2ea44f",
  paused: "#e8910c",
  error: "#d93025",
  track: "rgba(128, 128, 128, 0.45)",
} as const;

/** The two sizes the toolbar uses (the manifest's default_icon). */
export const ICON_SIZES = [16, 32] as const;

export const DEFAULT_ICON_PATHS = { 16: "icons/icon16.png", 32: "icons/icon32.png" } as const;

/** What the ring shows: its colour and how much of it is filled (1 for a full ring). */
export function ringFor(state: IconState): { color: string; fill: number } | null {
  switch (state.kind) {
    case "downloading":
      // Sizes not known yet: a quarter, so the ring still says "under way".
      return { color: RING_COLORS.downloading, fill: state.progress ?? 0.25 };
    case "paused":
      return { color: RING_COLORS.paused, fill: state.progress ?? 1 };
    case "error":
      return { color: RING_COLORS.error, fill: 1 };
    default:
      return null;
  }
}

type Canvas2D = OffscreenCanvasRenderingContext2D;

export function canDraw(): boolean {
  return typeof OffscreenCanvas === "function" && typeof createImageBitmap === "function";
}

let baseIcon: Promise<ImageBitmap | null> | null = null;

/**
 * The extension's own 32 px icon, read once from the extension's own files: no network request (the privacy test
 * allows this one fetch, of this file only).
 */
function loadBaseIcon(): Promise<ImageBitmap | null> {
  baseIcon ??= (async () => {
    try {
      const response = await fetch(chrome.runtime.getURL("icons/icon32.png"));
      return await createImageBitmap(await response.blob());
    } catch {
      return null;
    }
  })();
  return baseIcon;
}

function drawOne(size: number, icon: ImageBitmap | null, ring: { color: string; fill: number }): ImageData {
  const canvas = new OffscreenCanvas(size, size);
  const context = canvas.getContext("2d") as Canvas2D;
  const line = Math.max(2, Math.round(size * 0.15));
  const radius = size / 2 - line / 2;
  const inset = line + Math.max(1, Math.round(size * 0.04));
  if (icon) context.drawImage(icon, inset, inset, size - inset * 2, size - inset * 2);
  context.lineWidth = line;
  context.lineCap = "butt";
  context.beginPath();
  context.strokeStyle = RING_COLORS.track;
  context.arc(size / 2, size / 2, radius, 0, Math.PI * 2);
  context.stroke();
  const fill = Math.min(Math.max(ring.fill, 0), 1);
  if (fill > 0) {
    context.beginPath();
    context.strokeStyle = ring.color;
    context.arc(size / 2, size / 2, radius, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * fill);
    context.stroke();
  }
  return context.getImageData(0, 0, size, size);
}

/** The icon images for a ring, by size, or null when they can't be drawn here. */
export async function ringImages(ring: { color: string; fill: number }): Promise<Record<number, ImageData> | null> {
  if (!canDraw()) return null;
  try {
    const icon = await loadBaseIcon();
    const images: Record<number, ImageData> = {};
    for (const size of ICON_SIZES) images[size] = drawOne(size, icon, ring);
    return images;
  } catch {
    return null;
  }
}
