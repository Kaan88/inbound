using DownloadManager.Contracts;
using Microsoft.UI.Xaml;

namespace DownloadManager.Desktop.WinUI;

public partial class App : Application
{
    private const int AnyProcess = -1;

    /// <summary>The Host's <c>UiPages.Updates</c>: the window doesn't reference the Runtime, so the word is kept here too.</summary>
    internal const string UiPageUpdates = "updates";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Lets the window another process is about to signal take the foreground (Windows only; a no-op elsewhere).</summary>
    internal static void AllowAnyForeground()
    {
        if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(AnyProcess);
    }

    private readonly ActivationSignal _activation;
    private readonly bool _secondaryInstance;
    private MainWindow? _window;
    private DownloadWindow? _downloadWindow;
    private UpdateWindow? _updateWindow;
    private readonly bool _updateMode;
    private readonly Guid? _jobId;
    private readonly Guid? _packageId;
    private PackageWindow? _packageWindow;

    /// <summary><c>--server</c>: the saved server a progress or package window is for (design D9, section 5).</summary>
    private readonly string? _serverId;

    public App()
    {
        UiTrace.Write("App ctor begin");
        // Audit L8: a Host message this build could not read is skipped; the trace says which.
        HostConnection.Trace = UiTrace.Write;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject);
        UnhandledException += (_, e) =>
        {
            LogCrash(e.Exception);
            e.Handled = false;
        };

#if HAS_UNO
        // The first frames are drawn with the application's theme, before any window's own: without this a dark choice
        // flashed light on a cold start. The saved choice (ui-state.json) is known before the Host answers.
        RequestedTheme = AppTheme.Element == ElementTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
#endif
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            throw;
        }

        UiTrace.Write("App InitializeComponent complete");
        var commandLine = Environment.GetCommandLineArgs();
        _updateMode = commandLine.Contains("update-progress");
        var jobIndex = Array.FindIndex(commandLine, arg => arg == "--job");
        if (jobIndex >= 0 && jobIndex + 1 < commandLine.Length &&
            Guid.TryParse(commandLine[jobIndex + 1], out var jobId)) _jobId = jobId;
        var packageIndex = Array.FindIndex(commandLine, arg => arg == "--package");
        if (packageIndex >= 0 && packageIndex + 1 < commandLine.Length &&
            Guid.TryParse(commandLine[packageIndex + 1], out var packageId)) _packageId = packageId;
        _serverId = WindowInstance.ServerId(commandLine);
        // The same download's window for another server is another window, so the server's id is in its name.
        var name = WindowInstance.EventName(PipeNames.CurrentUserKey(), _updateMode, _jobId, _packageId, _serverId);
#if HAS_UNO
        // The prototype runs beside the WinUI window on the same instance; it must not take over its activation.
        name += ".Uno";
