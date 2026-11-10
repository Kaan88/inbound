using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.AvaloniaUi;

/// <summary>
/// The main window: the WinUI MainWindow's layout and its list logic (snapshot, job events, packages, filters, sort),
/// carried over with the WinUI types swapped for Avalonia's. Everything about the Host goes through the shared
/// <see cref="HostConnection"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly HostConnection _connection = new();
    private readonly RemoteClient _remote = new(action => Dispatcher.UIThread.Post(action));
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<Guid, JobRow> _jobsById = [];
    private readonly Dictionary<Guid, JobRow> _packageRows = [];
    private readonly HashSet<Guid> _expandedPackages = [];
    private ObservableCollection<JobRow> _visibleJobs = [];
    private List<DownloadGroupDto> _groups = [];
    private SettingsDto? _settings;
    private HostStatusDto? _status;
    private string _category = "all";
    private string _sortKey = "added";
    private bool _sortAscending;
    private bool _connected;
    private bool _snapshotReady;

    // One sample a second, but only while something transfers: an idle window has no timer running (owner priority:
    // idle wake-ups). The WinUI window ticks every second whenever it is open.
    private readonly DispatcherTimer _sampleTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        JobsList.ItemsSource = _visibleJobs;
        BuildHeader();
        CategoryList.SelectedIndex = 0;
        _sampleTimer.Tick += (_, _) => Sample();
        ApplyBackdrop();
        ActualThemeVariantChanged += (_, _) => OnThemeChanged();
        OnThemeChanged();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) SpeedGraph.IsSeen = WindowState != WindowState.Minimized && SpeedGraph.IsVisible;
        };

        _connection.SnapshotReceived += snapshot => Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));
        _connection.JobChanged += (job, type) => Dispatcher.UIThread.Post(() => ApplyJobEvent(job, type));
        _connection.GroupsChanged += groups => Dispatcher.UIThread.Post(() => { _groups = groups; RebuildVisible(); });
        _connection.ConnectionChanged += (connected, error) => Dispatcher.UIThread.Post(() => ApplyConnection(connected, error));
        _connection.HostExiting += () => Dispatcher.UIThread.Post(Close);
        _connection.Start();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        UpdateButtons();
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _sampleTimer.Stop();
        _lifetime.Cancel();
        await _remote.DisposeAsync();
        await _connection.DisposeAsync();
        _lifetime.Dispose();
    }

    // ---- Look: Mica / blur where the platform has it (UI.R30), solid tokens otherwise ----

    private FrostTokens? _frost;

    private void ApplyBackdrop()
    {
        TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
        void Sync()
        {
            var level = ActualTransparencyLevel;
            var frosted = level != WindowTransparencyLevel.None && level != WindowTransparencyLevel.Transparent;
            if (frosted && _frost is null)
            {
                _frost = new FrostTokens();
                Resources.MergedDictionaries.Add(_frost);
            }
            else if (!frosted && _frost is not null)
            {
                Resources.MergedDictionaries.Remove(_frost);
                _frost = null;
            }
            if (frosted) Background = Brushes.Transparent;
            else Bind(BackgroundProperty, this.GetResourceObservable("InboundSurfaceBrush"));
            Backdrop = level.ToString();
        }
        PropertyChanged += (_, e) => { if (e.Property == ActualTransparencyLevelProperty) Sync(); };
        Sync();
    }

    /// <summary>What the platform gave: Mica, AcrylicBlur, Blur or None.</summary>
    internal string Backdrop { get; private set; } = "None";

    private void OnThemeChanged()
    {
        StatusBrushes.UseTheme(ActualThemeVariant);
        DarkModeMenuItem.IsChecked = ActualThemeVariant == ThemeVariant.Dark;
    }

    // ---- Header from the WinUI column set ----

    private readonly Dictionary<string, TextBlock> _headerText = [];

    private void BuildHeader()
    {
        var columns = ListColumnSets.Downloads().Columns.Where(column => column.Key != "extraction").ToList();
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            JobsHeader.ColumnDefinitions.Add(column.Key == "name"
                ? new ColumnDefinition(1, GridUnitType.Star)
                : new ColumnDefinition(column.DefaultWidth, GridUnitType.Pixel));
            if (!column.HasHeader) continue;
            var text = new TextBlock
            {
                Text = column.Title,
                Classes = { "header" },
                HorizontalAlignment = column.RightAligned ? HorizontalAlignment.Right : column.Centered ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                Margin = column.RightAligned ? new Thickness(0, 0, ListColumn.RightPadding - 4, 0) : default,
            };
            _headerText[column.Key] = text;
            var button = new Button { Classes = { "header" }, Content = text, Tag = column.Key };
            ToolTip.SetTip(button, $"Sort by {column.Spoken}");
            button.Click += (_, _) =>
            {
                (_sortKey, _sortAscending) = ListSort.Next(_sortKey, _sortAscending, column.SortKey());
                ShowSortHeader();
                RebuildVisible(sorting: true);
            };
            Grid.SetColumn(button, i);
            JobsHeader.Children.Add(button);
        }
    }

    private void ShowSortHeader()
    {
        foreach (var (key, text) in _headerText)
        {
            var title = ListColumnSets.Downloads().Columns.First(column => column.Key == key).Title;
            text.Text = key == _sortKey ? $"{title} {(_sortAscending ? "▲" : "▼")}" : title;
        }
    }

    // ---- Host data (the WinUI MainWindow's ApplySnapshot / ApplyJobEvent / RebuildVisible) ----

    private void ApplySnapshot(SnapshotDto snapshot)
    {
        if (!_snapshotReady) _ = LoadSpeedHistoryAsync();
        _snapshotReady = true;
        _settings = snapshot.Settings;
        _status = snapshot.Status;
        _groups = snapshot.Settings.DownloadGroups;
        FollowThemeSetting(snapshot.Settings.Theme);
        var selected = SelectedIds();
        _jobsById.Clear();
        foreach (var job in snapshot.Jobs)
        {
            var row = new JobRow();
            row.Update(job);
            _jobsById[row.Id] = row;
        }
        RebuildVisible(selected);
        UpdateStatusText();
        SyncSampling();
    }

    private void ApplyJobEvent(JobDto job, string changeType)
    {
        if (changeType == "removed")
        {
            if (_jobsById.Remove(job.Id, out var removed))
            {
                _visibleJobs.Remove(removed);
                if (removed.PackageId is not null) RebuildVisible();
            }
        }
        else if (PackageOf(job.Id) is { } package)
        {
            if (!_jobsById.TryGetValue(job.Id, out var member)) _jobsById[job.Id] = member = new JobRow();
            member.Update(job);
            var row = _packageRows.GetValueOrDefault(package.Id);
            var shouldShow = row is not null && row.IsExpanded && Matches(member);
            if (row is null || member.PackageId != package.Id || shouldShow != _visibleJobs.Contains(member)) RebuildVisible();
            else row.UpdatePackage(package, PackageMembers(package), row.IsExpanded);
        }
        else
        {
            if (!_jobsById.TryGetValue(job.Id, out var row)) _jobsById[job.Id] = row = new JobRow();
            row.Update(job);
            if (row.PackageId is not null)
            {
                row.PackageId = null;
                RebuildVisible();
            }
            else
            {
                var isVisible = _visibleJobs.Contains(row);
                if (Matches(row) && !isVisible) _visibleJobs.Insert(0, row);
                else if (!Matches(row) && isVisible) _visibleJobs.Remove(row);
            }
        }

        EmptyText.IsVisible = _visibleJobs.Count == 0;
        UpdateStatusText();
        UpdateButtons();
        SyncSampling();
    }

    private void ApplyConnection(bool connected, string? error)
    {
        _connected = connected;
        TorBoxCategory.IsEnabled = connected && _connection.Hello?.Capabilities.Contains("remote") == true;
        TorBoxButton.IsEnabled = TorBoxCategory.IsEnabled;
        if (!TorBoxCategory.IsEnabled && TorBoxView.IsVisible) CategoryList.SelectedIndex = 0;
        _snapshotReady = false;
        ConnectionText.Text = connected ? "Host connected" : $"Host unavailable{(error is null ? string.Empty : $": {error}")}";
        if (connected) UpdateStatusText();
        else SpeedGraph.AddSample(DateTimeOffset.UtcNow, null);
        EmptyText.IsVisible = _visibleJobs.Count == 0;
        UpdateButtons();
        SyncSampling();
    }

    private void FollowThemeSetting(string? theme)
    {
        if (_themeChosenHere) return;
        RequestedThemeVariant = ThemeChoices.Normalize(theme) switch
        {
            ThemeChoices.Light => ThemeVariant.Light,
            ThemeChoices.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private bool _themeChosenHere;

    private DownloadGroupDto? PackageOf(Guid jobId) =>
        _groups.FirstOrDefault(group => group.JobIds.Contains(jobId) && group.JobIds.Count(_jobsById.ContainsKey) >= 2);

    private List<JobRow> PackageMembers(DownloadGroupDto group) => group.JobIds
        .Where(_jobsById.ContainsKey).Select(id => _jobsById[id])
        .OrderBy(job => job.FileName, NaturalStringComparer.Instance).ToList();

    private bool Matches(JobRow job) => _category switch
    {
        "all" or "queues" => true,
        "finished" => job.State == "completed",
        "unfinished" => job.State is not ("completed" or "cancelled"),
        "active" => job.State is "connecting" or "downloading" or "verifying" or "retryWaiting",
        "queued" => job.State is "queued" or "paused",
        "failed" => job.State == "failed",
        _ => job.CategoryId == _category,
    };

    private IEnumerable<JobRow> Sorted(IEnumerable<JobRow> rows) => _sortKey switch
    {
        "name" => ListSort.Order(rows, job => job.FileName, NaturalStringComparer.Instance, _sortAscending),
        "queue" => ListSort.Order(rows, job => job.QueueText, NaturalStringComparer.Instance, _sortAscending, job => job.QueueText.Length == 0),
        "size" => ListSort.Order(rows, job => job.TotalSize, null, _sortAscending, job => job.TotalSize <= 0),
        "status" => ListSort.Order(rows, job => job.StateText, NaturalStringComparer.Instance, _sortAscending),
        "eta" => ListSort.Order(rows, job => job.Remaining, null, _sortAscending, job => job.Remaining is null),
        "rate" => ListSort.Order(rows, job => job.BytesPerSecond, null, _sortAscending, job => job.BytesPerSecond <= 0),
        "lastTry" => ListSort.Order(rows, job => job.LastUpdated, null, _sortAscending),
        "description" => ListSort.Order(rows, job => job.Description, NaturalStringComparer.Instance, _sortAscending, job => job.Description.Length == 0),
        _ => ListSort.Order(rows, job => job.CreatedAt, null, _sortAscending),
    };

    private void RebuildVisible(HashSet<Guid>? keepSelected = null, bool sorting = false)
    {
        keepSelected ??= SelectedIds();
        var packaged = new HashSet<Guid>();
        var top = new List<JobRow>();
        var liveRows = new HashSet<Guid>();
        foreach (var group in _groups)
        {
            var members = PackageMembers(group);
            if (members.Count < 2) continue;
            foreach (var member in members) { packaged.Add(member.Id); member.PackageId = group.Id; }
            if (!members.Any(Matches)) continue;
            if (!_packageRows.TryGetValue(group.Id, out var row)) _packageRows[group.Id] = row = new JobRow();
            row.UpdatePackage(group, members, _expandedPackages.Contains(group.Id));
            liveRows.Add(group.Id);
            top.Add(row);
        }
        foreach (var stale in _packageRows.Keys.Where(id => !liveRows.Contains(id)).ToList()) _packageRows.Remove(stale);
        foreach (var job in _jobsById.Values)
        {
            if (packaged.Contains(job.Id)) continue;
            job.PackageId = null;
            if (Matches(job)) top.Add(job);
        }

        var target = new List<JobRow>();
        foreach (var row in Sorted(top))
        {
            target.Add(row);
            if (row.IsPackage && row.IsExpanded)
                foreach (var child in _sortKey == "added" ? row.Children.Where(Matches) : Sorted(row.Children.Where(Matches))) target.Add(child);
        }

        // In place, as in WinUI, so rows that stay keep their containers.
        if (ListSort.ReplacesWholeList(sorting, _visibleJobs, target))
        {
            _visibleJobs = new ObservableCollection<JobRow>(target);
            JobsList.ItemsSource = _visibleJobs;
        }
        for (var i = 0; i < target.Count; i++)
        {
            if (i < _visibleJobs.Count && ReferenceEquals(_visibleJobs[i], target[i])) continue;
            var existing = -1;
            for (var j = i + 1; j < _visibleJobs.Count; j++)
                if (ReferenceEquals(_visibleJobs[j], target[i])) { existing = j; break; }
            if (existing >= 0) _visibleJobs.Move(existing, i);
            else _visibleJobs.Insert(i, target[i]);
        }
        while (_visibleJobs.Count > target.Count) _visibleJobs.RemoveAt(_visibleJobs.Count - 1);

        var want = _visibleJobs.Where(row => keepSelected.Contains(row.Id)).ToList();
        if (!JobsList.SelectedItems!.OfType<JobRow>().ToHashSet().SetEquals(want))
        {
            JobsList.SelectedItems!.Clear();
            foreach (var row in want) JobsList.SelectedItems.Add(row);
        }

        EmptyText.IsVisible = _visibleJobs.Count == 0;
        UpdateButtons();
    }

    private HashSet<Guid> SelectedIds() => JobsList.SelectedItems?.OfType<JobRow>().Select(row => row.Id).ToHashSet() ?? [];

    private List<JobRow> SelectedRows => JobsList.SelectedItems?.OfType<JobRow>().ToList() ?? [];

    /// <summary>Selected files; a selected package contributes all of its files.</summary>
    private List<JobRow> SelectedJobs => SelectedRows.SelectMany(row => row.IsPackage ? row.Children : [row]).Distinct().ToList();

    private void UpdateButtons()
    {
        var rows = SelectedRows;
        var selection = SelectedJobs;
        AddButton.IsEnabled = _connected;
        PauseButton.IsEnabled = _connected && rows.Any(row => row.CanPause);
        ResumeButton.IsEnabled = _connected && rows.Any(row => row.CanResume);
        DeleteButton.IsEnabled = _connected && selection.Count > 0;
        StopAllButton.IsEnabled = _connected && _jobsById.Values.Any(job => job.CanPause);
        DeleteCompletedButton.IsEnabled = _connected && _jobsById.Values.Any(job => job.State == "completed");
        FileDownloadNowItem.IsEnabled = ResumeButton.IsEnabled;
        FileStopItem.IsEnabled = PauseButton.IsEnabled;
        FileDeleteItem.IsEnabled = DeleteButton.IsEnabled;
        var one = selection.Count == 1 && rows.Count == 1 && !rows[0].IsPackage ? rows[0] : null;
        FileOpenItem.IsEnabled = one is { State: "completed" } && File.Exists(one.FinalFilePath);
        FileOpenFolderItem.IsEnabled = one is not null && Directory.Exists(one.Destination);
    }

    private void UpdateStatusText()
    {
        var active = _jobsById.Values.Count(job => job.State is "connecting" or "downloading" or "verifying");
        var retrying = _jobsById.Values.Count(job => job.State == "retryWaiting");
        var queued = _jobsById.Values.Count(job => job.State == "queued");
        var text = $"{_jobsById.Count} downloads · {active} active · {queued} queued" + (retrying > 0 ? $" · {retrying} retrying" : string.Empty);
        var selected = SelectedJobs.Count;
        if (selected > 1) text += $"  ({selected} selected)";
        StatusText.Text = text;
        if (_connected)
        {
            var host = _status?.HostVersion;
            ConnectionText.Text = "Host connected" + (host is null ? string.Empty : $" · {host}")
                + (_status?.BrowserIntegration is { Length: > 0 } integration ? $" · browser {integration}" : string.Empty);
        }
    }

    // ---- Speed graph ----

    private void SyncSampling()
    {
        var busy = _connected && _jobsById.Values.Any(job => job.State is "connecting" or "downloading" or "verifying" or "retryWaiting");
        if (busy && !_sampleTimer.IsEnabled) { Sample(); _sampleTimer.Start(); }
        else if (!busy && _sampleTimer.IsEnabled) { _sampleTimer.Stop(); Sample(); }
    }

    private void Sample()
    {
        var now = DateTimeOffset.UtcNow;
        double? rate = !_connected || !_snapshotReady
            ? null
            : _jobsById.Values.Where(job => job.State is "connecting" or "downloading" or "verifying" or "retryWaiting" && now - job.LastUpdated <= TimeSpan.FromSeconds(10))
                .Sum(job => double.IsFinite(job.BytesPerSecond) ? Math.Max(0, job.BytesPerSecond) : 0);
        SpeedGraph.AddSample(now, rate);
    }

    private async Task LoadSpeedHistoryAsync()
    {
        try
        {
            var reply = await _connection.GetSpeedHistoryAsync();
            if (reply.Type != MessageTypes.SpeedHistoryResult) return;
            var history = reply.GetPayload<SpeedHistoryDto>();
            SpeedGraph.LoadHistory(history.Start, history.IntervalSeconds, history.BytesPerSecond);
        }
        catch (Exception)
        {
            // An older Host has no history; the graph starts from now.
        }
    }

    // ---- Commands ----

    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        if (!_connected) return;
        var dialog = new AddUrlDialog(_settings?.DefaultDownloadDirectory ?? string.Empty);
        var request = await dialog.ShowDialog<AddJobRequest?>(this);
        if (request is null) return;
        try
        {
            var add = (await _connection.AddAsync(request)).GetPayload<AddJobResponse>();
            if (!add.Accepted) await MessageDialog.ShowAsync(this, "Add URL", add.Error ?? "The host rejected the download.");
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(this, "Add URL", ex.Message);
        }
    }

    private async void Resume_Click(object? sender, RoutedEventArgs e)
        => await RunAsync("Resume", SelectedRows.Where(row => row.CanResume), row => row.IsPackage
            ? _connection.GroupActionAsync(new GroupActionRequest { GroupId = row.Id, Action = "resume" })
            : _connection.ResumeAsync(row.Id));

    private async void Pause_Click(object? sender, RoutedEventArgs e)
        => await RunAsync("Stop", SelectedRows.Where(row => row.CanPause), row => row.IsPackage
            ? _connection.GroupActionAsync(new GroupActionRequest { GroupId = row.Id, Action = "pause" })
            : _connection.PauseAsync(row.Id));

    private async void StopAll_Click(object? sender, RoutedEventArgs e)
    {
        try { await _connection.PauseAllAsync(); }
        catch (Exception ex) { await MessageDialog.ShowAsync(this, "Stop All", ex.Message); }
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        var selection = SelectedJobs;
        if (selection.Count == 0) return;
        var anyCompleted = selection.Any(job => job.State == "completed");
        var title = selection.Count == 1 ? "Delete download?" : $"Delete {selection.Count} downloads?";
        var question = selection.Count == 1 ? $"Delete \"{selection[0].FileName}\" from the list?" : $"Delete {selection.Count} downloads from the list?";
        var choice = await MessageDialog.ConfirmAsync(this, title,
            anyCompleted ? question : question + " Partial data is always removed.",
            "Delete", anyCompleted ? "Also delete the downloaded files" : null);
        if (choice is not { } deleteFiles) return;
        try
        {
            var result = (await _connection.RemoveManyAsync(selection.Select(job => job.Id).ToList(), deleteFiles || !anyCompleted))
                .GetPayload<JobActionResponse>();
            if (!result.Ok) await MessageDialog.ShowAsync(this, "Delete incomplete", result.Error ?? "Some downloads could not be removed.");
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(this, "Delete incomplete", ex.Message);
        }
    }

    private async void DeleteCompleted_Click(object? sender, RoutedEventArgs e)
    {
        var completed = _jobsById.Values.Where(job => job.State == "completed").ToList();
        if (completed.Count == 0) return;
        var choice = await MessageDialog.ConfirmAsync(this, "Delete completed",
            $"Remove {completed.Count} completed item(s) from the list? The downloaded files will stay on disk.", "Remove from list", null);
        if (choice is null) return;
        try { await _connection.RemoveManyAsync(completed.Select(job => job.Id).ToList(), deleteFile: false); }
        catch (Exception ex) { await MessageDialog.ShowAsync(this, "Delete completed", ex.Message); }
    }

    private async Task RunAsync(string label, IEnumerable<JobRow> rows, Func<JobRow, Task<IpcMessage>> action)
    {
        var failures = new List<string>();
        foreach (var row in rows.ToList())
        {
            try
            {
                var reply = await action(row);
                if (reply.Type == MessageTypes.JobActionResult && reply.GetPayload<JobActionResponse>() is { Ok: false } result)
                    failures.Add($"{row.FileName}: {result.Error}");
            }
            catch (Exception ex)
            {
                failures.Add($"{row.FileName}: {ex.Message}");
            }
        }
        if (failures.Count > 0) await MessageDialog.ShowAsync(this, label, string.Join(Environment.NewLine, failures));
    }

    private void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedRows is [{ IsPackage: false } row] && File.Exists(row.FinalFilePath)) Shell(row.FinalFilePath);
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedRows is [{ IsPackage: false } row] && Directory.Exists(row.Destination)) Shell(row.Destination);
    }

    private static void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { /* Nothing registered to open it. */ }
    }

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    private async void About_Click(object? sender, RoutedEventArgs e)
        => await MessageDialog.ShowAsync(this, "About Inbound Download Manager",
            $"Avalonia prototype (D7 stage 2), version {Protocol.HostVersion}.\nBackdrop: {Backdrop}. Host: {_status?.HostVersion ?? "not connected"}.");

    private void DarkMode_Click(object? sender, RoutedEventArgs e)
    {
        _themeChosenHere = true;
        RequestedThemeVariant = ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    private void SpeedGraphMenu_Click(object? sender, RoutedEventArgs e)
    {
        SpeedGraph.IsVisible = ShowSpeedGraphMenuItem.IsChecked;
        SpeedGraph.IsSeen = SpeedGraph.IsVisible && WindowState != WindowState.Minimized;
    }

    private void HideCategories_Click(object? sender, RoutedEventArgs e)
    {
        CategoryPane.IsVisible = !HideCategoriesMenuItem.IsChecked;
        MainArea.ColumnDefinitions[0].Width = CategoryPane.IsVisible ? new GridLength(192) : new GridLength(0);
    }

    private void Sort_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key }) return;
        _sortKey = key;
        _sortAscending = ListSort.StartsAscending(key);
        ShowSortHeader();
        RebuildVisible(sorting: true);
    }

    private void TorBox_Click(object? sender, RoutedEventArgs e)
    {
        if (TorBoxCategory.IsEnabled) CategoryList.SelectedItem = TorBoxCategory;
    }

    private async void CategoryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is ListBoxItem { Tag: string tag })
        {
            var torbox = tag == "torbox";
            TorBoxView.IsVisible = torbox;
            DownloadsPane.IsVisible = !torbox;
            if (torbox)
            {
                await TorBoxView.OpenAsync(_remote, _lifetime.Token);
                return;
            }
            _category = tag;
            RebuildVisible();
        }
    }

    private void JobsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        UpdateStatusText();
    }

    private void JobsList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SelectedRows is [{ IsPackage: true } package]) Toggle(package);
    }

    private void Chevron_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: JobRow { IsPackage: true } row })
        {
            Toggle(row);
            e.Handled = true;
        }
    }

    private void Toggle(JobRow package)
    {
        if (!_expandedPackages.Remove(package.Id)) _expandedPackages.Add(package.Id);
        RebuildVisible();
    }

    // Tunnelling, because the list would take the arrow keys for itself.
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.T && e.KeyModifiers == KeyModifiers.Control && TorBoxCategory.IsEnabled)
        {
            CategoryList.SelectedItem = TorBoxCategory;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && JobsList.IsKeyboardFocusWithin) { Delete_Click(this, new RoutedEventArgs()); e.Handled = true; }
        // Right opens a selected package, Left closes it.
        if (e.Key is Key.Right or Key.Left && JobsList.IsKeyboardFocusWithin && SelectedRows is [{ IsPackage: true } package] && package.IsExpanded != (e.Key == Key.Right))
        {
            Toggle(package);
            e.Handled = true;
        }
    }
}
