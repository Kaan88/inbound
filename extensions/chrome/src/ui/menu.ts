// A small menu from a button (EXT.R8): the kit's .menu, opening upward from the popup's status bar like the desktop's
// server switcher. Arrow keys move, Enter or Space chooses, Esc closes, and focus goes back to the button either way.
// EXT.R17: the popup is not grown to hold the menu. It goes below its point or row when the whole menu fits there,
// above it otherwise, is shifted to stay inside the popup on both axes, and scrolls when it is taller than the popup.

export interface MenuItem {
  label: string;
  checked?: boolean;
  disabled?: boolean;
  /** A tooltip, such as why the item is off. */
  title?: string;
  onSelect: () => void;
}

/** How long a click that closed a menu by pressing outside it is waited for, so it can be swallowed. */
const DISMISS_CLICK_MS = 1000;

/** The room kept between a menu and the popup's edges. */
export const MENU_EDGE = 4;
/** The room kept between a menu and the row or button it came from. */
export const MENU_GAP = 2;

export interface MenuPlacement {
  left: number;
  top: number;
  /** The tallest the menu may be; it scrolls when it wants more. */
  maxHeight: number;
}

export interface MenuPlacementInput {
  /** The size the menu wants. */
  width: number;
  height: number;
  /** The popup's visible size. */
  viewport: { width: number; height: number };
  /** A right-click's point, or the row's own start: the menu goes under it when there is room, above it otherwise. */
  at?: { x: number; y: number };
  /** The element the menu came from: under it, or above it with `upward` (the status bar's switcher). */
  anchor?: { left: number; top: number; bottom: number };
  upward?: boolean;
}

/**
 * EXT.R17: where a menu goes in a popup that does not grow for it. Downwards from its point or anchor when the whole
 * menu fits below, upwards otherwise, then kept inside on both axes. A menu taller than the popup is capped there and
 * scrolls.
 */
export function placeMenu({ width, height, viewport, at, anchor, upward = false }: MenuPlacementInput): MenuPlacement {
  // The popup's own height is all the menu may use: it is never made taller for the menu.
  const room = Math.max(0, viewport.height - MENU_EDGE * 2);
  const tall = Math.min(Math.max(height, 0), room);
  const lowest = Math.max(MENU_EDGE, viewport.height - MENU_EDGE - tall);
  const down = at ? at.y : anchor ? anchor.bottom + MENU_GAP : MENU_EDGE;
  const up = at ? at.y - tall : anchor ? anchor.top - MENU_GAP - tall : MENU_EDGE;
  // Below when the whole menu fits there, above when it doesn't; a menu that fits nowhere is put at the top and scrolls.
  const top = upward || down + tall > viewport.height - MENU_EDGE ? up : down;
  const wanted = at ? at.x : anchor ? anchor.left : MENU_EDGE;
  const mostLeft = Math.max(MENU_EDGE, viewport.width - MENU_EDGE - width);
  return {
    left: Math.min(Math.max(wanted, MENU_EDGE), mostLeft),
    top: Math.min(Math.max(top, MENU_EDGE), lowest),
    maxHeight: room,
  };
}

let closeOpen: (() => void) | null = null;

/** Closes the open menu, if any. */
export function closeMenu(): void {
  closeOpen?.();
}

/** Whether a menu is open. */
export function menuOpen(): boolean {
  return closeOpen !== null;
}

/**
 * Opens a menu under (or above) the anchor, or at a point: a right-click's position (EXT.R11), kept inside the popup.
 */
export function openMenu(anchor: HTMLElement, items: MenuItem[], options: { upward?: boolean; at?: { x: number; y: number } } = {}): void {
  closeMenu();
  const menu = document.createElement("div");
  menu.className = "menu popup-menu";
  menu.setAttribute("role", "menu");
  const buttons = items.map((item) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "menu-item";
    button.setAttribute("role", item.checked === undefined ? "menuitem" : "menuitemradio");
    if (item.checked !== undefined) button.setAttribute("aria-checked", String(item.checked));
    button.disabled = item.disabled === true;
    if (item.title) button.title = item.title;
    const mark = document.createElement("span");
    mark.className = "menu-check";
    mark.textContent = item.checked ? "✓" : "";
    mark.setAttribute("aria-hidden", "true");
    const text = document.createElement("span");
    text.textContent = item.label;
    button.append(mark, text);
    // Focus goes back to the button the menu came from, as after Esc, so the keyboard does not land on the page.
    button.addEventListener("click", () => { close(); anchor.focus(); item.onSelect(); });
    menu.append(button);
    return button;
  });

  menu.style.position = "fixed";
  // Hidden until it is placed: measured where it was made, never seen there.
  menu.style.visibility = "hidden";
  document.body.append(menu);

  // EXT.R17: measured once, then kept inside the popup as it is now; the page's height is never asked to change.
  const wanted = menu.getBoundingClientRect();
  const place = () => {
    const rect = anchor.getBoundingClientRect();
    const spot = placeMenu({
      width: wanted.width,
      height: wanted.height,
      viewport: { width: window.innerWidth, height: window.innerHeight },
      at: options.at,
      anchor: { left: rect.left, top: rect.top, bottom: rect.bottom },
      upward: options.upward,
    });
    menu.style.left = `${spot.left}px`;
    menu.style.top = `${spot.top}px`;
    menu.style.maxHeight = `${spot.maxHeight}px`;
  };
  place();
  menu.style.overflowY = "auto";
  menu.style.visibility = "";
  window.addEventListener("resize", place);
  anchor.setAttribute("aria-expanded", "true");

  const move = (step: number) => {
    const enabled = buttons.filter((button) => !button.disabled);
    if (enabled.length === 0) return;
    const at = enabled.indexOf(document.activeElement as HTMLButtonElement);
    enabled[(at + step + enabled.length) % enabled.length]!.focus();
  };
  const onKey = (event: KeyboardEvent) => {
    if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); close(); anchor.focus(); }
    else if (event.key === "ArrowDown") { event.preventDefault(); move(1); }
    else if (event.key === "ArrowUp") { event.preventDefault(); move(-1); }
    else if (event.key === "Tab") close();
  };
  const onPointer = (event: Event) => {
    const target = event.target as Node;
    // A button that opened the menu closes it with its own click; a row that opened it is outside like the rest.
    if (menu.contains(target) || (anchor.contains(target) && anchor.closest("button"))) return;
    close();
    // A right-click (or any non-primary press) brings no click to swallow; arming then would eat a pick in the next menu.
    if ((event as PointerEvent).button !== undefined && (event as PointerEvent).button !== 0) return;
    // A click outside only closes the menu, as Windows menus do: it does not also open the row under it (EXT.R18).
    const swallow = (click: Event) => { click.stopPropagation(); click.preventDefault(); };
    const page = document;
    page.addEventListener("click", swallow, { capture: true, once: true });
    setTimeout(() => page.removeEventListener("click", swallow, true), DISMISS_CLICK_MS);
  };
  function close(): void {
    if (closeOpen !== close) return;
    closeOpen = null;
    menu.remove();
    window.removeEventListener("resize", place);
    anchor.setAttribute("aria-expanded", "false");
    document.removeEventListener("keydown", onKey, true);
    document.removeEventListener("pointerdown", onPointer, true);
  }
  closeOpen = close;
  document.addEventListener("keydown", onKey, true);
  document.addEventListener("pointerdown", onPointer, true);
  (buttons.find((button) => button.getAttribute("aria-checked") === "true" && !button.disabled) ??
    buttons.find((button) => !button.disabled))?.focus();
}
