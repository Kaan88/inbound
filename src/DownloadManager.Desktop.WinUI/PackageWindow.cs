using System.Diagnostics;
using System.Globalization;
using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// One progress window for a whole package: overall progress, a row per file, and below them the same
/// details a file's own window shows, for the selected file. The Host opens it instead of one window
/// per member; double-clicking a file, or Enter on it, opens that file's own window, which also has its
/// speed limiter and completion options.
/// Closing it never stops the Host or the downloads.
/// </summary>
internal sealed class PackageWindow : Window
{
    private readonly Guid _packageId;
    private readonly HostConnection _connection;
    private readonly Action<Guid>? _openFileWindow;

    // Archive state comes over the connection above: each of these windows has a connection of its own (a process of
    // its own when the Host opened it), so a second connection here adds up.
    private readonly RemoteState _archives = new();
    private readonly Dictionary<Guid, JobDto> _jobs = [];
    private readonly Dictionary<Guid, Row> _rows = [];
    private List<Guid> _members = [];
    private bool _closed;
    private bool _groupKnown;
    private bool _passwordSavedHere;

    /// <summary>The Host's list said it holds a password for this package, set or no set.</summary>
    private bool _passwordOnRecord;

    /// <summary>A password was saved or cleared in this window: its section stays, so its message stays readable.</summary>
    private bool _passwordActedHere;
    private bool _showingSaved;
    private bool _hostConnected;
    private bool _archivesRead;
    private int _archiveRead;

