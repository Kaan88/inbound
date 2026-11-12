using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What a provider panel needs from the main window, which knows the local downloads and settings.</summary>
/// <param name="Settings">The Host's settings as last seen, or null before the first snapshot.</param>
/// <param name="LocalFiles">The local downloads behind a job, as the main list has them.</param>
/// <param name="RememberFolder">Records a folder as the last one used.</param>
/// <param name="Ui">The main window's saved layout, which the panel's columns and order are part of.</param>
/// <param name="SaveUi">Saves that layout.</param>
/// <param name="WindowHandle">The window the panel is in just now, for the folder picker.</param>
internal sealed record ProviderPanelContext(
    Func<SettingsDto?> Settings,
    Func<RemoteJobDto, IReadOnlyCollection<JobDto>> LocalFiles,
    Action<string> RememberFolder,
    UiState Ui,
    Action SaveUi,
    Func<IntPtr> WindowHandle);

/// <summary>UI.R32: how the panel is laid out. Full is the main window's sidebar view (A) and the window of its own; Drawer is C.</summary>
internal enum ProviderPanelMode
{
    Full,
    Drawer,
}

/// <summary>What a Download sent to the list, for the notice there ("Added 4 files from …").</summary>
internal sealed record ProviderDownloadNotice(string Provider, string Kind, string ItemId, Guid RemoteJobId, string Text, bool FromDrawer);

/// <summary>One line of the item list. Public because the list's bindings read it by reflection.</summary>
public sealed class RemoteItemRow : INotifyPropertyChanged
{
    private RemoteItemView _view;
    private RemoteItemDto _item;
    private string? _onList;
    private object? _expansion;

    internal RemoteItemRow(RemoteItemDto item, RemoteItemView view, string? onList = null)
    {
        _item = item;
        _view = view;
        _onList = onList;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal RemoteItemDto Item => _item;
    internal RemoteItemView View => _view;

    /// <summary>The files, once read. A refreshed list reads them again.</summary>
    internal List<RemoteFileDto>? Files { get; set; }

    /// <summary>What was checked when this row's files were last on show. File ids only mean anything within one item.</summary>
    internal List<string> Chosen { get; set; } = [];
    /// <summary>Its files have been shown once; from then on the list keeps what was chosen, even nothing.</summary>
    internal bool FilesShown { get; set; }

    public string Name => _view.Name;
    public string Type => _view.Type;
    public string Status => _view.Status;
    public string Size => _view.Size;
    public string FileCount => _view.Files;
    public string Added => _view.Added;
    public string Expires => _view.Expires;
    public string ToolTip => _view.ToolTip;
    public Microsoft.UI.Xaml.Media.Brush StatusBrush => JobViewModel.ToneBrush(_view.Tone);

    /// <summary>A card's line before the status: how many files and how big, as far as the provider says.</summary>
    public string CardLine
    {
        get
        {
            var parts = new List<string>();
            if (_view.Files.Length > 0) parts.Add(_view.Files == "1" ? "1 file" : $"{_view.Files} files");
            if (_view.Size.Length > 0) parts.Add(_view.Size);
            return parts.Count == 0 ? string.Empty : string.Join(" · ", parts) + " ·";
        }
    }

    /// <summary>UI.R32: "On list · 31%" once its downloads are on the download list.</summary>
    public string OnList => _onList ?? string.Empty;
    public Visibility OnListVisibility => _onList is null ? Visibility.Collapsed : Visibility.Visible;
    public Microsoft.UI.Xaml.Media.Brush OnListBrush => JobViewModel.ToneBrush(_onList?.EndsWith("done", StringComparison.Ordinal) == true ? RemoteRowTone.Done : RemoteRowTone.Active);

    /// <summary>In the drawer, the chosen card holds its files here; every other card holds nothing.</summary>
    public object? Expansion
    {
        get => _expansion;
        set
        {
            if (ReferenceEquals(_expansion, value)) return;
            _expansion = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Expansion)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chevron)));
        }
    }

    public string Chevron => _expansion is null ? "" : "";

    internal void Update(RemoteItemDto item, RemoteItemView view, string? onList)
    {
        _item = item;
        if (_view == view && _onList == onList) return;
        _view = view;
        _onList = onList;
        // Each by name: an empty name would also read Expansion again, and that is the one thing that must not move.
        foreach (var name in new[] { nameof(Name), nameof(Type), nameof(Status), nameof(Size), nameof(FileCount), nameof(Added), nameof(Expires), nameof(ToolTip), nameof(StatusBrush), nameof(CardLine), nameof(OnList), nameof(OnListVisibility), nameof(OnListBrush) })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public override string ToString() => _onList is null ? _view.AccessibleName : $"{_view.AccessibleName}, {_onList}";
}

/// <summary>
/// UI.R32: the account's own lists in one reusable panel: one tab per kind the provider has, the chosen item's files
/// as a tree, and a way to send them to the download list. The main window shows it in place of the list (A) or as a
/// drawer (C); a window of its own can hold it too. Every call that waits on the provider goes through
/// <see cref="RemoteClient"/> on a connection of its own, so nothing here can hold up the main window. Nothing reads
/// or ticks while the panel is hidden (<see cref="SetShown"/>). Remote stages are words, never a progress bar.
/// </summary>
internal sealed class ProviderPanel
{
    private const int PageSize = 50;

    /// <summary>The details pane's width in the full view.</summary>
    private const double DetailWidth = 360;