#endif
        _activation = ActivationSignal.Create(name, out var createdNew);
        _secondaryInstance = !createdNew;
        UiTrace.Write($"App ctor end secondary={_secondaryInstance}");
    }

    /// <summary>
    /// UI.R40: the name the main window holds while its Check for Updates dialog is open. The Host's update progress
    /// window (<c>--page update-progress</c>) sees it and doesn't open: the dialog shows that progress itself.
    /// </summary>
    internal static string UpdateDialogName()
    {
        var name = WindowInstance.EventName(PipeNames.CurrentUserKey(), false, null, null, null) + ".UpdateDialog";
#if HAS_UNO
        name += ".Uno";
#endif
        return name;
    }

    /// <summary>The folder <see cref="TorrentOpenRequests"/> queues into: the data folder, as the window's own state.</summary>
    internal static string TorrentQueueDirectory()
    {
        var isolated = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA");
        return TorrentOpenRequests.QueueDirectory(string.IsNullOrWhiteSpace(isolated) ? AppFolders.DataRoot() : isolated);
    }

    private static bool TryQueueTorrent(string input, bool fromDownload)
    {
        try
        {
            TorrentOpenRequests.Enqueue(TorrentQueueDirectory(), input, DateTimeOffset.UtcNow, fromDownload);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UiTrace.Write($"A torrent to open couldn't be queued: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>EXT.R12: for the running main window, which takes it when this launch asks it to show itself.</summary>
    private static void TryQueueShowJob(Guid jobId)
    {
        try
        {
            ShowJobRequests.Write(TorrentQueueDirectory(), jobId, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UiTrace.Write($"A download to show couldn't be passed on: {ex.GetType().Name}");
        }
    }

    private static void LogCrash(object? exception)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "ui-crash.log");
            File.AppendAllText(path, CrashLogText.Entry(DateTimeOffset.Now, exception));
        }
        catch (Exception)
        {
            // never mask the original failure
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Before any window resolves the window brush; Resources is not ready in the constructor.
        WindowLayout.UseSolidWindowsWithoutMica(Resources);
        // Files and programs opened from a window inherit the current directory; keep it out of
        // app\, or they hold that folder against updates for as long as they stay open.
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Directory.Exists(profile)) Environment.CurrentDirectory = profile;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        var commandLine = Environment.GetCommandLineArgs();
        var pageIndex = Array.FindIndex(commandLine, arg => string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase));
        var openSettings = pageIndex >= 0 && pageIndex + 1 < commandLine.Length &&
            string.Equals(commandLine[pageIndex + 1], "settings", StringComparison.OrdinalIgnoreCase);
        // UI.R40: the tray's Check for Updates… and update entries open the main window's update dialog.
        var openUpdates = !_updateMode && pageIndex >= 0 && pageIndex + 1 < commandLine.Length &&
            string.Equals(commandLine[pageIndex + 1], UiPageUpdates, StringComparison.OrdinalIgnoreCase);
        // The main window's update dialog shows the progress the Host would open this window for.
        if (_updateMode && ActivationSignal.IsOwned(UpdateDialogName()))
        {
            UiTrace.Write("Update progress window not opened: the update dialog shows it");
            Exit();
            return;
        }

        // T1 step 6b: a magnet or .torrent file opened from Windows. Queued first, so whichever window takes it, this
        // launch's or the one already running, finds it in the same place.
        // T10: the Host may name several finished .torrent files in one launch, each after its own --open-torrent.
        var openTorrents = _updateMode || _jobId is not null || _packageId is not null ? new List<string>() : TorrentOpenRequests.ParseAll(commandLine);
        var fromDownload = TorrentOpenRequests.IsFromDownload(commandLine);
        var openTorrent = openTorrents.Where(input => TryQueueTorrent(input, fromDownload)).ToList() is { Count: > 0 } queued ? queued[0] : null;
        // EXT.R12: the extension's "Show in app": the main window at one download.
        var showJob = _updateMode || _jobId is not null || _packageId is not null ? null : UiArguments.ShowJobId(commandLine);
        // Explicit launches can restart an exited Host even when the hidden
        // frontend already owns the single-instance activation event.
        HostLauncher.EnsureStarted();
        if (_secondaryInstance)
        {
            if (openTorrent is not null)
            {
                if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(AnyProcess);
                _activation.SignalOpen();
                Exit();
                return;
            }

            if (showJob is { } shown) TryQueueShowJob(shown);

            // This launch came from a click, so it may take the foreground; the running window may not unless
            // it is allowed to, and without this Windows leaves it behind (UI.B4).
            if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(AnyProcess);
            if (openUpdates) _activation.SignalUpdates();
            else _activation.Signal(openSettings);
            Exit();
            return;
        }

        if (_updateMode)
        {
            _updateWindow = new UpdateWindow();
            _updateWindow.Closed += (_, _) => Exit();
            _updateWindow.Activate();
            _ = Task.Run(() =>
            {
                while (true)
                {
                    _activation.Show.WaitOne();
                    _updateWindow.DispatcherQueue.TryEnqueue(() => _updateWindow.ShowWindow());
                }
            });
            return;
        }

        ServerTarget? server = null;
        if (_serverId is not null && (_jobId is not null || _packageId is not null))
        {
            // The token is read from the window's own store; it never travels on the command line.
            server = ServerStore.ForThisUser().Target(_serverId);
            if (server is null)
            {
                UiTrace.Write("A progress window was asked for a server that isn't saved or can't be opened here");
                Exit();
                return;
            }
        }

        if (_packageId is { } package)
        {
            _packageWindow = new PackageWindow(package, server);
            _packageWindow.Closed += (_, _) => Exit();
            WindowPlacement.ActivateCascaded(_packageWindow);
            _ = Task.Run(() =>
            {
                while (true)
                {
                    _activation.Show.WaitOne();
                    _packageWindow.DispatcherQueue.TryEnqueue(() => _packageWindow.ShowWindow());
                }
            });
            return;
        }

        if (_jobId is { } id)
        {
            _downloadWindow = new DownloadWindow(id, commandLine.Contains("--manual"), server);
            _downloadWindow.Closed += (_, _) => Exit();
            WindowPlacement.ActivateCascaded(_downloadWindow);
            _ = Task.Run(() =>
            {
                while (true)
                {
                    _activation.Show.WaitOne();
                    _downloadWindow.DispatcherQueue.TryEnqueue(() => _downloadWindow.ShowWindow());
                }
            });
            return;
        }

        UiTrace.Write("OnLaunched begin");
        try
        {
            _window = new MainWindow();
            UiTrace.Write("MainWindow constructed");
            _window.Activate();
            UiTrace.Write("MainWindow activated");
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            throw;
        }

        if (openSettings)
        {
            _window.RequestSettingsPage();
        }

        if (openUpdates)
        {
            _window.RequestUpdatesDialog();
        }

        if (openTorrent is not null)
        {
            _window.RequestQueuedTorrents();
        }

        if (showJob is { } showJobId)
        {
            _window.RequestShowJob(showJobId);
        }

        _ = Task.Run(() =>
        {
            while (true)
            {
                var activation = WaitHandle.WaitAny([_activation.Show, _activation.Settings, _activation.Open, _activation.Updates]);
                _window.DispatcherQueue.TryEnqueue(() =>
                {
                    _window.ShowFromTray();
                    if (activation == 1) _window.RequestSettingsPage();
                    else if (activation == 2) _window.RequestQueuedTorrents();
                    else if (activation == 3) _window.RequestUpdatesDialog();
                    else if (ShowJobRequests.Take(TorrentQueueDirectory(), DateTimeOffset.UtcNow) is { } shown) _window.RequestShowJob(shown);
                });
            }
        });
    }
}