    private readonly Grid _root = new() { Padding = new Thickness(20, 16, 20, 16), RowSpacing = 12 };
    // UI.R31: Progress Details' header: a folder glyph on a tile, the package's name, how far it is, and a state chip.
    private readonly ItemHeader _header = new("\uE8B7");
    // UI.R29 direction B: the package's ring and four tiles, then Files and Extraction tabs.
    private readonly ProgressRing _ring = new() { Width = 124, Height = 124, IsIndeterminate = false, Maximum = 100 };
    private readonly TextBlock _ringValue = new() { FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly FontIcon _ringGlyph = new() { Glyph = "\uE73E", FontSize = 32, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _ringCaption = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
    private readonly List<InfoTile> _tiles = [];
    // UI.R34: Progress Details' speed card, for the whole package, sampled once a second by the same rule (UI.B20).
    private readonly DownloadSpeedGraph _chart = new() { Heading = "Speed", PeriodSeconds = 60 };
    private Border? _chartCard;
    private readonly DispatcherTimer _chartTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    // With no Extraction tab, one tab would look odd: the files get a section heading instead.
    private readonly TextBlock _filesHeading = InboundUi.SectionTitle("Files");
    private readonly Grid _tabRow = new();
    private readonly SelectorBar _tabs = new() { Margin = new Thickness(-12, 0, -12, 0) };
    private readonly SelectorBarItem _filesTab = new() { Text = "Files", IsSelected = true };
    private readonly SelectorBarItem _extractionTab = new() { Text = "Extraction", Visibility = Visibility.Collapsed };
    private FrameworkElement? _listFrame;
    private FrameworkElement? _fileDetailsView;
    private bool _archivesApply;
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection(),
    };
    private readonly TextBlock _fileHeading = InboundUi.SectionTitle(string.Empty);
    // The thin map of Progress Details' Overview, not the Connections tab's 14px one.
    private readonly SegmentMapBar _fileMap = new() { Visibility = Visibility.Collapsed, Height = 6, CornerRadius = new CornerRadius(3) };
    private readonly ProgressBar _fileMoving = InboundTheme.Styled(new ProgressBar { Maximum = 100, Visibility = Visibility.Collapsed });
    private readonly JobDetailsPanel _fileDetails = new() { ShowSavePath = true };
    private Guid? _pickedFile;
    private Guid? _shownFile;
    private bool _selectingFile;
    private readonly TextBlock _notice = InboundUi.Secondary();
    // UI.R31: labelled buttons with glyphs, as Progress Details has them. Once the package is done, the actions
    // that can't apply hide, Open folder is the accent button and Delete is the red-trash button.
    private readonly Button _delete = InboundUi.DeleteButton();
    private readonly Button _pause = InboundUi.IconButton("\uE769", "Stop all", "InboundIconPauseBrush");
    private readonly Button _resume = InboundUi.IconButton("\uE768", "Resume all", "InboundIconStartBrush");
    private readonly Button _cancel = InboundUi.IconButton("\uE711", "Cancel package", "InboundIconDangerBrush");
    private readonly Button _openFolder = InboundUi.IconButton("\uE838", "Open folder", accent: true);
    private readonly Button _close = new() { Content = "Hide", MinWidth = 92 };
    private readonly StackPanel _archivePanel = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly PasswordBox _archivePassword = new() { Header = "Archive password", PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly Button _savePassword = new() { Content = "Save password", MinWidth = 100, IsEnabled = false, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _clearPassword = new() { Content = "Clear password", MinWidth = 100, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly StatusLine _passwordStatus = new("Archive password status");
    private readonly StatusLine _extractionStatus = new("Extraction");

    private sealed record Row(ListViewItem Item, Guid JobId, TextBlock Name, TextBlock Status, ProgressBar Bar, TextBlock Size);

    /// <param name="target">The Host the package is on: this PC, or a saved server (<c>--server</c>).</param>
    /// <param name="openFileWindow">T11: set when the main window opened this one in its own process; a file's
    /// Progress Details then opens there too. Null: this window is a process of its own, and so is a file's window.
    /// Such a window also closes itself when this PC's Host exits, and leaves <see cref="ShownServer"/> alone.</param>
    public PackageWindow(Guid packageId, ServerTarget? target = null, Action<Guid>? openFileWindow = null)
    {
        _packageId = packageId;
        _connection = new HostConnection(target);
        _openFileWindow = openFileWindow;
        AppTheme.Track(_root);
        Title = ServerNames.ForWindow("Package Progress — Inbound Download Manager", _connection.Target);
        // U-WL-24: short enough for a 768-pixel screen with the taskbar; the file list and details scroll.
        WindowLayout.ResizeForDpi(this, 720, 680);
        WindowPlacement.Track(this, "package");
        var icon = WindowLayout.AppIconPath;
        if (File.Exists(icon)) AppWindow.SetIcon(WindowLayout.IconArgument(icon));

        // The chosen file's details are the longer of the two, and the list scrolls well in less room.
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(2, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            _root.RowDefinitions.Add(new RowDefinition { Height = height, MinHeight = height.IsStar ? 84 : 0 });
        _openFolder.Visibility = Visibility.Collapsed;
        _delete.Visibility = Visibility.Collapsed;
        _delete.Margin = new Thickness(0, 0, 16, 0);
        ToolTipService.SetToolTip(_delete, "Remove this package's files from the list and delete them");
        _root.Children.Add(_header.Element);
        var overview = new StackPanel { Spacing = 12 };
        overview.Children.Add(BuildOverview());
        _chartCard = InboundUi.Card(_chart, new Thickness(0));
        _chartCard.Visibility = Visibility.Collapsed;
        overview.Children.Add(_chartCard);
        Add(overview, 1);
        _chartTimer.Tick += (_, _) => SampleChart();
        AppWindow.Changed += (_, change) => { if (change.DidPresenterChange || change.DidVisibilityChange) UpdateChartSeen(); };
        _tabs.Items.Add(_filesTab);
        _tabs.Items.Add(_extractionTab);
        _tabs.SelectionChanged += (_, _) => ShowTab();
        // The tabs are in the window only while the Extraction tab applies; a collapsed SelectorBar still kept its room.
        _tabRow.Children.Add(_filesHeading);
        Add(_tabRow, 2);
        var listFrame = InboundUi.ListCard(_list);
        _listFrame = listFrame;
        AutomationProperties.SetName(_list, "Files in this package");
        if (Application.Current.Resources.TryGetValue("InboundListRowStyle", out var rowStyle) && rowStyle is Style style) _list.ItemContainerStyle = style;
        _list.SelectionChanged += (_, _) => OnFileSelected();
        _list.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter || _shownFile is not { } file) return;
            args.Handled = true;
            OpenFileWindow(file);
        };
        Add(listFrame, 3);
        Add(_fileDetailsView = BuildFileDetails(), 4);
        BuildArchivePanel();
        Add(_archivePanel, 5);
        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 6; i++) footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _notice.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_notice);
        var column = 1;
        foreach (var button in new[] { _delete, _openFolder, _pause, _resume, _cancel, _close })
        {
            Grid.SetColumn(button, column++);
            footer.Children.Add(button);
        }
        Add(footer, 6);

        void ApplyColors()
        {
            _root.Background = InboundTheme.Brush("InboundWindowBrush", _root);
            _ring.Background = InboundTheme.Brush("InboundProgressTrackBrush", _root);
            _ringCaption.Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", _root);
            _ringGlyph.Foreground = InboundTheme.Brush("InboundStatusDoneBrush", _root);
            Render();
        }
        _root.Loaded += (_, _) => ApplyColors();
        _root.ActualThemeChanged += (_, _) => ApplyColors();

        _pause.Click += async (_, _) => await RunPackageActionAsync("pause");
        _resume.Click += async (_, _) => await RunPackageActionAsync("resume");
        _cancel.Click += async (_, _) => await ConfirmCancelAsync();
        _openFolder.Click += (_, _) => OpenFolder();
        _delete.Click += async (_, _) => await ConfirmDeleteAsync();
        _close.Click += (_, _) => Close();

        _archives.ArchiveSets.CollectionChanged += (_, _) => { if (!_closed) RenderArchives(); };
        _archives.PropertyChanged += (_, _) => { if (!_closed) RenderArchives(); };
        _connection.ArchiveChanged += change => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _archives.ArchivesSupported = true;
            _archives.Apply(change);
        });

