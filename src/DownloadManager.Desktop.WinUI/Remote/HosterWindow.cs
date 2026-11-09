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
    // UI.R31: each host is a card row with an Up or Down chip.
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly Button _close = new() { Content = "Close", MinWidth = 92 };
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
        // UI.R31: the footer, as in every secondary window: what the list says on the left, Close on the right.
        _status.Text.Style = InboundUi.Style("InboundSecondaryTextStyle");
        _status.Text.VerticalAlignment = VerticalAlignment.Center;
        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_status.Text);
        Grid.SetColumn(_close, 1);
        footer.Children.Add(_close);
        Grid.SetRow(footer, 2);
        _root.Children.Add(footer);
        _close.Click += (_, _) => Close();
        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) => { args.Handled = true; Close(); };
        _root.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        _root.KeyboardAccelerators.Add(escape);

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
            _rows.Children.Add(InboundUi.EmptyState("\uE721", "No host matches."));
    }

    private static UIElement Row(RemoteHosterDto hoster)
    {
        var row = new Grid { ColumnSpacing = 10 };
        // The icon's column exists only when there is an icon, so a row without one isn't indented.
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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
            text.Children.Add(InboundUi.Secondary(details));
        if (!string.IsNullOrWhiteSpace(hoster.Note))
            text.Children.Add(InboundUi.Secondary(hoster.Note));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var state = new StatusChip(hoster.Available ? "Up" : "Down", hoster.Available ? "InboundStatusDoneBrush" : "InboundStatusFailedBrush");
        state.Element.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(state.Element, 2);
        row.Children.Add(state.Element);
        var card = InboundUi.Card(row, new Thickness(12, 8, 12, 8));
        AutomationProperties.SetName(card, $"{hoster.Name}, {state.Text.Text}");
        return card;
    }

    private static string Details(RemoteHosterDto hoster)
    {
        var parts = new List<string>();
        var linkLimit = hoster.DailyLinkLimit is > 0 ? hoster.DailyLinkLimit : null;
        if (hoster.DailyLinksUsed is { } used) parts.Add(linkLimit is { } limit ? $"Links today {used} of {limit}" : $"Links today {used}");
        else if (linkLimit is { } limit) parts.Add($"{limit} links a day");
        if (hoster.DailyBandwidthUsed is { } bytesUsed)
            parts.Add(hoster.DailyBandwidthLimit is { } bytesLimit and > 0
                ? $"Today {UiFormat.Bytes(bytesUsed)} of {UiFormat.Bytes(bytesLimit)}"
                : $"Today {UiFormat.Bytes(bytesUsed)}");
        else if (hoster.DailyBandwidthLimit is { } dailyBytes and > 0) parts.Add($"{UiFormat.Bytes(dailyBytes)} a day");
        if (hoster.PerLinkSizeLimit is { } perLink and > 0) parts.Add($"Up to {UiFormat.Bytes(perLink)} a file");
        return string.Join(" · ", parts);
    }
}
