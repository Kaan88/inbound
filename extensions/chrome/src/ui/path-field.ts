// A folder that does not fit shows its end, the folder's own name, with the middle left out ("D:\Downloads\…\Films"),
// and the whole path as its tooltip. A folder field shows the shortened text over the input while it is not being
// edited; focusing it shows the real value, which is what is sent.

/** The text shortened in the middle until `fits` accepts it, keeping the last folder whole while it can. */
export function middleEllipsis(text: string, fits: (candidate: string) => boolean): string {
  if (!text || fits(text)) return text;
  const trimmed = text.replace(/[\\/]+$/, "");
  const cut = Math.max(trimmed.lastIndexOf("\\"), trimmed.lastIndexOf("/"));
  let tail = cut >= 0 ? text.slice(cut) : "";
  if (!tail || !fits(`…${tail}`)) {
    // Even the folder's name does not fit: as much of the end as does.
    let lo = 1;
    let hi = text.length;
    while (lo < hi) {
      const mid = Math.ceil((lo + hi) / 2);
      if (fits(`…${text.slice(text.length - mid)}`)) lo = mid; else hi = mid - 1;
    }
    tail = text.slice(text.length - lo);
    return `…${tail}`;
  }
  // The start as long as it fits before the ellipsis and the name.
  let lo = 0;
  let hi = text.length - tail.length;
  while (lo < hi) {
    const mid = Math.ceil((lo + hi) / 2);
    if (fits(`${text.slice(0, mid)}…${tail}`)) lo = mid; else hi = mid - 1;
  }
  return `${text.slice(0, lo)}…${tail}`;
}

let canvas: HTMLCanvasElement | null = null;

/** A `fits` test for an element of this width and font; null where nothing can be measured (no layout). */
function fitter(element: HTMLElement, width: number): ((candidate: string) => boolean) | null {
  if (width <= 0) return null;
  let context: CanvasRenderingContext2D | null = null;
  try {
    canvas ??= document.createElement("canvas");
    context = canvas.getContext("2d");
  } catch {
    return null;
  }
  if (!context) return null;
  const style = getComputedStyle(element);
  context.font = style.font || `${style.fontSize} ${style.fontFamily}`;
  return (candidate) => context!.measureText(candidate).width <= width;
}

/** Shortens a span's text in the middle to its width, the full text as its tooltip. */
export function fitPathText(span: HTMLElement, text: string): void {
  span.title = text;
  span.textContent = text;
  const fits = fitter(span, span.clientWidth);
  if (fits) span.textContent = middleEllipsis(text, fits);
}

/**
 * Puts a shortened copy of a folder input's value over it while it is not focused. The input keeps its value, id and
 * events; setting `.value` from code, typing, focus and a new width all redraw it.
 */
export function attachPathField(input: HTMLInputElement): () => void {
  // A page without a real DOM (the tests' stand-ins) keeps the plain field.
  if (typeof input.replaceWith !== "function" || !input.parentNode) return () => undefined;
  const wrap = document.createElement("span");
  wrap.className = "path-field";
  const shown = document.createElement("span");
  shown.className = "path-display mono";
  shown.setAttribute("aria-hidden", "true");
  input.replaceWith(wrap);
  wrap.append(input, shown);

  const redraw = () => {
    const value = input.value;
    input.title = value;
    const style = getComputedStyle(input);
    const padding = (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0);
    const border = (parseFloat(style.borderLeftWidth) || 0) + (parseFloat(style.borderRightWidth) || 0);
    const fits = value ? fitter(input, input.offsetWidth - padding - border) : null;
    const text = fits ? middleEllipsis(value, fits) : value;
    const shortened = text !== value;
    shown.textContent = shortened ? text : "";
    wrap.classList.toggle("shortened", shortened);
  };

  // `.value = …` from the form code goes through here too, so the copy never shows an old folder.
  const valueProperty = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(input), "value");
  if (valueProperty?.get && valueProperty.set) {
    Object.defineProperty(input, "value", {
      configurable: true,
      get: () => valueProperty.get!.call(input),
      set: (next: string) => { valueProperty.set!.call(input, next); redraw(); },
    });
  }
  input.addEventListener("input", redraw);
  input.addEventListener("blur", redraw);
  try {
    if (typeof ResizeObserver === "function") new ResizeObserver(() => redraw()).observe(input);
  } catch { /* drawn on the next change */ }
  redraw();
  return redraw;
}
