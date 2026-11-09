using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The provider's file hosts: up or down, today's use against the limits, and notes. It reads the list the Host
/// keeps for an hour, so opening it costs no call to the provider unless that list has gone stale.
/// </summary>
internal sealed class HosterWindow : Window
{
    private static HosterWindow? _open;

    private readonly RemoteClient _client;
    private readonly Grid _root = new() { Padding = new Thickness(16), RowSpacing = 10 };
    private readonly TextBox _search = new() { PlaceholderText = "Search hosts", MinWidth = 240 };
    private readonly CheckBox _downOnly = new() { Content = "Down only" };
    private readonly Button _reload = new() { Content = "Reload", MinWidth = 92 };
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly StatusLine _status = new("Host list status");
    private List<RemoteHosterDto> _hosters = [];

    private HosterWindow(RemoteClient client)
    {
        _client = client;
        AppTheme.Track(_root);
        Title = "Hosters — Inbound Download Manager";
        WindowLayout.ResizeForDpi(this, 760, 640);
        WindowPlacement.Track(this, "hosters");

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        top.Children.Add(_search);
        _downOnly.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(_downOnly);
        top.Children.Add(_reload);
        _root.Children.Add(top);

        var scroller = new ScrollViewer { Content = _rows, Padding = InboundTheme.DialogScrollPadding, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 1);
        _root.Children.Add(scroller);
        _status.Text.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_status.Text, 2);
        _root.Children.Add(_status.Text);

        AutomationProperties.SetName(_search, "Search hosts");
        AutomationProperties.SetName(_rows, "File hosts");
        ToolTipService.SetToolTip(_reload, "Read the list again. It is kept for an hour, so this asks the provider only when it is older.");

        _search.TextChanged += (_, _) => Fill();
        _downOnly.Click += (_, _) => Fill();
        _reload.Click += (_, _) => _ = LoadAsync();
        _root.Loaded += (_, _) => _root.Background = InboundTheme.Brush("InboundWindowBrush", _root);
        _root.ActualThemeChanged += (_, _) => _root.Background = InboundTheme.Brush("InboundWindowBrush", _root);
        Content = _root;
        WindowLayout.ApplyChrome(this, _root);
        Closed += (_, _) => _open = null;
    }

    /// <summary>Opens the window, or brings the open one forward.</summary>
    public static void Show(RemoteClient client)
    {
        if (_open is null)
        {
            _open = new HosterWindow(client);
            _ = _open.LoadAsync();
        }

        _open.AppWindow.Show();
        if (_open.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        _open.Activate();
    }

    private async Task LoadAsync()
    {
        _reload.IsEnabled = false;
        _status.Show("Reading the host list…");
        try
        {
            var result = await _client.GetHostersAsync();
            if (!result.Ok)
            {
                _status.Say(RemoteText.Failure(result.Error), isProblem: true);
                return;
            }

            Title = $"{result.ProviderName ?? "Provider"} Hosters — Inbound Download Manager";
            _hosters = result.Hosters;
            Fill();
            var when = result.FetchedAt is { } at ? $" Read at {at.ToLocalTime():HH:mm}." : string.Empty;
            if (result.Problem is { } problem) _status.Say($"{problem}{when}", isProblem: true);
            else _status.Show($"{_hosters.Count(h => h.Available)} of {_hosters.Count} hosts up.{when}");
        }
        catch (Exception ex)
        {
            _status.Say(RemoteText.Failure(ex.Message), isProblem: true);
        }
        finally
        {
            _reload.IsEnabled = true;
        }
    }

    private void Fill()
    {
        var term = _search.Text.Trim();
        _rows.Children.Clear();
        foreach (var hoster in _hosters)
        {
            if (_downOnly.IsChecked == true && hoster.Available) continue;
            if (term.Length > 0 && !hoster.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !hoster.Domains.Any(domain => domain.Contains(term, StringComparison.OrdinalIgnoreCase))) continue;
            _rows.Children.Add(Row(hoster));
        }

        if (_rows.Children.Count == 0 && _hosters.Count > 0)
            _rows.Children.Add(new TextBlock { Text = "No host matches.", Opacity = 0.72, Margin = new Thickness(4, 8, 0, 0) });
    }

    private static UIElement Row(RemoteHosterDto hoster)
    {
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 6, 4, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

        if (Uri.TryCreate(hoster.IconUrl, UriKind.Absolute, out var icon) && icon.Scheme == Uri.UriSchemeHttps)
        {
            var image = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            image.Source = new BitmapImage(icon) { DecodePixelWidth = 40 };
            image.ImageFailed += (_, _) => image.Visibility = Visibility.Collapsed;
            row.Children.Add(image);
        }

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = hoster.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (Details(hoster) is { Length: > 0 } details)
            text.Children.Add(new TextBlock { Text = details, FontSize = 12, Opacity = 0.72, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(hoster.Note))
            text.Children.Add(new TextBlock { Text = hoster.Note, FontSize = 12, Opacity = 0.72, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var state = new TextBlock
        {
            Text = hoster.Available ? "Up" : "Down",
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = InboundTheme.Brush(hoster.Available ? "InboundStatusDoneBrush" : "InboundStatusFailedBrush", row),
        };
        Grid.SetColumn(state, 2);
        row.Children.Add(state);
        AutomationProperties.SetName(row, $"{hoster.Name}, {state.Text}");
        return row;
    }

    private static string Details(RemoteHosterDto hoster)
    {
        var parts = new List<string>();
        var linkLimit = hoster.DailyLinkLimit is > 0 ? hoster.DailyLinkLimit : null;
        if (hoster.DailyLinksUsed is { } used) parts.Add(linkLimit is { } limit ? $"Links today {used} of {limit}" : $"Links today {used}");
        else if (linkLimit is { } limit) parts.Add($"{limit} links a day");
        if (hoster.DailyBandwidthUsed is { } bytesUsed)
            parts.Add(hoster.DailyBandwidthLimit is { } bytesLimit and > 0
                ? $"Today {FolderChooser.Size(bytesUsed)} of {FolderChooser.Size(bytesLimit)}"
                : $"Today {FolderChooser.Size(bytesUsed)}");
        else if (hoster.DailyBandwidthLimit is { } dailyBytes and > 0) parts.Add($"{FolderChooser.Size(dailyBytes)} a day");
        if (hoster.PerLinkSizeLimit is { } perLink and > 0) parts.Add($"Up to {FolderChooser.Size(perLink)} a file");
        return string.Join(" · ", parts);
    }
}
