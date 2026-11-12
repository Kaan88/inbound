namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.B28: a window opened in the main window's process (Progress Details, a package's window) by a double-click came
/// up and then fell behind the window it was opened from. The rest of the click is handled by that window after the
/// new one was shown, and both share one thread, so it was made the active window again. The new window is brought forward again once that input has ended. Nothing
/// here touches a window.
/// </summary>
internal static class WindowFrontRules
{
    /// <summary>How often the mouse buttons and Enter are looked at while one is still held.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(15);

    /// <summary>How long a held button or key is waited for; after that the window is brought forward anyway.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(2);

    /// <summary>True while the input that opened the window may still be going on: wait before bringing it forward.</summary>
    public static bool KeepWaiting(bool inputHeld, TimeSpan waited) => inputHeld && waited < LongestWait;

    /// <summary>
    /// How long after the input ended the window is still watched. The opener can take the front back a moment after the
    /// buttons are up, from a release it had not handled yet (seen once in four first opens with a single check).
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(600);

    /// <summary>The most times the window is brought forward again for one open.</summary>
    public const int MostBringBacks = 3;

    /// <summary>
    /// Whether to keep watching after the input ended: not once the window is closed or hidden, the user has started
    /// something new (a button or key is down again), the settle time is over or it was brought back often enough.
    /// </summary>
    public static bool KeepWatching(bool windowShown, bool newInput, TimeSpan sinceInputEnded, int bringBacks)
        => windowShown && !newInput && sinceInputEnded < Settle && bringBacks < MostBringBacks;

    /// <summary>
    /// Whether to bring the new window forward again: only when the window it was opened from took the front back from
    /// it. A window of another app the user went to keeps the front, as does another window of this app (one a download
    /// finishing opened, say), and a window that is already in front, closed or hidden is left alone.
    /// </summary>
    /// <param name="windowShown">The new window is still open and shown.</param>
    /// <param name="frontIsWindow">The new window is the one in front now.</param>
    /// <param name="frontIsOpener">The window it was opened from is the one in front now.</param>
    public static bool BringBack(bool windowShown, bool frontIsWindow, bool frontIsOpener)
        => windowShown && !frontIsWindow && frontIsOpener;
}
