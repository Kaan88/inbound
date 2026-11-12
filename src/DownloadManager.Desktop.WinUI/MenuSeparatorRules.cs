namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Which of a menu's separators show (UI.B25). Each menu rule hides the items that don't apply, and a separator can be
/// left with nothing on one side of it: two lines in a row, or one at the top or bottom. Nothing here touches the UI.
/// </summary>
internal static class MenuSeparatorRules
{
    /// <summary>
    /// For each entry of a menu, in order: whether it shows after the collapse. <paramref name="isSeparator"/> and
    /// <paramref name="wanted"/> say, for each entry, what it is and whether its own rule shows it. Items keep their
    /// own visibility. A separator shows only when its rule wants it, a shown item comes before it with no shown
    /// separator in between, and a shown item comes after it. So never two in a row, never one first or last.
    /// </summary>
    public static bool[] Visible(IReadOnlyList<bool> isSeparator, IReadOnlyList<bool> wanted)
    {
        if (isSeparator.Count != wanted.Count) throw new ArgumentException("One wanted flag for each entry.", nameof(wanted));
        var result = new bool[isSeparator.Count];
        var itemShownSinceLine = false;
        int? pending = null;
        for (var i = 0; i < isSeparator.Count; i++)
        {
            if (!isSeparator[i])
            {
                result[i] = wanted[i];
                if (!wanted[i]) continue;
                // A line waiting for an item below it gets one.
                if (pending is { } line) result[line] = true;
                pending = null;
                itemShownSinceLine = true;
                continue;
            }

            // A line with a shown item above it waits for one below it; the first of a run of lines is the one kept.
            if (wanted[i] && itemShownSinceLine && pending is null)
            {
                pending = i;
                itemShownSinceLine = false;
            }
        }

        return result;
    }
}