    /// <summary>How tall a card's file tree may get before it scrolls.</summary>
    private const double CardTreeHeight = 300;

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
                <Grid ColumnSpacing="6">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Text="{Binding Name}" FontSize="12" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                    <Border Grid.Column="1" Visibility="{Binding OnListVisibility}" CornerRadius="9" BorderThickness="1" Padding="6,0,6,1" VerticalAlignment="Center" BorderBrush="{Binding OnListBrush}">
                        <TextBlock Text="{Binding OnList}" FontSize="10.5" Foreground="{Binding OnListBrush}" />
                    </Border>
                </Grid>
                <TextBlock Grid.Column="1" Text="{Binding Type}" FontSize="11" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Foreground="{ThemeResource InboundSecondaryTextBrush}" />
                <TextBlock Grid.Column="2" Text="{Binding Status}" FontSize="11" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Foreground="{Binding StatusBrush}" />
                <TextBlock Grid.Column="3" Text="{Binding Size}" FontSize="11" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                <TextBlock Grid.Column="4" Text="{Binding FileCount}" FontSize="11" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Foreground="{ThemeResource InboundSecondaryTextBrush}" />
                <TextBlock Grid.Column="5" Text="{Binding Added}" FontSize="11" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Foreground="{ThemeResource InboundSecondaryTextBrush}" />
                <TextBlock Grid.Column="6" Text="{Binding Expires}" FontSize="11" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Foreground="{ThemeResource InboundSecondaryTextBrush}" />
            </Grid>
        </DataTemplate>
        """;

    /// <summary>The drawer's two-line card. Its root is not a Grid, so the column header never sizes it.</summary>
    private const string CardTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <Border Padding="6,6,8,6" Background="Transparent" ToolTipService.ToolTip="{Binding ToolTip}">
                <Grid ColumnSpacing="6">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <FontIcon Glyph="{Binding Chevron}" FontSize="10" VerticalAlignment="Top" Margin="0,5,0,0" Foreground="{ThemeResource InboundSecondaryTextBrush}" />
                    <StackPanel Grid.Column="1" Spacing="2">
                        <TextBlock Text="{Binding Name}" FontSize="12" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                        <Grid ColumnSpacing="6">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <TextBlock Text="{Binding CardLine}" FontSize="11" Foreground="{ThemeResource InboundSecondaryTextBrush}" />
                            <TextBlock Grid.Column="1" Text="{Binding Status}" FontSize="11" Foreground="{Binding StatusBrush}" TextTrimming="CharacterEllipsis" />
                            <Border Grid.Column="2" Visibility="{Binding OnListVisibility}" CornerRadius="9" BorderThickness="1" Padding="6,0,6,1" BorderBrush="{Binding OnListBrush}">
                                <TextBlock Text="{Binding OnList}" FontSize="10.5" Foreground="{Binding OnListBrush}" />
                            </Border>
                        </Grid>
                        <ContentControl Content="{Binding Expansion}" IsTabStop="False" HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch" />
                    </StackPanel>
                </Grid>
            </Border>
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
    private readonly ProviderPanelContext _context;
    private readonly bool _inMainWindow;
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
    private bool _shown;
    private bool _settingSource;
    private bool _destinationFromJob;
    private string _freeDestination = string.Empty;
    private RemoteItemRow? _treeRow;
    private string _filesProblem = string.Empty;
    private string? _pendingTop;
    private readonly DispatcherTimer _localTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ListColumnLayout _columns = ListColumnSets.ProviderItems();
    private ColumnHeaderRow _header = null!;
    private double _headerMinimumWidth;
    private string? _sortKey;
    private bool _sortAscending;
    private bool _reordering;
    private ProviderPanelMode _mode = ProviderPanelMode.Full;
    private bool _pinned;

    private readonly Grid _root = new();
    private readonly Grid _full = new() { Padding = new Thickness(16, 12, 16, 14), RowSpacing = 10 };
    private readonly Grid _drawer = new() { Padding = new Thickness(12, 10, 12, 12), RowSpacing = 8 };
    private readonly StackPanel _expansion = new() { Spacing = 4, Margin = new Thickness(0, 6, 0, 2) };
    private readonly List<(UIElement Piece, Border Full, Border Drawer)> _slots = [];

    private readonly SelectorBar _tabs = new();
    private readonly ProgressRing _busy = new() { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _refresh = InboundUi.IconButton("", "Refresh", "InboundIconInfoBrush");
    private readonly Button _checkNow = new() { Content = "Check now", MinWidth = 92 };
    private readonly Button _hosters = new() { Content = "Hosters", MinWidth = 92 };
    private readonly Button _toDrawer = InboundUi.IconButton("", "Show as drawer", "InboundIconNeutralBrush");
    private readonly Button _toFull = IconOnly("", "Open full view");
    private readonly ToggleButton _pin = new() { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(8, 5, 8, 5), MinWidth = 0 };
    private readonly Button _closeDrawer = IconOnly("", "Close");
    private readonly DropDownButton _sort = new() { FontSize = 12, Padding = new Thickness(8, 3, 8, 4) };
    private readonly TextBlock _summary = InboundUi.Secondary();
    // UI.R31: the list is framed as a card, as Progress Details frames its lists.
    private readonly Border _listFrame = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
    private readonly Grid _listGrid = new();
    private readonly Border _headerChrome = new() { BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(7, 7, 0, 0) };
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection(),
    };

    /// <summary>Uno's list items fill a selected item with the older accent-blue brushes; here they are the subtle fills WinUI 3 uses.</summary>
    private static void UseSubtleSelection(ListView list)
    {
        foreach (var (key, subtle) in new[]
                 {
                     ("SystemControlHighlightListAccentLowBrush", "SubtleFillColorSecondaryBrush"),
                     ("SystemControlHighlightListAccentMediumBrush", "SubtleFillColorTertiaryBrush"),
                     ("SystemControlHighlightListAccentHighBrush", "SubtleFillColorSecondaryBrush"),
                 })
        {
            if (Application.Current.Resources.TryGetValue(subtle, out var brush)) list.Resources[key] = brush;
        }
    }

    private readonly DataTemplate _rowTemplate = (DataTemplate)XamlReader.Load(ItemTemplate);
    private readonly DataTemplate _cardTemplate = (DataTemplate)XamlReader.Load(CardTemplate);
    private readonly Style? _rowStyle = InboundUi.Style("InboundListRowStyle");
    private readonly Style _cardStyle = CardStyle();
    private readonly TextBlock _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(16) };
    private readonly Button _loadMore = new() { Content = "Load more", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    // UI.R31: the selected item gets Progress Details' header: a glyph tile, the name, its details, a state chip.
    private readonly ItemHeader _detailHeader = new("");
    private TextBlock _detailTitle => _detailHeader.Title;
    private TextBlock _detailLine => _detailHeader.Subline;
    private readonly StatusLine _problem;
    private readonly RemoteFileTreeView _tree = new();
    private readonly TextBox _destination = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _browse = new() { Content = "Browse…", MinWidth = 84 };
    private readonly StackPanel _destinationBlock = new() { Spacing = 4 };
    private readonly StatusLine _destinationHint;
    private readonly StatusLine _status;
    private readonly Button _downloadSelected = InboundUi.IconButton("", "Download selected", accent: true);
    private readonly Button _downloadAll = new() { Content = "Download all", MinWidth = 92 };
    private readonly Button _delete = new() { MinWidth = 92, Visibility = Visibility.Collapsed };
    private readonly Button _close = new() { Content = "Close", MinWidth = 92 };

    /// <param name="inMainWindow">In the main window it can move to a drawer or its own window; in its own window it closes.</param>
    public ProviderPanel(RemoteClient client, RemoteAccountDto account, ProviderPanelContext context, bool inMainWindow)
    {
        _client = client;
        _context = context;
        _inMainWindow = inMainWindow;
        Provider = account.Provider;
        _name = RemoteText.ProviderName(account);
        _hasKey = RemoteRules.CanBrowse(account);
        _problem = new StatusLine($"Problem with the selected {_name} item");
        _destinationHint = new StatusLine("Destination note");
        _status = new StatusLine($"My {_name} status");
        _status.Text.TextWrapping = TextWrapping.NoWrap;
        _status.Text.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Text.FontSize = 12;

        BuildPieces();
        BuildFull();
        BuildDrawer();
        foreach (var kind in account.ItemKinds)
        {
            var page = new KindPage(kind);
            _pages.Add(page);
            var tab = new SelectorBarItem { Text = RemoteText.KindHeading(kind), Tag = kind };
            AutomationProperties.SetName(tab, RemoteText.KindHeading(kind));
            _tabs.Items.Add(tab);
        }

        _tabs.SelectionChanged += (_, _) => ShowSelectedTab();
        UseSubtleSelection(_list);
        _list.SelectionChanged += (_, _) => OnItemSelected();
        _list.ContextRequested += List_ContextRequested;
        _refresh.Click += (_, _) => RefreshPage();
        _loadMore.Click += (_, _) => { if (_page is { } page) _ = LoadAsync(page, fresh: false, more: true); };
        _checkNow.Click += (_, _) => _ = CheckNowAsync();
        _hosters.Click += (_, _) => HosterWindow.Show(_client);
        _toDrawer.Click += (_, _) => DrawerRequested?.Invoke();
        _toFull.Click += (_, _) => FullViewRequested?.Invoke();
        _closeDrawer.Click += (_, _) => CloseRequested?.Invoke();
        _pin.Click += (_, _) =>
        {
            _pinned = _pin.IsChecked == true;
            UpdatePin();
            PinChanged?.Invoke(_pinned);
        };
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
        _close.Click += (_, _) => CloseRequested?.Invoke();

        _client.State.AccountChanged += OnAccountChanged;
        _client.State.KeySaved += OnKeySaved;
        _client.State.Jobs.CollectionChanged += OnJobsChanged;

        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) => args.Handled = EscapePressed?.Invoke() == true;
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
        _freeDestination = ProviderViewMemory.Destination(null, settings?.LastDestinationDirectory, settings?.DefaultDownloadDirectory);
        SetDestination(_freeDestination);

        // The Host doesn't announce a job's local progress, so what a linked item says is read here, while it shows.
        _localTimer.Tick += (_, _) => RefreshLinked();

        ApplyMode();
        RenderDetail();
        if (_tabs.Items.Count > 0) _tabs.SelectedItem = _tabs.Items[0];
    }

    public string Provider { get; }

    public string ProviderName => _name;

    public FrameworkElement Element => _root;

    public bool IsShown => _shown;

    public ProviderPanelMode Mode => _mode;

    /// <summary>A Download sent its files to the list.</summary>
    public event Action<ProviderDownloadNotice>? Downloaded;

    /// <summary>"Show in download list" on an item whose downloads are there.</summary>
    public event Action<RemoteJobDto>? ShowInListRequested;

    public event Action? OwnWindowRequested;
    public event Action? FullViewRequested;
    public event Action? DrawerRequested;
    public event Action? CloseRequested;
    public event Action<bool>? PinChanged;

    /// <summary>Esc in the panel. True when the host did something with it.</summary>
    public event Func<bool>? EscapePressed;

    public bool Pinned
    {
        get => _pinned;
        set
        {
            _pinned = value;
            UpdatePin();
        }
    }

    // ---- Shown or not (no idle cost) ----------------------------------------------------------------

    /// <summary>
    /// Only a shown panel reads anything: on showing, the open tab is read (the Host may answer from what it read
    /// lately), and the local figures are looked at every two seconds. Hidden, nothing ticks and nothing is asked.
    /// </summary>
    public void SetShown(bool shown)
    {
        if (_closed || _shown == shown) return;
        _shown = shown;
        if (!shown)
        {
            _localTimer.Stop();
            return;
        }

        _localTimer.Start();
        RefreshLinked();
        if (_page is { } page && _loads == 0) _ = LoadAsync(page, fresh: false, more: false);
    }

    // ---- Modes and the place carried between them ---------------------------------------------------

    public void SetMode(ProviderPanelMode mode)
    {
        if (_mode == mode) return;
        var top = TopItemId();
        _mode = mode;
        ApplyMode();
        ScrollToTop(top ?? _selected?.Item.ItemId);
    }

    /// <summary>Where the panel is, to carry to another view of it.</summary>
    public ProviderViewState Capture()
        => new(_page?.Kind, _selected?.Item.ItemId, TopItemId(), _destinationFromJob ? _freeDestination : _destination.Text);

    /// <summary>Takes up a place carried from another view: the tab, the chosen item, the scroll and the folder.</summary>
    public void Restore(ProviderViewState state)
    {
        if (_closed) return;
        if (!string.IsNullOrWhiteSpace(state.Destination))
        {
            _freeDestination = state.Destination;
            if (!_destinationFromJob) SetDestination(_freeDestination);
        }

        var kind = ProviderViewMemory.Tab(state, _pages.Select(page => page.Kind).ToList());
        if (_pages.FirstOrDefault(page => page.Kind == kind) is not { } target) return;
        target.SelectedItemId = state.SelectedItemId ?? target.SelectedItemId;
        _pendingTop = state.TopItemId ?? state.SelectedItemId;
        if (!ReferenceEquals(target, _page))
        {
            _tabs.SelectedItem = _tabs.Items.FirstOrDefault(item => item.Tag as string == target.Kind);
        }
        else
        {
            SelectRemembered(target);
        }

        if (target.Loaded) ApplyPendingTop(target);
    }

    /// <summary>"Show in My provider" from the download list: that item's tab, chosen and scrolled to.</summary>
    public void Reveal(string kind, string itemId) => Restore(new ProviderViewState(kind, itemId, itemId, null));

    private string? TopItemId()
    {
        if (_page is not { } page || _list.ItemsPanelRoot is not ItemsStackPanel panel) return null;
        var index = panel.FirstVisibleIndex;
        return index >= 0 && index < page.Rows.Count ? page.Rows[index].Item.ItemId : null;
    }

    private void ApplyPendingTop(KindPage page)
    {
        if (_pendingTop is null) return;
        var ids = page.Rows.Select(row => row.Item.ItemId).ToHashSet(StringComparer.Ordinal);
        var top = ProviderViewMemory.Top(new ProviderViewState(page.Kind, page.SelectedItemId, _pendingTop, null), ids);
        _pendingTop = null;
        ScrollToTop(top);
    }

    private void ScrollToTop(string? itemId)
    {
        if (itemId is null || _page is not { } page) return;
        _list.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_closed || page.Rows.FirstOrDefault(row => row.Item.ItemId == itemId) is not { } row) return;
            _list.UpdateLayout();
            _list.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
        });
    }

    private void SelectRemembered(KindPage page)
    {
        var ids = page.Rows.Select(row => row.Item.ItemId).ToHashSet(StringComparer.Ordinal);
        if (ProviderViewMemory.Selected(new ProviderViewState(page.Kind, page.SelectedItemId, null, null), ids) is not { } id) return;
        var row = page.Rows.First(candidate => candidate.Item.ItemId == id);
        if (!ReferenceEquals(_list.SelectedItem, row)) _list.SelectedItem = row;
    }

    // ---- Layout ------------------------------------------------------------------------------------

    private static Button IconOnly(string glyph, string name)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 13 }, Padding = new Thickness(8, 5, 8, 5), MinWidth = 0 };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    private static Style CardStyle()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(4)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }

    /// <summary>A piece shared by both layouts. Each layout has a slot for it, and only the shown one holds it.</summary>
    private (Border Full, Border Drawer) Slot(UIElement piece)
    {
        var full = new Border();
        var drawer = new Border();
        _slots.Add((piece, full, drawer));
        return (full, drawer);
    }

    private static T At<T>(T element, int row = 0, int column = 0) where T : FrameworkElement
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        return element;
    }

    private void BuildPieces()
    {
        AutomationProperties.SetName(_tabs, $"{_name} item types");
        _tabs.Loaded += (_, _) => NameItemsList(_tabs);
        AutomationProperties.SetName(_busy, $"Asking {_name}");
        AutomationProperties.SetName(_refresh, $"Refresh the list from {_name}");
        ToolTipService.SetToolTip(_refresh, $"Read this list from {_name} again (F5)");
        AutomationProperties.SetName(_checkNow, $"Check {_name} now");
        AutomationProperties.SetName(_hosters, $"{_name} hosters");
        ToolTipService.SetToolTip(_hosters, $"Which file hosts {_name} can fetch from, and today's use");
        ToolTipService.SetToolTip(_toDrawer, $"Show My {_name} as a drawer beside the download list (Ctrl+T)");
        ToolTipService.SetToolTip(_toFull, $"Show My {_name} in place of the download list");
        AutomationProperties.SetName(_toFull, $"Open My {_name} in full view");
        AutomationProperties.SetName(_closeDrawer, $"Close My {_name}");
        ToolTipService.SetToolTip(_closeDrawer, "Close (Esc)");
        AutomationProperties.SetName(_list, $"Items on your {_name} account");
        AutomationProperties.SetName(_loadMore, $"Load more items from {_name}");
        AutomationProperties.SetName(_destination, "Destination folder");
        AutomationProperties.SetName(_browse, "Browse for the destination folder");
        AutomationProperties.SetName(_downloadSelected, "Download the selected files");
        AutomationProperties.SetName(_downloadAll, "Download all files of this item");
        // U-WL-11, TB.F13: a window of its own closes once a download starts, so its buttons say it beforehand.
        ToolTipService.SetToolTip(_downloadAll, _inMainWindow ? "Adds every file of this item to the download list" : "Adds every file of this item to the download list, then closes this window");
        ToolTipService.SetToolTip(_downloadSelected, _inMainWindow ? "Adds the ticked files to the download list" : "Adds the ticked files to the download list, then closes this window");
        AutomationProperties.SetName(_close, $"Close My {_name}");
        _delete.Content = $"Delete from {_name}…";
        AutomationProperties.SetName(_delete, $"Delete this item from {_name}");
        ToolTipService.SetToolTip(_delete, "Asks first. It can't be undone.");
        AutomationProperties.SetName(_sort, "Sort the items");

        // The list: same density as the main one. The header sets the widths, so that wide columns can scroll sideways.
        _listGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
        _listGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _listGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _listGrid.Children.Add(At(BuildListHeader()));
        var listArea = new Grid();
        listArea.Children.Add(_list);
        listArea.Children.Add(_empty);
        _listGrid.Children.Add(At(listArea, row: 1));
        _listGrid.Children.Add(At(_loadMore, row: 2));
        var sideways = new ScrollViewer
        {
            Content = _listGrid,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        sideways.SizeChanged += (_, args) => _listGrid.Width = args.NewSize.Width;
        _header.MinimumWidthChanged += width =>
        {
            _headerMinimumWidth = width;
            if (_mode == ProviderPanelMode.Full) _listGrid.MinWidth = width;
        };
        _header.ApplyToAll();
        _listFrame.Child = sideways;
        _listFrame.Padding = new Thickness(1);

        _detailHeader.Element.Margin = new Thickness(0, 0, 0, 4);
        // The chip says the status, and screen readers hear it change.
        _detailHeader.Chip.Announce = true;
        AutomationProperties.SetName(_tree, "Files of the selected item");

        // The label sits above the box, so the path gets the pane's width.
        var folder = new Grid { ColumnSpacing = 8 };
        folder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folder.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _destination.Header = "Destination folder";
        _browse.VerticalAlignment = VerticalAlignment.Bottom;
        folder.Children.Add(_destination);
        folder.Children.Add(At(_browse, column: 1));
        _destinationBlock.Children.Add(FolderChooser.Attach(folder, _destination));
        _destinationBlock.Children.Add(_destinationHint.Text);
        UpdatePin();

        void ApplyColors()
        {
            // The list keeps an opaque surface for reading; around it is frosted.
            _listFrame.Background = InboundTheme.Brush("InboundSurfaceBrush", _root);
            _listFrame.BorderBrush = InboundTheme.Brush("InboundCardStrokeBrush", _root);
            _empty.Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", _root);
            _headerChrome.Background = InboundTheme.Brush("InboundChromeBrush", _root);
            _headerChrome.BorderBrush = InboundTheme.Brush("InboundDividerBrush", _root);
        }

        _root.Loaded += (_, _) => ApplyColors();
        _root.ActualThemeChanged += (_, _) => ApplyColors();
    }

    private void UpdatePin()
    {
        _pin.IsChecked = _pinned;
        var words = _pinned ? "Unpin: let the drawer cover the list again" : "Pin: keep the drawer open beside the list";
        AutomationProperties.SetName(_pin, _pinned ? "Unpin the drawer" : "Pin the drawer beside the list");
        ToolTipService.SetToolTip(_pin, words);
        if (_pin.Content is FontIcon icon) icon.Glyph = _pinned ? "" : "";
    }

    private void BuildFull()
    {
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
        {
            _full.RowDefinitions.Add(new RowDefinition { Height = height });
        }

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 5; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var cloud = new FontIcon { Glyph = "", FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
        InboundUi.Follow(cloud, "InboundIconInfoBrush", brush => cloud.Foreground = brush);
        titleRow.Children.Add(cloud);
        titleRow.Children.Add(InboundUi.PageTitle($"My {_name}"));
        title.Children.Add(titleRow);
        var (statusFull, statusDrawer) = Slot(_status.Text);
        title.Children.Add(statusFull);
        header.Children.Add(title);
        var (busyFull, busyDrawer) = Slot(_busy);
        var (refreshFull, refreshDrawer) = Slot(_refresh);
        header.Children.Add(At(busyFull, column: 1));
        header.Children.Add(At(refreshFull, column: 2));
        header.Children.Add(At(_checkNow, column: 3));
        header.Children.Add(At(_hosters, column: 4));
        foreach (var button in new FrameworkElement[] { busyFull, refreshFull, _checkNow, _hosters }) button.VerticalAlignment = VerticalAlignment.Center;
        if (_inMainWindow)
        {
            _toDrawer.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(At(_toDrawer, column: 5));
        }

        _full.Children.Add(header);
        var (tabsFull, tabsDrawer) = Slot(_tabs);
        _full.Children.Add(At(tabsFull, row: 1));

        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DetailWidth) });
        var (listFull, listDrawer) = Slot(_listFrame);
        content.Children.Add(listFull);

        var detail = new Grid { RowSpacing = 6 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
        {
            detail.RowDefinitions.Add(new RowDefinition { Height = height });
        }

        detail.Children.Add(_detailHeader.Element);
        var (problemFull, problemDrawer) = Slot(_problem.Text);
        detail.Children.Add(At(problemFull, row: 1));
        var (treeFull, treeDrawer) = Slot(_tree);
        detail.Children.Add(At(treeFull, row: 2));
        var (destinationFull, destinationDrawer) = Slot(_destinationBlock);
        detail.Children.Add(At(destinationFull, row: 3));
        var downloads = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var (selectedFull, selectedDrawer) = Slot(_downloadSelected);
        var (allFull, allDrawer) = Slot(_downloadAll);
        downloads.Children.Add(selectedFull);
        downloads.Children.Add(allFull);
        detail.Children.Add(At(downloads, row: 4));
        var more = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        more.Children.Add(_delete);
        if (!_inMainWindow) more.Children.Add(_close);
        detail.Children.Add(At(more, row: 5));
        content.Children.Add(At(detail, column: 1));
        _full.Children.Add(At(content, row: 2));

        // The drawer gets the same pieces in its own places; see BuildDrawer.
        _drawerSlots = (statusDrawer, busyDrawer, refreshDrawer, tabsDrawer, listDrawer, problemDrawer, treeDrawer, destinationDrawer, selectedDrawer, allDrawer);
    }

    private (Border Status, Border Busy, Border Refresh, Border Tabs, Border List, Border Problem, Border Tree, Border Destination, Border Selected, Border All) _drawerSlots;

    private void BuildDrawer()
    {
        var slots = _drawerSlots;
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
        {
            _drawer.RowDefinitions.Add(new RowDefinition { Height = height });
        }

        var header = new Grid { ColumnSpacing = 4 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cloud = new FontIcon { Glyph = "", FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        InboundUi.Follow(cloud, "InboundIconInfoBrush", brush => cloud.Foreground = brush);
        header.Children.Add(cloud);
        var title = InboundUi.SectionTitle($"My {_name}");
        title.FontSize = 15;
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(At(title, column: 1));
        header.Children.Add(At(_toFull, column: 2));
        header.Children.Add(At(_pin, column: 3));
        header.Children.Add(At(_closeDrawer, column: 4));
        _drawer.Children.Add(header);
        _drawer.Children.Add(At(slots.Tabs, row: 1));

        var tools = new Grid { ColumnSpacing = 6 };
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        slots.Status.VerticalAlignment = VerticalAlignment.Center;
        tools.Children.Add(slots.Status);
        tools.Children.Add(At(slots.Busy, column: 1));
        tools.Children.Add(At(_sort, column: 2));
        tools.Children.Add(At(slots.Refresh, column: 3));
        _drawer.Children.Add(At(tools, row: 2));
        _drawer.Children.Add(At(slots.List, row: 3));

        var footer = new StackPanel { Spacing = 8 };
        footer.Children.Add(slots.Destination);
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.TextTrimming = TextTrimming.CharacterEllipsis;
        actions.Children.Add(_summary);
        actions.Children.Add(At(slots.Selected, column: 1));
        actions.Children.Add(At(slots.All, column: 2));
        footer.Children.Add(actions);
        _drawer.Children.Add(At(footer, row: 4));

        // The chosen card's files: why there are none, if so, then the tree.
        _expansion.Children.Add(slots.Problem);
        _expansion.Children.Add(slots.Tree);

        var menu = new MenuFlyout();
        foreach (var column in _columns.Columns)
        {
            var key = column.Key;
            var item = new MenuFlyoutItem { Text = column.Title };
            item.Click += (_, _) => HeaderSort_Clicked(key);
            menu.Items.Add(item);
        }

        _sort.Flyout = menu;
        UpdateSortButton();
    }

    private void UpdateSortButton()
    {
        var title = _sortKey is { } key ? _columns.Columns.FirstOrDefault(column => column.Key == key)?.Title : null;
        _sort.Content = title is null ? "Sort" : $"{title} {(_sortAscending ? "↑" : "↓")}";
        ToolTipService.SetToolTip(_sort, title is null ? "Sort the items" : $"Sorted by {title}, {(_sortAscending ? "ascending" : "descending")}. Choose it again to reverse.");
    }

    /// <summary>Moves every shared piece into the slots of the layout in use, and the list into its look.</summary>
    private void ApplyMode()
    {
        var drawer = _mode == ProviderPanelMode.Drawer;
        foreach (var (_, full, drawerSlot) in _slots)
        {
            full.Child = null;
            drawerSlot.Child = null;
        }

        foreach (var row in _pages.SelectMany(page => page.Rows)) row.Expansion = null;
        foreach (var (piece, full, drawerSlot) in _slots) (drawer ? drawerSlot : full).Child = piece;

        _root.Children.Clear();
        _root.Children.Add(drawer ? _drawer : _full);

        _headerChrome.Visibility = drawer ? Visibility.Collapsed : Visibility.Visible;
        _listGrid.RowDefinitions[0].Height = new GridLength(drawer ? 0 : 27);
        _listGrid.MinWidth = drawer ? 0 : _headerMinimumWidth;
        _list.ItemContainerStyle = drawer ? _cardStyle : _rowStyle;
        _list.ItemTemplate = drawer ? _cardTemplate : _rowTemplate;
        _tree.MaxHeight = drawer ? CardTreeHeight : double.PositiveInfinity;
        if (drawer && _selected is { } selected) selected.Expansion = _expansion;
        RenderDetail();
    }

    private FrameworkElement BuildListHeader()
    {
        var header = new Grid { Padding = new Thickness(6, 0, 6, 0), ColumnSpacing = 8 };
        _headerChrome.Child = header;
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
        return _headerChrome;
    }

    private void HeaderSort_Clicked(string key)
    {
        (_sortKey, _sortAscending) = _sortKey is { } current ? ListSort.Next(current, _sortAscending, key) : (key, ListSort.StartsAscending(key));
        _header.ShowSort(_sortKey, _sortAscending);
        UpdateSortButton();
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

    // ---- The row menu (U-WL-11, as the web UI's D2d) ------------------------------------------------

    /// <summary>A right-click, the menu key or Shift+F10 on a row selects it first, then opens its quick actions.</summary>
    private void List_ContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        var element = args.OriginalSource as DependencyObject;
        while (element is not null and not ListViewItem) element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        if (element is not ListViewItem { Content: RemoteItemRow row } container) return;
        args.Handled = true;
        if (!ReferenceEquals(_list.SelectedItem, row)) _list.SelectedItem = row;

        var job = RemoteRules.JobFor(_client.State.Jobs, row.Item);
        var onList = job is not null && ProviderOnList.Tag(job, _context.LocalFiles(job)) is not null;
        var menu = new MenuFlyout();
        foreach (var entry in RemoteRules.ItemMenu(row.View.CanDownload && _hasKey, job, _name, _acting, _loads > 0, onList: onList, ownWindow: _inMainWindow))
        {
            if (entry.Action is not { } action)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                continue;
            }

            var item = new MenuFlyoutItem { Text = entry.Label, IsEnabled = !entry.Disabled, Icon = MenuGlyph(action) };
            if (action == "copyName") item.KeyboardAcceleratorTextOverride = "Ctrl+C";
            if (action == "refresh") item.KeyboardAcceleratorTextOverride = "F5";
            item.Click += (_, _) => RunMenuAction(action, row);
            menu.Items.Add(item);
        }

        if (args.TryGetPosition(container, out var point)) menu.ShowAt(container, point);
        else menu.ShowAt(container);
    }

    private static FontIcon? MenuGlyph(string action) => action switch
    {
        "open" or "downloadAll" => new FontIcon { Glyph = "" },
        "showInList" => new FontIcon { Glyph = "" },
        "checkNow" => new FontIcon { Glyph = "" },
        "copyName" => new FontIcon { Glyph = "" },
        "refresh" => new FontIcon { Glyph = "" },
        "ownWindow" => new FontIcon { Glyph = "" },
        "deleteFromProvider" => new FontIcon { Glyph = "" },
        _ => null,
    };

    private void RunMenuAction(string action, RemoteItemRow row)
    {
        if (_closed || !ReferenceEquals(_selected, row)) return;
        switch (action)
        {
            case "open":
                _tree.Focus(FocusState.Programmatic);
                break;
            case "downloadAll":
                _ = DownloadAsync(all: true);
                break;
            case "showInList":
                if (RemoteRules.JobFor(_client.State.Jobs, row.Item) is { } job) ShowInListRequested?.Invoke(job);
                break;
            case "checkNow":
                _ = CheckNowAsync();
                break;
            case "copyName":
                CopyName(row.View.Name);
                break;
            case "refresh":
                RefreshPage();
                break;
            case "ownWindow":
                OwnWindowRequested?.Invoke();
                break;
            case "deleteFromProvider":
                _ = DeleteFromProviderAsync();
                break;
        }
    }

    private void CopyName(string name)
    {
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(name);
        try
        {
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            _status.Show($"Copied the name of '{name}'.");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            _status.Show("Another program is using the clipboard. Try again.", isProblem: true);
        }
    }

    // ---- Pages -------------------------------------------------------------------------------------

    private void ShowSelectedTab()
    {
        if (_tabs.SelectedItem?.Tag is not string kind) return;
        var page = _pages.FirstOrDefault(candidate => candidate.Kind == kind);
        if (page is null) return;
        if (_selected is { } old) old.Expansion = null;
        _page = page;
        SortRows(page);
        _list.ItemsSource = page.Rows;
        _list.SelectedItem = page.Rows.FirstOrDefault(row => row.Item.ItemId == page.SelectedItemId);
        OnItemSelected();
        UpdateEmptyState();
        if (!_shown) return;
        if (!page.Loaded) _ = LoadAsync(page, fresh: false, more: false);
        else if (page.LoadFailure is { } failure) _status.Show(failure, isProblem: true);
        else
        {
            _status.Show(Counted(page));
            ApplyPendingTop(page);
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
                Provider = Provider,
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
            if (onShow && _selected is null) SelectRemembered(page);
            if (onShow) ApplyPendingTop(page);
            if (onShow && more && _sortKey is not null) ShowWhereTheOrderPuts(page);
            if (onShow) _status.Show(result.Items.Count == 0 && !more ? string.Empty : Counted(page));
            UpdateEmptyState();
            if (onShow && !more && _selected is not null) RenderDetail(readFiles: true);
            readTheRest = onShow && RemoteItemSort.NeedsTheRest(_sortKey, page.HasMore, result.Items.Count);
        }
        catch (OperationCanceledException)
        {
            // A newer load took over, or the panel closed.
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
            var job = RemoteRules.JobFor(jobs, item);
            var view = ViewOf(item, job);
            if (known.TryGetValue(item.ItemId, out var row) && (!more || page.Rows.Contains(row)))
            {
                row.Update(item, view, OnListOf(job));
                row.Files = null;
                if (!more)
                {
                    var current = page.Rows.IndexOf(row);
                    if (current >= 0 && current != at) page.Rows.Move(current, at);
                    at++;
                }

                continue;
            }

            page.Rows.Insert(Math.Min(at, page.Rows.Count), new RemoteItemRow(item, view, OnListOf(job)));
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
                : page is { Loaded: false } && _loads == 0 && page.LoadFailure is not null
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
        // The files leave the card they were on before they can go to the next one.
        if (_selected is { } old) old.Expansion = null;
        _selected = row;
        if (_page is { } page && (row is not null || page.Rows.Count == 0)) page.SelectedItemId = row?.Item.ItemId;
        if (_mode == ProviderPanelMode.Drawer && row is not null) row.Expansion = _expansion;
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
            // UI.R31: no header with nothing chosen; the files card says what to do, centred and quiet.
            _detailHeader.Element.Visibility = Visibility.Collapsed;
            _detailTitle.Text = string.Empty;
            ToolTipService.SetToolTip(_detailTitle, null);
            _detailLine.Text = string.Empty;
            ToolTipService.SetToolTip(_detailLine, null);
            _detailHeader.Chip.Set(string.Empty, "InboundStatusNeutralBrush");
            _problem.Show(string.Empty);
            _tree.ShowMessage("Select an item to see its files.");
            UpdateActions();
            return;
        }

        var job = RemoteRules.JobFor(_client.State.Jobs, row.Item);
        var view = ViewOf(row.Item, job);
        row.Update(row.Item, view, OnListOf(job));
        _detailHeader.Element.Visibility = _mode == ProviderPanelMode.Full ? Visibility.Visible : Visibility.Collapsed;
        _detailHeader.Chip.Set(view.Status, ToneKey(view.Tone), filled: CacheBadge.IsCachedWords(view.Status));
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

        _problem.Show(view.Problem.Length > 0 ? view.Problem : _filesProblem, isProblem: true);
        UpdateDestinationFor(job);
        UpdateActions();
        if (readFiles) _ = ReadFilesAsync(row);
    }

    private static string ToneKey(RemoteRowTone tone) => tone switch
    {
        RemoteRowTone.Failed => "InboundStatusFailedBrush",
        RemoteRowTone.Waiting => "InboundStatusWaitingBrush",
        RemoteRowTone.Active => "InboundStatusActiveBrush",
        RemoteRowTone.Done => "InboundStatusDoneBrush",
        _ => "InboundStatusNeutralBrush",
    };

    private RemoteItemView ViewOf(RemoteItemDto item, RemoteJobDto? job)
        => RemoteText.Item(item, _name, job, job is { State: "localStarted" } ? RemoteText.LocalWords(_context.LocalFiles(job)) : null);

    private string? OnListOf(RemoteJobDto? job) => job is null ? null : ProviderOnList.Tag(job, _context.LocalFiles(job));

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
                Provider = Provider,
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
            var job = RemoteRules.JobFor(_client.State.Jobs, item);
            row.Update(item, ViewOf(item, job), OnListOf(job));
            RenderDetail();
            ShowFiles(row, row.Files);
        }
        catch (OperationCanceledException)
        {
            // Another item was chosen, or the panel closed.
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
            // UI.R17: the first time an item's files are shown, they all start chosen.
            _tree.SetFiles(files, chooseAll: !row.FilesShown, keep: row.Chosen);
            row.FilesShown = true;
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
        var chosen = _tree.Tree.ChosenCount;
        _refresh.IsEnabled = _hasKey && _loads == 0;
        _downloadSelected.IsEnabled = canDownload && chosen > 0;
        _downloadAll.IsEnabled = canDownload;
        _loadMore.IsEnabled = _loads == 0;
        _tree.IsChoiceLocked = _acting;
        _summary.Text = row is null
            ? "Select an item to choose its files"
            : row.Files is { Count: > 0 } ? (chosen == 1 ? "1 file ticked" : $"{chosen} files ticked") : row.View.Name;

        var checkable = _client.State.Jobs.Any(candidate => RemoteRules.SameProvider(candidate.Provider, Provider) && RemoteRules.Actions(candidate).CheckNow);
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
            if (await Pickers.PickFolderAsync(_context.WindowHandle()) is { } folder) _destination.Text = folder;
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

        var count = all ? row.Files?.Count : chosen!.Count;
        var notice = (Guid id) => new ProviderDownloadNotice(Provider, row.Item.Kind, row.Item.ItemId, id, ProviderNotice.Added(count, row.View.Name), _mode == ProviderPanelMode.Drawer);

        // An add past a job on the list would only return that job and ignore the choice.
        var job = RemoteRules.JobFor(_client.State.Jobs, row.Item);
        if (job is not null && job.State != "failed")
        {
            await RunAsync("Sending your choice…", async token =>
            {
                var result = await _client.SelectAsync(new RemoteSelectRequest { JobId = job.Id, All = all, FileIds = chosen }, token);
                return result.Ok
                    ? (string.Empty, false, notice(job.Id))
                    : (RemoteText.Failure(result.Error, null, _name), true, null);
            });
            return;
        }

        var destination = _destination.Text.Trim();
        // The folder is on the Host the window shows, so that Host's path rules decide (a Linux server's /downloads).
        if (destination.Length > 0 && !ServerPaths.IsFull(destination, ShownServer.PathStyle))
        {
            _destinationHint.Show($"Enter {ServerPaths.FullFolderWords(ShownServer.PathStyle)}, or leave this empty for the default folder.", isProblem: true);
            _destination.Focus(FocusState.Programmatic);
            return;
        }

        var settings = _context.Settings();
        await RunAsync("Adding to the download list…", async token =>
        {
            var result = await _client.AddAsync(new RemoteAddRequestDto
            {
                Provider = Provider,
                ItemKind = row.Item.Kind,
                ItemId = row.Item.ItemId,
                Destination = destination.Length > 0 ? destination : null,
                QueueId = settings?.DefaultQueueId ?? 0,
                StartMode = "startNow",
                Selection = all ? "all" : "chosen",
                FileIds = chosen,
            }, token);
            if (!result.Ok) return (RemoteText.Failure(result.Error, null, _name), true, null);
            if (destination.Length > 0) _context.RememberFolder(destination);
            // Started: the download list takes over (TB.F13). Otherwise the panel stays and says why.
            if (RemoteRules.DownloadStarted(result)) return (string.Empty, false, notice(result.Job!.Id));
            if (!string.IsNullOrWhiteSpace(result.Note)) return (DownloadManager.Core.Providers.UrlRedactor.HideAddresses(result.Note) ?? string.Empty, true, null);
            var added = result.Job!;
            return (RemoteText.Failure(added.FailureMessage, added.FailureKind, _name), true, null);
        });
    }

    private async Task CheckNowAsync()
    {
        var job = _selected is { } row ? RemoteRules.JobFor(_client.State.Jobs, row.Item) : null;
        var one = job is not null && RemoteRules.Actions(job).CheckNow ? job : null;
        await RunAsync("Asking the Host to check…", async token =>
        {
            var result = await _client.CheckNowAsync(one?.Id, one is null ? Provider : null, token);
            return result.Ok
                ? ($"Checking {_name} now. The status changes when it answers, unless {_name} has asked this app to wait.", false, null)
                : (RemoteText.Failure(result.Error, null, _name), true, null);
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
            PrimaryButtonStyle = InboundUi.DangerDialogButton,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return;
        }

        var page = _page;
        await RunAsync($"Deleting from {_name}…", async token =>
        {
            var result = await _client.DeleteRemoteAsync(job.Id, confirmed: true, token);
            if (!result.Ok) return (RemoteText.Failure(result.Error, null, _name), true, null);
            row.Expansion = null;
            page?.Rows.Remove(row);
            return ($"Deleted '{row.View.Name}' from {_name}.", false, null);
        });
        UpdateEmptyState();
    }

    /// <summary>Runs one action. A finished Download hands its notice to the host, which shows the list.</summary>
    private async Task RunAsync(string working, Func<CancellationToken, Task<(string Text, bool IsProblem, ProviderDownloadNotice? Done)>> call)
    {
        _acting = true;
        UpdateBusy();
        UpdateActions();
        _status.Show(working);
        try
        {
            var (text, isProblem, done) = await call(_closing.Token);
            if (_closed) return;
            if (done is not null)
            {
                _status.Show(string.Empty);
                Downloaded?.Invoke(done);
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

    /// <summary>Changes only what is different, so this can run often without the list blinking. Hidden, it waits.</summary>
    public void RefreshLinked()
    {
        if (_closed || !_shown) return;
        foreach (var row in _pages.SelectMany(page => page.Rows))
        {
            var job = RemoteRules.JobFor(_client.State.Jobs, row.Item);
            row.Update(row.Item, ViewOf(row.Item, job), OnListOf(job));
        }

        RenderDetail();
    }

    private void OnAccountChanged(RemoteAccountDto account)
    {
        if (_closed || !RemoteRules.SameProvider(account.Provider, Provider)) return;
        var had = _hasKey;
        _hasKey = RemoteRules.CanBrowse(account);
        if (had == _hasKey) return;
        if (!_hasKey)
        {
            _status.Show($"The {_name} API key is no longer usable, so My {_name} can't read your account.", isProblem: true);
        }
        else if (_page is { } page && _shown)
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
        if (_closed || (provider is not null && !RemoteRules.SameProvider(provider, Provider))) return;
        _detailLoad?.Cancel();
        if (_selected is { } old) old.Expansion = null;
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
        if (_hasKey && _shown && _page is { } shown) _ = LoadAsync(shown, fresh: true, more: false);
        UpdateEmptyState();
    }

    /// <summary>Ends what is still running and lets go of the Host's state. The panel isn't used again.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _shown = false;
        if (_selected is { } old) old.Expansion = null;
        _client.State.AccountChanged -= OnAccountChanged;
        _client.State.KeySaved -= OnKeySaved;
        _client.State.Jobs.CollectionChanged -= OnJobsChanged;
        _localTimer.Stop();
        _closing.Cancel();
    }
}
