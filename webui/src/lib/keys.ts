// Keys the add dialogs and the shell react to. Pure, so the tests don't need a browser.

interface KeyLike {
  key: string;
  code?: string;
  altKey?: boolean;
  ctrlKey?: boolean;
  metaKey?: boolean;
  shiftKey?: boolean;
  isComposing?: boolean;
  defaultPrevented?: boolean;
  target?: unknown;
}

/** Enter in a single-line text field submits the dialog; in a text area, a button, a box or a list it does not. */
export function submitsOnEnter(event: KeyLike): boolean {
  if (event.key !== 'Enter' || event.isComposing || event.defaultPrevented || event.shiftKey) return false;
  const target = event.target as { tagName?: string; type?: string } | null | undefined;
  if (target?.tagName?.toUpperCase() !== 'INPUT') return false;
  const type = (target.type ?? 'text').toLowerCase();
  return !['checkbox', 'radio', 'button', 'submit', 'reset', 'file', 'range', 'color'].includes(type);
}

/**
 * Add URL from anywhere: Alt+N. Ctrl+N and Ctrl+Shift+N belong to the browser. AltGr reports Ctrl as well on
 * Windows, so a layout that types characters with it never triggers this.
 */
export function opensAddUrl(event: KeyLike): boolean {
  return !!event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey && (event.code === 'KeyN' || (!event.code && event.key.toLowerCase() === 'n'));
}
