// Which toolbar buttons fit (U-WU-2, U-WU-9). The bar never scrolls sideways: when the window is too narrow, the
// buttons that matter least go into More, the rightmost first among equals, and More itself always stays.

export interface ToolSize {
  id: string;
  /** Its width with its label, in pixels. */
  width: number;
  /** Lower is kept longer. */
  keep: number;
  /** Buttons of one group sit together; a separator goes between groups. */
  group: number;
}

/**
 * The ids that go into More. `available` is the bar's inner width with More taken off; `gap` is the space between
 * two buttons and `separator` the width a group separator takes (with its margins).
 */
export function foldTools(tools: readonly ToolSize[], available: number, gap = 2, separator = 9): Set<string> {
  const folded = new Set<string>();
  const shown = () => tools.filter((tool) => !folded.has(tool.id));
  const width = () => {
    const list = shown();
    const groups = new Set(list.map((tool) => tool.group)).size;
    // A separator before every group, the one before More included.
    return list.reduce((sum, tool) => sum + tool.width + gap, 0) + groups * separator;
  };
  const order = tools
    .map((tool, index) => ({ tool, index }))
    .sort((a, b) => b.tool.keep - a.tool.keep || b.index - a.index)
    .map((entry) => entry.tool);
  for (const tool of order) {
    if (width() <= available) break;
    folded.add(tool.id);
  }
  return folded;
}
