using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>A Progress Details window (one download) or a package's window.</summary>
internal enum ProgressWindowKind
{
    Download,
    Package,
}

/// <summary>
/// T11: which progress window this is. The same download's window for another server is another window, as it is for
/// the named events (<see cref="WindowInstance.EventName"/>).
/// </summary>
internal readonly record struct ProgressWindowKey(ProgressWindowKind Kind, Guid Id, string? ServerId)
{
    public static ProgressWindowKey For(ProgressWindowKind kind, Guid id, ServerTarget target) =>
        new(kind, id, target.IsLocal ? null : target.Id!.ToLowerInvariant());
}

/// <summary>
/// T11: the progress and package windows the main window opened in its own process, one per
/// <see cref="ProgressWindowKey"/>, and when that process ends. With "Closing the window ends it" (PERF.5) on, closing the
/// main window while some are open hides it, and the process ends once the last of them closes; showing the main window
/// again calls that off. Nothing here touches the UI.
/// </summary>
internal sealed class ProgressWindowSet<TWindow> where TWindow : class
{
    private readonly Dictionary<ProgressWindowKey, TWindow> _open = [];

    public int Count => _open.Count;

    /// <summary>The main window was closed with "ends it" on while windows were open: the last to close ends the process.</summary>
    public bool EndsWithLastWindow { get; private set; }

    /// <summary>
    /// Whether a finished download may open its completion window. Not while the process only waits for its last progress
    /// window to close: before T11 the process had ended by then, and new windows would keep it running.
    /// </summary>
    public bool OpensCompletionWindows => !EndsWithLastWindow;

    public IReadOnlyList<TWindow> Windows => [.. _open.Values];

    /// <summary>The window already open for this key, which is shown instead of opening another.</summary>
    public TWindow? Find(ProgressWindowKey key) => _open.GetValueOrDefault(key);

    public void Add(ProgressWindowKey key, TWindow window) => _open[key] = window;

    /// <summary>A window closed.</summary>
    /// <returns>True when the process ends now: it was the last one, and the main window was closed meanwhile.</returns>
    public bool Closed(ProgressWindowKey key, TWindow window)
    {
        // A window that isn't the one kept for its key (closed after another took its place) changes nothing.
        if (_open.TryGetValue(key, out var kept) && ReferenceEquals(kept, window)) _open.Remove(key);
        return EndsWithLastWindow && _open.Count == 0;
    }

    /// <summary>The main window closed. <paramref name="endsProcess"/> is the PERF.5 setting.</summary>
    /// <returns>True when the process ends now: the setting is on and no progress window is open.</returns>
    public bool MainWindowClosed(bool endsProcess)
    {
        EndsWithLastWindow = endsProcess && _open.Count > 0;
        return endsProcess && _open.Count == 0;
    }

    /// <summary>The main window is shown again: the process no longer ends with the last progress window.</summary>
    public void MainWindowShown() => EndsWithLastWindow = false;
}
