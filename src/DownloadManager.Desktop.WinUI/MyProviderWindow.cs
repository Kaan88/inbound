using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What the My provider window needs from the main window, which knows the local downloads and settings.</summary>
/// <param name="Settings">The Host's settings as last seen, or null before the first snapshot.</param>
/// <param name="LocalStatus">How this job's local downloads are doing, or null when none are known.</param>
/// <param name="RememberFolder">Records a folder as the last one used.</param>
/// <param name="Ui">The main window's saved layout, which this window's columns and order are part of.</param>
/// <param name="SaveUi">Saves that layout.</param>
internal sealed record MyProviderContext(
    Func<SettingsDto?> Settings,
    Func<RemoteJobDto, string?> LocalStatus,
    Action<string> RememberFolder,
    UiState Ui,
    Action SaveUi);

/// <summary>One line of the item list. Public because the list's bindings read it by reflection.</summary>
public sealed class RemoteItemRow : INotifyPropertyChanged
{
    private RemoteItemView _view;
    private RemoteItemDto _item;

    internal RemoteItemRow(RemoteItemDto item, RemoteItemView view)
    {
        _item = item;
        _view = view;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal RemoteItemDto Item => _item;
    internal RemoteItemView View => _view;

    /// <summary>The files, once read. A refreshed list reads them again.</summary>
    internal List<RemoteFileDto>? Files { get; set; }

    /// <summary>What was checked when this row's files were last on show. File ids only mean anything within one item.</summary>
    internal List<string> Chosen { get; set; } = [];

    public string Name => _view.Name;
    public string Type => _view.Type;
    public string Status => _view.Status;
    public string Size => _view.Size;
    public string FileCount => _view.Files;
    public string Added => _view.Added;
    public string Expires => _view.Expires;
    public string ToolTip => _view.ToolTip;
    public Microsoft.UI.Xaml.Media.Brush StatusBrush => JobViewModel.ToneBrush(_view.Tone);

    internal void Update(RemoteItemDto item, RemoteItemView view)
    {
        _item = item;
        if (_view == view) return;
        _view = view;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public override string ToString() => _view.AccessibleName;
}

/// <summary>
/// The account's own lists: one page per kind the provider has, the chosen item's files as a tree, and
/// a way to send them to the download list. Every call that waits on the provider goes through
/// <see cref="RemoteClient"/> on a connection of its own, so nothing here can hold up the main window,
/// and closing this window ends what is still running. Local progress is not shown here: those are
/// ordinary rows in the main list, in a package. Remote stages are words, never a progress bar.
/// </summary>
internal sealed class MyProviderWindow : Window
{
    private const int PageSize = 50;

    /// <summary>Header and a row or two, with Load more when it shows.</summary>
    private const double ListMinimumHeight = 110;

    /// <summary>The detail's own lines, each on one line, and about 120 for the file tree.</summary>
    private const double DetailMinimumHeight = 300;

    /// <summary>All six buttons fit in a row at this width.</summary>
    private const int WindowMinimumWidth = 880;

    /// <summary>What both minimums above need, with the rest of the window's lines and the title bar.</summary>
    private const int WindowMinimumHeight = 620;

    private const string ItemTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <Grid Height="27" Padding="6,0" ColumnSpacing="8" Background="Transparent" ToolTipService.ToolTip="{Binding ToolTip}">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="86" />
                    <ColumnDefinition Width="190" />
                    <ColumnDefinition Width="70" />
                    <ColumnDefinition Width="58" />
                    <ColumnDefinition Width="82" />
                    <ColumnDefinition Width="82" />
                </Grid.ColumnDefinitions>
                <TextBlock Text="{Binding Name}" FontSize="12" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                <TextBlock Grid.Column="1" Text="{Binding Type}" FontSize="11" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Foreground="{ThemeResource InboundSecondaryTextBrush}" />
                <TextBlock Grid.Column="2" Text="{Binding Status}" FontSize="11" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Foreground="{Binding StatusBrush}" />
                <TextBlock Grid.Column="3" Text="{Binding Size}" FontSize="11" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                <TextBlock Grid.Column="4" Text="{Binding FileCount}" FontSize="11" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Foreground="{ThemeResource InboundSecondaryTextBrush}" />
                <TextBlock Grid.Column="5" Text="{Binding Added}" FontSize="11" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Foreground="{ThemeResource InboundSecondaryTextBrush}" />
                <TextBlock Grid.Column="6" Text="{Binding Expires}" FontSize="11" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Foreground="{ThemeResource InboundSecondaryTextBrush}" />
            </Grid>
        </DataTemplate>
        """;

    private sealed class KindPage(string kind)
    {
        public string Kind { get; } = kind;
        public ObservableCollection<RemoteItemRow> Rows { get; } = [];
        public bool HasMore { get; set; }
        public int NextOffset { get; set; }
        public bool Loaded { get; set; }
        public string? SelectedItemId { get; set; }
        public CancellationTokenSource? Load { get; set; }

        /// <summary>The last load's failure, to show again on coming back to this page.</summary>
        public string? LoadFailure { get; set; }

        /// <summary>The key changed, so the next load must not be answered from what the old key read.</summary>
        public bool ReadFresh { get; set; }
    }

    private readonly RemoteClient _client;
    private readonly MyProviderContext _context;
    private readonly string _provider;
    private readonly string _name;
    private readonly CancellationTokenSource _closing = new();
    private readonly List<KindPage> _pages = [];
    private KindPage? _page;
    private RemoteItemRow? _selected;
    private CancellationTokenSource? _detailLoad;
    private bool _hasKey;
    private bool _acting;
    private int _loads;
    private bool _closed;
    private bool _settingSource;
    private bool _destinationFromJob;
    private string _freeDestination = string.Empty;
    private RemoteItemRow? _treeRow;
    private string _filesProblem = string.Empty;
    private readonly DispatcherTimer _localTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ListColumnLayout _columns = ListColumnSets.ProviderItems();
    private ColumnHeaderRow _header = null!;
    private string? _sortKey;
    private bool _sortAscending;
    private bool _reordering;

    private readonly Grid _root = new() { Padding = new Thickness(14), RowSpacing = 8 };
    private readonly SelectorBar _tabs = new();
    private readonly ProgressRing _busy = new() { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _refresh = new() { Content = "Refresh", MinWidth = 92 };
    private readonly Button _checkNow = new() { Content = "Check Now", MinWidth = 92 };
    private readonly Border _listFrame = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection(),
    };

    private readonly TextBlock _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(16) };
    private readonly Button _loadMore = new() { Content = "Load more", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly TextBlock _detailTitle = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detailLine = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StatusLine _detailStatus;
    private readonly StatusLine _problem;
    private readonly RemoteFileTreeView _tree = new();
    private readonly TextBox _destination = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _browse = new() { Content = "Browse…", MinWidth = 92 };
    private readonly StatusLine _destinationHint;
    private readonly StatusLine _status;
    private readonly Button _downloadSelected = new() { Content = "Download selected", MinWidth = 92, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly Button _downloadAll = new() { Content = "Download all", MinWidth = 92 };
    private readonly Button _delete = new() { MinWidth = 92, Visibility = Visibility.Collapsed };
    private readonly Button _close = new() { Content = "Close", MinWidth = 92 };

    public MyProviderWindow(RemoteClient client, RemoteAccountDto account, MyProviderContext context)
    {
        _client = client;
        _context = context;
        _provider = account.Provider;
        _name = RemoteText.ProviderName(account);
        _hasKey = RemoteRules.CanBrowse(account);
        _detailStatus = new StatusLine($"Status of the selected {_name} item");
        _problem = new StatusLine($"Problem with the selected {_name} item");
        _destinationHint = new StatusLine("Destination note");
        _status = new StatusLine($"My {_name} status");

        var theme = UiStateStore.Load().DarkMode;
        _root.RequestedTheme = theme is { } dark ? (dark ? ElementTheme.Dark : ElementTheme.Light) : ElementTheme.Default;
        Title = $"My {_name} — Inbound Download Manager";
        WindowLayout.SetMinimumSize(this, WindowMinimumWidth, WindowMinimumHeight);
        WindowLayout.ResizeForDpi(this, 1000, 720);
        WindowPlacement.Track(this, "my-provider");

        BuildLayout();
        foreach (var kind in account.ItemKinds)
        {
            var page = new KindPage(kind);
            _pages.Add(page);
            var tab = new SelectorBarItem { Text = RemoteText.KindHeading(kind), Tag = kind };
            AutomationProperties.SetName(tab, RemoteText.KindHeading(kind));
            _tabs.Items.Add(tab);
        }

        _tabs.SelectionChanged += (_, _) => ShowSelectedTab();
        _list.SelectionChanged += (_, _) => OnItemSelected();
        _refresh.Click += (_, _) => RefreshPage();
        _loadMore.Click += (_, _) => { if (_page is { } page) _ = LoadAsync(page, fresh: false, more: true); };
        _checkNow.Click += (_, _) => _ = CheckNowAsync();
        _tree.SelectionChanged += (_, _) => UpdateActions();
        _browse.Click += async (_, _) => await BrowseAsync();
        _destination.TextChanged += (_, _) =>
        {
            if (_settingSource) return;
            _freeDestination = _destination.Text;
            _destinationHint.Show(string.Empty);
        };
        _downloadSelected.Click += (_, _) => _ = DownloadAsync(all: false);
        _downloadAll.Click += (_, _) => _ = DownloadAsync(all: true);
        _delete.Click += (_, _) => _ = DeleteFromProviderAsync();
        _close.Click += (_, _) => Close();

        _client.State.AccountChanged += OnAccountChanged;
        _client.State.KeySaved += OnKeySaved;
        _client.State.Jobs.CollectionChanged += OnJobsChanged;
        Closed += (_, _) => OnClosed();

        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) => { args.Handled = true; Close(); };
        _root.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        _root.KeyboardAccelerators.Add(escape);
        var refresh = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.F5 };
        refresh.Invoked += (_, args) =>
        {
            args.Handled = true;
            if (_refresh.IsEnabled) RefreshPage();
        };
        _root.KeyboardAccelerators.Add(refresh);

        var settings = _context.Settings();
        _freeDestination = settings?.LastDestinationDirectory ?? settings?.DefaultDownloadDirectory ?? string.Empty;
        SetDestination(_freeDestination);

        // The Host doesn't announce a job's local progress, so what a linked item says is read here.
        _localTimer.Tick += (_, _) => RefreshLinked();
        _localTimer.Start();

        Content = _root;
        WindowLayout.ApplyChrome(this, _root);
        RenderDetail();
        if (_tabs.Items.Count > 0) _tabs.SelectedItem = _tabs.Items[0];
    }

    /// <summary>Follows the main window, whose theme the status colors in this list come from.</summary>
    internal void UseTheme(ElementTheme theme) => _root.RequestedTheme = theme;

    public void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
    }

    private void BuildLayout()
    {
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(1.25, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
        {
            _root.RowDefinitions.Add(new RowDefinition { Height = height });
        }

        // On a short window the list gives up its room first, so the file tree keeps enough rows to use.
        _root.RowDefinitions[1].MinHeight = ListMinimumHeight;
        _root.RowDefinitions[2].MinHeight = DetailMinimumHeight;

        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(_tabs);
        AddAt(top, _busy, column: 1);
        AddAt(top, _refresh, column: 2);
        AddAt(top, _checkNow, column: 3);
        AddAt(_root, top, row: 0);

        AutomationProperties.SetName(_tabs, $"{_name} item types");
        _tabs.Loaded += (_, _) => NameItemsList(_tabs);
        AutomationProperties.SetName(_busy, $"Asking {_name}");
        AutomationProperties.SetName(_refresh, $"Refresh the list from {_name}");
        ToolTipService.SetToolTip(_refresh, $"Read this list from {_name} again (F5)");
        AutomationProperties.SetName(_checkNow, $"Check {_name} now");
        AutomationProperties.SetName(_list, $"Items on your {_name} account");
        AutomationProperties.SetName(_loadMore, $"Load more items from {_name}");
        AutomationProperties.SetName(_destination, "Destination folder");
        AutomationProperties.SetName(_browse, "Browse for the destination folder");
        AutomationProperties.SetName(_downloadSelected, "Download the selected files");
        AutomationProperties.SetName(_downloadAll, "Download all files of this item");
        AutomationProperties.SetName(_close, $"Close My {_name}");
        _delete.Content = $"Delete from {_name}…";
        AutomationProperties.SetName(_delete, $"Delete this item from {_name}");
        ToolTipService.SetToolTip(_delete, "Asks first. It can't be undone.");

        // The list: same density as the main one. The header sets the widths, so that wide columns can scroll sideways.
        if (Application.Current.Resources.TryGetValue("InboundListRowStyle", out var rowStyle) && rowStyle is Style style) _list.ItemContainerStyle = style;
        _list.ItemTemplate = (DataTemplate)XamlReader.Load(ItemTemplate);
        var listGrid = new Grid { MinWidth = _columns.MinimumTotal };
        listGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
        listGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        listGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddAt(listGrid, BuildListHeader(), row: 0);
        var listArea = new Grid();
        listArea.Children.Add(_list);
        listArea.Children.Add(_empty);
        AddAt(listGrid, listArea, row: 1);
        AddAt(listGrid, _loadMore, row: 2);
        var sideways = new ScrollViewer
        {
            Content = listGrid,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        sideways.SizeChanged += (_, args) => listGrid.Width = args.NewSize.Width;
        _header.MinimumWidthChanged += width => listGrid.MinWidth = width;
        _header.ApplyToAll();
        _listFrame.Child = sideways;
        AddAt(_root, _listFrame, row: 1);

        // The chosen item.
        var detail = new Grid { RowSpacing = 4 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
        {
            detail.RowDefinitions.Add(new RowDefinition { Height = height });
        }

        AddAt(detail, _detailTitle, row: 0);
        AddAt(detail, _detailLine, row: 1);
        AddAt(detail, _detailStatus.Text, row: 2);
        AddAt(detail, _problem.Text, row: 3);
        AddAt(detail, _tree, row: 4);
        AutomationProperties.SetName(_tree, "Files of the selected item");
        _tree.Margin = new Thickness(0, 4, 0, 0);
        var folder = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        folder.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folder.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folder.Children.Add(new TextBlock { Text = "Destination folder", VerticalAlignment = VerticalAlignment.Center });
        AddAt(folder, _destination, column: 1);
        AddAt(folder, _browse, column: 2);
        AddAt(detail, folder, row: 5);
        AddAt(detail, _destinationHint.Text, row: 6);
        AddAt(_root, detail, row: 2);

        AddAt(_root, _status.Text, row: 3);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { _downloadSelected, _downloadAll, _delete, _close }) buttons.Children.Add(button);
        AddAt(_root, new ScrollViewer { Content = buttons, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled }, row: 4);

        void ApplyColors()
        {
            _root.Background = InboundTheme.Brush("InboundSurfaceBrush", _root);
            _listFrame.BorderBrush = InboundTheme.Brush("InboundDividerBrush", _root);
            _empty.Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", _root);
            _detailLine.Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", _root);
        }

        _root.Loaded += (_, _) => ApplyColors();
        _root.ActualThemeChanged += (_, _) => ApplyColors();
    }

    private FrameworkElement BuildListHeader()
    {
        var header = new Grid { Padding = new Thickness(6, 0, 6, 0), ColumnSpacing = 8 };
        var chrome = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        void ApplyColors()
        {
            chrome.Background = InboundTheme.Brush("InboundChromeBrush", _root);
            chrome.BorderBrush = InboundTheme.Brush("InboundDividerBrush", _root);
        }

        _root.Loaded += (_, _) => ApplyColors();
        _root.ActualThemeChanged += (_, _) => ApplyColors();

        var saved = _context.Ui;
        _columns.Import(saved.ProviderColumnWidths);
        _sortKey = ListColumnSets.IsProviderSort(saved.ProviderSortKey) ? saved.ProviderSortKey : null;
        _sortAscending = saved.ProviderSortAscending;
        _header = new ColumnHeaderRow(_columns, header, _list, _root, HeaderSort_Clicked, () =>
        {
            saved.ProviderColumnWidths = _columns.Export();
            _context.SaveUi();
        });
        if (_sortKey is not null) _header.ShowSort(_sortKey, _sortAscending);
        return chrome;
    }

    private void HeaderSort_Clicked(string key)
    {
        (_sortKey, _sortAscending) = _sortKey is { } current ? ListSort.Next(current, _sortAscending, key) : (key, ListSort.StartsAscending(key));
        _header.ShowSort(_sortKey, _sortAscending);
        _context.Ui.ProviderSortKey = _sortKey;
        _context.Ui.ProviderSortAscending = _sortAscending;
        _context.SaveUi();
        if (_page is not { } page) return;
        KeepingSelection(() => SortRows(page));
        ShowWhereTheOrderPuts(page);

        // One that is reading already goes on by itself when it ends, and starting another here would cancel it.
        if (_loads == 0 && RemoteItemSort.NeedsTheRest(_sortKey, page.HasMore, page.Rows.Count)) _ = LoadAsync(page, fresh: false, more: true);
    }

    /// <summary>The list keeps its place on whichever row was at the top, which would hide the new first rows.</summary>
    private void ShowWhereTheOrderPuts(KindPage page)
    {
        if (_list.SelectedItem is { } selected) _list.ScrollIntoView(selected);
        else if (page.Rows.Count > 0) _list.ScrollIntoView(page.Rows[0]);
    }

    /// <summary>Puts a page in the order the header asks for. With no order asked for, it stays as the provider answered.</summary>
    private void SortRows(KindPage page)
    {
        if (_sortKey is not { } key) return;
        var target = RemoteItemSort.Order(page.Rows.ToList(), row => row.Item, row => row.View, key, _sortAscending).ToList();
        for (var i = 0; i < target.Count; i++)
        {
            if (!ReferenceEquals(page.Rows[i], target[i])) page.Rows.Move(page.Rows.IndexOf(target[i]), i);
        }
    }

    /// <summary>A moved row can lose its selection for a moment, which must not read as another item being chosen and read its files again.</summary>
    private void KeepingSelection(Action reorder)
    {
        var selected = _selected;
        _reordering = true;
        try
        {
            reorder();
            if (selected is not null && _page?.Rows.Contains(selected) == true && !ReferenceEquals(_list.SelectedItem, selected)) _list.SelectedItem = selected;
        }
        finally
        {
            _reordering = false;
        }

        OnItemSelected();
    }

    private static void AddAt(Panel panel, FrameworkElement element, int row = 0, int column = 0)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        panel.Children.Add(element);
    }

    // ---- Pages -------------------------------------------------------------------------------------

    private void ShowSelectedTab()
    {
        if (_tabs.SelectedItem?.Tag is not string kind) return;
        var page = _pages.FirstOrDefault(candidate => candidate.Kind == kind);
        if (page is null) return;
        _page = page;
        SortRows(page);
        _list.ItemsSource = page.Rows;
        _list.SelectedItem = page.Rows.FirstOrDefault(row => row.Item.ItemId == page.SelectedItemId);
        OnItemSelected();
        UpdateEmptyState();
        if (!page.Loaded) _ = LoadAsync(page, fresh: false, more: false);
        else if (page.LoadFailure is { } failure) _status.Show(failure, isProblem: true);
        else
        {
            _status.Show(Counted(page));
            if (_loads == 0 && RemoteItemSort.NeedsTheRest(_sortKey, page.HasMore, page.Rows.Count)) _ = LoadAsync(page, fresh: false, more: true);
        }
    }

    private void RefreshPage()
    {
        if (_page is { } page) _ = LoadAsync(page, fresh: true, more: false);
    }

    private async Task LoadAsync(KindPage page, bool fresh, bool more)
    {
        if (_closed || !_hasKey) return;
        page.Load?.Cancel();
        var load = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        page.Load = load;
        BeginLoad();
        _status.Show($"Asking {_name}…");
        var readTheRest = false;
        try
        {
            var result = await _client.GetItemsAsync(new RemoteItemRequest
            {
                Provider = _provider,
                Kind = page.Kind,
                Offset = more ? page.NextOffset : 0,
                Limit = PageSize,
                Fresh = fresh || page.ReadFresh,
            }, load.Token);
            if (_closed || page.Load != load) return;
            var onShow = ReferenceEquals(page, _page);
            if (!result.Ok)
            {
                page.LoadFailure = RemoteText.Failure(result.Error, result.FailureKind, _name);
                if (onShow) _status.Show(page.LoadFailure, isProblem: true);
                UpdateEmptyState();
                return;
            }

            page.LoadFailure = null;
            page.ReadFresh = false;
            page.Loaded = true;
            page.HasMore = result.HasMore;
            page.NextOffset = (more ? page.NextOffset : 0) + result.Items.Count;
            KeepingSelection(() =>
            {
                Apply(page, result.Items, more);
                SortRows(page);
            });
            if (onShow && more && _sortKey is not null) ShowWhereTheOrderPuts(page);
            if (onShow) _status.Show(result.Items.Count == 0 && !more ? string.Empty : Counted(page));
            UpdateEmptyState();
            if (onShow && !more && _selected is not null) RenderDetail(readFiles: true);
            readTheRest = onShow && RemoteItemSort.NeedsTheRest(_sortKey, page.HasMore, result.Items.Count);
        }
        catch (OperationCanceledException)
        {
            // A newer load took over, or the window closed.
        }
        finally
        {
            EndLoad();
        }

        if (readTheRest && !_closed && page.Load == load) _ = LoadAsync(page, fresh: false, more: true);
    }

    private string Counted(KindPage page)
    {
        var shown = page.Rows.Count == 1 ? "1 item" : $"{page.Rows.Count} items";
        return page.HasMore ? $"{shown} so far. There may be more." : shown;
    }

    private void Apply(KindPage page, IReadOnlyList<RemoteItemDto> items, bool more)
    {
        var jobs = _client.State.Jobs;
        var known = page.Rows.ToDictionary(row => row.Item.ItemId, StringComparer.Ordinal);
        if (!more)
        {
            // In place, so the selection and the scroll position survive a refresh.
            var fresh = items.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
            for (var i = page.Rows.Count - 1; i >= 0; i--)
            {
                if (!fresh.Contains(page.Rows[i].Item.ItemId)) page.Rows.RemoveAt(i);
            }
        }

        var at = more ? page.Rows.Count : 0;
        foreach (var item in items)
        {
            var view = ViewOf(item, RemoteRules.JobFor(jobs, item));
            if (known.TryGetValue(item.ItemId, out var row) && (!more || page.Rows.Contains(row)))
            {
                row.Update(item, view);
                row.Files = null;
                if (!more)
                {
                    var current = page.Rows.IndexOf(row);
                    if (current >= 0 && current != at) page.Rows.Move(current, at);
                    at++;
                }

                continue;
            }

            page.Rows.Insert(Math.Min(at, page.Rows.Count), new RemoteItemRow(item, view));
            at++;
        }
    }

    private void UpdateEmptyState()
    {
        var page = _page;
        var rows = page?.Rows.Count ?? 0;
        _empty.Text = !_hasKey
            ? $"No usable {_name} API key is saved. Add it in Options."
            : page is { Loaded: true } && rows == 0
                ? RemoteText.EmptyKind(page.Kind, _name)
                : page is { Loaded: false } && _loads == 0
                    ? $"This list could not be read. Select Refresh to ask {_name} again."
                    : string.Empty;
        _empty.Visibility = _empty.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _loadMore.Visibility = page is { HasMore: true } && _hasKey ? Visibility.Visible : Visibility.Collapsed;
        UpdateActions();
    }

    private void BeginLoad()
    {
        _loads++;
        UpdateBusy();
        UpdateEmptyState();
    }

    private void EndLoad()
    {
        _loads--;
        UpdateBusy();
        UpdateEmptyState();
    }

    private void UpdateBusy()
    {
        var busy = _loads > 0 || _acting;
        _busy.IsActive = busy;
        _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- The chosen item -----------------------------------------------------------------------------

    private void OnItemSelected()
    {
        if (_reordering) return;
        var row = _list.SelectedItem as RemoteItemRow;
        if (ReferenceEquals(row, _selected)) return;
        _selected = row;
        if (_page is { } page && (row is not null || page.Rows.Count == 0)) page.SelectedItemId = row?.Item.ItemId;
        RenderDetail(readFiles: true);
    }

    private void RenderDetail(bool readFiles = false)
    {
        var row = _selected;
        if (row is null)
        {
            _detailLoad?.Cancel();
            ParkChoice();
            _filesProblem = string.Empty;
            _detailTitle.Text = "Select an item to see its files.";
            ToolTipService.SetToolTip(_detailTitle, null);
            _detailLine.Text = string.Empty;
            ToolTipService.SetToolTip(_detailLine, null);
            _detailStatus.Show(string.Empty);
            _problem.Show(string.Empty);
            _tree.ShowMessage(string.Empty);
            UpdateActions();
            return;
        }

        var job = RemoteRules.JobFor(_client.State.Jobs, row.Item);
        var view = ViewOf(row.Item, job);
        row.Update(row.Item, view);
        // This runs every few seconds, and setting a tooltip again would close one being read.
        if (_detailTitle.Text != view.Name)
        {
            _detailTitle.Text = view.Name;
            ToolTipService.SetToolTip(_detailTitle, view.Name);
        }

        if (_detailLine.Text != view.Details)
        {
            _detailLine.Text = view.Details;
            ToolTipService.SetToolTip(_detailLine, view.Details);
        }

        _detailStatus.Show(view.Status);
        _problem.Show(view.Problem.Length > 0 ? view.Problem : _filesProblem, isProblem: true);
        UpdateDestinationFor(job);
        UpdateActions();
        if (readFiles) _ = ReadFilesAsync(row);
    }

    private RemoteItemView ViewOf(RemoteItemDto item, RemoteJobDto? job)
        => RemoteText.Item(item, _name, job, job is { State: "localStarted" } ? _context.LocalStatus(job) : null);

    /// <summary>Keeps what is checked with the row it belongs to, before the tree shows anything else.</summary>
    private void ParkChoice()
    {
        if (_treeRow is { } row) row.Chosen = _tree.Tree.ChosenFileIds.ToList();
        _treeRow = null;
    }

    private async Task ReadFilesAsync(RemoteItemRow row)
    {
        _detailLoad?.Cancel();
        ParkChoice();
        _filesProblem = string.Empty;
        _problem.Show(row.View.Problem, isProblem: true);
        if (row.Files is { } known)
        {
            ShowFiles(row, known);
            return;
        }

        var load = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _detailLoad = load;
        _tree.ShowMessage($"Asking {_name} for the files…");
        BeginLoad();
        try
        {
            var result = await _client.GetItemsAsync(new RemoteItemRequest
            {
                Provider = _provider,
                Kind = row.Item.Kind,
                ItemId = row.Item.ItemId,
            }, load.Token);
            if (_closed || _detailLoad != load || !ReferenceEquals(row, _selected)) return;
            if (!result.Ok || result.Items.Count == 0)
            {
                _tree.ShowMessage(string.Empty);
                _filesProblem = RemoteText.Failure(result.Error, result.FailureKind, _name);
                _problem.Show(_filesProblem, isProblem: true);
                return;
            }

            var item = result.Items[0];
            row.Files = item.Files ?? [];
            row.Update(item, ViewOf(item, RemoteRules.JobFor(_client.State.Jobs, item)));
            RenderDetail();
            ShowFiles(row, row.Files);
        }
        catch (OperationCanceledException)
        {
            // Another item was chosen, or the window closed.
        }
        finally
        {
            EndLoad();
        }
    }

    private void ShowFiles(RemoteItemRow row, List<RemoteFileDto> files)
    {
        if (files.Count > 0)
        {
            _tree.SetFiles(files, keep: row.Chosen);
            _treeRow = row;
        }
        else
        {
            _tree.ShowMessage(row.Item.Stage == "ready"
                ? $"{_name} lists no files for this."
                : !row.View.CanDownload
                    ? $"This item failed on {_name}, so there are no files to download."
                    : $"{_name} doesn't list the files yet. Download all still works: it starts when {_name} has them.");
        }

        UpdateActions();
    }

    /// <summary>The bar has a list inside its template, which a screen reader would otherwise reach with no name.</summary>
    private void NameItemsList(DependencyObject parent)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is ItemsView list)
            {
                AutomationProperties.SetName(list, $"{_name} item types");
                return;
            }

            NameItemsList(child);
        }
    }

    private void UpdateActions()
    {
        var row = _selected;
        var job = row is null ? null : RemoteRules.JobFor(_client.State.Jobs, row.Item);
        var free = _hasKey && !_acting;
        var canDownload = free && row is { View.CanDownload: true };
        _refresh.IsEnabled = _hasKey && _loads == 0;
        _downloadSelected.IsEnabled = canDownload && _tree.Tree.ChosenCount > 0;
        _downloadAll.IsEnabled = canDownload;
        _loadMore.IsEnabled = _loads == 0;
        _tree.IsChoiceLocked = _acting;

        var checkable = _client.State.Jobs.Any(candidate => RemoteRules.SameProvider(candidate.Provider, _provider) && RemoteRules.Actions(candidate).CheckNow);
        _checkNow.IsEnabled = free && checkable;
        ToolTipService.SetToolTip(_checkNow, checkable
            ? $"Ask {_name} about your waiting items now, without waiting for the next check"
            : $"Nothing is waiting on {_name}, so there is nothing to check");

        // Only what the Host would take: a job that knows its item, with local files behind it for the rest.
        _delete.Visibility = RemoteRules.CanDeleteFromProvider(job) ? Visibility.Visible : Visibility.Collapsed;
        _delete.IsEnabled = free;
    }

    /// <summary>A job already on the list keeps its folder. Taking another one means removing it from the list first.</summary>
    private void UpdateDestinationFor(RemoteJobDto? job)
    {
        var active = job is not null && job.State != "failed";
        _destination.IsReadOnly = active;
        _browse.IsEnabled = !active;
        if (active)
        {
            _destinationFromJob = true;
            SetDestination(job!.Destination ?? string.Empty);
            _destinationHint.Show(job.State == "localStarted"
                ? "These downloads already have this folder. New files you select go there too."
                : "This item is already on the download list with this folder.");
        }
        else if (_destinationFromJob)
        {
            _destinationFromJob = false;
            SetDestination(_freeDestination);
            _destinationHint.Show(string.Empty);
        }
    }

    private void SetDestination(string text)
    {
        _settingSource = true;
        _destination.Text = text;
        _settingSource = false;
    }

    private async Task BrowseAsync()
    {
        _browse.IsEnabled = false;
        try
        {
            if (await Pickers.PickFolderAsync(WinRT.Interop.WindowNative.GetWindowHandle(this)) is { } folder) _destination.Text = folder;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            _destinationHint.Show("The folder picker couldn't open. Type the folder instead.", isProblem: true);
        }
        finally
        {
            _browse.IsEnabled = !_destination.IsReadOnly;
        }
    }

    // ---- Doing things --------------------------------------------------------------------------------

    private async Task DownloadAsync(bool all)
    {
        if (_selected is not { } row || _acting) return;
        var chosen = all ? null : _tree.Tree.ChosenFileIds.ToList();
        if (!all && chosen!.Count == 0) return;
        if (_tree.Tree.AllChosen)
        {
            all = true;
            chosen = null;
        }

        // An add past a job on the list would only return that job and ignore the choice.
        var job = RemoteRules.JobFor(_client.State.Jobs, row.Item);
        if (job is not null && job.State != "failed")
        {
            await RunAsync("Sending your choice…", async token =>
            {
                var result = await _client.SelectAsync(new RemoteSelectRequest { JobId = job.Id, All = all, FileIds = chosen }, token);
                return result.Ok
                    ? (string.Empty, false, true)
                    : (RemoteText.Failure(result.Error, null, _name), true, false);
            });
            return;
        }

        var destination = _destination.Text.Trim();
        if (destination.Length > 0 && !Path.IsPathFullyQualified(destination))
        {
            _destinationHint.Show("Enter a full folder path, such as D:\\Downloads, or leave this empty for the default folder.", isProblem: true);
            _destination.Focus(FocusState.Programmatic);
            return;
        }

        var settings = _context.Settings();
        await RunAsync("Adding to the download list…", async token =>
        {
            var result = await _client.AddAsync(new RemoteAddRequestDto
            {
                Provider = _provider,
                ItemKind = row.Item.Kind,
                ItemId = row.Item.ItemId,
                Destination = destination.Length > 0 ? destination : null,
                QueueId = settings?.DefaultQueueId ?? 0,
                StartMode = "startNow",
                Selection = all ? "all" : "chosen",
                FileIds = chosen,
            }, token);
            if (!result.Ok) return (RemoteText.Failure(result.Error, null, _name), true, false);
            if (destination.Length > 0) _context.RememberFolder(destination);
            // Started: the window has done its job and closes. Otherwise it stays and says why.
            if (RemoteRules.DownloadStarted(result)) return (string.Empty, false, true);
            if (!string.IsNullOrWhiteSpace(result.Note)) return (DownloadManager.Core.Providers.UrlRedactor.HideAddresses(result.Note) ?? string.Empty, true, false);
            var added = result.Job!;
            return (RemoteText.Failure(added.FailureMessage, added.FailureKind, _name), true, false);
        });
    }

    private async Task CheckNowAsync()
    {
        var job = _selected is { } row ? RemoteRules.JobFor(_client.State.Jobs, row.Item) : null;
        var one = job is not null && RemoteRules.Actions(job).CheckNow ? job : null;
        await RunAsync("Asking the Host to check…", async token =>
        {
            var result = await _client.CheckNowAsync(one?.Id, one is null ? _provider : null, token);
            return result.Ok
                ? ($"Checking {_name} now. The status above changes when it answers, unless {_name} has asked this app to wait.", false, false)
                : (RemoteText.Failure(result.Error, null, _name), true, false);
        });
    }

    private async Task DeleteFromProviderAsync()
    {
        if (_selected is not { } row || RemoteRules.JobFor(_client.State.Jobs, row.Item) is not { } job || !RemoteRules.CanDeleteFromProvider(job)) return;
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            RequestedTheme = _root.ActualTheme,
            Title = $"Delete from {_name}?",
            Content = new TextBlock { Text = RemoteText.DeleteQuestion(row.View.Name, _name), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = $"Delete from {_name}",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return;
        }

        var page = _page;
        await RunAsync($"Deleting from {_name}…", async token =>
        {
            var result = await _client.DeleteRemoteAsync(job.Id, confirmed: true, token);
            if (!result.Ok) return (RemoteText.Failure(result.Error, null, _name), true, false);
            page?.Rows.Remove(row);
            return ($"Deleted '{row.View.Name}' from {_name}.", false, false);
        });
        UpdateEmptyState();
    }

    private async Task RunAsync(string working, Func<CancellationToken, Task<(string Text, bool IsProblem, bool Done)>> call)
    {
        _acting = true;
        UpdateBusy();
        UpdateActions();
        _status.Show(working);
        try
        {
            var (text, isProblem, done) = await call(_closing.Token);
            if (done)
            {
                Close();
                return;
            }

            _status.Show(text, isProblem);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _acting = false;
            if (!_closed)
            {
                UpdateBusy();
                UpdateActions();
            }
        }
    }

    // ---- What the Host says meanwhile -----------------------------------------------------------------

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshLinked();

    /// <summary>Changes only what is different, so this can run often without the list blinking.</summary>
    private void RefreshLinked()
    {
        if (_closed) return;
        foreach (var row in _pages.SelectMany(page => page.Rows))
        {
            row.Update(row.Item, ViewOf(row.Item, RemoteRules.JobFor(_client.State.Jobs, row.Item)));
        }

        RenderDetail();
    }

    private void OnAccountChanged(RemoteAccountDto account)
    {
        if (_closed || !RemoteRules.SameProvider(account.Provider, _provider)) return;
        var had = _hasKey;
        _hasKey = RemoteRules.CanBrowse(account);
        if (had == _hasKey) return;
        if (!_hasKey)
        {
            _status.Show($"The {_name} API key is no longer usable, so this window can't read your account.", isProblem: true);
        }
        else if (_page is { } page)
        {
            _status.Show(string.Empty);
            _ = LoadAsync(page, fresh: false, more: false);
        }

        UpdateEmptyState();
    }

    /// <summary>
    /// A saved key can be another account's, and that shows in nothing the account says. So everything
    /// read with the old key goes: an old item id sent to the new account fails or finds something else.
    /// </summary>
    private void OnKeySaved(string? provider)
    {
        if (_closed || (provider is not null && !RemoteRules.SameProvider(provider, _provider))) return;
        _detailLoad?.Cancel();
        _list.SelectedItem = null;
        foreach (var page in _pages)
        {
            page.Load?.Cancel();
            page.Load = null;
            page.Rows.Clear();
            page.Loaded = false;
            page.HasMore = false;
            page.NextOffset = 0;
            page.SelectedItemId = null;
            page.LoadFailure = null;
            page.ReadFresh = true;
        }

        _selected = null;
        _treeRow = null;
        RenderDetail();
        _status.Show(string.Empty);
        if (_hasKey && _page is { } shown) _ = LoadAsync(shown, fresh: true, more: false);
        UpdateEmptyState();
    }

    private void OnClosed()
    {
        _closed = true;
        _client.State.AccountChanged -= OnAccountChanged;
        _client.State.KeySaved -= OnKeySaved;
        _client.State.Jobs.CollectionChanged -= OnJobsChanged;
        _localTimer.Stop();
        _closing.Cancel();
    }
}
