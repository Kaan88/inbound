// What a menu shows once the rules have hidden what doesn't apply (UI.B25). Pure, so it is tested without a browser.

/** The part of a menu entry that decides whether it shows. */
export interface MenuEntryShape {
  separator?: boolean;
  hidden?: boolean;
}

/**
 * The shown entries: hidden ones left out, and a separator kept only with a shown item on each side of it, so there is
 * never one first, last or two in a row, however many items between them were hidden.
 */
export function pruneMenu<T extends MenuEntryShape>(list: readonly T[]): T[] {
  const result: T[] = [];
  let pending: T | null = null;
  for (const item of list) {
    if (item.hidden) continue;
    if (item.separator) {
      // The first of a run of separators waits for an item below it, and only after an item above it.
      if (result.length > 0 && pending === null) pending = item;
      continue;
    }
    if (pending !== null) result.push(pending);
    pending = null;
    result.push(item);
  }
  return result;
}