        // A window of its own process ends it; one in the main window's process closes, and the main window ends the process.
        _connection.HostExiting += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_openFileWindow is null) Application.Current.Exit();
            else if (!_closed) Close();
        });
        _connection.SnapshotReceived += snapshot => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            // Another server's theme is its own; this window keeps this PC's. In the main window's process that window
            // follows it for every window (T11).
            if (!_connection.IsRemote && _openFileWindow is null) AppTheme.FollowSettings(snapshot.Settings);
            _jobs.Clear();
            foreach (var job in snapshot.Jobs) _jobs[job.Id] = job;
            SetMembers(snapshot.Settings.DownloadGroups);
            _ = ReadArchivesAsync();
            _ = LoadSpeedHistoryAsync();
        });
        _connection.GroupsChanged += groups => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            SetMembers(groups);
            if (!_archivesRead) _ = ReadArchivesAsync();
        });
        _connection.JobChanged += (job, type) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            if (type == "removed") _jobs.Remove(job.Id);
            else _jobs[job.Id] = job;
            if (!_members.Contains(job.Id)) return;
            Render();
            // A name learned or changed can make the archive section apply.
            RenderArchives();
        });
        _connection.ConnectionChanged += (connected, error) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _hostConnected = connected;
            _fileDetails.SetConnected(connected);
            _notice.Text = connected ? string.Empty : $"Host disconnected. Reconnecting… {error}";
            // A drop ends the files' flat line while they wait (audit M11).
            UpdateChartTimer();
        });
        Closed += async (_, _) =>
        {
            _closed = true;
            _chartTimer.Stop();
            _fileDetails.Stop();
            _archivePassword.Password = string.Empty;
            await _connection.DisposeAsync();
        };
        _root.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        _root.KeyboardAccelerators.Add(Escape());
        Content = _root;
        WindowLayout.ApplyChrome(this, _root);
        Render();
        _connection.Start();
    }

    public void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private void Add(FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        _root.Children.Add(element);
    }

    private KeyboardAccelerator Escape()
    {
        var accelerator = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        accelerator.Invoked += (_, args) => { args.Handled = true; Close(); };
        return accelerator;
    }

    /// <summary>The ring with the package's percentage beside four tiles: downloaded, speed, time left, files.</summary>
    private FrameworkElement BuildOverview()
    {
        var row = new Grid { ColumnSpacing = 20 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var ring = new Grid { Width = 124, Height = 124, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_ring, "Package progress");
        ring.Children.Add(_ring);
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        label.Children.Add(_ringGlyph);
        label.Children.Add(_ringValue);
        label.Children.Add(_ringCaption);
        ring.Children.Add(label);
        row.Children.Add(ring);
        var tiles = new Grid { ColumnSpacing = 8, RowSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        tiles.ColumnDefinitions.Add(new ColumnDefinition());
        tiles.ColumnDefinitions.Add(new ColumnDefinition());
        tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < 4; index++)
        {
            var tile = new InfoTile();
            Grid.SetRow(tile.Element, index / 2);
            Grid.SetColumn(tile.Element, index % 2);
            tiles.Children.Add(tile.Element);
            _tiles.Add(tile);
        }
        Grid.SetColumn(tiles, 1);
        row.Children.Add(tiles);
        return row;
    }

    /// <summary>The package's figure for the graph: every file's rate by the main window's rule, added up.</summary>
    private void SampleChart(DateTimeOffset? at = null)
    {
        if (_closed) return;
        var now = at ?? DateTimeOffset.UtcNow;
        var rates = _members.Where(_jobs.ContainsKey).Select(id => _jobs[id])
            .Select(job => SpeedSample.JobRate(job.State, job.BytesPerSecond, job.UpdatedAt, _connection.Clock.ServerNow(now)));
        _chart.AddSample(now, SpeedSample.Total(_hostConnected, rates.ToList()));
    }

    /// <summary>
    /// F-12: the files' figures from the Host, added up second by second, so the package's graph opened mid-download
    /// shows what came before. Files the Host has none for (not running, or an older Host) add nothing.
    /// </summary>
    private async Task LoadSpeedHistoryAsync()
    {
        try
        {
            var totals = new SortedDictionary<long, long>();
            foreach (var id in _members.ToList())
            {
                if (_jobs.GetValueOrDefault(id) is not { State: "downloading" or "connecting" or "verifying" or "retryWaiting" }) continue;
                var reply = await _connection.GetSpeedHistoryAsync(id);
                if (_closed) return;
                if (reply.Type != MessageTypes.SpeedHistoryResult || reply.GetPayload<SpeedHistoryDto>() is not { } history || history.JobId != id) continue;
                var start = history.Start.ToUnixTimeSeconds();
                for (var i = 0; i < history.BytesPerSecond.Count; i++)
                {
                    var second = start + i * (long)history.IntervalSeconds;
                    totals[second] = totals.GetValueOrDefault(second) + Math.Max(0, history.BytesPerSecond[i]);
                }
            }
            if (totals.Count == 0) return;
            var first = totals.Keys.First();
            var values = new List<long>();
            for (var second = first; second <= totals.Keys.Last(); second++) values.Add(totals.GetValueOrDefault(second));
            _chart.LoadHistory(DateTimeOffset.FromUnixTimeSeconds(first), 1, values);
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Package speed history not loaded: {ex.Message}");
        }
    }

    private void UpdateChartSeen()
    {
        var shown = !_closed && AppWindow.IsVisible
            && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        _chart.IsSeen = shown && _chartCard is { Visibility: Visibility.Visible };
        _fileDetails.SetShown(shown);
        UpdateChartTimer();
    }

    private long? _chartStoppedAt;
    private readonly ChartIdle _chartIdle = new();

    /// <summary>
    /// Audit M11: as in Progress Details, the package's chart samples every second only while it can be seen and a file
    /// transfers. While the files wait or are paused nothing runs: <see cref="ChartIdle"/> adds the wait's zeros when one
    /// moves again. Seen again after a stop of more than a few seconds, it reads the Host's history.
    /// </summary>
    private void UpdateChartTimer()
    {
        var now = DateTimeOffset.UtcNow;
        var members = _members.Where(_jobs.ContainsKey).Select(id => _jobs[id]).ToList();
        var transferring = members.Any(job => SpeedSample.IsActive(job.State));
        var waiting = !transferring && members.Any(job => job.State is not ("completed" or "failed" or "cancelled"));
        var (zeros, begins) = _chartIdle.Update(waiting, _hostConnected, now);
        if (zeros.Count > 0) _chart.AddSamples(zeros.Select(at => (at, (double?)0)));
        if (!_chart.IsSeen || !transferring)
        {
            if (_chartTimer.IsEnabled)
            {
                _chartTimer.Stop();
                _chartStoppedAt = Environment.TickCount64;
            }
            // The wait's first figure: 0 from now until a file moves again.
            if (begins) SampleChart(now);
            return;
        }

        if (_chartTimer.IsEnabled) return;
        if (_chartStoppedAt is { } stoppedAt && Environment.TickCount64 - stoppedAt > 5_000 && _hostConnected) _ = LoadSpeedHistoryAsync();
        _chartStoppedAt = null;
        SampleChart();
        _chartTimer.Start();
    }

    private void ShowTab()
    {
        var extraction = _tabs.SelectedItem == _extractionTab && _archivesApply;
        if (_archivesApply != _tabRow.Children.Contains(_tabs))
        {
            if (_archivesApply) _tabRow.Children.Insert(0, _tabs);
            else _tabRow.Children.Remove(_tabs);
        }
        _filesHeading.Visibility = _archivesApply ? Visibility.Collapsed : Visibility.Visible;
        if (_listFrame is { } list) list.Visibility = extraction ? Visibility.Collapsed : Visibility.Visible;
        if (_fileDetailsView is { } details) details.Visibility = extraction ? Visibility.Collapsed : Visibility.Visible;
        _archivePanel.Visibility = extraction ? Visibility.Visible : Visibility.Collapsed;
        // The archive section takes the list's room when its tab is open.
        Grid.SetRow(_archivePanel, extraction ? 3 : 5);
    }

    private void SetTile(int index, string label, string value, string unit = "") => _tiles[index].Set(label, value, unit);

    /// <summary>The same segment map and figures as a file's own window, for the selected file.</summary>
    private FrameworkElement BuildFileDetails()
    {
        // UI.R35: the file's connections show as Progress Details' Connections tab shows them, with no expander.
        _fileDetails.UseOpenTable();
        _fileDetails.Attach(_connection, _fileMap, _fileMoving, _root);
        _fileDetails.Problem += words => _notice.Text = words;
        ToolTipService.SetToolTip(_fileMap, "Start positions and download progress by connection");
        var bars = new Grid();
        bars.Children.Add(_fileMap);
        bars.Children.Add(_fileMoving);
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(0, 0, 12, 0) };
        panel.Children.Add(_fileHeading);
        panel.Children.Add(bars);
        panel.Children.Add(_fileDetails);
        // UI.R31: the selected file's facts sit in a card, as Progress Details' Connections tab has them.
        var scroller = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        return InboundUi.Card(scroller, new Thickness(12, 8, 0, 8));
    }

    /// <summary>Someone chose a file. It stays the one shown until it leaves the package.</summary>
    private void OnFileSelected()
    {
        if (_selectingFile || _list.SelectedItem is not ListViewItem { Tag: Guid file }) return;
        _pickedFile = file;
        ShowFileDetails(_jobs.GetValueOrDefault(file));
    }

    private void ShowFileDetails(JobDto? job)
    {
        _shownFile = job?.Id;
        if (job is null)
        {
            _fileHeading.Text = string.Empty;
            _fileDetails.ShowNothing("No file selected");
            return;
        }

        _fileHeading.Text = string.IsNullOrWhiteSpace(job.FileName) ? "Download" : job.FileName;
        _fileDetails.Show(job);
    }

    private void SetMembers(List<DownloadGroupDto> groups)
    {
        var group = groups.FirstOrDefault(candidate => candidate.Id == _packageId);
        if (group is null)
        {
            _members = [];
            _groupKnown = false;
            _header.Title.Text = "Package removed";
            _header.Chip.Set("Removed", "InboundStatusNeutralBrush");
            _notice.Text = "This package no longer exists. Its files are listed individually in the main window.";
        }
        else
        {
            _header.Title.Text = group.Name;
            _groupKnown = true;
            _members = [.. group.JobIds];
        }
        Render();
        RenderArchives();
    }

    // ---- Archives: the password for what this package brings, and how far extraction has got ----

    private void BuildArchivePanel()
    {
        var hint = InboundUi.Secondary(ArchiveText.PackagePasswordHint);
        AutomationProperties.SetName(hint, hint.Text);
        AutomationProperties.SetName(_archivePassword, "Archive password for this package. It isn't for the download.");
        AutomationProperties.SetName(_savePassword, "Save the archive password");
        AutomationProperties.SetName(_clearPassword, "Clear the saved archive password");
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_archivePassword);
        Grid.SetColumn(_savePassword, 1);
        Grid.SetColumn(_clearPassword, 2);
        row.Children.Add(_savePassword);
        row.Children.Add(_clearPassword);
        _archivePanel.Children.Add(row);
        _archivePanel.Children.Add(hint);
        _archivePanel.Children.Add(_passwordStatus.Text);
        _archivePanel.Children.Add(_extractionStatus.Text);

        _archivePassword.PasswordChanged += (_, _) => _savePassword.IsEnabled = _archivePassword.Password.Length > 0;
        _archivePassword.KeyDown += async (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter || _archivePassword.Password.Length == 0) return;
            args.Handled = true;
            await SaveArchivePasswordAsync();
        };
        _savePassword.Click += async (_, _) => await SaveArchivePasswordAsync();
        _clearPassword.Click += async (_, _) => await ClearArchivePasswordAsync();
    }

    /// <summary>The list a connection starts from. Events on that connection keep it current after that.</summary>
    private async Task ReadArchivesAsync()
    {
        var read = ++_archiveRead;
        _archivesRead = false;
        _archives.BeginArchiveList();
        ArchiveListReply reply;
        try
        {
            reply = RemoteClient.ReadArchiveListReply(await _connection.ListArchivesAsync());
        }
        catch (Exception ex) when (RemoteClient.IsCommunicationFailure(ex))
        {
            // A later change of the package's members, or the next connection, asks again.
            return;
        }

        if (_closed || read != _archiveRead || !reply.Ok) return;
        _archivesRead = true;
        _archives.ArchivesSupported = true;
        _passwordOnRecord = reply.PackagesWithPassword.Contains(_packageId);
        _archives.ApplyArchiveList(reply.Sets);
        RenderArchives();
    }

    /// <summary>Answers in words either way. No exception's words reach a message, because a password passes through here.</summary>
    private async Task<ArchiveActionResult> SendArchivePasswordAsync(string password)
    {
        try
        {
            var result = RemoteClient.ReadArchiveActionReply(await _connection.SetArchivePasswordAsync(new ArchiveSetPasswordRequest { PackageId = _packageId, Password = password }));
            if (!_closed && result.Set is { } set) _archives.ApplyArchiveReply(set);
            return result;
        }
        catch (Exception ex) when (RemoteClient.IsCommunicationFailure(ex))
        {
            return new ArchiveActionResult { Error = RemoteClient.DescribeFailure(ex, reachedHost: ex is IOException || _hostConnected) };
        }
    }

    private List<ArchiveSetDto> PackageSets() => _archives.ArchiveSets
        .Where(set => set.PackageId == _packageId || set.JobIds.Any(_members.Contains))
        .OrderBy(set => set.Name, NaturalStringComparer.Instance)
        .ToList();

    private void RenderArchives()
    {
        var sets = PackageSets();
        var names = _members.Select(id => _jobs.TryGetValue(id, out var job) ? job.FileName : null).ToList();
        // A password on record shows the section even with no archive left, so it can still be cleared.
        var hasArchives = PackageArchiveSection.Show(names, sets.Count, _passwordSavedHere || _passwordOnRecord || sets.Any(set => set.HasPassword));
        _archivesApply = _archives.ArchivesSupported && _groupKnown && (hasArchives || _passwordActedHere);
        _extractionTab.Visibility = _archivesApply ? Visibility.Visible : Visibility.Collapsed;
        if (!_archivesApply && _tabs.SelectedItem == _extractionTab) _tabs.SelectedItem = _filesTab;
        ShowTab();

        // The Host says whether a password is held only through a set, and a new package may have none yet.
        _clearPassword.IsEnabled = _passwordSavedHere || _passwordOnRecord || sets.Count == 0 || sets.Any(set => set.HasPassword);

        var lines = sets.Take(6).Select(set => $"{set.Name}: {ArchiveText.FullWithNote(set)}").ToList();
        if (sets.Count > 6) lines.Add($"and {sets.Count - 6} more");
        var asking = sets.Where(set => set.NeedsPassword).ToList();
        if (asking.Count > 0)
        {
            // What was just saved has been tried and has failed, so it no longer gets the last word.
            if (_showingSaved)
            {
                _showingSaved = false;
                _passwordStatus.Show(string.Empty);
            }

            lines.Insert(0, asking.Any(set => set.FailureKind == "wrongPassword")
                ? "That password was wrong. The downloads are fine. Enter the right one above, and extraction runs again."
                : "An archive here needs its password. The downloads are fine. Enter it above, and extraction runs.");
        }

        _extractionStatus.Show(string.Join(Environment.NewLine, lines), asking.Any(set => set.FailureKind == "wrongPassword"));
    }

    private async Task SaveArchivePasswordAsync()
    {
        var password = _archivePassword.Password;

        // Out of the box before anything is sent, whatever the answer turns out to be.
        _archivePassword.Password = string.Empty;
        if (password.Length == 0) return;
        _showingSaved = false;
        _passwordActedHere = true;
        _passwordStatus.Show("Saving the archive password…");
        var result = await SendArchivePasswordAsync(password);
        password = string.Empty;
        if (_closed) return;
        if (result.Ok)
        {
            _passwordSavedHere = true;
            _passwordStatus.Show("Archive password saved. It is used when this package's archives are extracted.");
            RenderArchives();
            _showingSaved = true;
            return;
        }

        _passwordStatus.Show($"The archive password wasn't saved. {RemoteText.Failure(result.Error)}", isProblem: true);
        RenderArchives();
    }

    private async Task ClearArchivePasswordAsync()
    {
        _showingSaved = false;
        _passwordActedHere = true;
        _passwordStatus.Show("Clearing the archive password…");
        var result = await SendArchivePasswordAsync(string.Empty);
        if (_closed) return;
        if (result.Ok)
        {
            _passwordSavedHere = false;
            _passwordOnRecord = false;
            _passwordStatus.Show("Any archive password saved for this package is gone.");
        }
        else
        {
            _passwordStatus.Show($"The archive password wasn't cleared. {RemoteText.Failure(result.Error)}", isProblem: true);
        }

        RenderArchives();
    }

    private void Render()
    {
        var members = _members.Where(_jobs.ContainsKey).Select(id => _jobs[id])
            .OrderBy(job => job.FileName, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var stale in _rows.Keys.Except(members.Select(job => job.Id)).ToList())
        {
            _list.Items.Remove(_rows[stale].Item);
            _rows.Remove(stale);
        }
        var names = ShellWords.PackageFileNames(members.Select(job => ((string?)FolderOf(job), job.FileName)).ToList());
        for (var index = 0; index < members.Count; index++)
        {
            var job = members[index];
            var name = names[index];
            if (!_rows.TryGetValue(job.Id, out var row)) _rows[job.Id] = row = CreateRow(job.Id);
            var at = _list.Items.IndexOf(row.Item);
            if (at != index)
            {
                if (at >= 0) _list.Items.RemoveAt(at);
                _list.Items.Insert(Math.Min(index, _list.Items.Count), row.Item);
            }
            row.Name.Text = name;
            AutomationProperties.SetName(row.Item, $"{name}, {StateText(job)}");
            row.Status.Text = StateText(job);
            // UI.R31: the list's status colors.
            row.Status.Foreground = InboundTheme.Brush(InboundUi.StateBrush(job.State), _root);
            row.Bar.IsIndeterminate = job.TotalBytes is not > 0 && job.State is "connecting" or "downloading";
            row.Bar.Value = job.State == "completed" ? 100 : Percent(job.ReceivedBytes, job.TotalBytes);
            row.Size.Text = job.TotalBytes is > 0 ? (job.State == "completed" ? Bytes(job.TotalBytes.Value) : $"{Bytes(job.ReceivedBytes)} / {Bytes(job.TotalBytes.Value)}") : Bytes(job.ReceivedBytes);
            row.Bar.Visibility = job.State == "completed" ? Visibility.Collapsed : Visibility.Visible;
        }

        var shown = ProgressDetailRules.FileToShow(members, _pickedFile);
        _selectingFile = true;
        _list.SelectedItem = shown is { } file ? _rows[file].Item : null;
        _selectingFile = false;
        ShowFileDetails(shown is { } showing ? _jobs[showing] : null);

        var total = members.Sum(job => job.TotalBytes ?? 0);
        var received = members.Sum(job => job.State == "completed" && job.TotalBytes is > 0 ? job.TotalBytes.Value : job.ReceivedBytes);
        var completed = members.Count(job => job.State == "completed");
        var speed = members.Sum(job => job.State is "downloading" ? job.BytesPerSecond ?? 0 : 0);
        var allKnown = members.Count > 0 && members.All(job => job.TotalBytes is > 0);
        var states = members.Select(job => (string?)job.State).ToList();
        _header.Subline.Text = members.Count == 0 ? string.Empty : CompletionBurst.Summary(states);
        var allDone = members.Count > 0 && completed == members.Count;
        var anyFailed = members.Any(job => job.State == "failed");
        if (_groupKnown) SetHeaderChip(members, allDone, anyFailed, speed);
        var percent = allDone ? 100 : allKnown ? Percent(received, total) : 0;
        // The speed card shows while anything in the package can still move, as in Progress Details.
        var moving = members.Any(job => job.State is not ("completed" or "failed" or "cancelled"));
        _chartCard!.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
        UpdateChartSeen();
        _ring.Value = percent;
        _ring.IsIndeterminate = !allKnown && !allDone && speed > 0;
        _ring.Foreground = InboundTheme.Brush(allDone ? "InboundStatusDoneBrush" : anyFailed ? "InboundStatusFailedBrush" : "InboundProgressFillBrush", _root);
        _ringGlyph.Visibility = allDone ? Visibility.Visible : Visibility.Collapsed;
        _ringValue.Visibility = allDone ? Visibility.Collapsed : Visibility.Visible;
        _ringValue.Text = allKnown || members.Count == 0 ? $"{percent:0}%" : Bytes(received);
        _ringCaption.Text = members.Count == 0 ? string.Empty : allDone ? "complete" : $"{completed} of {members.Count} files";
        AutomationProperties.SetName(_ring, allDone ? "Package complete" : $"Package {percent:0} percent downloaded");
        var remaining = allKnown && speed > 0 ? TimeSpan.FromSeconds((total - received) / speed) : (TimeSpan?)null;
        if (allKnown)
        {
            var (number, unit) = UiFormat.Of(received, total);
            SetTile(0, "Downloaded", number, unit);
        }
        else if (members.Count == 0) SetTile(0, "Downloaded", "—");
        else
        {
            var (number, unit) = UiFormat.Split(received);
            SetTile(0, "Downloaded", number, unit);
        }
        if (speed > 0)
        {
            var (number, unit) = UiFormat.Split((long)speed);
            SetTile(1, "Speed", number, unit + "/s");
        }
        else SetTile(1, "Speed", "—");
        // U-WL-15: the list's own words for time left.
        SetTile(2, "Time left", remaining is { } left && DurationText.TimeLeft(left) is { Length: > 0 } words ? words : "—");
        if (members.Count == 0) SetTile(3, "Files", "—");
        else SetTile(3, "Files", $"{completed}", $"of {members.Count} done");
        _filesTab.Text = _filesHeading.Text = members.Count > 0 ? $"Files ({members.Count})" : "Files";

        // Every file has finished one way or another (some may have failed): the window is done, says how, and the
        // files that were saved can be shown in their folder (U-WR-16).
        var settled = CompletionBurst.Settled(states);
        // Another server's folder is on that machine.
        _openFolder.Visibility = settled && RemoteDataRules.MayUseFiles(_connection.IsRemote) ? Visibility.Visible : Visibility.Collapsed;
        _pause.IsEnabled = PackageRules.PauseTargets(members).Count > 0;
        _resume.IsEnabled = PackageRules.ResumeTargets(_packageId, members).Count > 0;
        _cancel.IsEnabled = PackageRules.CancelTargets(members).Count > 0;
        // UI.R31: once every file has settled, what can't apply hides rather than greys out.
        _pause.Visibility = !settled || _pause.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        _resume.Visibility = !settled || _resume.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        _cancel.Visibility = !settled || _cancel.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        _delete.Visibility = settled && members.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _close.Content = settled ? "Close" : "Hide";
        var title = ServerNames.ForWindow($"{CompletionBurst.Title(states) ?? "Package Progress"} — Inbound Download Manager", _connection.Target);
        if (Title != title) Title = title;
    }

    private Row CreateRow(Guid jobId)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 6, 8, 6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        var status = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var size = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        InboundUi.Follow(size, "InboundSecondaryTextBrush", brush => size.Foreground = brush);
        var bar = InboundTheme.Styled(new ProgressBar { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 4, 0, 0) });
        Grid.SetColumn(status, 1);
        Grid.SetColumn(size, 2);
        Grid.SetRow(bar, 1);
        Grid.SetColumnSpan(bar, 3);
        grid.Children.Add(name);
        grid.Children.Add(status);
        grid.Children.Add(size);
        grid.Children.Add(bar);
        ToolTipService.SetToolTip(grid, "Double-click for this file's own window");
        grid.DoubleTapped += (_, _) => OpenFileWindow(jobId);
        var item = new ListViewItem { Content = grid, Tag = jobId };
        AutomationProperties.SetHelpText(item, "Its details show below. Enter opens this file's own window.");
        return new Row(item, jobId, name, status, bar, size);
    }

    private async Task RunPackageActionAsync(string action)
    {
        try
        {
            var result = (await _connection.GroupActionAsync(new GroupActionRequest { GroupId = _packageId, Action = action }))
                .GetPayload<GroupActionResult>();
            _notice.Text = !result.Ok
                ? result.FailedJobs.Count > 0 ? $"{result.FailedJobs.Count} file(s) could not be changed." : result.Error ?? "The package could not be changed."
                : result.LeftCancelled > 0 ? $"{result.LeftCancelled} cancelled file(s) stayed cancelled. Resume them one by one to bring them back."
                : string.Empty;
        }
        catch (Exception ex) { _notice.Text = ex.Message; }
    }

    private async Task ConfirmCancelAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            RequestedTheme = _root.ActualTheme,
            Title = "Cancel package?",
            Content = new TextBlock
            {
                Text = "Cancel the unfinished files in this package? Finished files and partial data are kept. " +
                       "Resume can undo this only if the cancel stopped every file in the package.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Cancel package",
            PrimaryButtonStyle = InboundUi.DangerDialogButton,
            CloseButtonText = "Keep downloading",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await DialogGuard.ShowAsync(dialog) == ContentDialogResult.Primary) await RunPackageActionAsync("cancel");
    }

    /// <summary>The header's chip: the package's state in its status color.</summary>
    private void SetHeaderChip(List<JobDto> members, bool allDone, bool anyFailed, double speed)
    {
        if (members.Count == 0) { _header.Chip.Set(string.Empty, "InboundStatusNeutralBrush"); return; }
        var settled = CompletionBurst.Settled(members.Select(job => (string?)job.State).ToList());
        if (allDone) _header.Chip.Set("Completed", "InboundStatusDoneBrush");
        else if (anyFailed && settled) _header.Chip.Set("Failed", "InboundStatusFailedBrush");
        else if (settled) _header.Chip.Set("Stopped", "InboundStatusNeutralBrush");
        else if (speed > 0 || members.Any(job => job.State is "downloading" or "connecting")) _header.Chip.Set("Downloading", "InboundStatusActiveBrush");
        else _header.Chip.Set(members.Any(job => job.State == "queued") ? "Queued" : "Paused", "InboundStatusWaitingBrush");
    }

    /// <summary>Delete, as Progress Details has it: the package's files leave the list, and their files go too unless unticked.</summary>
    private async Task ConfirmDeleteAsync()
    {
        var ids = _members.Where(_jobs.ContainsKey).ToList();
        if (ids.Count == 0) return;
        var deleteFiles = new CheckBox { Content = "Also delete the downloaded files", IsChecked = true };
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = $"Delete the {ids.Count} file(s) of this package from the list?",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(deleteFiles);
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            RequestedTheme = _root.ActualTheme,
            Title = "Delete package?",
            Content = body,
            PrimaryButtonText = "Delete",
            PrimaryButtonStyle = InboundUi.DangerDialogButton,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary || _closed) return;
        try
        {
            var reply = await _connection.RemoveManyAsync(ids, deleteFiles.IsChecked == true);
            if (reply.Type == MessageTypes.JobActionResult && reply.GetPayload<JobActionResponse>() is { Ok: false } result)
            {
                _notice.Text = result.Error ?? "The Host could not delete this package.";
                return;
            }
            Close();
        }
        catch (Exception ex) { if (!_closed) _notice.Text = ex.Message; }
    }

    private static string? FolderOf(JobDto job)
        => !string.IsNullOrWhiteSpace(job.FinalFilePath) ? Path.GetDirectoryName(job.FinalFilePath) : job.DestinationDirectory;

    private void OpenFileWindow(Guid jobId)
    {
        if (_openFileWindow is { } open)
        {
            open(jobId);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, WindowInstance.Arguments("job", jobId, _connection.Target, manual: true)) { UseShellExecute = false });
        }
        catch (Exception ex) { _notice.Text = $"Could not open progress details: {ex.Message}"; }
    }

    private void OpenFolder()
    {
        if (!RemoteDataRules.MayUseFiles(_connection.IsRemote)) return;
        var folder = _members.Select(id => _jobs.TryGetValue(id, out var job) ? Path.GetDirectoryName(job.FinalFilePath) : null)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
        if (folder is null) { _notice.Text = "The download folder was not found."; return; }
        try { ShellActions.Start(ShellActions.OpenFolder(folder))?.Dispose(); }
        catch (Exception ex) { _notice.Text = $"Could not open the folder: {ex.Message}"; }
    }

    private static string StateText(JobDto job) => job.State switch
    {
        "downloading" => job.TotalBytes is > 0 ? $"{Percent(job.ReceivedBytes, job.TotalBytes):0}%" : "Downloading",
        "connecting" => "Connecting",
        "queued" => "Queued",
        "paused" => "Paused",
        "completed" => "Complete",
        "failed" => "Failed",
        "cancelled" => "Cancelled",
        "retryWaiting" or "retrying" => "Retrying",
        _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(job.State),
    };

    private static double Percent(long received, long? total)
        => total is > 0 ? Math.Clamp(received * 100.0 / total.Value, 0, 100) : 0;

    private static string Bytes(long bytes) => UiFormat.Bytes(bytes);
}
