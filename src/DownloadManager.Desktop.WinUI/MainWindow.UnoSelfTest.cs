#if HAS_UNO
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Diagnostics for the Uno builds, off unless DOWNLOADMANAGER_UNO_SELFTEST=1 (the macOS workflow sets it): after the
/// first frame the window writes its size and a heartbeat to ui-trace.log every half second, opens About and closes it,
/// then opens Options and closes it. A heartbeat that stops shows the UI thread stuck, and when. Nothing else changes.
/// </summary>
public sealed partial class MainWindow
{
    private static bool UnoSelfTestOn => Environment.GetEnvironmentVariable("DOWNLOADMANAGER_UNO_SELFTEST") == "1";

    private void StartUnoSelfTest()
    {
        if (!UnoSelfTestOn) return;
        var beat = 0;
        var heartbeat = DispatcherQueue.CreateTimer();
        heartbeat.Interval = TimeSpan.FromMilliseconds(500);
        heartbeat.Tick += (_, _) => UiTrace.Write($"selftest heartbeat {++beat}: main {Describe(this)}" +
                                                  (_settingsWindow is { } options ? $", options {Describe(options)}" : ""));
        heartbeat.Start();
        _ = RunUnoSelfTestAsync();
    }

    private static string Describe(Window window)
    {
        try
        {
            var app = window.AppWindow;
            return $"{app.Size.Width}x{app.Size.Height} px at {app.Position.X},{app.Position.Y}";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    private async Task RunUnoSelfTestAsync()
    {
        try
        {
            await Task.Delay(2000);
            UiTrace.Write("selftest: opening About");
            MenuAbout_Click(this, new RoutedEventArgs());
            await Task.Delay(2000);
            var dialogs = VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot)
                .Select(popup => FindDialog(popup.Child, 4)).OfType<ContentDialog>().ToList();
            UiTrace.Write($"selftest: closing About ({dialogs.Count} dialog open)");
            foreach (var dialog in dialogs) dialog.Hide();
            UiTrace.Write("selftest: About closed");

            await Task.Delay(3000);
            UiTrace.Write("selftest: opening Options");
            MenuOptions_Click(this, new RoutedEventArgs());
            await Task.Delay(4000);
            UiTrace.Write($"selftest: closing Options ({(_settingsWindow is null ? "not open" : "open")})");
            _settingsWindow?.Close();
            UiTrace.Write("selftest: Options closed");
        }
        catch (Exception ex)
        {
            UiTrace.Write("selftest failed: " + ex);
        }
    }

    private static ContentDialog? FindDialog(DependencyObject? element, int depth)
    {
        if (element is ContentDialog dialog) return dialog;
        if (element is null || depth == 0) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (FindDialog(VisualTreeHelper.GetChild(element, i), depth - 1) is { } found) return found;
        }

        return null;
    }
}
#endif
