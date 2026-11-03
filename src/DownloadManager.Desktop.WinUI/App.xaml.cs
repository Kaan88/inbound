using DownloadManager.Contracts;
using Microsoft.UI.Xaml;

namespace DownloadManager.Desktop.WinUI;

public partial class App : Application
{
    private readonly EventWaitHandle _showEvent;
    private readonly bool _secondaryInstance;
    private MainWindow? _window;

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
        var name = $"Local\\DownloadManager.UI.Show.{PipeNames.CurrentUserKey()}";
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name, out var createdNew);
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
        if (_secondaryInstance)
        {
            _showEvent.Set();
            Exit();
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

        var commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Contains("--page", StringComparer.OrdinalIgnoreCase) ||
            commandLine.Contains("settings", StringComparer.OrdinalIgnoreCase))
        {
            _window.RequestSettingsPage();
        }

        _ = Task.Run(() =>
        {
            while (true)
            {
                if (!_showEvent.WaitOne(TimeSpan.FromSeconds(1)))
                {
                    continue;
                }

                _window.DispatcherQueue.TryEnqueue(() => _window.ShowFromTray());
            }
        });
    }
}
