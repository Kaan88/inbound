using DownloadManager.Contracts;
using Microsoft.UI.Xaml;

namespace DownloadManager.Desktop.WinUI;

public partial class App : Application
{
    private const int AnyProcess = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private readonly EventWaitHandle _showEvent;
    private readonly EventWaitHandle _settingsEvent;
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
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject);
        UnhandledException += (_, e) =>
        {
            LogCrash(e.Exception);
            e.Handled = false;
        };

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
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name, out var createdNew);
        _settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Settings");
        _secondaryInstance = !createdNew;
        UiTrace.Write($"App ctor end secondary={_secondaryInstance}");
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
        // Explicit launches can restart an exited Host even when the hidden
        // frontend already owns the single-instance activation event.
        HostLauncher.EnsureStarted();
        if (_secondaryInstance)
        {
            // This launch came from a click, so it may take the foreground; the running window may not unless
            // it is allowed to, and without this Windows leaves it behind (UI.B4).
            AllowSetForegroundWindow(AnyProcess);
            (openSettings ? _settingsEvent : _showEvent).Set();
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
                    _showEvent.WaitOne();
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
                    _showEvent.WaitOne();
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
                    _showEvent.WaitOne();
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

        _ = Task.Run(() =>
        {
            while (true)
            {
                var activation = WaitHandle.WaitAny([_showEvent, _settingsEvent]);
                _window.DispatcherQueue.TryEnqueue(() =>
                {
                    _window.ShowFromTray();
                    if (activation == 1) _window.RequestSettingsPage();
                });
            }
        });
    }
}
