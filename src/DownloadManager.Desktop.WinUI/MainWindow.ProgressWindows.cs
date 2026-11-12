using DownloadManager.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T11: Progress Details and package windows opened from this window are built in this process, as Scheduler and
/// Statistics are, each with its own connection, instead of starting the app again for each. Windows the Host opens by
/// itself (on start, on completion) are still processes of their own (<see cref="App"/>, <c>--job</c>/<c>--package</c>).
/// </summary>
public sealed partial class MainWindow
{
    private readonly ProgressWindowSet<Window> _progressWindows = new();

    // The completion burst's windows, opened one per dispatcher turn at low priority so the list keeps up.
    private readonly Queue<(ProgressWindowKind Kind, Guid Id, ServerTarget Target)> _windowsToOpen = new();
    private bool _windowOpenQueued;

    /// <summary>Progress Details for a download, opened from this window (a double-click, Enter, a menu).</summary>
    private void OpenDownloadWindow(Guid jobId) => OpenProgressWindow(ProgressWindowKind.Download, jobId, _connection.Target, this);

    /// <summary>
    /// Shows the window already open for this download or package, or opens one here. One the Host started as a process
    /// of its own comes to the front instead: it holds the same name. <paramref name="openedFrom"/> is the window the user
    /// opened it from, which must not keep the front once the click is over (UI.B28); null when nobody asked (a burst of
    /// finished downloads).
    /// </summary>
    private void OpenProgressWindow(ProgressWindowKind kind, Guid id, ServerTarget target, Window? openedFrom = null)
    {
        var key = ProgressWindowKey.For(kind, id, target);
        if (_progressWindows.Find(key) is { } open)
        {
            ShowProgressWindow(open);
            if (openedFrom is not null) WindowFront.KeepInFrontAfterInput(open, openedFrom);
            return;
        }

        // The window takes its name, so a later window the Host starts for it (on completion, say) shows this one.
        ActivationSignal? signal = null;
        var name = ProgressWindowEventName(kind, id, target);
        try
        {
            signal = ActivationSignal.Create(name, out var createdNew);
            if (!createdNew)
            {
                signal.Dispose();
                // The progress window is another process: without this Windows leaves it behind this one (UI.B4).
                App.AllowAnyForeground();
                if (ActivationSignal.TryShowOwner(name)) return;
                // It closed in between: open one here, without the name.
                signal = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException or WaitHandleCannotBeOpenedException)
        {
            // Without its name the window still opens; only a later launch for it can't find it.
            signal?.Dispose();
            signal = null;
        }

        Window window;
        try
        {
            // A package's file opened from its window must stay in front of that window, as one opened here does.
            PackageWindow? package = null;
            window = kind == ProgressWindowKind.Package
                ? package = new PackageWindow(id, target, jobId => OpenProgressWindow(ProgressWindowKind.Download, jobId, target, package))
                : new DownloadWindow(id, manual: true, target, inMainWindowProcess: true);
        }
        catch (Exception ex)
        {
            signal?.Dispose();
            UiTrace.Write($"Progress window not opened: {ex.GetType().Name}");
            _ = ShowMessageAsync("Could not open progress", ex.Message);
            return;
        }

        _progressWindows.Add(key, window);
        // Blocks no thread of its own and wakes nothing while idle: the thread pool waits on the handle.
        var wait = signal is null ? null : ThreadPool.RegisterWaitForSingleObject(signal.Show, (_, _) => window.DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(_progressWindows.Find(key), window)) ShowProgressWindow(window);
        }), null, Timeout.Infinite, executeOnlyOnce: false);
        window.Closed += (_, _) =>
        {
            wait?.Unregister(null);
            signal?.Dispose();
            if (_progressWindows.Closed(key, window)) EndWindowProcess("the last progress window closed");
        };
        WindowPlacement.ActivateCascaded(window);
        if (openedFrom is not null) WindowFront.KeepInFrontAfterInput(window, openedFrom);
    }

    private static void ShowProgressWindow(Window window)
    {
        if (window is DownloadWindow download) download.ShowWindow();
        else if (window is PackageWindow package) package.ShowWindow();
    }

    /// <summary>A download's Progress Details window open in this process for the shown server, or null.</summary>
    private Window? InProcessDownloadWindow(Guid jobId)
        => _progressWindows.Find(ProgressWindowKey.For(ProgressWindowKind.Download, jobId, _connection.Target));

    private static string ProgressWindowEventName(ProgressWindowKind kind, Guid id, ServerTarget target)
    {
        var name = WindowInstance.WindowName(PipeNames.CurrentUserKey(), kind == ProgressWindowKind.Package, id, target);
#if HAS_UNO
        // The prototype's windows take their own names (App).
        name += ".Uno";
#endif
        return name;
    }

    /// <summary>Opens these windows one per dispatcher turn, at low priority: a burst doesn't hold up the list.</summary>
    private void OpenWindowsInTurns(IEnumerable<(ProgressWindowKind Kind, Guid Id, ServerTarget Target)> windows)
    {
        foreach (var window in windows) _windowsToOpen.Enqueue(window);
        QueueNextWindowOpen();
    }

    private void QueueNextWindowOpen()
    {
        if (_windowOpenQueued || _windowsToOpen.Count == 0) return;
        _windowOpenQueued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _windowOpenQueued = false;
            if (!_windowsToOpen.TryDequeue(out var next)) return;
            // The main window was closed meanwhile with "ends it" on: the rest of the burst stays closed.
            if (!_progressWindows.OpensCompletionWindows)
            {
                _windowsToOpen.Clear();
                return;
            }

            OpenProgressWindow(next.Kind, next.Id, next.Target);
            QueueNextWindowOpen();
        });
    }

    /// <summary>
    /// PERF.5: the main window was closed with "Closing the window ends it" on. It hides; the process ends now, or with
    /// the last progress window this process opened. Showing the main window again calls that off. The window is hidden
    /// already.
    /// </summary>
    private void CloseMainWindowCompletely()
    {
        if (_progressWindows.MainWindowClosed(endsProcess: true))
        {
            EndWindowProcess("the main window closed");
            return;
        }

        UiTrace.Write($"MainWindow hidden; the process ends with the last of {_progressWindows.Count} progress windows");
    }

    private static void EndWindowProcess(string why)
    {
        UiTrace.Write($"Window process ending: {why}");
        Application.Current.Exit();
#if HAS_UNO
        // Uno's X11 head leaves the process running after Exit.
        Environment.Exit(0);
#endif
    }
}
