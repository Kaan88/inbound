// The app's outline glyphs as SVG elements (EXT.R8). The paths are the web UI's (webui/src/lib/icons.ts), so the
// extension, the web UI and the desktop window draw the same marks; colours come from the caller's CSS (currentColor).

import { fileKindIcon, iconPaths } from "../../../../webui/src/lib/icons";

const SVG = "http://www.w3.org/2000/svg";

/** An icon, aria-hidden: the button or row carrying it has the words. */
export function icon(name: string, size = 16): SVGSVGElement {
  const svg = document.createElementNS(SVG, "svg") as SVGSVGElement;
  for (const [key, value] of Object.entries({
    width: String(size), height: String(size), viewBox: "0 0 24 24", fill: "none", stroke: "currentColor",
    "stroke-width": "1.75", "stroke-linecap": "round", "stroke-linejoin": "round", "aria-hidden": "true", focusable: "false",
  })) svg.setAttribute(key, value);
  svg.setAttribute("class", `icon icon-${name}`);
  const path = document.createElementNS(SVG, "path");
  path.setAttribute("d", iconPaths[name] ?? iconPaths.info ?? "");
  svg.append(path);
  return svg;
}

/** The desktop list's file-kind glyph for a file name. */
export function fileIcon(fileName: string, size = 16): SVGSVGElement {
  return icon(fileKindIcon(fileName), size);
}

/** Fills every [data-icon] placeholder of a page with its glyph (the pages' static buttons). */
export function fillIcons(root: ParentNode = document): void {
  for (const holder of Array.from(root.querySelectorAll<HTMLElement>("[data-icon]"))) {
    const size = Number(holder.dataset.iconSize ?? "16");
    holder.replaceChildren(icon(holder.dataset.icon ?? "info", Number.isFinite(size) ? size : 16));
  }
}
