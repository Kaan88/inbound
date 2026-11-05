using DownloadManager.Contracts;
using Microsoft.UI.Xaml;

namespace DownloadManager.Desktop.WinUI;

public partial class App : Application
{
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
        var name = $"Local\\DownloadManager.UI.Show.{PipeNames.CurrentUserKey()}" +
            (_updateMode ? ".Update" : _jobId is { } id ? $".Job.{id:D}" : _packageId is { } pid ? $".Package.{pid:D}" : string.Empty);
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
            File.AppendAllText(path, $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // never mask the original failure
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        var pageIndex = Array.FindIndex(commandLine, arg => string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase));
        var openSettings = pageIndex >= 0 && pageIndex + 1 < commandLine.Length &&
            string.Equals(commandLine[pageIndex + 1], "settings", StringComparison.OrdinalIgnoreCase);
        // Explicit launches can restart an exited Host even when the hidden
        // frontend already owns the single-instance activation event.
        HostLauncher.EnsureStarted();
        if (_secondaryInstance)
        {
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

        if (_packageId is { } package)
        {
            _packageWindow = new PackageWindow(package);
            _packageWindow.Closed += (_, _) => Exit();
            _packageWindow.Activate();
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
            _downloadWindow = new DownloadWindow(id, commandLine.Contains("--manual"));
            _downloadWindow.Closed += (_, _) => Exit();
            _downloadWindow.Activate();
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
