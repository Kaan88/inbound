// EXT.R12: draws the toolbar icon for a state (toolbar-status.ts): the extension's own icon, smaller, inside a ring.
// EXT.R14: a thicker ring with a halo, and a check badge for downloads finished since the popup was last opened.
// Drawn with OffscreenCanvas, which Chrome's service worker and Firefox's event page have; where it is missing the
// icon stays as it is and only its tooltip changes.

import type { IconState } from "./toolbar-status";

/**
 * Ring colours (EXT.R14): saturated status colours, and a track and a halo that make the ring read on light and dark
 * toolbars alike. The halo is white (it outlines the ring on a dark toolbar and vanishes into a light one); the track
 * is a mid grey that shows on both.
 */
export const RING_COLORS = {
  downloading: "#1fc052",
  paused: "#f59e0b",
  error: "#ef3b2d",
  finished: "#1fc052",
  track: "#7d8288",
  halo: "rgba(255, 255, 255, 0.95)",
  check: "#ffffff",
} as const;

/** The two sizes the toolbar uses (the manifest's default_icon). */
export const ICON_SIZES = [16, 32] as const;

export const DEFAULT_ICON_PATHS = { 16: "icons/icon16.png", 32: "icons/icon32.png" } as const;

export interface Ring {
  color: string;
  /** How much of the ring is filled (1 for a full ring). */
  fill: number;
  /** A check badge in the corner: downloads from this browser finished and have not been looked at. */
  check?: boolean;
}

/** What the ring shows: its colour, how much of it is filled, and whether the finished mark is on it. */
export function ringFor(state: IconState, finished = 0): Ring | null {
  const mark = finished > 0 ? { check: true } : {};
  switch (state.kind) {
    case "downloading":
      // Sizes not known yet: a quarter, so the ring still says "under way".
      return { color: RING_COLORS.downloading, fill: state.progress ?? 0.25, ...mark };
    case "paused":
      return { color: RING_COLORS.paused, fill: state.progress ?? 1, ...mark };
    case "error":
      return { color: RING_COLORS.error, fill: 1, ...mark };
    default:
      return finished > 0 ? { color: RING_COLORS.finished, fill: 1, check: true } : null;
  }
}

/** The ring's geometry at a size, in pixels: what the drawing and the tests share. */
export function ringGeometry(size: number): { line: number; halo: number; radius: number; inset: number; badge: number } {
  const line = Math.max(3, Math.round(size * 0.19));
  const halo = line + 2;
  return {
    line,
    halo,
    radius: size / 2 - halo / 2,
    inset: line + 1,
    // The check badge's radius (bottom right).
    badge: Math.round(size * 0.3),
  };
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

function drawOne(size: number, icon: ImageBitmap | null, ring: Ring): ImageData {
  const canvas = new OffscreenCanvas(size, size);
  const context = canvas.getContext("2d") as Canvas2D;
  const { line, halo, radius, inset, badge } = ringGeometry(size);
  const centre = size / 2;
  if (icon) context.drawImage(icon, inset, inset, size - inset * 2, size - inset * 2);
  context.lineCap = "butt";
  context.lineWidth = halo;
  context.beginPath();
  context.strokeStyle = RING_COLORS.halo;
  context.arc(centre, centre, radius, 0, Math.PI * 2);
  context.stroke();
  context.lineWidth = line;
  context.beginPath();
  context.strokeStyle = RING_COLORS.track;
  context.arc(centre, centre, radius, 0, Math.PI * 2);
  context.stroke();
  const fill = Math.min(Math.max(ring.fill, 0), 1);
  if (fill > 0) {
    context.beginPath();
    context.strokeStyle = ring.color;
    context.arc(centre, centre, radius, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * fill);
    context.stroke();
  }
  if (ring.check) drawCheck(context, size, badge);
  return context.getImageData(0, 0, size, size);
}

/** A green disc with a white check and a white outline, in the bottom right corner. */
function drawCheck(context: Canvas2D, size: number, badge: number): void {
  const cx = size - badge - 1;
  const cy = size - badge - 1;
  context.beginPath();
  context.fillStyle = RING_COLORS.halo;
  context.arc(cx, cy, badge + 1, 0, Math.PI * 2);
  context.fill();
  context.beginPath();
  context.fillStyle = RING_COLORS.finished;
  context.arc(cx, cy, badge, 0, Math.PI * 2);
  context.fill();
  context.beginPath();
  context.strokeStyle = RING_COLORS.check;
  context.lineWidth = Math.max(2, Math.round(size * 0.1));
  context.lineCap = "round";
  context.lineJoin = "round";
  context.moveTo(cx - badge * 0.5, cy + badge * 0.02);
  context.lineTo(cx - badge * 0.12, cy + badge * 0.4);
  context.lineTo(cx + badge * 0.52, cy - badge * 0.36);
  context.stroke();
}

/** The icon images for a ring, by size, or null when they can't be drawn here. */
export async function ringImages(ring: Ring): Promise<Record<number, ImageData> | null> {
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
