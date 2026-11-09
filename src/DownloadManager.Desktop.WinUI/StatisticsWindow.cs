using System.Globalization;
using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Live view of durable download statistics reported by the Host.</summary>
public sealed class StatisticsWindow : Window
{
    private readonly HostConnection _connection;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _captured = InboundUi.Secondary();
    private readonly Dictionary<string, TextBlock> _values = new(StringComparer.Ordinal);
    // UI.R31: the transfer totals are Progress Details' tiles, a value with its unit in a smaller grey run.
    private readonly Dictionary<string, InfoTile> _tiles = new(StringComparer.Ordinal);
    private bool _closed;
    private bool _refreshing;

    /// <param name="target">The Host whose statistics these are: this PC, or the server the main window shows.</param>
    public StatisticsWindow(ServerTarget? target = null)
    {
        _connection = new HostConnection(target);
        Title = _connection.IsRemote ? $"Statistics — {_connection.Target.Name} — Inbound Download Manager" : "Statistics — Inbound Download Manager";
        // Tall enough that the closing note is not cut off at 100%; it still scrolls on a shorter screen.
        WindowLayout.ResizeForDpi(this, 650, 720);
        WindowPlacement.Track(this, "statistics");
        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        var root = new StackPanel { Spacing = 14, Margin = new Thickness(16) };
        AppTheme.Track(root);
        var heading = new StackPanel { Spacing = 2 };
        heading.Children.Add(InboundUi.PageTitle("Download statistics"));
        heading.Children.Add(_captured);
        root.Children.Add(heading);
        // An empty notice takes no room, so the title doesn't float away from the tiles.
        _notice.Visibility = Visibility.Collapsed;
        _notice.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            _notice.Visibility = string.IsNullOrEmpty(_notice.Text) ? Visibility.Collapsed : Visibility.Visible);
        root.Children.Add(_notice);

        AddTiles(root, "Transfer totals",
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

        var note = InboundUi.Secondary("Recorded data measures saved download progress, not network-interface traffic. Removed entries stay in the statistics.");
        note.Margin = new Thickness(0, 0, 0, 4);
        root.Children.Add(note);
        var scroller = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
        };

        // UI.R23: a Close button at the bottom right, as Options and Scheduler have, outside the scrolling part.
        var close = new Button { Content = "Close", MinWidth = 92, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 8, 16, 16) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(close, "StatisticsCloseButton");
        close.Click += (_, _) => Close();
        var frame = new Grid();
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame.Children.Add(scroller);
        Grid.SetRow(close, 1);
        frame.Children.Add(close);
        frame.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape });
        frame.KeyboardAccelerators[0].Invoked += (_, args) => { args.Handled = true; Close(); };
        frame.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        AppTheme.Track(frame);
        void ApplyBackground() => frame.Background = InboundTheme.Brush("InboundWindowBrush", frame);
        frame.ActualThemeChanged += (_, _) => ApplyBackground();
        frame.Loaded += (_, _) => ApplyBackground();
        Content = frame;
        WindowLayout.ApplyChrome(this, frame);

        Closed += async (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _connection.ConnectionChanged -= OnConnectionChanged;
            await _connection.DisposeAsync();
        };
        _connection.ConnectionChanged += OnConnectionChanged;
        _connection.HostExiting += () => DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
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

    private void AddTiles(StackPanel root, string title, params (string Key, string Label)[] tiles)
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(InboundUi.SectionTitle(title));
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var index = 0; index < tiles.Length; index++)
        {
            if (index % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var tile = new InfoTile();
            tile.Set(tiles[index].Label, "—");
            Grid.SetRow(tile.Element, index / 2);
            Grid.SetColumn(tile.Element, index % 2);
            grid.Children.Add(tile.Element);
            _tiles[tiles[index].Key] = tile;
        }
        section.Children.Add(grid);
        root.Children.Add(section);
    }

    /// <summary>A section of counts: one card of rows, grey names and the values on the right.</summary>
    private void AddSection(StackPanel root, string title, params (string Key, string Label)[] rows)
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(InboundUi.SectionTitle(title));
        var list = new StackPanel { Spacing = 6 };
        foreach (var (key, label) in rows)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = InboundUi.Secondary(label);
            name.FontSize = 13;
            name.VerticalAlignment = VerticalAlignment.Center;
            var value = new TextBlock
            {
                Text = "—", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 1);
            row.Children.Add(name);
            row.Children.Add(value);
            list.Children.Add(row);
            _values[key] = value;
        }
        section.Children.Add(InboundUi.Card(list));
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
        SetTile("recorded", stats.RecordedBytes, string.Empty, ExactBytes(stats.RecordedBytes));
        SetTile("completed", stats.CompletedBytes, string.Empty, ExactBytes(stats.CompletedBytes));
        SetTile("todayBytes", stats.CompletedTodayBytes, string.Empty, ExactBytes(stats.CompletedTodayBytes));
        var rate = double.IsFinite(stats.BytesPerSecond) ? Math.Max(0, stats.BytesPerSecond) : 0;
        SetTile("rate", (long)rate, "/s", ExactBytes(stats.BytesPerSecond) + " per second");
        Set("completedCount", Number(stats.CompletedCount));
        Set("todayCount", Number(stats.CompletedTodayCount));
        Set("failed", Number(stats.FailedCount));
        Set("cancelled", Number(stats.CancelledCount));
        Set("retained", Number(stats.RetainedCount));
        Set("archived", Number(stats.ArchivedCount));
        Set("active", Number(stats.ActiveCount));
        Set("queued", Number(stats.QueuedCount));
        Set("paused", Number(stats.PausedCount));
        // UI.R31: moments as the list writes them.
        _captured.Text = $"Updated {UiFormat.Moment(stats.CapturedAt)}" +
            (stats.FirstRecordedAt is { } first ? $" · Tracking since {UiFormat.Moment(first)}" : " · No recorded downloads yet");
    }

    private void Set(string key, string value, string? exact = null)
    {
        if (!_values.TryGetValue(key, out var text)) return;
        text.Text = value;
        ToolTipService.SetToolTip(text, exact ?? value);
    }

    private void SetTile(string key, long bytes, string suffix, string exact)
    {
        if (!_tiles.TryGetValue(key, out var tile)) return;
        tile.SetBytes(tile.Label.Text, bytes, suffix);
        ToolTipService.SetToolTip(tile.Element, exact);
    }

    private void ClearValues()
    {
        foreach (var value in _values.Values) { value.Text = "—"; ToolTipService.SetToolTip(value, null); }
        foreach (var tile in _tiles.Values) { tile.Set(tile.Label.Text, "—"); ToolTipService.SetToolTip(tile.Element, null); }
    }

    private static string Number(int value) => Math.Max(0, value).ToString("N0", CultureInfo.CurrentCulture);
    private static string ExactBytes(long value) => $"{Math.Max(0, value).ToString("N0", CultureInfo.CurrentCulture)} bytes";
    private static string ExactBytes(double value) => $"{Math.Max(0, double.IsFinite(value) ? value : 0).ToString("N0", CultureInfo.CurrentCulture)} bytes";

}
