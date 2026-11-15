#if HAS_UNO
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Diagnostics for the Uno builds, off unless DOWNLOADMANAGER_UNO_SELFTEST=1 (the macOS workflow sets it): after the
/// first frame the window writes its size and a heartbeat to ui-trace.log every half second, then opens About, the first
/// download's Progress Details and Options, and closes each: by a click where the workflow can click (it reads the
/// "click ... at x,y" lines), else from here. A heartbeat that stops shows the UI thread stuck, and when.
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
            // About, closed by a click on its OK button when the workflow can click (cliclick), else from here.
            await Task.Delay(2000);
            UiTrace.Write("selftest: opening About");
            MenuAbout_Click(this, new RoutedEventArgs());
            await Task.Delay(1500);
            var about = OpenDialogs().FirstOrDefault();
            if (about is not null && FindButton(about, "OK") is { } ok) UiTrace.Write($"selftest: click About OK at {ScreenPoint(this, ok)}");
            if (!await WaitAsync(() => !OpenDialogs().Any(), 10000))
            {
                UiTrace.Write("selftest: About not closed by a click; closing it from here");
                foreach (var dialog in OpenDialogs()) dialog.Hide();
            }
            UiTrace.Write("selftest: About closed");

            // Progress Details of the first download, closed by a click on its close button, else from here.
            if (_visibleJobs.FirstOrDefault(row => !row.IsRemoteRow) is { } job)
            {
                await Task.Delay(2000);
                UiTrace.Write("selftest: opening Progress Details");
                OpenDownloadWindow(job.Id);
                await Task.Delay(2500);
                if (_progressWindows.Find(ProgressWindowKey.For(ProgressWindowKind.Download, job.Id, _connection.Target)) is { } progress)
                {
                    UiTrace.Write($"selftest: Progress Details {Describe(progress)}");
                    UiTrace.Write($"selftest: click Progress Details close at {CloseButtonPoint(progress)}");
                    var closed = false;
                    progress.Closed += (_, _) => closed = true;
                    if (!await WaitAsync(() => closed, 10000))
                    {
                        UiTrace.Write("selftest: Progress Details not closed by a click; closing it from here");
                        progress.Close();
                    }
                    UiTrace.Write("selftest: Progress Details closed");
                }
                else UiTrace.Write("selftest: Progress Details did not open");
            }
            else UiTrace.Write("selftest: no download, so no Progress Details");

            await Task.Delay(2000);
            UiTrace.Write("selftest: opening Options");
            MenuOptions_Click(this, new RoutedEventArgs());
            await Task.Delay(4000);
            UiTrace.Write($"selftest: closing Options ({(_settingsWindow is null ? "not open" : "open")})");
            _settingsWindow?.Close();
            UiTrace.Write("selftest: Options closed");

            // A short window: the sidebar scrolls (ScrollSidebarOnUno) and its last entry can be reached.
            await Task.Delay(1000);
            AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = AppWindow.Size.Width, Height = (int)Math.Round(420 * UnoScreens.Scale()) });
            await Task.Delay(1500);
            if (CategoryList.Parent is ScrollViewer sidebar)
            {
                UiTrace.Write($"selftest: sidebar extent {sidebar.ExtentHeight:0}, viewport {sidebar.ViewportHeight:0}, scrollable {sidebar.ScrollableHeight:0}");
                sidebar.ChangeView(null, sidebar.ScrollableHeight, null, disableAnimation: true);
                await Task.Delay(800);
                UiTrace.Write($"selftest: sidebar scrolled to {sidebar.VerticalOffset:0} of {sidebar.ScrollableHeight:0}");
            }
            else UiTrace.Write("selftest: the sidebar has no ScrollViewer");
            UiTrace.Write("selftest: short window");
            await Task.Delay(3000);

            // Last, the main window itself, by its close button: the workflow then checks that the process ends.
            await Task.Delay(2000);
            UiTrace.Write($"selftest: click main close at {CloseButtonPoint(this)}");
            UiTrace.Write("selftest: done");
        }
        catch (Exception ex)
        {
            UiTrace.Write("selftest failed: " + ex);
        }
    }

    private List<ContentDialog> OpenDialogs() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot)
        .Select(popup => FindDialog(popup.Child, 4)).OfType<ContentDialog>().ToList();

    private static async Task<bool> WaitAsync(Func<bool> done, int milliseconds)
    {
        for (var waited = 0; waited < milliseconds; waited += 250)
        {
            if (done()) return true;
            await Task.Delay(250);
        }

        return done();
    }

    /// <summary>The centre of <paramref name="element"/> in global screen points, top-left origin (what cliclick takes),
    /// from where Cocoa has the window.</summary>
    private static string ScreenPoint(Window window, FrameworkElement element)
    {
        if (UnoScreens.MacWindowFrame(window.Title) is not { } frame) return "unknown";
        var centre = element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(element.ActualWidth / 2, element.ActualHeight / 2));
        return $"{Math.Round(frame.Left + centre.X)},{Math.Round(frame.Top + frame.TitleBar + centre.Y)}";
    }

    /// <summary>The red close button of a standard macOS title bar: about 20 by 14 points in from the top-left corner.</summary>
    private static string CloseButtonPoint(Window window)
        => UnoScreens.MacWindowFrame(window.Title) is { } frame ? $"{Math.Round(frame.Left + 20)},{Math.Round(frame.Top + 14)}" : "unknown";

    private static Button? FindButton(DependencyObject element, string text)
    {
        if (element is Button { Content: string content } button && content == text) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (FindButton(VisualTreeHelper.GetChild(element, i), text) is { } found) return found;
        }

        return null;
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
