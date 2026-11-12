import { describe, expect, it } from 'vitest';
import { pruneMenu, type MenuEntryShape } from '../src/lib/menu';
import { arrowTarget } from '../src/lib/dialogs';

// A menu written as text: 'i' a shown item, 'h' a hidden item, '-' a separator, '_' a hidden separator.
function collapse(menu: string): string {
  const entries: (MenuEntryShape & { c: string })[] = [...menu].map((c) => ({
    c,
    separator: c === '-' || c === '_',
    hidden: c === 'h' || c === '_',
  }));
  return pruneMenu(entries).map((entry) => (entry.separator ? '-' : 'i')).join('');
}

describe('pruneMenu (UI.B25)', () => {
  it.each([
    ['i-i', 'i-i'],
    ['i--i', 'i-i'],
    ['i-h-i', 'i-i'],
    ['-i-i-', 'i-i'],
    ['h-i', 'i'],
    ['i-h', 'i'],
    // The old rule kept a separator here: the one after it was dropped only as the last entry.
    ['i--', 'i'],
    ['i-h-h', 'i'],
    ['i_i', 'ii'],
    ['hh-hh', ''],
    ['', ''],
    ['i-hh-hh-i-i', 'i-i-i'],
  ])('%s shows as %s', (menu, expected) => {
    expect(collapse(menu)).toBe(expected);
  });

  it('keeps the entries themselves', () => {
    const a: MenuEntryShape & { label: string } = { label: 'a' };
    const line = { separator: true };
    const b: MenuEntryShape & { label: string } = { label: 'b' };
    expect(pruneMenu([a, line, { separator: true }, b])).toEqual([a, line, b]);
    expect(pruneMenu([a, line, b])[1]).toBe(line);
  });
});

describe('arrowTarget (UI.R36)', () => {
  const buttons = ['Cancel', 'Delete'];
  it('moves between the buttons with all four arrows', () => {
    expect(arrowTarget(buttons, 0, 'ArrowRight')).toBe('Delete');
    expect(arrowTarget(buttons, 1, 'ArrowLeft')).toBe('Cancel');
    expect(arrowTarget(buttons, 0, 'ArrowDown')).toBe('Delete');
    expect(arrowTarget(buttons, 1, 'ArrowUp')).toBe('Cancel');
  });
  it('stays at the ends and leaves other keys and other controls alone', () => {
    expect(arrowTarget(buttons, 1, 'ArrowRight')).toBeNull();
    expect(arrowTarget(buttons, 0, 'ArrowLeft')).toBeNull();
    expect(arrowTarget(buttons, 0, 'Enter')).toBeNull();
    expect(arrowTarget(buttons, -1, 'ArrowRight')).toBeNull();
    expect(arrowTarget(['Cancel'], 0, 'ArrowRight')).toBeNull();
  });
  it('swaps Left and Right in a right-to-left page', () => {
    expect(arrowTarget(buttons, 0, 'ArrowLeft', true)).toBe('Delete');
    expect(arrowTarget(buttons, 1, 'ArrowRight', true)).toBe('Cancel');
  });
  it('reaches a check box in the text from the buttons', () => {
    const stops = ['Delete the files too', 'Cancel', 'Delete'];
    expect(arrowTarget(stops, 1, 'ArrowUp')).toBe('Delete the files too');
    expect(arrowTarget(stops, 0, 'ArrowDown')).toBe('Cancel');
  });
});
