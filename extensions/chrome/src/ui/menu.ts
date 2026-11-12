// A small menu from a button (EXT.R8): the kit's .menu, opening upward from the popup's status bar like the desktop's
// server switcher. Arrow keys move, Enter or Space chooses, Esc closes, and focus goes back to the button either way.

export interface MenuItem {
  label: string;
  checked?: boolean;
  disabled?: boolean;
  /** A tooltip, such as why the item is off. */
  title?: string;
  onSelect: () => void;
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
 * Opens a menu under (or above) the anchor, or at a point: a right-click's position (EXT.R11), kept inside the window.
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

  const rect = anchor.getBoundingClientRect();
  menu.style.position = "fixed";
  if (options.at) {
    menu.style.left = `${Math.max(4, options.at.x)}px`;
    menu.style.top = `${Math.max(4, options.at.y)}px`;
  } else {
    menu.style.left = `${Math.max(4, rect.left)}px`;
    if (options.upward) menu.style.bottom = `${Math.max(4, window.innerHeight - rect.top + 2)}px`;
    else menu.style.top = `${rect.bottom + 2}px`;
  }
  document.body.append(menu);
  if (options.at) {
    // Inside the popup: moved left or up by what would stick out.
    const box = menu.getBoundingClientRect();
    if (box.right > window.innerWidth - 4) menu.style.left = `${Math.max(4, window.innerWidth - 4 - box.width)}px`;
    if (box.bottom > window.innerHeight - 4) menu.style.top = `${Math.max(4, window.innerHeight - 4 - box.height)}px`;
  }
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
    if (!menu.contains(target) && !anchor.contains(target)) close();
  };
  function close(): void {
    if (closeOpen !== close) return;
    closeOpen = null;
    menu.remove();
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
