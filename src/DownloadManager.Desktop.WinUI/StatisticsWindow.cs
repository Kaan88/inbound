using System.Globalization;
using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Live view of durable download statistics reported by the Host.</summary>
public sealed class StatisticsWindow : Window
{
    private readonly HostConnection _connection = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _captured = new() { Opacity = 0.72, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, TextBlock> _values = new(StringComparer.Ordinal);
    private bool _closed;
    private bool _refreshing;

    public StatisticsWindow()
    {
        Title = "Statistics — Rill Download Manager";
        WindowLayout.ResizeForDpi(this, 650, 650);
        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        var root = new StackPanel { Spacing = 14, Margin = new Thickness(16) };
        root.RequestedTheme = UiStateStore.Load().DarkMode switch
        {
            true => ElementTheme.Dark,
            false => ElementTheme.Light,
            null => ElementTheme.Default,
        };
        root.Children.Add(new TextBlock
        {
            Text = "Download statistics", FontSize = 23,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        root.Children.Add(_captured);
        root.Children.Add(_notice);

        AddSection(root, "Transfer totals",
            ("recorded", "Recorded progress"),
            ("completed", "Completed download data"),
            ("todayBytes", "Completed today"),
            ("rate", "Current transfer rate"));
        AddSection(root, "Download counts",
            ("completedCount", "Completed"),
            ("todayCount", "Completed today"),
            ("failed", "Failed"),
            ("cancelled", "Cancelled"),
            ("retained", "Retained in list"),
            ("archived", "Archived history entries"));
        AddSection(root, "Current activity",
            ("active", "Active"),
            ("queued", "Queued"),
            ("paused", "Paused"));

        root.Children.Add(new TextBlock
        {
            Text = "Recorded data measures saved download progress, not network-interface traffic. Removed entries are retained in statistics starting with this version; older deleted history cannot be recovered.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.78,
            Margin = new Thickness(0, 3, 0, 4),
        });
        var scroller = new ScrollViewer
        {
            RequestedTheme = root.RequestedTheme,
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
        };

        void ApplyBackground() => scroller.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(scroller.ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 250, 250, 250));
        scroller.ActualThemeChanged += (_, _) => ApplyBackground();
        scroller.Loaded += (_, _) => ApplyBackground();
        Content = scroller;

        Closed += async (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _connection.ConnectionChanged -= OnConnectionChanged;
            await _connection.DisposeAsync();
        };
        _connection.ConnectionChanged += OnConnectionChanged;
        _timer.Tick += async (_, _) => await RefreshAsync();
        _connection.Start();
        _timer.Start();
        _ = RefreshAsync();
    }

    public void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private void AddSection(StackPanel root, string title, params (string Key, string Label)[] rows)
    {
        var section = new StackPanel { Spacing = 7 };
        section.Children.Add(new TextBlock
        {
            Text = title, FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 1),
        });
        foreach (var (key, label) in rows)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock
            {
                Text = "—", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 1);
            row.Children.Add(name);
            row.Children.Add(value);
            section.Children.Add(row);
            _values[key] = value;
        }
        root.Children.Add(section);
    }

    private void OnConnectionChanged(bool connected, string? message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            if (connected) _ = RefreshAsync();
            else
            {
                ClearValues();
                _captured.Text = "Live statistics unavailable while the Host is disconnected.";
                _notice.Text = string.IsNullOrWhiteSpace(message) ? "Waiting for the background Host…" : message;
            }
        });
    }

    private async Task RefreshAsync()
    {
        if (_closed || _refreshing) return;
        _refreshing = true;
        try
        {
            var reply = await _connection.GetStatisticsAsync();
            if (_closed) return;
            if (reply.Type != MessageTypes.StatisticsResult)
                throw new InvalidDataException("The Host returned an unexpected statistics response.");
            Apply(reply.GetPayload<DownloadStatisticsDto>());
            _notice.Text = string.Empty;
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                if (!_connection.IsConnected) ClearValues();
                _notice.Text = "Could not refresh statistics: " + ex.Message;
            }
        }
        finally { _refreshing = false; }
    }

    private void Apply(DownloadStatisticsDto stats)
    {
        Set("recorded", FormatBytes(stats.RecordedBytes), ExactBytes(stats.RecordedBytes));
        Set("completed", FormatBytes(stats.CompletedBytes), ExactBytes(stats.CompletedBytes));
        Set("todayBytes", FormatBytes(stats.CompletedTodayBytes), ExactBytes(stats.CompletedTodayBytes));
        Set("rate", FormatBytes(stats.BytesPerSecond) + "/s", ExactBytes(stats.BytesPerSecond) + " per second");
        Set("completedCount", Number(stats.CompletedCount));
        Set("todayCount", Number(stats.CompletedTodayCount));
        Set("failed", Number(stats.FailedCount));
        Set("cancelled", Number(stats.CancelledCount));
        Set("retained", Number(stats.RetainedCount));
        Set("archived", Number(stats.ArchivedCount));
        Set("active", Number(stats.ActiveCount));
        Set("queued", Number(stats.QueuedCount));
        Set("paused", Number(stats.PausedCount));
        _captured.Text = $"Updated {stats.CapturedAt.ToLocalTime():g}" +
            (stats.FirstRecordedAt is { } first ? $" · Tracking since {first.ToLocalTime():d}" : " · No recorded downloads yet");
    }

    private void Set(string key, string value, string? exact = null)
    {
        if (!_values.TryGetValue(key, out var text)) return;
        text.Text = value;
        ToolTipService.SetToolTip(text, exact ?? value);
    }

    private void ClearValues()
    {
        foreach (var value in _values.Values) { value.Text = "—"; ToolTipService.SetToolTip(value, null); }
    }

    private static string Number(int value) => Math.Max(0, value).ToString("N0", CultureInfo.CurrentCulture);
    private static string ExactBytes(long value) => $"{Math.Max(0, value).ToString("N0", CultureInfo.CurrentCulture)} bytes";
    private static string ExactBytes(double value) => $"{Math.Max(0, double.IsFinite(value) ? value : 0).ToString("N0", CultureInfo.CurrentCulture)} bytes";

    private static string FormatBytes(long value) => FormatBytes((double)Math.Max(0, value));

    private static string FormatBytes(double value)
    {
        value = Math.Max(0, double.IsFinite(value) ? value : 0);
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value.ToString("N0", CultureInfo.CurrentCulture)} {units[unit]}"
            : $"{value.ToString("N2", CultureInfo.CurrentCulture)} {units[unit]}";
    }
}
