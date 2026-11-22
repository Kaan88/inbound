using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.11 R4, the <c>home</c> screen: the All remotes page. An address box to open once, "Add a remote", and the remotes as
/// a list styled like the main list (remote with its dot, kind, the folder on this PC, last used, and what runs). Double
/// click or Enter opens one; the context menu (also Shift+F10 and the menu key) is Open, Edit remote (F2), Test, Forget the
/// certificate where one is trusted, Remember for an address opened once, and Remove (Delete). Colours follow the theme, and
/// "Today" and "Yesterday" are redrawn at midnight while the page is shown. Built in code so it is the same on Linux.
/// </summary>
internal sealed class SitesHomePanel : UserControl
{
    private readonly TextBox _address = new() { PlaceholderText = SitesHomeRules.OpenHint, Padding = new Thickness(10, 6, 10, 6) };
    private readonly Button _open = new() { Content = "Open", MinWidth = 72 };
    private readonly Button _add = new() { Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 120 };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(2, 4, 0, 0) };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false, Padding = new Thickness(0) };
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 12, 14, 0) };
    private readonly Dictionary<string, Row> _rows = [];
    private List<SiteDto> _sites = [];
    private List<SiteTransferDto> _transfers = [];

    /// <summary>Open the remote.</summary>
    public event Action<SiteDto>? OpenRequested;

    /// <summary>The user typed an address to open once.</summary>
    public event Action<string>? OpenAddressRequested;

    public event Action? AddRequested;
    public event Action<SiteDto>? EditRequested;
    public event Action<SiteDto>? TestRequested;
    public event Action<SiteDto>? RemoveRequested;

    /// <summary>"Remember" an address opened once (<c>sites.keep</c>).</summary>
    public event Action<SiteDto>? RememberRequested;

    public event Action<SiteDto>? ForgetCertificateRequested;

    // Redraws "Today hh:mm" and "Yesterday" when the day changes, only while the page is shown.
    private readonly DispatcherTimer _midnight = new();
    private bool _shown;
    private bool _messageIsProblem;

    private sealed class Row
    {
        public required Grid Grid { get; init; }
        public required Microsoft.UI.Xaml.Shapes.Ellipse DotOk { get; init; }
        public required Microsoft.UI.Xaml.Shapes.Ellipse DotWarn { get; init; }
        public required TextBlock Name { get; init; }
        public required TextBlock Kind { get; init; }
        public required TextBlock Folder { get; init; }
        public required TextBlock Last { get; init; }
        public required TextBlock Activity { get; init; }
        public string Signature { get; set; } = "";
    }

    private static readonly GridLength[] Widths = [new(2, GridUnitType.Star), new(120), new(2, GridUnitType.Star), new(130), new(170)];

    public SitesHomePanel()
    {
        _add.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new FontIcon { Glyph = "", FontSize = 14 }, new TextBlock { Text = SitesHomeRules.AddRemote } },
        };
        AutomationProperties.SetName(_address, "Address to open once");
        AutomationProperties.SetName(_open, "Open the address");
        AutomationProperties.SetName(_add, SitesHomeRules.AddRemote);
        AutomationProperties.SetName(_list, SitesHomeRules.AllRemotes);
        AutomationProperties.SetLiveSetting(_message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        _address.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            OpenAddress();
        };
        _open.Click += (_, _) => OpenAddress();
        _add.Click += (_, _) => AddRequested?.Invoke();
        _list.DoubleTapped += (_, e) =>
        {
            // Only a double click on a row opens; one on the empty part of the list does not.
            if (SelectedSite is { } site && e.OriginalSource is FrameworkElement) OpenRequested?.Invoke(site);
        };
        _list.KeyDown += (_, e) =>
        {
            if (SelectedSite is not { } site) return;
            switch (e.Key)
            {
                case VirtualKey.Enter:
                    e.Handled = true;
                    OpenRequested?.Invoke(site);
                    break;
                case VirtualKey.F2 when SitesHomeRules.CanEdit(site):
                    e.Handled = true;
                    EditRequested?.Invoke(site);
                    break;
                case VirtualKey.Delete:
                    // Remove asks first, as the menu's Remove does.
                    e.Handled = true;
                    RemoveRequested?.Invoke(site);
                    break;
            }
        };
        // Shift+F10 and the menu key: the selected row's menu, at the row.
        _list.ContextRequested += (_, e) =>
        {
            if (_list.SelectedItem is not Grid grid || grid.ContextFlyout is not MenuFlyout menu) return;
            e.Handled = true;
            if (e.TryGetPosition(grid, out var point)) menu.ShowAt(grid, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
            else menu.ShowAt(grid);
        };
        _midnight.Tick += (_, _) =>
        {
            _midnight.Stop();
            Redraw();
        };

        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_open, 1);
        Grid.SetColumn(_add, 2);
        top.Children.Add(_address);
        top.Children.Add(_open);
        top.Children.Add(_add);

        var header = HeaderRow();
        var listBox = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new Grid
            {
                RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = new GridLength(1, GridUnitType.Star) } },
            },
        };
        var inner = (Grid)listBox.Child;
        Grid.SetRow(_list, 1);
        inner.Children.Add(header);
        inner.Children.Add(_list);

        var page = new Grid { Margin = new Thickness(10), RowSpacing = 8 };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.Children.Add(top);
        Grid.SetRow(_message, 1);
        page.Children.Add(_message);
        Grid.SetRow(listBox, 2);
        page.Children.Add(listBox);
        // Inside the list's box, under the headings (it used to sit over them).
        Grid.SetRow(_empty, 1);
        _empty.VerticalAlignment = VerticalAlignment.Top;
        inner.Children.Add(_empty);
        Content = page;

        InboundUi.Follow(listBox, "InboundDividerBrush", brush => listBox.BorderBrush = brush);
        InboundUi.Follow(_empty, "InboundSecondaryTextBrush", brush => _empty.Foreground = brush);
        InboundUi.Follow(_message, "InboundErrorTextBrush", brush =>
        {
            if (_messageIsProblem) _message.Foreground = brush;
        });
    }

    /// <summary>The page is shown (true) or left: the midnight redraw runs only while it is shown.</summary>
    public void SetShown(bool shown)
    {
        _shown = shown;
        if (shown) ScheduleMidnight();
        else _midnight.Stop();
    }

    /// <summary>One timer, set for just after the next local midnight, when "Today" turns into "Yesterday".</summary>
    private void ScheduleMidnight()
    {
        _midnight.Stop();
        if (!_shown) return;
        var now = DateTime.Now;
        _midnight.Interval = now.Date.AddDays(1).AddSeconds(1) - now;
        _midnight.Start();
    }

    private void Redraw()
    {
        foreach (var row in _rows.Values) row.Signature = "";
        Update(_sites, _transfers);
        ScheduleMidnight();
    }

    private Grid HeaderRow()
    {
        var grid = new Grid { Padding = new Thickness(14, 8, 14, 8), ColumnSpacing = 8 };
        string[] names = ["Remote", "Kind", "Folder on this PC", "Last used", ""];
        for (var i = 0; i < Widths.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = Widths[i] });
            var text = new TextBlock { Text = names[i], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12 };
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }

        AutomationProperties.SetAccessibilityView(grid, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return grid;
    }

    private SiteDto? SelectedSite => (_list.SelectedItem as Grid)?.Tag as SiteDto;

    /// <summary>Gives the address box focus (the page was just opened).</summary>
    public void FocusAddress() => _address.Focus(FocusState.Programmatic);

    /// <summary>The line under the address box: a reason the address was not opened, or nothing.</summary>
    public void SayMessage(string? text, bool isProblem = true)
    {
        _message.Text = text ?? string.Empty;
        _message.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        _messageIsProblem = isProblem;
        if (isProblem) _message.Foreground = InboundTheme.Brush("InboundErrorTextBrush", _message);
        else _message.ClearValue(TextBlock.ForegroundProperty);
    }

    public bool AddEnabled
    {
        get => _add.IsEnabled;
        set => _add.IsEnabled = value;
    }

    private void OpenAddress()
    {
        var text = _address.Text.Trim();
        if (text.Length == 0)
        {
            SayMessage(SitesHomeRules.OpenHint.Replace("Open an address once, for example", "Type an address such as"));
            return;
        }

        SayMessage(null);
        OpenAddressRequested?.Invoke(text);
    }

    /// <summary>Clears the address box once it opened.</summary>
    public void ClearAddress() => _address.Text = string.Empty;

    /// <summary>Shows the remotes and what runs on them. Rows keep their place and selection when only the activity moved.</summary>
    public void Update(IReadOnlyList<SiteDto> sites, IReadOnlyList<SiteTransferDto> transfers)
    {
        _sites = [.. sites];
        _transfers = [.. transfers];
        var selectedId = SelectedSite?.Id;
        var now = DateTimeOffset.Now;
        var rebuild = _list.Items.Count != _sites.Count || _list.Items.OfType<Grid>().Select(g => (g.Tag as SiteDto)?.Id).SequenceEqual(_sites.Select(s => s.Id)) == false;
        if (rebuild)
        {
            _list.Items.Clear();
            // New rows each time the set changes: a row is never moved from one list container to another.
            _rows.Clear();
            foreach (var site in _sites)
            {
                var row = BuildRow();
                _rows[site.Id] = row;
                row.Grid.Tag = site;
                _list.Items.Add(row.Grid);
            }
        }

        foreach (var site in _sites)
        {
            var row = _rows[site.Id];
            row.Grid.Tag = site;
            var badge = SitesHomeRules.Badge(_transfers, site.Id);
            var activity = SitesHomeRules.ActivityText(badge) ?? string.Empty;
            var trouble = SitesHomeRules.InTrouble(site);
            var signature = $"{site.Name}|{site.Kind}|{site.LocalFolder}|{site.LastUsedAt?.UtcTicks}|{activity}|{trouble}|{SitesHomeRules.DotWords(site)}|{site.Unsaved}|{site.TrustedCertificates.Count}";
            if (signature == row.Signature && !rebuild) continue;
            row.Signature = signature;
            row.Name.Text = site.Name;
            row.Kind.Text = SitesHomeRules.KindText(site.Kind);
            row.Folder.Text = site.LocalFolder ?? string.Empty;
            row.Last.Text = SitesHomeRules.LastUsedText(site.LastUsedAt, now);
            row.Activity.Text = activity;
            row.DotOk.Visibility = trouble ? Visibility.Collapsed : Visibility.Visible;
            row.DotWarn.Visibility = trouble ? Visibility.Visible : Visibility.Collapsed;
            var words = SitesHomeRules.ActivityWords(badge);
            AutomationProperties.SetName(row.Grid, $"{site.Name}, {SitesHomeRules.KindText(site.Kind)}, {SitesHomeRules.DotWords(site)}, last used {row.Last.Text}{(words is null ? "" : ", " + words)}");
        }

        if (rebuild && selectedId is not null && _list.Items.OfType<Grid>().FirstOrDefault(g => (g.Tag as SiteDto)?.Id == selectedId) is { } again) _list.SelectedItem = again;
        _empty.Text = _sites.Count == 0 ? $"No remotes yet. Press \"{SitesHomeRules.AddRemote}\" for a server or a cloud drive, or type an address above to open one once." : string.Empty;
        _empty.Visibility = _sites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Row BuildRow()
    {
        var grid = new Grid { Padding = new Thickness(14, 8, 14, 8), ColumnSpacing = 8, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        foreach (var width in Widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        var dotOk = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        var dotWarn = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        InboundUi.Follow(dotOk, "InboundStatusDoneBrush", brush => dotOk.Fill = brush);
        InboundUi.Follow(dotWarn, "InboundStatusWaitingBrush", brush => dotWarn.Fill = brush);
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var dots = new Grid { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        dots.Children.Add(dotOk);
        dots.Children.Add(dotWarn);
        nameRow.Children.Add(dots);
        nameRow.Children.Add(name);

        var kind = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var chip = new Border
        {
            Child = kind,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 1, 8, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        InboundUi.Follow(chip, "InboundDividerBrush", brush => chip.BorderBrush = brush);
        InboundUi.Follow(kind, "InboundSecondaryTextBrush", brush => kind.Foreground = brush);
        var folder = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var last = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var activity = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        InboundUi.Follow(activity, "InboundProgressFillBrush", brush => activity.Foreground = brush);
        InboundUi.Follow(folder, "InboundSecondaryTextBrush", brush => folder.Foreground = brush);
        InboundUi.Follow(last, "InboundSecondaryTextBrush", brush => last.Foreground = brush);

        UIElement[] cells = [nameRow, chip, folder, last, activity];
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn((FrameworkElement)cells[i], i);
            grid.Children.Add(cells[i]);
        }

        grid.ContextFlyout = BuildMenu(grid);
        // A right click selects the row first, as the main list does.
        grid.RightTapped += (_, _) => _list.SelectedItem = grid;
        return new Row { Grid = grid, DotOk = dotOk, DotWarn = dotWarn, Name = name, Kind = kind, Folder = folder, Last = last, Activity = activity };
    }

    private MenuFlyout BuildMenu(Grid grid)
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, Action<SiteDto> run)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) =>
            {
                if (grid.Tag is SiteDto site) run(site);
            };
            return item;
        }

        menu.Items.Add(Item("Open", site => OpenRequested?.Invoke(site)));
        var edit = Item("Edit remote", site => EditRequested?.Invoke(site));
        edit.KeyboardAcceleratorTextOverride = "F2";
        menu.Items.Add(edit);
        var remember = Item("Remember", site => RememberRequested?.Invoke(site));
        menu.Items.Add(remember);
        menu.Items.Add(Item("Test", site => TestRequested?.Invoke(site)));
        var forget = Item("Forget the certificate", site => ForgetCertificateRequested?.Invoke(site));
        menu.Items.Add(forget);
        menu.Items.Add(new MenuFlyoutSeparator());
        var remove = Item("Remove", site => RemoveRequested?.Invoke(site));
        remove.KeyboardAcceleratorTextOverride = "Delete";
        menu.Items.Add(remove);
        // What the row's remote allows, read as the menu opens: an address opened once is remembered, not edited.
        menu.Opening += (_, _) =>
        {
            if (grid.Tag is not SiteDto site) return;
            edit.IsEnabled = SitesHomeRules.CanEdit(site);
            remember.Visibility = site.Unsaved ? Visibility.Visible : Visibility.Collapsed;
            forget.Visibility = SitesHomeRules.CanForgetCertificate(site) ? Visibility.Visible : Visibility.Collapsed;
        };
        return menu;
    }
}
