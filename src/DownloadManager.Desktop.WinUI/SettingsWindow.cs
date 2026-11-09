using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Classic desktop settings window: page list on the left, one scrolling page on the right and
/// OK / Cancel / Apply at the bottom right. Validation errors are shown inline in the footer and
/// jump to the offending page instead of closing the window and losing edits.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly Grid _root = new();
    // Empty transitions: the default entrance animation slid the page list up on open.
    private readonly ListView _nav = new()
    {
        SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0, 6, 0, 6),
        ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection(),
    };
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, IsTabStop = false };
    private readonly StatusLine _error = new("Settings message");
    private readonly Button _ok = new() { Content = "OK", MinWidth = 92, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly Button _cancel = new() { Content = "Cancel", MinWidth = 92 };
    private readonly Button _apply = new() { Content = "Apply", MinWidth = 92 };
    private readonly List<UIElement> _pages = [];
    private readonly TaskCompletionSource _closed = new();

    /// <summary>Validates and saves. Returns null on success, otherwise the message and the page to show.</summary>
    public Func<Task<(string? Error, int Page)>>? Save { get; set; }

    public SettingsWindow()
    {
        Title = "Options — Inbound Download Manager";
        WindowLayout.ResizeForDpi(this, 860, 620);
        WindowPlacement.Track(this, "options");
        AppTheme.Track(_root);
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_nav, "Options pages");
        var navBorder = new Border { Child = _nav, BorderThickness = new Thickness(0, 0, 1, 0) };
        _root.Children.Add(navBorder);
        Grid.SetColumn(_page, 1);
        _root.Children.Add(_page);

        var footer = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 8, BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _error.Text.VerticalAlignment = VerticalAlignment.Center;
        _error.Text.MaxLines = 3;
        footer.Children.Add(_error.Text);
        Grid.SetColumn(_ok, 1); footer.Children.Add(_ok);
        Grid.SetColumn(_cancel, 2); footer.Children.Add(_cancel);
        Grid.SetColumn(_apply, 3); footer.Children.Add(_apply);
        Grid.SetRow(footer, 1); Grid.SetColumnSpan(footer, 2);
        _root.Children.Add(footer);

        void ApplyColors()
        {
            _root.Background = InboundTheme.Brush("InboundSurfaceBrush", _root);
            var chrome = InboundTheme.Brush("InboundChromeBrush", _root);
            var divider = InboundTheme.Brush("InboundDividerBrush", _root);
            navBorder.Background = chrome; navBorder.BorderBrush = divider;
            footer.Background = chrome; footer.BorderBrush = divider;
        }
        _root.Loaded += (_, _) => ApplyColors();
        _root.ActualThemeChanged += (_, _) => ApplyColors();

        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedIndex >= 0 && _nav.SelectedIndex < _pages.Count) _page.Content = _pages[_nav.SelectedIndex];
        };
        _ok.Click += async (_, _) => { if (await RunSaveAsync()) Close(); };
        _apply.Click += async (_, _) => await RunSaveAsync();
        _cancel.Click += (_, _) => Close();
        Content = _root;
        WindowLayout.ApplyChrome(this, _root);
        Closed += (_, _) => _closed.TrySetResult();
        _root.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        _root.KeyboardAccelerators.Add(Accelerator(Windows.System.VirtualKey.Escape, () => Close()));
    }

    public Task WaitClosedAsync() => _closed.Task;

    public void AddPage(string title, string glyph, params UIElement[] controls)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(4, 2, 4, 2) };
        item.Children.Add(new FontIcon { Glyph = glyph, FontSize = 15 });
        item.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        var navItem = new ListViewItem { Content = item, MinHeight = 34 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(navItem, title);
        _nav.Items.Add(navItem);

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(24, 18, 28, 24), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var control in controls) panel.Children.Add(control);
        var scroller = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        // Tab stops on it, to scroll with the keys.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(scroller, $"{title} page");
        _pages.Add(scroller);
        if (_nav.SelectedIndex < 0) _nav.SelectedIndex = 0;
    }

    /// <summary>Takes back what the footer says, once what it complained about is dealt with.</summary>
    public void ClearMessage() => _error.Show(string.Empty);

    public void ShowPage(int index)
    {
        if (index >= 0 && index < _pages.Count) _nav.SelectedIndex = index;
    }

    /// <summary>Section heading inside a page.</summary>
    public static TextBlock Section(string text) => new()
    {
        Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 10, 0, -4),
    };

    /// <summary>Secondary explanatory text under a section.</summary>
    public static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.72, FontSize = 12 };

    /// <summary>A folder text box with a Browse… button that opens the folder picker over this window.</summary>
    public (UIElement Row, TextBox Box) FolderField(string header, string? text, string? placeholder = null, bool inlineLabel = false, bool favorites = true)
    {
        var box = new TextBox { Header = inlineLabel ? null : header, Text = text ?? string.Empty, PlaceholderText = placeholder ?? string.Empty };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, header);
        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom, MinWidth = 92 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(browse, "Browse for " + header);
        browse.Click += async (_, _) =>
        {
            try
            {
                if (await Pickers.PickFolderAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), startInDownloads: false, start: box.Text) is { } folder) box.Text = folder;
            }
            catch (Exception ex)
            {
                _error.Say($"Could not open the folder picker: {ex.Message}", isProblem: true);
            }
        };
        var row = new Grid { ColumnSpacing = 8 };
        if (inlineLabel)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            var label = new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(label);
        }
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var offset = inlineLabel ? 1 : 0;
        Grid.SetColumn(box, offset);
        row.Children.Add(box);
        Grid.SetColumn(browse, offset + 1);
        row.Children.Add(browse);
        return (favorites ? FolderChooser.Attach(row, box) : row, box);
    }

    public static NumberBox Number(string header, double? value, double min, double max, string? placeholder = null) => new()
    {
        Header = header,
        Minimum = min,
        Maximum = max,
        Value = value ?? double.NaN,
        PlaceholderText = placeholder ?? string.Empty,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        HorizontalAlignment = HorizontalAlignment.Left,
        Width = 220,
    };

    private async Task<bool> RunSaveAsync()
    {
        if (Save is null) return true;
        _ok.IsEnabled = _apply.IsEnabled = false;
        _error.Show(string.Empty);
        try
        {
            var (error, page) = await Save();
            if (error is null) return true;
            _error.Say(error, isProblem: true);
            ShowPage(page);
            return false;
        }
        catch (Exception ex)
        {
            _error.Say(ex.Message, isProblem: true);
            return false;
        }
        finally
        {
            _ok.IsEnabled = _apply.IsEnabled = true;
        }
    }

    private static Microsoft.UI.Xaml.Input.KeyboardAccelerator Accelerator(Windows.System.VirtualKey key, Action action)
    {
        var accelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, args) => { args.Handled = true; action(); };
        return accelerator;
    }
}
