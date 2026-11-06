using System.Collections.ObjectModel;
using System.Diagnostics;
using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace DownloadManager.Desktop.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<JobViewModel> _visibleJobs = new();
    private readonly Dictionary<Guid, JobViewModel> _jobsById = new();
    private readonly HostConnection _connection;
    private readonly RemoteClient _remote;
    private MenuFlyoutSubItem? _addToQueueMenu;
    private List<DownloadGroupDto> _groups = new();
    private readonly Dictionary<Guid, JobViewModel> _packageRows = new();
    private StatisticsWindow? _statisticsWindow;
    private SchedulerWindow? _schedulerWindow;
    private SettingsDto? _settings;
    private HostStatusDto? _status;
    private SettingsWindow? _settingsWindow;
    private bool _pendingSettingsOpen;
    private string _category = "all";
    private static readonly TimeSpan PassingStatusTime = TimeSpan.FromSeconds(5);
    private string _search = string.Empty;
    private string? _passingStatus;
    private DateTimeOffset _passingStatusUntil;
    private string _sortKey = "added";
    private bool _sortAscending;
    private bool _toolbarLarge = true;
    private bool _hideCategories;
    private bool _refreshingCategories;
    private UiState _uiState = new();
    private bool _graphConnected;
    private bool _graphSnapshotReady;
    private readonly DispatcherTimer _speedHistoryTimer = new() { Interval = TimeSpan.FromSeconds(1) };


    public MainWindow()
    {
        UiTrace.Write("MainWindow ctor begin");
        _uiState = UiStateStore.Load();
        InitializeComponent();
        UiTrace.Write("MainWindow InitializeComponent complete");
        RootGrid.RequestedTheme = _uiState.DarkMode is { } darkMode
            ? (darkMode ? ElementTheme.Dark : ElementTheme.Light)
            : ElementTheme.Default;
        SpeedGraph.Visibility = _uiState.ShowSpeedGraph ? Visibility.Visible : Visibility.Collapsed;
        ShowSpeedGraphMenuItem.IsChecked = _uiState.ShowSpeedGraph;
        SpeedGraph.IsExpanded = _uiState.SpeedGraphExpanded;
        SpeedGraph.ExpandedChanged += (_, _) => SaveUiState();
        _speedHistoryTimer.Tick += (_, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            var active = _jobsById.Values.Where(job => job.State is "connecting" or "downloading" or "verifying" or "retryWaiting").ToArray();
            // A gap means "no data" (Host disconnected). A download that has gone quiet (stalled,
            // connecting, waiting to retry) is transferring nothing, so it counts as 0, not unknown.
            double? rate = !_graphConnected || !_graphSnapshotReady
                ? null
                : active.Where(job => now - job.LastUpdated <= TimeSpan.FromSeconds(10))
                    .Sum(job => double.IsFinite(job.BytesPerSecond) ? Math.Max(0, job.BytesPerSecond) : 0);
            SpeedGraph.AddSample(now, rate);
        };
        _speedHistoryTimer.Start();
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            DarkModeMenuItem.IsChecked = RootGrid.ActualTheme == ElementTheme.Dark;
            ApplyTitleBarTheme(DarkModeMenuItem.IsChecked);
            StatusBrushes.UseTheme(RootGrid.ActualTheme);
            // They share the status colors, so they can't be left on the other theme.
            foreach (var window in _providerWindows.Values) window.UseTheme(RootGrid.RequestedTheme);
        };
        DarkModeMenuItem.IsChecked = RootGrid.ActualTheme == ElementTheme.Dark;
        StatusBrushes.UseTheme(RootGrid.ActualTheme);
        JobsList.ItemsSource = _visibleJobs;
        BuildJobsContextMenu();
        UiTrace.Write("MainWindow collections initialized");

        try
        {
            var width = Math.Clamp(_uiState.WindowWidth, 900, 8000);
            var height = Math.Clamp(_uiState.WindowHeight, 560, 8000);

            // A size saved at close is real pixels. The first one isn't, or a scaled screen would get a tiny window.
            if (width == UiState.FirstWindowWidth && height == UiState.FirstWindowHeight) WindowLayout.ResizeForDpi(this, width, height);
            else AppWindow.Resize(new SizeInt32(width, height));
            WindowPlacement.Track(this, "main");
            var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }

            ApplyTitleBarTheme(DarkModeMenuItem.IsChecked);
        }
        catch (Exception)
        {
            // Window chrome is cosmetic; startup must not depend on it.
        }

        UiTrace.Write("MainWindow chrome initialized");
        AppWindow.Closing += OnWindowClosing;

        _connection = new HostConnection();
        _connection.SnapshotReceived += snapshot => DispatcherQueue.TryEnqueue(() => ApplySnapshot(snapshot));
        _connection.JobChanged += (job, type) => DispatcherQueue.TryEnqueue(() => ApplyJobEvent(job, type));
        _connection.GroupsChanged += groups => DispatcherQueue.TryEnqueue(() => { _groups = groups; RebuildVisible(); });
        _connection.ConnectionChanged += (connected, error) =>
            DispatcherQueue.TryEnqueue(() => ApplyConnection(connected, error));
        _connection.HostExiting += () => DispatcherQueue.TryEnqueue(() =>
        {
            UiTrace.Write("Host announced application exit");
            Application.Current.Exit();
        });
        _connection.Start();
        UiTrace.Write("MainWindow connection started");

        // On connections of its own: nothing it waits for can hold up the one above.
        _remote = new RemoteClient(action => DispatcherQueue.TryEnqueue(() => action()));
        InitializeRemoteUi();
        InitializeArchiveUi();
        _remote.Start();

        RootGrid.KeyboardAccelerators.Add(CreateAccelerator(
            Windows.System.VirtualKey.N,
            Windows.System.VirtualKeyModifiers.Control,
            () => AddButton_Click(this, new RoutedEventArgs())));
        RootGrid.KeyboardAccelerators.Add(CreateAccelerator(
            Windows.System.VirtualKey.F,
            Windows.System.VirtualKeyModifiers.Control,
            () => MenuFind_Click(this, new RoutedEventArgs())));
        RootGrid.KeyboardAccelerators.Add(CreateAccelerator(
            Windows.System.VirtualKey.F3,
            Windows.System.VirtualKeyModifiers.None,
            () => MenuFindNext_Click(this, new RoutedEventArgs())));
        RootGrid.KeyboardAccelerators.Add(CreateAccelerator(
            Windows.System.VirtualKey.M,
            Windows.System.VirtualKeyModifiers.Control,
            () => MenuMoveRename_Click(this, new RoutedEventArgs())));
        RootGrid.KeyboardAccelerators.Add(CreateAccelerator(
            Windows.System.VirtualKey.S,
            Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
            () =>
            {
                _sortKey = "size";
                _sortAscending = false;
                RebuildVisible();
            }));

        _sortKey = _uiState.SortKey is "added" or "name" or "size" or "status" or "rate" or "lastTry"
            ? _uiState.SortKey
            : "added";
        _sortAscending = _uiState.SortAscending;
        _toolbarLarge = _uiState.ToolbarLarge;
        _hideCategories = _uiState.HideCategories;
        HideCategoriesMenuItem.IsChecked = _hideCategories;
        CategoryColumn.Width = _hideCategories ? new GridLength(0) : new GridLength(192);
        SetToolbarScale(_toolbarLarge);
        CategoryList.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(_uiState.Category))
        {
            var saved = CategoryList.Items
                .OfType<ListBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, _uiState.Category,
                    StringComparison.OrdinalIgnoreCase));
            if (saved is not null && saved.IsEnabled &&
                saved.Tag is not "queues")
            {
                CategoryList.SelectedItem = saved;
            }
            else if (_uiState.Category.StartsWith("category:", StringComparison.Ordinal))
            {
                _category = _uiState.Category; // Restored once Host category definitions arrive.
            }
            else if (_uiState.Category.StartsWith(RemoteWaitingTag, StringComparison.Ordinal) || _uiState.Category == ArchiveSetsTag)
            {
                _pendingRemoteCategory = _uiState.Category; // Restored once the Host says what it has.
            }
        }

        UpdateButtons();
        UiTrace.Write("MainWindow ctor end");
    }

    private static KeyboardAccelerator CreateAccelerator(
        Windows.System.VirtualKey key,
        Windows.System.VirtualKeyModifiers modifiers,
        Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            action();
        };
        return accelerator;
    }

    public void ShowFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    public void RequestSettingsPage()
    {
        ShowFromTray();
        _pendingSettingsOpen = true;
        if (_settings is not null)
        {
            _pendingSettingsOpen = false;
            _ = ShowSettingsDialogAsync();
        }
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Closing the window hides it. Downloads and the browser bridge stay alive
        // in the background host; Exit is an explicit tray/menu action.
        SaveUiState(sender);
        args.Cancel = true;
        sender.Hide();
    }

    private void SaveUiState(AppWindow? window = null)
    {
        try
        {
            // One state object: TogglePackage saves it too, so neither can undo the other's changes.
            var size = (window ?? AppWindow).Size;
            _uiState.WindowWidth = size.Width;
            _uiState.WindowHeight = size.Height;
            _uiState.Category = _category;
            _uiState.SortKey = _sortKey;
            _uiState.SortAscending = _sortAscending;
            _uiState.ToolbarLarge = _toolbarLarge;
            _uiState.HideCategories = _hideCategories;
            _uiState.ShowSpeedGraph = ShowSpeedGraphMenuItem.IsChecked;
            _uiState.SpeedGraphExpanded = SpeedGraph.IsExpanded;
            UiStateStore.Save(_uiState);
        }
        catch (Exception ex)
        {
            UiTrace.Write($"UI state save failed: {ex.Message}");
        }
    }

    private void ApplySnapshot(SnapshotDto snapshot)
    {
        _graphSnapshotReady = true;
        _settings = snapshot.Settings;
        _groups = snapshot.Settings.DownloadGroups;
        RefreshCustomCategories();
        _status = snapshot.Status;
        RefreshQueueMenus();

        var selectedId = (JobsList.SelectedItem as JobViewModel)?.Id;
        _jobsById.Clear();
        foreach (var job in snapshot.Jobs)
        {
            var vm = new JobViewModel();
            vm.Update(job);
            _jobsById[vm.Id] = vm;
        }

        RebuildVisible(selectedId);
        UpdateStatusText();

        if (_pendingSettingsOpen)
        {
            _pendingSettingsOpen = false;
            _ = ShowSettingsDialogAsync();
        }
    }

    private void ApplyJobEvent(JobDto job, string changeType)
    {
        if (changeType == "removed")
        {
            if (_jobsById.Remove(job.Id, out var removed))
            {
                _visibleJobs.Remove(removed);
                if (removed.PackageId is not null) RebuildVisible();

                // A record whose last file left the list now needs a row of its own.
                if (_setByJob.ContainsKey(job.Id)) QueueArchiveSync();
            }
        }
        else if (PackageOf(job.Id) is { } package)
        {
            if (!_jobsById.TryGetValue(job.Id, out var member))
            {
                member = new JobViewModel();
                _jobsById[job.Id] = member;
            }

            member.Update(job);
            ShowProviderSource(member);
            ShowArchiveState(member);
            var row = _packageRows.GetValueOrDefault(package.Id);
            var shouldShow = row is not null && row.IsExpanded && Matches(member);
            if (row is null || member.PackageId != package.Id || shouldShow != _visibleJobs.Contains(member))
                RebuildVisible();
            else
                row.UpdatePackage(package, PackageMembers(package), row.IsExpanded);
        }
        else
        {
            if (!_jobsById.TryGetValue(job.Id, out var vm))
            {
                vm = new JobViewModel();
                _jobsById[job.Id] = vm;
            }

            vm.Update(job);
            ShowProviderSource(vm);
            ShowArchiveState(vm);
            if (vm.PackageId is not null)
            {
                // It just left its package.
                vm.PackageId = null;
                RebuildVisible();
                UpdateStatusText();
                UpdateButtons();
                return;
            }
            var isVisible = _visibleJobs.Contains(vm);
            if (Matches(vm))
            {
                if (!isVisible)
                {
                    // Under the remote rows, which stay on top.
                    _visibleJobs.Insert(_visibleJobs.TakeWhile(row => row.IsSideRow).Count(), vm);
                }
            }
            else if (isVisible)
            {
                _visibleJobs.Remove(vm);
            }
        }

        EmptyText.Visibility = _visibleJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusText();
        UpdateButtons();

        // Preserve notifications for headless jobs when the main UI is already open.
        if (changeType == "completed" && job.Source == "cli" && (job.ShowCompletionDialog ?? _settings?.ShowCompletionDialog) == true)
            OpenDownloadWindow(job.Id);
    }

    private void ApplyConnection(bool connected, string? error)
    {
        _graphConnected = connected;
        _graphSnapshotReady = false;
        ConnectionText.Text = connected
            ? "Host connected"
            : $"Host unavailable{(error is null ? string.Empty : $": {error}")}";
        if (connected) UpdateStatusText();

        // The rows stay on the list while the Host is away, so it isn't empty.
        EmptyText.Visibility = _visibleJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    /// <summary>What single-item commands act on. A remote row is never it: those have commands of their own.</summary>
    private JobViewModel? FirstLocalRow => JobsList.SelectedItem is JobViewModel { IsSideRow: false } row
        ? row
        : JobsList.SelectedItems.OfType<JobViewModel>().FirstOrDefault(candidate => !candidate.IsSideRow);

    private JobViewModel? SelectedJob => FirstLocalRow is { IsPackage: false } job ? job : null;

    /// <summary>Selected files; a selected package row contributes all of its files.</summary>
    private List<JobViewModel> SelectedJobs =>
        JobsList.SelectedItems.OfType<JobViewModel>()
            .Where(row => !row.IsSideRow)
            .SelectMany(row => row.IsPackage ? row.Children : [row])
            .Distinct().ToList();

    private JobViewModel? SelectedPackage => FirstLocalRow is { IsPackage: true } row ? row : null;

    /// <summary>Selected package rows. The Host acts on each as one unit.</summary>
    private List<JobViewModel> SelectedPackageRows => JobsList.SelectedItems.OfType<JobViewModel>().Where(row => row.IsPackage).ToList();

    /// <summary>Selected file rows that a selected package row does not already cover.</summary>
    private List<JobViewModel> SelectedLooseFiles
    {
        get
        {
            var packages = SelectedPackageRows.Select(row => row.Id).ToHashSet();
            return JobsList.SelectedItems.OfType<JobViewModel>()
                .Where(row => !row.IsSideRow && !row.IsPackage && (row.PackageId is not { } package || !packages.Contains(package)))
                .ToList();
        }
    }

    /// <summary>The package a job is shown in; only packages with at least two listed files count.</summary>
    private DownloadGroupDto? PackageOf(Guid jobId) =>
        _groups.FirstOrDefault(group => group.JobIds.Contains(jobId) && group.JobIds.Count(_jobsById.ContainsKey) >= 2);

    private List<JobViewModel> PackageMembers(DownloadGroupDto group) => group.JobIds
        .Where(_jobsById.ContainsKey).Select(id => _jobsById[id])
        .OrderBy(job => job.FileName, NaturalStringComparer.Instance).ToList();

    private void UpdateButtons()
    {
        var connected = _connection?.IsConnected ?? false;
        var selection = SelectedJobs;
        var rows = JobsList.SelectedItems.OfType<JobViewModel>().ToList();
        AddButton.IsEnabled = connected;
        PauseButton.IsEnabled = connected && rows.Any(row => row.CanPause);
        ResumeButton.IsEnabled = connected && rows.Any(row => row.CanResume);
        CancelPackageButton.Visibility = rows.Any(row => row.IsPackage) ? Visibility.Visible : Visibility.Collapsed;
        CancelPackageButton.IsEnabled = connected && rows.Any(row => row.CanCancelPackage);
        var sideRows = rows.Count(row => row.IsSideRow);
        var removableSideRows = FirstLocalRow is null && sideRows > 0 ? SelectedRemoteJobs.Count + CurrentArchivePlan().Remove.Count : 0;
        DeleteButton.IsEnabled = connected && (selection.Count > 0 || removableSideRows > 0);
        var deleteToolTip = FirstLocalRow is null && sideRows > 0 ? "Remove the selected rows from the list" : "Delete the selected download and its partial data";
        if (deleteToolTip != _deleteToolTip) ToolTipService.SetToolTip(DeleteButton, _deleteToolTip = deleteToolTip);
        SyncFileMenu(sideRows, removableSideRows);
        StopAllButton.IsEnabled = connected && _jobsById.Values.Any(j => j.CanPause);
        DeleteCompletedButton.IsEnabled = connected && _jobsById.Values.Any(j => j.State == "completed");
        StartQueueButton.IsEnabled = connected && _settings is not null;
        StopQueueButton.IsEnabled = connected && _settings is not null;
        SyncArchiveMenus();
    }

    private string _deleteToolTip = string.Empty;

    /// <summary>A command with nothing to act on is dimmed and says why in its own words, like Extract.</summary>
    private void SyncFileMenu(int sideRows, int removableSideRows)
    {
        var plan = FileMenuRules.Plan(SelectedJob is not null, SelectedPackage is not null, sideRows, removableSideRows);
        Apply(FileOpenItem, "Open", plan.FileReason);
        Apply(FileOpenFolderItem, "Open Folder", plan.FileReason);
        Apply(FileMoveRenameItem, "Move/Rename…", plan.FileReason);
        Apply(FileProgressItem, "Progress Details…", plan.LocalReason);
        Apply(FileDownloadNowItem, "Download Now", plan.LocalReason);
        Apply(FileStopItem, "Stop Download", plan.LocalReason);
        Apply(FileRedownloadItem, "Redownload", plan.FileReason);
        Apply(FileRemoveItem, "Remove", plan.RemoveReason);

        static void Apply(MenuFlyoutItem item, string text, string? reason)
        {
            var label = FileMenuRules.Label(text, reason);
            if (item.Text != label) item.Text = label;
            if (item.IsEnabled != (reason is null)) item.IsEnabled = reason is null;
        }
    }

    /// <summary>A message about something just done. The usual counts come back by themselves.</summary>
    private void ShowPassingStatus(string words)
    {
        _passingStatus = words;
        _passingStatusUntil = DateTimeOffset.UtcNow + PassingStatusTime;
        StatusText.Text = words;
        StatusAnnouncer.Text = words;
        LiveRegion.Announce(StatusAnnouncer);
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = PassingStatusTime;
        timer.Tick += (_, _) => UpdateStatusText();
        timer.Start();
    }

    private void UpdateStatusText()
    {
        var active = _jobsById.Values.Count(j => j.State is "connecting" or "downloading" or "verifying");
        var retrying = _jobsById.Values.Count(j => j.State == "retryWaiting");
        var queued = _jobsById.Values.Count(j => j.State == "queued");
        var host = _status?.HostVersion;
        StatusText.Text = $"{_jobsById.Count} downloads · {active} active · {queued} queued" +
                          (retrying > 0 ? $" · {retrying} retrying" : string.Empty);
        if (_graphConnected)
        {
            ConnectionText.Text = "Host connected" + (host is null ? string.Empty : $" · {host}")
                + (_status?.BrowserIntegration is { Length: > 0 } integration ? $" · browser {integration}" : string.Empty);
        }
        var selected = SelectedJobs.Count;
        if (selected > 1)
        {
            StatusText.Text += $"  ({selected} selected)";
        }

        // One selected row with no local file: what is known about it, which the Description column may have off screen.
        if (FirstLocalRow is null && JobsList.SelectedItems.Count == 1 && JobsList.SelectedItem is JobViewModel { IsSideRow: true } side && side.Description.Length > 0)
        {
            StatusText.Text = $"{side.FileName}: {side.Description}";
        }

        if (_passingStatus is { } passing)
        {
            if (DateTimeOffset.UtcNow < _passingStatusUntil) StatusText.Text = passing;
            else _passingStatus = null;
        }

        if (_status?.StateDirectory is { } stateDirectory)
        {
            ToolTipService.SetToolTip(StatusText, stateDirectory);
        }
    }

    private bool Matches(JobViewModel job)
    {
        if (!string.IsNullOrWhiteSpace(_search))
        {
            var term = _search;
            if (job.FileName.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0 &&
                job.Url.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0 &&
                job.Description.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0 &&
                job.DetailText.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0 &&
                job.ArchiveText.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return _category switch
        {
            "all" => true,
            "finished" => job.State == "completed",
            "unfinished" => job.State is not ("completed" or "cancelled"),
            "active" => job.State is "connecting" or "downloading" or "verifying" or "retryWaiting",
            "queued" => job.State is "queued" or "paused",
            "failed" => job.State == "failed",
            "compressed" or "documents" or "music" or "programs" or "video" or "other" => job.CategoryId == _category,
            _ when _category.StartsWith(RemoteWaitingTag, StringComparison.Ordinal) => false,
            ArchiveSetsTag => false,
            _ when _category.StartsWith("category:", StringComparison.Ordinal) => job.CategoryId == _category[9..],
            _ => true,
        };
    }

    private void RebuildVisible(Guid? selectedId = null)
    {
        // Keep the whole (multi-)selection by id. Clearing the collection can leave the
        // ListView selecting row 0, so the selection is always rebuilt explicitly below.
        var keepSelected = selectedId is { } only
            ? new HashSet<Guid> { only }
            : JobsList.SelectedItems.OfType<JobViewModel>().Select(row => row.Id).ToHashSet();
        var before = _visibleJobs.ToHashSet();
        var target = new List<JobViewModel>();

        var packaged = new HashSet<Guid>();
        var top = new List<JobViewModel>();
        var liveRows = new HashSet<Guid>();
        foreach (var group in _groups)
        {
            var members = PackageMembers(group);
            if (members.Count < 2) continue;
            foreach (var member in members) { packaged.Add(member.Id); member.PackageId = group.Id; }
            if (!members.Any(Matches)) continue;
            if (!_packageRows.TryGetValue(group.Id, out var row)) _packageRows[group.Id] = row = new JobViewModel();
            row.UpdatePackage(group, members, _uiState.ExpandedPackages.Contains(group.Id));
            ShowPackageArchiveState(row);
            liveRows.Add(group.Id);
            top.Add(row);
        }
        foreach (var stale in _packageRows.Keys.Where(id => !liveRows.Contains(id)).ToList()) _packageRows.Remove(stale);
        foreach (var job in _jobsById.Values)
        {
            ShowProviderSource(job);
            ShowArchiveState(job);
            if (packaged.Contains(job.Id)) continue;
            job.PackageId = null;
            if (Matches(job)) top.Add(job);
        }

        var ordered = _sortKey switch
        {
            "name" => _sortAscending ? top.OrderBy(j => j.FileName, NaturalStringComparer.Instance) : top.OrderByDescending(j => j.FileName, NaturalStringComparer.Instance),
            "size" => _sortAscending ? top.OrderBy(j => j.TotalSize) : top.OrderByDescending(j => j.TotalSize),
            "status" => _sortAscending ? top.OrderBy(j => j.State) : top.OrderByDescending(j => j.State),
            "rate" => _sortAscending ? top.OrderBy(j => j.BytesPerSecond) : top.OrderByDescending(j => j.BytesPerSecond),
            "lastTry" => _sortAscending ? top.OrderBy(j => j.LastUpdated) : top.OrderByDescending(j => j.LastUpdated),
            _ => _sortAscending ? top.OrderBy(j => j.CreatedAt) : top.OrderByDescending(j => j.CreatedAt),
        };

        // On top and in their own order: a sort is about local files, and these have no size or rate.
        target.AddRange(RemoteRowsInView());
        target.AddRange(ArchiveRowsInView());

        foreach (var row in ordered)
        {
            target.Add(row);
            if (row.IsPackage && row.IsExpanded)
                foreach (var child in row.Children.Where(Matches)) target.Add(child);
        }

        // Sync in place instead of Clear + Add: rows that stay keep their containers, so
        // progress bars do not replay from another row's value and nothing jumps.
        for (var i = 0; i < target.Count; i++)
        {
            if (i < _visibleJobs.Count && ReferenceEquals(_visibleJobs[i], target[i])) continue;
            var existing = -1;
            for (var j = i + 1; j < _visibleJobs.Count; j++)
            {
                if (ReferenceEquals(_visibleJobs[j], target[i])) { existing = j; break; }
            }
            if (existing >= 0) _visibleJobs.Move(existing, i);
            else _visibleJobs.Insert(i, target[i]);
        }
        while (_visibleJobs.Count > target.Count) _visibleJobs.RemoveAt(_visibleJobs.Count - 1);

        var selectedNow = JobsList.SelectedItems.OfType<JobViewModel>().ToHashSet();
        if (!selectedNow.SetEquals(_visibleJobs.Where(row => keepSelected.Contains(row.Id))))
        {
            JobsList.SelectedItems.Clear();
            foreach (var row in _visibleJobs.Where(row => keepSelected.Contains(row.Id)).ToList())
                JobsList.SelectedItems.Add(row);
        }

        // Rows that appeared because a package opened fade in quickly.
        var added = target.Where(row => row.PackageId is not null && !before.Contains(row)).ToList();
        if (added.Count > 0 && before.Count > 0) FadeIn(added);

        UpdateEmptyText();
        EmptyText.Visibility = _visibleJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private ScrollViewer? _jobsScroller;

    private void JobsList_Loaded(object sender, RoutedEventArgs e)
    {
        // The template's ScrollViewer may not exist yet; retry on layout until it does.
        if (!HookJobsScroller()) JobsList.LayoutUpdated += JobsList_LayoutUpdated;
    }

    private void JobsList_LayoutUpdated(object? sender, object e)
    {
        if (HookJobsScroller()) JobsList.LayoutUpdated -= JobsList_LayoutUpdated;
    }

    private bool HookJobsScroller()
    {
        if (_jobsScroller is not null) return true;
        _jobsScroller = FindChild<ScrollViewer>(JobsList);
        if (_jobsScroller is null) return false;
        _jobsScroller.ViewChanged += (_, _) => SyncScrollBar();
        _jobsScroller.RegisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, (_, _) => SyncScrollBar());
        _jobsScroller.RegisterPropertyChangedCallback(ScrollViewer.ViewportHeightProperty, (_, _) => SyncScrollBar());
        SyncScrollBar();
        return true;
    }

    private double _thumbDragStartY = double.NaN;
    private double _thumbDragStartOffset;

    /// <summary>Sizes and places the custom scrollbar thumb from the list's ScrollViewer.</summary>
    private void SyncScrollBar()
    {
        if (_jobsScroller is null) return;
        var scrollable = _jobsScroller.ScrollableHeight;
        JobsScrollTrack.Visibility = scrollable > 0 ? Visibility.Visible : Visibility.Collapsed;
        var track = JobsScrollTrack.ActualHeight;
        if (scrollable <= 0 || track <= 0) return;
        var thumb = Math.Max(24, track * _jobsScroller.ViewportHeight / _jobsScroller.ExtentHeight);
        JobsScrollThumb.Height = thumb;
        var top = (track - thumb) * Math.Clamp(_jobsScroller.VerticalOffset / scrollable, 0, 1);
        JobsScrollThumb.Margin = new Thickness(3, top, 3, 0);
    }

    private void JobsScrollTrack_SizeChanged(object sender, SizeChangedEventArgs e) => SyncScrollBar();

    private void JobsScrollTrack_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_jobsScroller is null) return;
        var y = e.GetCurrentPoint(JobsScrollTrack).Position.Y;
        var top = JobsScrollThumb.Margin.Top;
        if (y >= top && y <= top + JobsScrollThumb.ActualHeight)
        {
            _thumbDragStartY = y;
            _thumbDragStartOffset = _jobsScroller.VerticalOffset;
            JobsScrollTrack.CapturePointer(e.Pointer);
        }
        else
        {
            // Click on the track pages toward the pointer, like a normal scrollbar.
            var page = Math.Max(27, _jobsScroller.ViewportHeight - 27);
            _jobsScroller.ChangeView(null, _jobsScroller.VerticalOffset + (y < top ? -page : page), null, true);
        }
        e.Handled = true;
    }

    private void JobsScrollTrack_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_jobsScroller is null || double.IsNaN(_thumbDragStartY)) return;
        var free = JobsScrollTrack.ActualHeight - JobsScrollThumb.ActualHeight;
        if (free <= 0) return;
        var dy = e.GetCurrentPoint(JobsScrollTrack).Position.Y - _thumbDragStartY;
        _jobsScroller.ChangeView(null, _thumbDragStartOffset + dy * _jobsScroller.ScrollableHeight / free, null, true);
    }

    private void JobsScrollTrack_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _thumbDragStartY = double.NaN;
        JobsScrollTrack.ReleasePointerCaptures();
    }

    private void JobsScrollTrack_PointerEntered(object sender, PointerRoutedEventArgs e) => JobsScrollThumb.Opacity = 0.75;

    private void JobsScrollTrack_PointerExited(object sender, PointerRoutedEventArgs e) => JobsScrollThumb.Opacity = 0.45;

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void FadeIn(IReadOnlyList<JobViewModel> rows)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            foreach (var row in rows)
            {
                if (JobsList.ContainerFromItem(row) is not UIElement container) continue;
                var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(140),
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, container);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
                storyboard.Children.Add(fade);
            }
            if (storyboard.Children.Count > 0) storyboard.Begin();
        });
    }

    private void PackageChevron_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is JobViewModel { IsPackage: true } row)
        {
            e.Handled = true;
            TogglePackage(row);
        }
    }

    // A fast second click on the chevron is another toggle, not a row double-click.
    private void PackageChevron_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is JobViewModel { IsPackage: true } row) TogglePackage(row);
    }

    private void PackageChevron_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
            border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x80, 0x80, 0x80)); // reads on light and dark
    }

    private void PackageChevron_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border) border.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void TogglePackage(JobViewModel row)
    {
        if (!row.IsPackage) return;
        if (!_uiState.ExpandedPackages.Remove(row.Id)) _uiState.ExpandedPackages.Add(row.Id);
        UiStateStore.Save(_uiState);
        RebuildVisible(row.Id);
    }

    private void JobsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        UpdateStatusText();
    }

    private void JobsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && SelectedJobs.Count > 0)
        {
            e.Handled = true;
            DeleteButton_Click(sender, e);
        }
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingCategories) return;
        if (CategoryList.SelectedItem is ListBoxItem { Tag: string tag })
        {
            if (tag == "queues")
            {
                _ = ShowQueuesAsync();
                return;
            }

            if (tag.StartsWith(RemoteBrowseTag, StringComparison.Ordinal))
            {
                OpenProviderWindow(tag[RemoteBrowseTag.Length..]);
                RestoreCategorySelection();
                return;
            }

            _category = tag;
            RebuildVisible();
        }
    }

    private void RefreshCustomCategories()
    {
        if (_settings is null) return;
        _refreshingCategories = true;
        try
        {
            for (var index = CategoryList.Items.Count - 1; index >= 0; index--)
                if (CategoryList.Items[index] is ListBoxItem { Tag: string tag } && tag.StartsWith("category:", StringComparison.Ordinal))
                    CategoryList.Items.RemoveAt(index);
            var insertion = 6;
            foreach (var category in _settings.CustomCategories)
                CategoryList.Items.Insert(insertion++, new ListBoxItem { Tag = "category:" + category.Id,
                    Content = category.Name, Padding = new Thickness(38, 2, 8, 2) });
            var selected = CategoryList.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag as string == _category);
            if (selected is not null) CategoryList.SelectedItem = selected;
            else if (_category.StartsWith("category:", StringComparison.Ordinal))
            {
                _category = "all";
                CategoryList.SelectedIndex = 0;
            }
        }
        finally { _refreshingCategories = false; }
    }

    private async void JobsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => await RunRowPrimaryAsync();

    /// <summary>What double-clicking a row does. Enter does the same.</summary>
    private async Task RunRowPrimaryAsync()
    {
        if (SelectedPackage is { } package)
        {
            TogglePackage(package);
            return;
        }
        if (FirstLocalRow is null && SelectedRemoteRows is [{ RemoteJob: { } remote }])
        {
            await RunRemotePrimaryAsync(remote);
            return;
        }
        if (FirstLocalRow is null && SelectedArchiveRows is [{ ArchiveSet: { } set }])
        {
            await RunArchivePrimaryAsync(set);
            return;
        }
        if (SelectedJob is { } job)
        {
            // Finished files open; anything still in progress shows its progress window.
            if (job.State == "completed") await OpenJobAsync(job);
            else OpenDownloadWindow(job.Id);
        }
    }

    private void JobsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not ListViewItem)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        if (element is ListViewItem { Content: JobViewModel job } &&
            !JobsList.SelectedItems.Contains(job))
        {
            JobsList.SelectedItem = job;
        }
    }

    private void BuildJobsContextMenu()
    {
        var flyout = new MenuFlyout();
        flyout.Opening += JobsContextMenu_Opening;
        flyout.Items.Add(CreateContextItem("Open", MenuOpen_Click));
        flyout.Items.Add(CreateContextItem("Open With…", MenuOpenWith_Click));
        flyout.Items.Add(CreateContextItem("Open Folder", MenuOpenFolder_Click));
        var moveRename = CreateContextItem("Move/Rename…", MenuMoveRename_Click);
        // The shortcut lives on the list, not the flyout item: invoking an accelerator of a
        // closed context flyout crashed WinUI natively (CoreMessagingXP 0xc000027b).
        moveRename.KeyboardAcceleratorTextOverride = "Ctrl+M";
        var moveShortcut = new KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.M,
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
        };
        moveShortcut.Invoked += (_, args) =>
        {
            args.Handled = true;
            if (SelectedJob?.State == "completed") MenuMoveRename_Click(moveRename, new RoutedEventArgs());
        };
        JobsList.KeyboardAccelerators.Add(moveShortcut);
        flyout.Items.Add(moveRename);
        _menuMoveRename = moveRename;
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateContextItem("Resume", MenuResume_Click));
        flyout.Items.Add(CreateContextItem("Stop", MenuPause_Click));
        flyout.Items.Add(_menuCancelPackage = CreateContextItem("Cancel Package…", MenuCancelPackage_Click));
        flyout.Items.Add(_menuRedownload = CreateContextItem("Redownload", MenuRedownload_Click));
        flyout.Items.Add(_menuRefreshUrl = CreateContextItem("Refresh Download Address…", MenuRefreshUrl_Click));
        flyout.Items.Add(new MenuFlyoutSeparator());
        _addToQueueMenu = new MenuFlyoutSubItem { Text = "Add to Queue" };
        flyout.Items.Add(_addToQueueMenu);
        flyout.Items.Add(CreateContextItem("Remove from Queue", MenuDeleteFromQueue_Click));
        _priorityMenu = new MenuFlyoutSubItem { Text = "Priority" };
        foreach (var priority in new[] { JobPriorities.High, JobPriorities.Normal, JobPriorities.Low })
        {
            var item = new ToggleMenuFlyoutItem { Text = JobPriorities.Name(priority), Tag = priority };
            item.Click += MenuPriority_Click;
            _priorityMenu.Items.Add(item);
        }
        flyout.Items.Add(_priorityMenu);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateContextItem("Remove", MenuRemove_Click));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateContextItem("Progress Details…", MenuProgressDetails_Click));
        flyout.Items.Add(_menuGroup = CreateContextItem("Group into Package…", MenuGroupSelected_Click));
        flyout.Items.Add(_menuUngroup = CreateContextItem("Remove from Package", MenuUngroup_Click));
        flyout.Items.Add(_menuRenamePackage = CreateContextItem("Rename Package…", MenuRenamePackage_Click));
        flyout.Items.Add(CreateContextItem("Change Category…", MenuChangeCategory_Click));
        flyout.Items.Add(CreateContextItem("Properties", MenuProperties_Click));
        AddRemoteContextItems(flyout);
        JobsList.ContextFlyout = flyout;
    }

    private static MenuFlyoutItem CreateContextItem(string text, RoutedEventHandler handler)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += handler;
        return item;
    }

    private MenuFlyoutItem? _menuMoveRename;
    private MenuFlyoutItem? _menuGroup;
    private MenuFlyoutItem? _menuUngroup;
    private MenuFlyoutItem? _menuRenamePackage;
    private MenuFlyoutItem? _menuCancelPackage;
    private MenuFlyoutItem? _menuRedownload;
    private MenuFlyoutItem? _menuRefreshUrl;
    private MenuFlyoutSubItem? _priorityMenu;

    private void JobsContextMenu_Opening(object? sender, object e)
    {
        // Offer only what applies to the current selection.
        var selection = SelectedJobs;
        ShowContextItemsForSelection();
        if (_menuMoveRename is not null) _menuMoveRename.IsEnabled = SelectedJob?.State == "completed";
        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        if (_menuGroup is not null) _menuGroup.Visibility = Show(selection.Count >= 2);
        if (_menuUngroup is not null) _menuUngroup.Visibility = Show(selection.Any(job => job.PackageId is not null));
        if (_menuRenamePackage is not null) _menuRenamePackage.Visibility = Show(SelectedPackage is not null || SelectedJob?.PackageId is not null);
        // A provider's file has no address of its own to edit or send again.
        var providerFile = selection.Any(job => job.IsProviderFile);
        if (_menuRedownload is not null) _menuRedownload.Visibility = Show(!providerFile);
        if (_menuRefreshUrl is not null) _menuRefreshUrl.Visibility = Show(!providerFile);
        if (_menuCancelPackage is not null)
        {
            var packages = SelectedPackageRows;
            _menuCancelPackage.Visibility = Show(packages.Count > 0);
            _menuCancelPackage.IsEnabled = packages.Any(row => row.CanCancelPackage);
        }

        HideLocalContextItemsIfRemoteOnly();
        ShowArchiveContextItems();

        if (_priorityMenu is not null)
        {
            var unfinished = selection.Where(job => job.State != "completed").ToList();
            _priorityMenu.IsEnabled = unfinished.Count > 0;
            foreach (var item in _priorityMenu.Items.OfType<ToggleMenuFlyoutItem>())
                item.IsChecked = item.Tag is int priority && unfinished.Count > 0 && unfinished.All(job => job.Priority == priority);
        }

        if (_addToQueueMenu is null)
        {
            return;
        }

        _addToQueueMenu.Items.Clear();
        if (_settings is null)
        {
            return;
        }

        foreach (var queue in _settings.Queues.OrderBy(q => q.Order))
        {
            var item = new MenuFlyoutItem { Text = queue.Name, Tag = queue.Id };
            item.Click += MenuAddToQueue_Click;
            _addToQueueMenu.Items.Add(item);
        }
    }

    private async void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is { } job)
        {
            await OpenJobAsync(job);
        }
    }

    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is { } job)
        {
            OpenContainingFolder(job);
        }
    }

    private async void MenuAddToQueue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: int queueId })
        {
            await RunOnSelectionAsync("Add to Queue", new GroupActionRequest { Action = "queue", QueueId = queueId },
                _ => true, id => _connection.MoveToQueueAsync(id, queueId));
        }
    }

    private async void MenuDeleteFromQueue_Click(object sender, RoutedEventArgs e)
    {
        await RunOnSelectionAsync("Remove from Queue", new GroupActionRequest { Action = "unqueue" },
            _ => true, id => _connection.DetachFromQueueAsync(id));
    }

    private async void MenuPriority_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: int priority })
        {
            await RunOnSelectionAsync("Priority", new GroupActionRequest { Action = "priority", Priority = priority },
                job => job.State != "completed", id => _connection.SetPriorityAsync(id, priority));
        }
    }

    private async void MenuCancelPackage_Click(object sender, RoutedEventArgs e)
    {
        var packages = SelectedPackageRows.Where(row => row.CanCancelPackage).ToList();
        if (packages.Count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = packages.Count == 1 ? "Cancel package?" : $"Cancel {packages.Count} packages?",
            Content = new TextBlock
            {
                Text = (packages.Count == 1 ? $"Cancel the unfinished files in '{packages[0].FileName}'?" : "Cancel the unfinished files in these packages?") +
                       " Finished files and partial data are kept. Resume can undo this only if the cancel stopped every file in the package.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Cancel Package",
            CloseButtonText = "Keep Downloading",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await RunOnSelectionAsync("Cancel Package", new GroupActionRequest { Action = "cancel" });
    }

    /// <summary>
    /// Applies one action to the selection: each package row as one Host action, so the Host's
    /// rules for mixed files hold, and any other selected file by itself.
    /// </summary>
    private async Task RunOnSelectionAsync(
        string label,
        GroupActionRequest packageAction,
        Func<JobViewModel, bool>? fileEligible = null,
        Func<Guid, Task<IpcMessage>>? fileAction = null)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        var files = fileEligible is null ? [] : SelectedLooseFiles.Where(fileEligible).ToList();
        foreach (var package in SelectedPackageRows)
        {
            try
            {
                var result = (await _connection.GroupActionAsync(new GroupActionRequest
                {
                    GroupId = package.Id,
                    Action = packageAction.Action,
                    QueueId = packageAction.QueueId,
                    Priority = packageAction.Priority,
                })).GetPayload<GroupActionResult>();
                if (!result.Ok) problems.Add($"{package.FileName}: {result.Error ?? "The package could not be changed."}");
                if (result.LeftCancelled > 0)
                    notes.Add($"{package.FileName}: {result.LeftCancelled} cancelled file(s) stayed cancelled. Resume them one by one to bring them back.");
            }
            catch (Exception ex) { problems.Add($"{package.FileName}: {ex.Message}"); }
        }

        if (files.Count > 0) await RunBulkActionAsync(files, fileAction!, label);
        else await RefreshAsync();
        if (problems.Count > 0) await ShowMessageAsync($"{label} incomplete", string.Join(Environment.NewLine, problems.Take(5)));
        else if (notes.Count > 0) ShowPassingStatus(string.Join("  ", notes.Take(2)));
    }

    private async void MenuOpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is not { } job || string.IsNullOrWhiteSpace(job.FinalFilePath) ||
            !File.Exists(job.FinalFilePath))
        {
            await ShowMessageAsync("Open with", "The completed file was not found.");
            return;
        }

        try
        {
            Process.Start(ShellActions.OpenWith(job.FinalFilePath));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Open with", ex.Message);
        }
    }

    private async void MenuMoveRename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        if (job.State != "completed")
        {
            await ShowMessageAsync("Move/Rename", "Move/Rename is available after the download completes.");
            return;
        }

        var nameBox = new TextBox { Header = "File name", Text = Path.GetFileName(job.FinalFilePath) };
        var folderBox = new TextBox { Text = Path.GetDirectoryName(job.FinalFilePath) ?? string.Empty };
        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        browse.Click += async (_, _) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker
                {
                    ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                if (await picker.PickSingleFolderAsync() is { } folder) folderBox.Text = folder.Path;
            }
            catch (Exception)
            {
                // The folder box stays editable when the picker cannot open.
            }
        };
        var folderRow = new Grid { ColumnSpacing = 8 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(folderBox);
        Grid.SetColumn(browse, 1);
        folderRow.Children.Add(browse);
        var panel = new StackPanel { Spacing = 10, MinWidth = 440 };
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = "Folder" });
        panel.Children.Add(folderRow);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Move or rename",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Opened += (_, _) =>
        {
            // Select the name without its extension, like Explorer's rename.
            nameBox.Focus(FocusState.Programmatic);
            var stem = Path.GetFileNameWithoutExtension(nameBox.Text);
            nameBox.Select(0, stem.Length > 0 ? stem.Length : nameBox.Text.Length);
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(folderBox.Text))
        {
            return;
        }

        var target = Path.Combine(folderBox.Text.Trim(), nameBox.Text.Trim());
        if (string.Equals(target, job.FinalFilePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(target), Path.GetFileName(job.FinalFilePath), StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var reply = await _connection.MoveJobAsync(job.Id, target);
            var result = reply.GetPayload<JobActionResponse>();
            if (!result.Ok)
            {
                await ShowMessageAsync("Move/Rename", result.Error ?? "The host rejected the move.");
                return;
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Move/Rename", ex.Message);
        }
    }

    private async void MenuRedownload_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        if (job.State is "connecting" or "downloading" or "queued" or "paused" or "retryWaiting")
        {
            await ShowMessageAsync("Redownload", "Stop the current download before redownloading.");
            return;
        }

        // The list holds no real address for these, and sending what it does hold could only fail.
        if (job.Url.StartsWith("remote://", StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessageAsync("Redownload", "This file comes from a provider account, so it can't be redownloaded from here. Add it again from the account.");
            return;
        }

        if (DownloadManager.Core.Providers.ProviderLinks.IsMasked(job.Url))
        {
            await ShowMessageAsync("Redownload", "This address has its key hidden, so it can't be sent again. Paste the original link to download the file again.");
            return;
        }

        // Only a web link can be a provider's. The job may be a direct choice made on purpose, so that stays on offer.
        if (LinkExtractor.IsWebLink(job.Url) && await ProviderAddAppliesAsync())
        {
            var address = await CheckGivenAddressAsync("Redownload", job.Url);
            if (address.Way == AddressWay.Cancelled) return;
            if (address.Way == AddressWay.Provider)
            {
                var provider = ProviderNameFor(address.Result);
                var answer = await AskAsync("Redownload", RemoteText.ProviderOffer(provider), $"Add through {provider}", "Download directly");
                if (answer == ContentDialogResult.None) return;
                if (answer == ContentDialogResult.Primary)
                {
                    await ShowProviderAddAsync(AddFocus.OneAddress, [job.Url], address.Result);
                    return;
                }
            }
        }

        try
        {
            var reply = await _connection.AddAsync(new AddJobRequest
            {
                Url = job.Url,
                DestinationDirectory = job.Destination,
                FileName = job.FileName,
                CategoryId = job.CategoryId,
                ConflictPolicy = "rename",
                Source = "api",
            });
            var add = reply.GetPayload<AddJobResponse>();
            if (!add.Accepted)
            {
                await ShowMessageAsync("Redownload", add.Error ?? "The host rejected the redownload.");
                return;
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Redownload", ex.Message);
        }
    }

    private void MenuFindNext_Click(object sender, RoutedEventArgs e)
    {
        if (JobsList.Items.Count == 0)
        {
            MenuFind_Click(sender, e);
            return;
        }

        var next = JobsList.SelectedIndex >= 0
            ? (JobsList.SelectedIndex + 1) % JobsList.Items.Count
            : 0;
        JobsList.SelectedIndex = next;
        JobsList.ScrollIntoView(JobsList.Items[next]);
        UpdateButtons();
        UpdateStatusText();
    }

    private void RefreshQueueMenus()
    {
        if (_settings is null)
        {
            return;
        }

        StartQueueMenu.Items.Clear();
        StopQueueMenu.Items.Clear();
        foreach (var queue in _settings.Queues.OrderBy(queue => queue.Order))
        {
            var start = new MenuFlyoutItem
            {
                Text = $"Start {queue.Name} download queue",
                IsEnabled = !queue.IsRunning,
            };
            start.Click += async (_, _) => await QueueActionFromMenuAsync(queue.Id, "start");
            StartQueueMenu.Items.Add(start);

            var stop = new MenuFlyoutItem
            {
                Text = $"Stop {queue.Name} download queue",
                IsEnabled = queue.IsRunning,
            };
            stop.Click += async (_, _) => await QueueActionFromMenuAsync(queue.Id, "stop");
            StopQueueMenu.Items.Add(stop);
        }
    }

    private async Task QueueActionFromMenuAsync(int queueId, string action)
    {
        try
        {
            var reply = await _connection.QueueActionAsync(new QueueActionRequest
            {
                QueueId = queueId,
                Action = action,
            });
            var result = reply.GetPayload<SettingsResult>();
            if (!result.Ok)
            {
                await ShowMessageAsync("Queue action failed", result.Error ?? "The host rejected the action.");
                return;
            }

            _settings = result.Settings;
            RefreshQueueMenus();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Queue action failed", ex.Message);
        }
    }

    private async void MenuRefreshUrl_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        if (job.IsProviderFile)
        {
            await ShowMessageAsync("Refresh download address", "This file comes from a provider account, which supplies a new address by itself whenever one is needed.");
            return;
        }

        var input = new TextBox
        {
            Text = job.Url,
            Header = "New download address",
            AcceptsReturn = false,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Refresh download address",
            Content = input,
            PrimaryButtonText = "Refresh",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(input.Text))
        {
            return;
        }

        // The address it already has stays usable: refusing that would strand a retry of a direct download.
        var newAddress = input.Text.Trim();
        if (newAddress != job.Url && LinkExtractor.IsWebLink(newAddress) && await ProviderAddAppliesAsync())
        {
            var address = await CheckGivenAddressAsync("Refresh download address", newAddress);
            if (address.Way == AddressWay.Cancelled) return;
            if (address.Way == AddressWay.Provider)
            {
                await ShowMessageAsync("Refresh download address", RemoteText.RefreshRefused(ProviderNameFor(address.Result)));
                return;
            }
        }

        await RunActionAsync(() => _connection.RefreshUrlAsync(job.Id, newAddress));
    }

    private void MenuProgressDetails_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPackage is { } package) { OpenPackageWindow(package.Id); return; }
        if (SelectedJob is not { } job) return;
        OpenDownloadWindow(job.Id);
    }

    private void OpenPackageWindow(Guid packageId)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--package {packageId:D}") { UseShellExecute = false });
        }
        catch (Exception ex) { _ = ShowMessageAsync("Could not open progress", ex.Message); }
    }

    private void OpenDownloadWindow(Guid jobId)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--job {jobId:D} --manual")
            {
                UseShellExecute = false,
            });
        }
        catch (Exception ex) { _ = ShowMessageAsync("Could not open progress", ex.Message); }
    }

    private void MenuSpeedGraph_Click(object sender, RoutedEventArgs e)
    {
        SpeedGraph.Visibility = ShowSpeedGraphMenuItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        SaveUiState();
    }

    private void MenuStatistics_Click(object sender, RoutedEventArgs e)
    {
        if (_statisticsWindow is null)
        {
            _statisticsWindow = new StatisticsWindow();
            _statisticsWindow.Closed += (_, _) => _statisticsWindow = null;
        }
        _statisticsWindow.ShowWindow();
    }

    private async Task<string?> AskPackageNameAsync(string title, string initial)
    {
        var name = new TextBox { Header = "Package name", MaxLength = 100, Text = initial };
        name.Loaded += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.ActualTheme,
            Title = title, Content = name, PrimaryButtonText = "OK", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(name.Text)
            ? name.Text.Trim() : null;
    }

    private async Task RunGroupActionAsync(string title, GroupActionRequest request)
    {
        try
        {
            var result = (await _connection.GroupActionAsync(request)).GetPayload<GroupActionResult>();
            if (!result.Ok) await ShowMessageAsync(title, result.Error ?? "The package could not be changed.");
            else { _groups = result.Groups; RebuildVisible(); }
        }
        catch (Exception ex) { await ShowMessageAsync(title, ex.Message); }
    }

    private async void MenuGroupSelected_Click(object sender, RoutedEventArgs e)
    {
        var selection = SelectedJobs;
        if (selection.Count < 2)
        {
            await ShowMessageAsync("Group into package", "Select at least two downloads to put in a package.");
            return;
        }
        var suggested = System.Text.RegularExpressions.Regex.Replace(selection[0].FileName,
            @"(\.part\d+\.rar|\.r\d{2,3}|\.z\d{2}|\.\d{3})$|\.[^.]+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (await AskPackageNameAsync("Group into package", suggested) is not { } name) return;
        await RunGroupActionAsync("Group into package", new GroupActionRequest
            { Action = "create", Name = name, JobIds = selection.Select(job => job.Id).ToList() });
    }

    private async void MenuUngroup_Click(object sender, RoutedEventArgs e)
    {
        var byPackage = SelectedJobs.Where(job => job.PackageId is not null).GroupBy(job => job.PackageId!.Value).ToList();
        if (byPackage.Count == 0) return;
        foreach (var package in byPackage)
        {
            var group = _groups.FirstOrDefault(candidate => candidate.Id == package.Key);
            if (group is null) continue;
            // Removing every file of a package dissolves it; the downloads stay.
            var request = package.Count() >= group.JobIds.Count
                ? new GroupActionRequest { Action = "delete", GroupId = package.Key }
                : new GroupActionRequest { Action = "detach", GroupId = package.Key, JobIds = package.Select(job => job.Id).ToList() };
            await RunGroupActionAsync("Remove from package", request);
        }
    }

    private async void MenuRenamePackage_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedPackage ?? (SelectedJob?.PackageId is { } id ? _packageRows.GetValueOrDefault(id) : null);
        if (row is null) return;
        if (await AskPackageNameAsync("Rename package", row.FileName) is not { } name) return;
        await RunGroupActionAsync("Rename package", new GroupActionRequest { Action = "rename", GroupId = row.Id, Name = name });
    }

    private async void MenuChangeCategory_Click(object sender, RoutedEventArgs e)
    {
        var selection = SelectedJobs;
        if (selection.Count == 0 || _settings is null) return;
        var categoryBox = new ComboBox { Header = "Category", HorizontalAlignment = HorizontalAlignment.Stretch };
        categoryBox.Items.Add(new ComboBoxItem { Content = "Automatic (file extension)", Tag = "" });
        foreach (var id in new[] { "compressed", "documents", "music", "programs", "video", "other" })
            categoryBox.Items.Add(new ComboBoxItem { Content = char.ToUpperInvariant(id[0]) + id[1..], Tag = id });
        foreach (var category in _settings.CustomCategories)
            categoryBox.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category.Id });
        categoryBox.SelectedIndex = 0;
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = (selection.Count == 1 ? $"Change the category of '{selection[0].FileName}'." : $"Change the category of {selection.Count} downloads.") + " Existing files stay in their current folders.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(categoryBox);
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.ActualTheme,
            Title = "Change category", Content = panel, PrimaryButtonText = "Apply", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || categoryBox.SelectedItem is not ComboBoxItem { Tag: string categoryId }) return;
        var errors = new List<string>();
        foreach (var job in selection)
        {
            try
            {
                var reply = await _connection.SetCategoryAsync(job.Id, categoryId);
                var result = reply.GetPayload<JobActionResponse>();
                if (!result.Ok) errors.Add($"{job.FileName}: {result.Error ?? "Update rejected."}");
            }
            catch (Exception ex) { errors.Add($"{job.FileName}: {ex.Message}"); }
        }
        await RefreshAsync();
        if (errors.Count > 0) await ShowMessageAsync("Some categories were not changed", string.Join("\n", errors));
    }

    private async void MenuProperties_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is not { } job || _settings is null)
        {
            return;
        }

        // Label/value grid; values are selectable so the URL or path can be copied.
        var info = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var secondary = InboundTheme.Brush("InboundSecondaryTextBrush", RootGrid);
        foreach (var (label, value) in new[]
        {
            ("File", job.FileName), ("Status", job.StateText), ("Size", job.SizeText),
            ("Queue", string.IsNullOrWhiteSpace(job.QueueName) ? "None" : job.QueueName),
            ("Source", job.IsProviderFile ? ProviderDisplayName(job.Dto?.RemoteProvider) : job.SourceText),
            ("Package", job.IsProviderFile ? _groups.FirstOrDefault(group => group.JobIds.Contains(job.Id))?.Name : null),
            ("Address", job.IsProviderFile ? null : job.Url), ("Saved to", job.FinalFilePath), ("Last try", job.LastTryText),
        })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var row = info.RowDefinitions.Count;
            info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, Foreground = secondary };
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(name, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            info.Children.Add(name);
            info.Children.Add(text);
        }
        var descriptionBox = new TextBox
        {
            Header = "Description",
            Text = job.RawDescription,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Height = 80,
        };
        var categoryBox = new ComboBox { Header = "Category", HorizontalAlignment = HorizontalAlignment.Stretch };
        categoryBox.Items.Add(new ComboBoxItem { Content = "Automatic (file extension)", Tag = "" });
        foreach (var id in new[] { "compressed", "documents", "music", "programs", "video", "other" })
            categoryBox.Items.Add(new ComboBoxItem { Content = char.ToUpperInvariant(id[0]) + id[1..], Tag = id });
        foreach (var category in _settings.CustomCategories)
            categoryBox.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category.Id });
        categoryBox.SelectedItem = categoryBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            item.Tag as string == (job.CategoryIsAutomatic ? "" : job.CategoryId)) ?? categoryBox.Items[0];
        var originalCategorySelection = categoryBox.SelectedItem;
        var completionEditor = new JobCompletionEditor(job.CompletionOptions, this, _settings.AllowSystemCompletionActions);
        var canEditCompletion = job.State != "completed";
        if (completionEditor.Content is Control completionControl) completionControl.IsEnabled = canEditCompletion;
        JobCompletionOptionsDto? completionOptions = null;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(info);
        panel.Children.Add(descriptionBox);
        panel.Children.Add(categoryBox);
        panel.Children.Add(completionEditor.Content);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Download properties",
            Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(180, RootGrid.ActualHeight - 220), VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (canEditCompletion)
            {
                if (!completionEditor.TryGetOptions(out var options, out var completionValidationError))
                {
                    args.Cancel = true;
                }
                else completionOptions = options;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var reply = await _connection.UpdateJobAsync(job.Id, descriptionBox.Text,
                ReferenceEquals(originalCategorySelection, categoryBox.SelectedItem) ? null :
                    (categoryBox.SelectedItem as ComboBoxItem)?.Tag as string, completionOptions);
            var result = reply.GetPayload<JobActionResponse>();
            if (!result.Ok)
            {
                await ShowMessageAsync("Could not update properties", result.Error ?? "The host rejected the update.");
                return;
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not update properties", ex.Message);
        }
    }

    private async void MenuExport_Click(object sender, RoutedEventArgs e)
    {
        var pathBox = new TextBox
        {
            Header = "Export file",
            Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "download-manager-list.json"),
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Export download list",
            Content = pathBox,
            PrimaryButtonText = "Export",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(pathBox.Text))
        {
            return;
        }

        try
        {
            var snapshot = await _connection.RefreshAsync();
            await File.WriteAllTextAsync(pathBox.Text.Trim(), JobListFile.Serialize(snapshot.Jobs));
            await ShowMessageAsync("Export download list",
                $"Exported {snapshot.Jobs.Count} downloads to {pathBox.Text.Trim()}.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not export download list", ex.Message);
        }
    }

    private async void MenuImport_Click(object sender, RoutedEventArgs e)
    {
        var pathBox = new TextBox
        {
            Header = "Import file",
            Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "download-manager-list.json"),
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Import download list",
            Content = pathBox,
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(pathBox.Text))
        {
            return;
        }

        IReadOnlyList<JobListEntry> entries;
        try
        {
            entries = JobListFile.Deserialize(await File.ReadAllTextAsync(pathBox.Text.Trim()));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not import download list", ex.Message);
            return;
        }

        if (entries.Count == 0)
        {
            await ShowMessageAsync("Import download list", "The file has no downloads.");
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Import downloads?",
            Content = new TextBlock
            {
                Text = $"Add {entries.Count} downloads from this file?" +
                    (RemoteText.UnreadableKeyNotice(_remote.State.Accounts) is { } keyNotice ? $" {keyNotice}" : string.Empty),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        Dictionary<string, RemoteClassifyResult>? answers = null;
        if (await ProviderAddAppliesAsync())
        {
            ShowPassingStatus($"Checking {entries.Count} addresses…");
            answers = await ImportRouting.ClassifyAllAsync(entries.Select(entry => entry.Url).Where(LinkExtractor.IsWebLink), _remote.OpenClassifier);
        }

        var accepted = 0;
        var failed = 0;
        string? firstError = null;
        var toReview = new List<(string Input, RemoteClassifyResult Result)>();
        foreach (var entry in entries)
        {
            if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                failed++;
                firstError ??= $"Invalid URL: {entry.Url}";
                continue;
            }

            var answer = answers?.GetValueOrDefault(entry.Url);
            var way = ImportRouting.WayFor(entry.Url, answer);
            if (way == ImportWay.Refused)
            {
                failed++;
                firstError ??= RemoteText.AddLineWords(new AddLine(entry.Url) { Result = answer }, ProviderNameFor(answer)).Detail;
                continue;
            }

            if (way == ImportWay.Review)
            {
                if (toReview.All(line => line.Input != entry.Url)) toReview.Add((entry.Url, answer!));
                continue;
            }

            try
            {
                var reply = await _connection.AddAsync(new AddJobRequest
                {
                    Url = entry.Url,
                    FileName = entry.FileName,
                    CategoryId = entry.CategoryId,
                    DestinationDirectory = entry.DestinationDirectory,
                    Description = entry.Description,
                    QueueId = entry.QueueId,
                    Connections = entry.RequestedConnections > 0 ? entry.RequestedConnections : null,
                    StartMode = entry.StartMode,
                    ConflictPolicy = entry.ConflictPolicy,
                    ExpectedSha256 = entry.ExpectedSha256,
                    Source = "import",
                });
                var add = reply.GetPayload<AddJobResponse>();
                if (add.Accepted)
                {
                    accepted++;
                }
                else
                {
                    failed++;
                    firstError ??= add.Error;
                }
            }
            catch (Exception ex)
            {
                failed++;
                firstError ??= ex.Message;
            }
        }

        await RefreshAsync();
        await ShowMessageAsync("Import download list", RemoteText.ImportSummary(accepted, entries.Count, failed, firstError, toReview.Count));
        if (toReview.Count > 0) await ShowProviderAddAsync(AddFocus.ImportedLinks, answered: toReview);
    }

    private void MenuAdd_Click(object sender, RoutedEventArgs e) => AddButton_Click(sender, e);

    private async void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _connection.ShutdownAsync();
        }
        catch (Exception)
        {
            // The host may already be exiting.
        }

        Application.Current.Exit();
    }

    private void MenuPause_Click(object sender, RoutedEventArgs e) => PauseButton_Click(sender, e);
    private void MenuResume_Click(object sender, RoutedEventArgs e) => ResumeButton_Click(sender, e);
    private async void MenuRemove_Click(object sender, RoutedEventArgs e)
    {
        var selection = SelectedJobs;
        if (selection.Count == 0)
        {
            if (FirstLocalRow is null) await RemoveSideRowsAsync();
            return;
        }

        await RunBulkActionAsync(selection, id => _connection.RemoveAsync(id, deleteFile: false), "Remove");
    }
    private void MenuOptions_Click(object sender, RoutedEventArgs e) => _ = ShowSettingsDialogAsync();

    private async void MenuPauseAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _connection.PauseAllAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Pause All", ex.Message);
        }
    }

    private async void MenuStopAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _connection.PauseAllAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Stop All", ex.Message);
        }
    }

    private async void MenuDeleteCompleted_Click(object sender, RoutedEventArgs e)
    {
        var completed = _jobsById.Values.Where(j => j.State == "completed").Select(j => j.Id).ToList();
        if (completed.Count == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Delete completed downloads?",
            Content = new TextBlock
            {
                Text = $"Remove {completed.Count} completed item(s) from the list? The downloaded files will stay on disk.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Remove from list",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        foreach (var id in completed)
        {
            try
            {
                await _connection.RemoveAsync(id, deleteFile: false);
            }
            catch (Exception)
            {
                // Keep removing the rest; the refresh below reconciles the list.
            }
        }

        await RefreshAsync();
    }

    private async void MenuCleanOrphans_Click(object sender, RoutedEventArgs e)
    {
        var known = _status?.OrphanPartFiles ?? new List<string>();
        var text = known.Count == 0
            ? "No leftover partial files were found at the last scan. The host rescans before cleaning."
            : $"{known.Count} partial file(s) are not attached to any download:\n" +
              string.Join("\n", known.Take(8)) + (known.Count > 8 ? "\n..." : string.Empty);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Clean up leftover partial files",
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock
                {
                    Text = text + "\n\nQuarantine moves them into a .dm-orphans folder next to where they were " +
                           "found. Partial files of existing downloads are never touched.",
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            },
            PrimaryButtonText = "Quarantine",
            SecondaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None)
        {
            return;
        }

        var delete = choice == ContentDialogResult.Secondary;
        try
        {
            var reply = await _connection.CleanOrphansAsync(delete);
            var result = reply.GetPayload<CleanOrphansResponse>();
            if (result is null)
            {
                await ShowMessageAsync("Clean up failed", "The host returned no result.");
                return;
            }

            var summary = $"{(delete ? "Deleted" : "Quarantined")} {result.Processed.Count} file(s).";
            if (result.Failed.Count > 0)
            {
                summary += $"\n{result.Failed.Count} could not be cleaned (in use?):\n" +
                           string.Join("\n", result.Failed.Take(8));
            }

            await ShowMessageAsync("Clean up leftover partial files", summary);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Clean up failed", ex.Message);
        }

        await RefreshAsync();
    }

    private async void MenuFind_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { Text = _search, PlaceholderText = "File name, URL or description" };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Find",
            Content = box,
            PrimaryButtonText = "Find",
            SecondaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _search = box.Text.Trim();
            RebuildVisible();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            _search = string.Empty;
            RebuildVisible();
        }
    }

    private void MenuHideCategories_Click(object sender, RoutedEventArgs e)
    {
        _hideCategories = HideCategoriesMenuItem.IsChecked;
        CategoryColumn.Width = _hideCategories ? new GridLength(0) : new GridLength(192);
    }

    private void MenuSort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string key })
        {
            return;
        }

        if (_sortKey == key)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortKey = key;
            _sortAscending = key is "name" or "status";
        }

        RebuildVisible();
    }

    private void MenuLargeToolbar_Click(object sender, RoutedEventArgs e) => SetToolbarScale(large: true);
    private void MenuSmallToolbar_Click(object sender, RoutedEventArgs e) => SetToolbarScale(large: false);

    private void SetToolbarScale(bool large)
    {
        _toolbarLarge = large;
        foreach (var button in ToolbarPanel.Children.OfType<Button>())
        {
            button.Width = large ? double.NaN : 34;
            button.MinWidth = large ? 72 : 34;
            button.Padding = large ? new Thickness(10, 2, 10, 2) : new Thickness(2);
            button.Height = large ? 58 : 32;
            if (button.Content is not StackPanel content)
            {
                continue;
            }

            if (content.Children.FirstOrDefault() is FontIcon icon)
            {
                icon.FontSize = large ? 25 : 16;
            }

            if (content.Children.LastOrDefault() is TextBlock label)
            {
                label.Visibility = large ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void MenuDarkMode_Click(object sender, RoutedEventArgs e)
    {
        var dark = DarkModeMenuItem.IsChecked;
        RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        _uiState.DarkMode = dark;
        ApplyTitleBarTheme(dark);
        SaveUiState();
    }

    private void ApplyTitleBarTheme(bool dark) => WindowLayout.ApplyTitleBar(this, dark);

    private async void MenuUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var check = (await _connection.CheckForUpdatesAsync())
                .GetPayload<UpdateCheckResult>();
            if (check.Status == "notInstalled")
            {
                await ShowMessageAsync("Check for updates",
                    "This portable/development build does not auto-update. Install with the installer build to enable updates.");
                return;
            }

            if (check.Status == "notConfigured")
            {
                await ShowMessageAsync("Check for updates",
                    check.Error ?? "Update settings are not configured.");
                return;
            }

            if (!check.Ok)
            {
                await ShowMessageAsync("Check for updates", check.Error ?? "The update check failed.");
                return;
            }

            if (check.Status != "updateAvailable")
            {
                await ShowMessageAsync("Check for updates",
                    $"You are up to date (installed {check.InstalledVersion}, latest {check.AvailableVersion}).");
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = "Update available",
                Content = new TextBlock
                {
                    Text = $"Installed: {check.InstalledVersion}\nAvailable: {check.AvailableVersion}\n" +
                           $"Size: {check.Size} bytes\n\nDownload and verify the installer now?",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Download",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
            };
            var updateChoice = await dialog.ShowAsync();
            if (updateChoice != ContentDialogResult.Primary)
            {
                await _connection.DeferUpdateAsync(check.AvailableVersion!);
                return;
            }

            var download = (await _connection.DownloadUpdateAsync())
                .GetPayload<UpdateDownloadResult>();
            if (!download.Ok)
            {
                await ShowMessageAsync("Update download", download.Error ?? "The download failed.");
                return;
            }

            var installDialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = "Install update now?",
                Content = new TextBlock
                {
                    Text = $"Verified installer saved to:\n{download.Path}\n\n" +
                           "The background Host will stop, install the update, and restart.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Install now",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await installDialog.ShowAsync() != ContentDialogResult.Primary)
            {
                await _connection.DeferUpdateAsync(check.AvailableVersion!);
                return;
            }

            var install = (await _connection.InstallUpdateAsync())
                .GetPayload<UpdateInstallResult>();
            if (!install.Ok && install.Status == "busy")
            {
                var forceDialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    RequestedTheme = RootGrid.ActualTheme,
                    Title = "Downloads are active",
                    Content = new TextBlock
                    {
                        Text = install.Error ?? "Active downloads would be interrupted.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = "Install anyway",
                    CloseButtonText = "Wait",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await forceDialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                install = (await _connection.InstallUpdateAsync(force: true))
                    .GetPayload<UpdateInstallResult>();
            }

            await ShowMessageAsync("Install update",
                install.Ok
                    ? $"Update {install.Version} scheduled. The Host is restarting."
                    : install.Error ?? "The update could not be scheduled.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Check for updates", ex.Message);
        }
    }

    private async void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        await ShowMessageAsync("Inbound Download Manager",
            $"Inbound Download Manager {typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown"}\n\n" +
            "Private download manager built around a durable background host, a replaceable WinUI 3 frontend, " +
            "and a Chrome Manifest V3 bridge.");
    }

    private void MenuDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var directory = _status?.StateDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            _ = ShowMessageAsync("Diagnostics", "The host state directory is not available yet.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _ = ShowMessageAsync("Diagnostics", ex.Message);
        }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        var providerAdd = await ProviderAddAppliesAsync();
        var urlBox = new ComboBox
        {
            Header = "URL",
            PlaceholderText = "https://example.com/file.zip",
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (var recent in _settings.RecentUrls.Take(20))
        {
            urlBox.Items.Add(recent);
        }
        // Like other download managers, start from a link already on the clipboard.
        try
        {
            var clip = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (clip.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                var text = (await clip.GetTextAsync()).Trim();
                if (text.Length < 4096 && Uri.TryCreate(text, UriKind.Absolute, out var clipUri) &&
                    (clipUri.Scheme == Uri.UriSchemeHttp || clipUri.Scheme == Uri.UriSchemeHttps || clipUri.Scheme == Uri.UriSchemeFtp ||
                     (providerAdd && LinkExtractor.IsMagnet(text))))
                    urlBox.Text = text;
            }
        }
        catch (Exception)
        {
            // Clipboard access is best-effort.
        }
        var addressError = new StatusLine("Address error");
        var addressPanel = new StackPanel { Spacing = 8, MinWidth = 460 };
        addressPanel.Children.Add(urlBox);
        addressPanel.Children.Add(addressError.Text);
        if (!providerAdd) AddUnreadableKeyNotice(addressPanel);
        var addressDialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Enter new address to download",
            Content = addressPanel,
            PrimaryButtonText = "Next",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        // An editable ComboBox keeps Escape for itself, even with its list closed.
        urlBox.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Escape || urlBox.IsDropDownOpen) return;
            args.Handled = true;
            addressDialog.Hide();
        };
        string url;
        RemoteClassifyResult? addressResult = null;
        string? addressNote = null;
        if (providerAdd)
        {
            var checkedAddress = await CheckAddressAsync(addressDialog, urlBox, addressError, addressPanel);
            switch (checkedAddress.Way)
            {
                case AddressWay.Cancelled:
                    return;
                case AddressWay.Provider:
                    await ShowProviderAddAsync(AddFocus.OneAddress, [checkedAddress.Input], checkedAddress.Result);
                    return;
            }

            url = checkedAddress.Input;
            addressResult = checkedAddress.Result;
            addressNote = checkedAddress.Result?.Note;
        }
        else
        {
            addressDialog.PrimaryButtonClick += (_, args) =>
            {
                if (!Uri.TryCreate(urlBox.Text.Trim(), UriKind.Absolute, out var candidate) ||
                    candidate.Scheme is not ("http" or "https"))
                {
                    addressError.Say("Enter an absolute http or https URL.", isProblem: true);
                    args.Cancel = true;
                }
            };
            if (await addressDialog.ShowAsync() != ContentDialogResult.Primary) return;
            url = urlBox.Text.Trim();
        }

        // A direct link holds an account key. The Host keeps the key out of everything it saves, and so does this.
        var urlDisplay = new TextBox { Header = addressResult?.Kind == "providerDirectLink" ? "Site" : "URL", Text = RemoteText.AddressToShow(url, addressResult), IsReadOnly = true };
        var nameBox = new TextBox
        {
            Header = "File name (optional)",
            PlaceholderText = "Resolved from the URL or server",
        };
        var destinationBox = new TextBox
        {
            Text = _settings.DefaultDownloadDirectory,
        };
        var browseDestination = new Button
        {
            Content = "Browse…",
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(destinationBox, "Destination folder");
        var destinationPickerError = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            Foreground = InboundTheme.Brush("InboundErrorTextBrush", RootGrid),
        };
        browseDestination.Click += async (_, _) =>
        {
            browseDestination.IsEnabled = false;
            destinationPickerError.Text = string.Empty;
            destinationPickerError.Visibility = Visibility.Collapsed;
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker
                {
                    ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                };
                picker.FileTypeFilter.Add("*");
                var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null)
                    destinationBox.Text = folder.Path;
            }
            catch (Exception ex)
            {
                destinationPickerError.Text = $"Could not open the folder picker: {ex.Message}";
                destinationPickerError.Visibility = Visibility.Visible;
            }
            finally
            {
                browseDestination.IsEnabled = true;
            }
        };
        var destinationRow = new Grid { ColumnSpacing = 8 };
        destinationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        destinationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(browseDestination, 1);
        destinationRow.Children.Add(destinationBox);
        destinationRow.Children.Add(browseDestination);
        var descriptionBox = new TextBox { Header = "Description (optional)" };
        var categoryBox = new ComboBox { Header = "Category", HorizontalAlignment = HorizontalAlignment.Stretch };
        categoryBox.Items.Add(new ComboBoxItem { Content = "Automatic (from file type)" });
        foreach (var category in FileCategories.Names.Append("other"))
            categoryBox.Items.Add(new ComboBoxItem { Content = char.ToUpperInvariant(category[0]) + category[1..], Tag = category });
        foreach (var category in _settings.CustomCategories)
            categoryBox.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category.Id });
        categoryBox.SelectedIndex = 0;
        var changingDestination = false;
        var customDestination = false;
        void UseCategoryFolder(bool force = false)
        {
            if (customDestination && !force) return;
            var fileName = string.IsNullOrWhiteSpace(nameBox.Text) ? Uri.UnescapeDataString(new Uri(url).AbsolutePath) : nameBox.Text;
            var category = (categoryBox.SelectedItem as ComboBoxItem)?.Tag as string
                ?? FileCategories.For(fileName, _settings.CustomCategories);
            var folder = _settings.CustomCategories.FirstOrDefault(item => item.Id == category)?.DestinationDirectory;
            if (string.IsNullOrWhiteSpace(folder)) _settings.CategoryFolders.TryGetValue(category, out folder);
            changingDestination = true;
            destinationBox.Text = string.IsNullOrWhiteSpace(folder) ? _settings.DefaultDownloadDirectory : folder;
            changingDestination = false;
            customDestination = false;
        }
        destinationBox.TextChanged += (_, _) => { if (!changingDestination) customDestination = true; };
        nameBox.TextChanged += (_, _) => UseCategoryFolder();
        categoryBox.SelectionChanged += (_, _) => UseCategoryFolder(force: true);
        UseCategoryFolder();
        var categoryName = new TextBox { Header = "New category name" };
        var categoryExtensions = new TextBox { Header = "File extensions (optional)", PlaceholderText = "zip 7z rar" };
        var categoryError = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        ContentDialog? fileInfoDialog = null;
        var createCategory = new Button { Content = "Create category" };
        createCategory.Click += async (_, _) =>
        {
            createCategory.IsEnabled = false;
            if (fileInfoDialog is not null) fileInfoDialog.IsPrimaryButtonEnabled = fileInfoDialog.IsSecondaryButtonEnabled = false;
            categoryError.Visibility = Visibility.Collapsed;
            try
            {
                var current = await _connection.RefreshAsync();
                var updated = CloneSettings(current.Settings);
                var category = new CategoryDefinitionDto { Id = Guid.NewGuid().ToString("N"), Name = categoryName.Text.Trim(),
                    DestinationDirectory = destinationBox.Text.Trim(),
                    Extensions = categoryExtensions.Text.Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList() };
                updated.CustomCategories.Add(category);
                var reply = await _connection.UpdateSettingsAsync(updated);
                var result = reply.GetPayload<SettingsResult>();
                if (!result.Ok) throw new InvalidOperationException(result.Error ?? "Could not create category.");
                _settings = result.Settings;
                RefreshCustomCategories();
                var option = new ComboBoxItem { Content = category.Name, Tag = category.Id };
                categoryBox.Items.Add(option);
                categoryBox.SelectedItem = option;
                categoryName.Text = categoryExtensions.Text = string.Empty;
            }
            catch (Exception ex)
            {
                categoryError.Text = ex.Message;
                categoryError.Visibility = Visibility.Visible;
            }
            finally
            {
                createCategory.IsEnabled = true;
                if (fileInfoDialog is not null) fileInfoDialog.IsPrimaryButtonEnabled = fileInfoDialog.IsSecondaryButtonEnabled = true;
            }
        };
        var categoryPanel = new StackPanel { Spacing = 8 };
        categoryPanel.Children.Add(categoryName);
        categoryPanel.Children.Add(categoryExtensions);
        categoryPanel.Children.Add(new TextBlock { Text = "Create a saved category using the destination folder above. Matching extensions apply to future downloads; leave blank for manual selection. Creating it saves immediately, even if this download is cancelled.", TextWrapping = TextWrapping.Wrap });
        categoryPanel.Children.Add(createCategory);
        categoryPanel.Children.Add(categoryError);
        var useAuthentication = new CheckBox { Content = "Use a user name and password for this download" };
        var authenticationUser = new TextBox { Header = "User name", IsEnabled = false };
        var authenticationPassword = new PasswordBox { Header = "Password", IsEnabled = false };
        useAuthentication.Checked += (_, _) => authenticationUser.IsEnabled = authenticationPassword.IsEnabled = true;
        useAuthentication.Unchecked += (_, _) => authenticationUser.IsEnabled = authenticationPassword.IsEnabled = false;
        var authenticationPanel = new StackPanel { Spacing = 8 };
        authenticationPanel.Children.Add(useAuthentication);
        authenticationPanel.Children.Add(authenticationUser);
        authenticationPanel.Children.Add(authenticationPassword);
        authenticationPanel.Children.Add(new TextBlock { Text = "HTTP Basic authentication. Credentials apply only to this download and are protected by Windows when saved.", TextWrapping = TextWrapping.Wrap });
        var authenticationError = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        authenticationPanel.Children.Add(authenticationError);
        var rememberFolder = new CheckBox { Content = "Remember this folder for manual downloads", IsChecked = true };
        categoryBox.SelectionChanged += (_, _) => rememberFolder.Content =
            (categoryBox.SelectedItem as ComboBoxItem)?.Tag is string
                ? "Remember this folder for the selected category" : "Remember this folder for manual downloads";

        var queueBox = new ComboBox { Header = "Queue" };
        foreach (var queue in _settings.Queues.OrderBy(q => q.Order))
        {
            queueBox.Items.Add(queue.Name);
        }

        var defaultQueueIndex = _settings.Queues
            .OrderBy(q => q.Order)
            .ToList()
            .FindIndex(q => q.Id == _settings.DefaultQueueId);
        queueBox.SelectedIndex = Math.Max(0, defaultQueueIndex);

        var conflictBox = new ComboBox { Header = "If the file exists" };
        conflictBox.Items.Add("Rename the new file");
        conflictBox.Items.Add("Overwrite the existing file");
        conflictBox.Items.Add("Fail the new download");
        conflictBox.SelectedIndex = 0;

        var metadataText = new TextBlock { Text = "File information has not been checked.", TextWrapping = TextWrapping.Wrap };
        var metadataBusy = new ProgressRing { IsActive = false, Visibility = Visibility.Collapsed, Width = 18, Height = 18 };
        var checkMetadata = new Button { Content = "Check file info" };
        var metadataActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        metadataActions.Children.Add(checkMetadata);
        metadataActions.Children.Add(metadataBusy);
        var metadataGeneration = 0;
        var metadataClosed = false;
        var nameEdited = false;
        var assigningServerName = false;
        CancellationTokenSource? metadataCancellation = null;
        nameBox.TextChanged += (_, _) => { if (!assigningServerName) nameEdited = true; };
        async Task PreviewMetadataAsync()
        {
            metadataCancellation?.Cancel();
            var generation = ++metadataGeneration;
            using var cancellation = new CancellationTokenSource();
            metadataCancellation = cancellation;
            metadataBusy.IsActive = true;
            metadataBusy.Visibility = Visibility.Visible;
            checkMetadata.IsEnabled = false;
            metadataText.Text = "Checking the server… You can start without waiting.";
            try
            {
                var request = new FileMetadataRequest { Url = url };
                if (useAuthentication.IsChecked == true)
                {
                    if (string.IsNullOrWhiteSpace(authenticationUser.Text) || authenticationUser.Text.IndexOfAny([':', '\r', '\n']) >= 0)
                        throw new ArgumentException("Enter a user name without colons or line breaks, then check file info again.");
                    request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                        $"{authenticationUser.Text}:{authenticationPassword.Password}"));
                }
                var metadata = await _connection.ReadFileMetadataAsync(request, cancellation.Token);
                if (metadataClosed || generation != metadataGeneration) return;
                if (!metadata.Ok) throw new InvalidOperationException(metadata.Error ?? "File information is unavailable.");
                var size = metadata.TotalBytes is { } bytes ? $"{bytes:N0} bytes" : "Size unknown";
                metadataText.Text = $"{size} · {metadata.ContentType ?? "Type unknown"}\nServer name: {metadata.FileName}";
                if (!nameEdited && !string.IsNullOrWhiteSpace(metadata.FileName))
                {
                    assigningServerName = true;
                    nameBox.Text = metadata.FileName;
                    assigningServerName = false;
                }
            }
            catch (OperationCanceledException)
            {
                if (!metadataClosed && generation == metadataGeneration)
                    metadataText.Text = "File information request timed out. You can still start the download.";
            }
            catch (Exception ex)
            {
                if (!metadataClosed && generation == metadataGeneration)
                    metadataText.Text = $"File information unavailable: {ex.Message} You can still start the download.";
            }
            finally
            {
                if (ReferenceEquals(metadataCancellation, cancellation)) metadataCancellation = null;
                if (!metadataClosed && generation == metadataGeneration)
                {
                    metadataBusy.IsActive = false;
                    metadataBusy.Visibility = Visibility.Collapsed;
                    checkMetadata.IsEnabled = true;
                }
            }
        }
        void MetadataCredentialsChanged()
        {
            metadataGeneration++;
            metadataCancellation?.Cancel();
            metadataBusy.IsActive = false;
            metadataBusy.Visibility = Visibility.Collapsed;
            checkMetadata.IsEnabled = true;
            metadataText.Text = "Credentials changed. Check file info again to refresh the preview.";
        }
        checkMetadata.Click += async (_, _) => await PreviewMetadataAsync();
        authenticationUser.TextChanged += (_, _) => MetadataCredentialsChanged();
        authenticationPassword.PasswordChanged += (_, _) => MetadataCredentialsChanged();
        useAuthentication.Checked += (_, _) => MetadataCredentialsChanged();
        useAuthentication.Unchecked += (_, _) => MetadataCredentialsChanged();

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(urlDisplay);
        if (!string.IsNullOrWhiteSpace(addressNote))
        {
            panel.Children.Add(new TextBlock { Text = DownloadManager.Core.Providers.UrlRedactor.HideAddresses(addressNote), TextWrapping = TextWrapping.Wrap });
        }

        panel.Children.Add(metadataText);
        panel.Children.Add(metadataActions);
        panel.Children.Add(nameBox);
        panel.Children.Add(categoryBox);
        panel.Children.Add(new TextBlock { Text = "Destination folder" });
        panel.Children.Add(destinationRow);
        panel.Children.Add(destinationPickerError);
        panel.Children.Add(rememberFolder);
        panel.Children.Add(new Expander { Header = "Create a category", Content = categoryPanel, HorizontalAlignment = HorizontalAlignment.Stretch });
        panel.Children.Add(descriptionBox);
        panel.Children.Add(new Expander { Header = "Authentication (optional)", Content = authenticationPanel,
            HorizontalAlignment = HorizontalAlignment.Stretch });
        var advanced = new StackPanel { Spacing = 10 };
        advanced.Children.Add(queueBox);
        advanced.Children.Add(conflictBox);
        panel.Children.Add(new Expander { Header = "Queue and file conflicts", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch });

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Download file info",
            Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(200, Math.Min(480, RootGrid.ActualHeight - 240)) },
            PrimaryButtonText = "Start download",
            SecondaryButtonText = "Download later",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        fileInfoDialog = dialog;
        dialog.Opened += async (_, _) => await PreviewMetadataAsync();
        dialog.Closing += (_, _) =>
        {
            metadataClosed = true;
            metadataGeneration++;
            metadataCancellation?.Cancel();
        };

        void ValidateAuthentication(ContentDialogButtonClickEventArgs args)
        {
            if (useAuthentication.IsChecked == true &&
                (string.IsNullOrWhiteSpace(authenticationUser.Text) || authenticationUser.Text.IndexOfAny([':', '\r', '\n']) >= 0))
            {
                authenticationError.Text = "Enter a user name without colons or line breaks.";
                authenticationError.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        }
        dialog.PrimaryButtonClick += (_, args) => ValidateAuthentication(args);
        dialog.SecondaryButtonClick += (_, args) => ValidateAuthentication(args);
        var downloadChoice = await dialog.ShowAsync();
        if (downloadChoice == ContentDialogResult.None)
        {
            return;
        }

        var existing = FindExistingJob(url);
        if (existing is not null)
        {
            var duplicateDialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = "Download already exists",
                Content = new TextBlock
                {
                    Text = $"{existing.FileName} ({existing.StateText}) is already in the list for this URL.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Download again",
                SecondaryButtonText = "Select existing",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            var duplicateResult = await duplicateDialog.ShowAsync();
            if (duplicateResult == ContentDialogResult.Secondary)
            {
                JobsList.SelectedItem = existing;
                return;
            }

            if (duplicateResult != ContentDialogResult.Primary)
            {
                return;
            }
        }

        var request = new AddJobRequest
        {
            Url = url,
            FileName = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim(),
            CategoryId = (categoryBox.SelectedItem as ComboBoxItem)?.Tag as string,
            DestinationDirectory = string.IsNullOrWhiteSpace(destinationBox.Text) ? null : destinationBox.Text.Trim(),
            StartMode = downloadChoice == ContentDialogResult.Secondary ? "later" : "now",
            Description = string.IsNullOrWhiteSpace(descriptionBox.Text) ? null : descriptionBox.Text.Trim(),
            RememberDestination = rememberFolder.IsChecked == true,
            ConflictPolicy = conflictBox.SelectedIndex switch
            {
                1 => "overwrite",
                2 => "fail",
                _ => "rename",
            },
            Source = "api",
            QueueId = _settings.Queues.OrderBy(q => q.Order)
                .ElementAtOrDefault(Math.Max(0, queueBox.SelectedIndex))?.Id ?? _settings.DefaultQueueId,
        };

        try
        {
            if (useAuthentication.IsChecked == true)
            {
                request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes($"{authenticationUser.Text}:{authenticationPassword.Password}"));
            }
            authenticationPassword.Password = string.Empty;
            var reply = await _connection.AddAsync(request);
            var add = reply.GetPayload<AddJobResponse>();
            if (!add.Accepted)
            {
                await ShowMessageAsync("Could not add download", add.Error ?? "The host rejected the request.");
                return;
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not add download", ex.Message);
        }
    }

    private async void MenuAddBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        if (await ProviderAddAppliesAsync())
        {
            await ShowProviderAddAsync(AddFocus.Links);
            return;
        }

        var pasteBox = new TextBox
        {
            Header = "Paste links or any text containing links",
            PlaceholderText = "https://example.com/file.part1.rar\nhttps://example.com/file.part2.rar",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 160,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(pasteBox, ScrollBarVisibility.Auto);
        try
        {
            var clip = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (clip.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                var text = await clip.GetTextAsync();
                if (LinkExtractor.HttpLinks(text).Count > 0) pasteBox.Text = text;
            }
        }
        catch (Exception)
        {
            // The clipboard can be locked by another app; the box just starts empty.
        }
        var keepPackage = new CheckBox { Content = "Keep as one package", IsChecked = true };
        var destinationBox = new TextBox
        {
            Header = "Destination folder",
            Text = _settings.DefaultDownloadDirectory,
        };
        var startMode = new ComboBox { Header = "Start" };
        startMode.Items.Add("Start now");
        startMode.Items.Add("Download later");
        startMode.SelectedIndex = 0;
        var queueBox = new ComboBox { Header = "Queue" };
        foreach (var queue in _settings.Queues.OrderBy(q => q.Order))
        {
            queueBox.Items.Add(queue.Name);
        }

        var defaultQueueIndex = _settings.Queues
            .OrderBy(q => q.Order)
            .ToList()
            .FindIndex(q => q.Id == _settings.DefaultQueueId);
        queueBox.SelectedIndex = Math.Max(0, defaultQueueIndex);

        var conflictBox = new ComboBox { Header = "If a file exists" };
        conflictBox.Items.Add("Rename the new file");
        conflictBox.Items.Add("Overwrite the existing file");
        conflictBox.Items.Add("Fail the new download");
        conflictBox.SelectedIndex = 0;
        var skipDuplicates = new CheckBox
        {
            Content = "Skip URLs that are already in the list",
            IsChecked = true,
        };

        var panel = new StackPanel { Spacing = 10 };
        AddUnreadableKeyNotice(panel);
        panel.Children.Add(pasteBox);
        panel.Children.Add(destinationBox);
        panel.Children.Add(startMode);
        panel.Children.Add(queueBox);
        panel.Children.Add(conflictBox);
        panel.Children.Add(skipDuplicates);
        panel.Children.Add(keepPackage);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Add links",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = "Review",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var found = LinkExtractor.HttpLinks(pasteBox.Text);
        if (found.Count == 0)
        {
            await ShowMessageAsync("Add links", "No http or https links were found in the pasted text.");
            return;
        }

        var checks = new List<(CheckBox Box, string Url)>();
        var list = new StackPanel { Spacing = 2 };
        foreach (var link in found)
        {
            var uri = new Uri(link);
            var name = Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty);
            var listed = FindExistingJob(link) is not null;
            var box = new CheckBox
            {
                IsChecked = !(listed && skipDuplicates.IsChecked == true),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = name.Length > 0 ? name : uri.Host, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock
                        {
                            Text = uri.Host + (listed ? " · already in the list" : string.Empty),
                            FontSize = 12,
                            Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", RootGrid),
                        },
                    },
                },
            };
            ToolTipService.SetToolTip(box, RemoteText.AddressToShow(link, null));
            checks.Add((box, link));
            list.Children.Add(box);
        }
        var toggleAll = new CheckBox { Content = "Select all", IsChecked = checks.All(c => c.Box.IsChecked == true) };
        toggleAll.Click += (_, _) => { foreach (var c in checks) c.Box.IsChecked = toggleAll.IsChecked == true; };
        var review = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = $"Review {found.Count} link{(found.Count == 1 ? string.Empty : "s")}",
            Content = new StackPanel
            {
                Spacing = 8,
                Children = { toggleAll, new ScrollViewer { Content = list, MaxHeight = 420 } },
            },
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await review.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        IReadOnlyList<string> urls = checks.Where(c => c.Box.IsChecked == true).Select(c => c.Url).ToList();
        if (urls.Count == 0)
        {
            return;
        }

        var queueId = _settings.Queues.OrderBy(q => q.Order)
            .ElementAtOrDefault(Math.Max(0, queueBox.SelectedIndex))?.Id ?? _settings.DefaultQueueId;
        var destination = string.IsNullOrWhiteSpace(destinationBox.Text)
            ? null
            : destinationBox.Text.Trim();
        var added = await AddOrdinaryLinksAsync(urls, keepPackage.IsChecked == true, destination, startMode.SelectedIndex == 1 ? "later" : "now", queueId, conflictBox.SelectedIndex);
        await RefreshAsync();
        await ShowMessageAsync("Add links", added.Summary);
    }

    /// <summary>Says why this ordinary dialog opened when the reason is a saved key that can't be read.</summary>
    private void AddUnreadableKeyNotice(Panel panel)
    {
        if (RemoteText.UnreadableKeyNotice(_remote.State.Accounts) is not { } words) return;
        var notice = new StatusLine("Provider key");
        notice.Show(words, isProblem: true);
        panel.Children.Add(notice.Text);
    }

    /// <summary>What came of adding ordinary links. The first job is how the package they went into gets found.</summary>
    private sealed record OrdinaryAddResult(int Requested, int Accepted, int Duplicates, int Failed, string? FirstError, Guid? FirstJobId)
    {
        public string Summary => $"Added {Accepted} of {Requested} downloads" +
            (Duplicates > 0 ? $" ({Duplicates} already queued)" : string.Empty) +
            (Failed > 0 ? $"; {Failed} failed. {FirstError}" : ".");
    }

    /// <summary>One <c>job.add</c> per link, in a package of their own when asked. Only for links nothing says should go through a provider.</summary>
    private async Task<OrdinaryAddResult> AddOrdinaryLinksAsync(IReadOnlyList<string> urls, bool keepPackage, string? destination, string start, int queueId, int conflictIndex)
    {
        string? packageKey = null;
        string? packageName = null;
        if (keepPackage && urls.Count >= 2)
        {
            packageKey = Guid.NewGuid().ToString("N");
            // Name a package of split-archive parts after the archive.
            var bases = urls.Select(link => System.Text.RegularExpressions.Regex.Match(
                    Uri.UnescapeDataString(new Uri(link).Segments.LastOrDefault() ?? string.Empty),
                    @"^(?<b>.+?)(\.part\d+\.rar|\.r\d{2,3}|\.z\d{2}|\.rar|\.zip|\.\d{3})$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .Select(match => match.Success ? match.Groups["b"].Value : null)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Otherwise the Host names it from the file names the servers report.
            packageName = bases is [{ } archive] ? archive : null;
        }

        var accepted = 0;
        var duplicates = 0;
        var failed = 0;
        string? firstError = null;
        Guid? firstJob = null;

        foreach (var url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                failed++;
                firstError ??= "That isn't an http or https address.";
                continue;
            }

            try
            {
                var reply = await _connection.AddAsync(new AddJobRequest
                {
                    Url = url,
                    DestinationDirectory = destination,
                    StartMode = start,
                    ConflictPolicy = conflictIndex switch
                    {
                        1 => "overwrite",
                        2 => "fail",
                        _ => "rename",
                    },
                    Source = "batch",
                    QueueId = queueId,
                    PackageKey = packageKey,
                    PackageName = packageName,
                });
                var add = reply.GetPayload<AddJobResponse>();
                if (add.Accepted)
                {
                    accepted++;
                    firstJob ??= add.Job?.Id;
                    if (add.Duplicate)
                    {
                        duplicates++;
                    }
                }
                else
                {
                    failed++;
                    firstError ??= add.Error;
                }
            }
            catch (Exception ex)
            {
                failed++;
                firstError ??= ex.Message;
            }
        }

        return new OrdinaryAddResult(urls.Count, accepted, duplicates, failed, firstError, firstJob);
    }

    private async Task ShowQueuesAsync()
    {
        var snapshot = await _connection.RefreshAsync();
        _settings = snapshot.Settings;
        var panel = new StackPanel { Spacing = 8 };
        var action = string.Empty;
        var actionQueue = 0;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Queues",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = Math.Max(220, Math.Min(480, RootGrid.ActualHeight - 240)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            PrimaryButtonText = "New queue",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
        };

        foreach (var queue in _settings.Queues.OrderBy(queue => queue.Order))
        {
            var members = snapshot.Jobs.Count(job => job.QueueId == queue.Id);
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 2 };
            info.Children.Add(new TextBlock
            {
                Text = $"{queue.Name} — {(queue.IsRunning ? "running" : "stopped")}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{members} download{(members == 1 ? "" : "s")}; " +
                       $"max {queue.MaxConcurrentDownloads} concurrent" +
                       (queue.QuotaEnabled ? $"; quota {queue.QuotaBytes:N0} bytes" : string.Empty),
                FontSize = 11,
                Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", RootGrid),
            });
            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            var actions = new StackPanel { Spacing = 4 };
            var primaryActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var orderActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var edit = new Button { Content = "Edit" };
            edit.Click += (_, _) =>
            {
                action = "edit";
                actionQueue = queue.Id;
                dialog.Hide();
            };
            primaryActions.Children.Add(edit);
            var files = new Button { Content = "Files" };
            files.Click += (_, _) =>
            {
                action = "files";
                actionQueue = queue.Id;
                dialog.Hide();
            };
            primaryActions.Children.Add(files);

            var start = new Button { Content = "Start", IsEnabled = !queue.IsRunning };
            start.Click += (_, _) =>
            {
                action = "start";
                actionQueue = queue.Id;
                dialog.Hide();
            };
            primaryActions.Children.Add(start);

            var stop = new Button { Content = "Stop", IsEnabled = queue.IsRunning };
            stop.Click += (_, _) =>
            {
                action = "stop";
                actionQueue = queue.Id;
                dialog.Hide();
            };
            primaryActions.Children.Add(stop);

            var orderedQueues = _settings.Queues.OrderBy(item => item.Order).ToList();
            var queueIndex = orderedQueues.FindIndex(item => item.Id == queue.Id);
            var moveUp = new Button { Content = "Move up", IsEnabled = queueIndex > 0 };
            moveUp.Click += (_, _) =>
            {
                action = "moveup";
                actionQueue = queue.Id;
                dialog.Hide();
            };
            orderActions.Children.Add(moveUp);
            var moveDown = new Button { Content = "Move down", IsEnabled = queueIndex >= 0 && queueIndex < orderedQueues.Count - 1 };
            moveDown.Click += (_, _) =>
            {
                action = "movedown";
                actionQueue = queue.Id;
                dialog.Hide();
            };
            orderActions.Children.Add(moveDown);
            var delete = new Button { Content = "Delete" };
            delete.Click += (_, _) =>
            {
                action = "delete";
                actionQueue = queue.Id;
                dialog.Hide();
            };
            orderActions.Children.Add(delete);
            actions.Children.Add(primaryActions);
            actions.Children.Add(orderActions);
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);

            panel.Children.Add(row);
            panel.Children.Add(new Border
            {
                Height = 1,
                Background = InboundTheme.Brush("InboundHighlightBrush", RootGrid),
            });
        }

        var dialogResult = await dialog.ShowAsync();
        if (action == "files")
        {
            await ShowQueueFilesAsync(actionQueue);
            await ShowQueuesAsync();
            return;
        }
        if (action == "edit")
        {
            if (await ShowQueueEditorAsync(actionQueue))
                await ShowQueuesAsync();
            return;
        }

        if (dialogResult == ContentDialogResult.Primary)
        {
            if (await ShowQueueEditorAsync())
                await ShowQueuesAsync();
            return;
        }

        if (action.Length == 0)
        {
            return;
        }

        int? moveContentsToQueueId = null;
        if (action == "delete")
        {
            var destination = new ComboBox { Header = "Move downloads and schedules to", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var queue in _settings.Queues.Where(queue => queue.Id != actionQueue).OrderBy(queue => queue.Order))
                destination.Items.Add(new ComboBoxItem { Content = queue.Name, Tag = queue.Id });
            destination.SelectedItem = destination.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                item.Tag is int id && id == _settings.DefaultQueueId);
            var deletePanel = new StackPanel { Spacing = 12 };
            deletePanel.Children.Add(new TextBlock { Text = "Downloads and schedules will move to the selected queue. A stopped destination pauses active transfers. Downloaded files stay in their current folders. The default queue cannot be deleted.", TextWrapping = TextWrapping.Wrap });
            deletePanel.Children.Add(destination);
            var confirm = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = "Delete queue?",
                Content = deletePanel,
                PrimaryButtonText = "Delete queue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                await ShowQueuesAsync();
                return;
            }
            if (destination.SelectedItem is not ComboBoxItem { Tag: int targetQueueId }) return;
            moveContentsToQueueId = targetQueueId;
        }

        var reply = await _connection.QueueActionAsync(new QueueActionRequest
        {
            QueueId = actionQueue,
            Action = action,
            MoveContentsToQueueId = moveContentsToQueueId,
        });
        var result = reply.GetPayload<SettingsResult>();
        if (!result.Ok)
        {
            await ShowMessageAsync("Queue action failed", result.Error ?? "The host rejected the action.");
            return;
        }

        _settings = result.Settings;
        await RefreshAsync();
        await ShowQueuesAsync();
    }

    private async Task<bool> ShowQueueEditorAsync(int? queueId = null)
    {
        var snapshot = await _connection.RefreshAsync();
        var settings = CloneSettings(snapshot.Settings);
        var existing = queueId is { } id
            ? settings.Queues.FirstOrDefault(queue => queue.Id == id)
            : null;
        if (queueId is not null && existing is null)
        {
            await ShowMessageAsync("Queue", "This queue no longer exists.");
            return false;
        }

        var name = new TextBox
        {
            Header = "Queue name",
            Text = existing?.Name ?? $"Queue {settings.Queues.Count + 1}",
            MaxLength = 80,
        };
        var concurrent = new TextBox
        {
            Header = "Download files at the same time (1–16)",
            Text = (existing?.MaxConcurrentDownloads ?? 1).ToString(System.Globalization.CultureInfo.CurrentCulture),
        };
        var quotaEnabled = new CheckBox
        {
            Content = "Enable download limit",
            IsChecked = existing?.QuotaEnabled ?? false,
        };
        var quotaMb = new TextBox
        {
            Header = "Download no more than (MB; max 10,485,760)",
            Text = ((existing?.QuotaBytes ?? 200L * 1024 * 1024) / (1024m * 1024m))
                .ToString("0.##", System.Globalization.CultureInfo.CurrentCulture),
        };
        var quotaHours = new TextBox
        {
            Header = "Every (hours; 1–8,760)",
            Text = (existing?.QuotaWindowHours ?? 5).ToString(System.Globalization.CultureInfo.CurrentCulture),
        };
        void UpdateQuotaControls()
        {
            quotaMb.IsEnabled = quotaEnabled.IsChecked == true;
            quotaHours.IsEnabled = quotaEnabled.IsChecked == true;
        }
        quotaEnabled.Checked += (_, _) => UpdateQuotaControls();
        quotaEnabled.Unchecked += (_, _) => UpdateQuotaControls();
        UpdateQuotaControls();

        var error = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            Foreground = InboundTheme.Brush("InboundErrorTextBrush", RootGrid),
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(name);
        panel.Children.Add(concurrent);
        panel.Children.Add(quotaEnabled);
        panel.Children.Add(quotaMb);
        panel.Children.Add(quotaHours);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = existing is null ? "New queue" : "Edit queue",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = Math.Max(220, Math.Min(480, RootGrid.ActualHeight - 240)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            PrimaryButtonText = existing is null ? "Create" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        int parsedConcurrent = 0;
        int parsedHours = 0;
        long parsedQuotaBytes = 0;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var message = string.Empty;
            var trimmedName = name.Text.Trim();
            var useQuota = quotaEnabled.IsChecked == true;
            var quotaValid = !useQuota ||
                (decimal.TryParse(quotaMb.Text, System.Globalization.NumberStyles.Number,
                     System.Globalization.CultureInfo.CurrentCulture, out var mb) &&
                 mb > 0 && mb <= 10_485_760m);
            var parsedMb = useQuota && quotaValid
                ? decimal.Parse(quotaMb.Text, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.CurrentCulture)
                : 0m;
            var quotaBytes = useQuota
                ? (long)Math.Round(parsedMb * 1024m * 1024m, MidpointRounding.AwayFromZero)
                : 0;
            if (trimmedName.Length == 0)
                message = "Enter a queue name.";
            else if (!int.TryParse(concurrent.Text, System.Globalization.NumberStyles.Integer,
                         System.Globalization.CultureInfo.CurrentCulture, out parsedConcurrent) ||
                     parsedConcurrent is < 1 or > 16)
                message = "Simultaneous downloads must be between 1 and 16.";
            else if (!quotaValid)
                message = "The quota must be a positive number of MB, up to 10,485,760.";
            else if (useQuota && quotaBytes < 1)
                message = "The quota must be large enough to represent at least one byte.";
            else if (useQuota &&
                     (!int.TryParse(quotaHours.Text, System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.CurrentCulture, out parsedHours) ||
                      parsedHours is < 1 or > 8_760))
                message = "The quota window must be between 1 and 8,760 hours.";
            else if (useQuota)
                parsedQuotaBytes = quotaBytes;

            if (message.Length > 0)
            {
                error.Text = message;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
            else
            {
                error.Text = string.Empty;
                error.Visibility = Visibility.Collapsed;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return false;

        // Rebase onto current settings after the modal editor closes so quota
        // usage accumulated while it was open is preserved.
        var saveSnapshot = await _connection.RefreshAsync();
        settings = CloneSettings(saveSnapshot.Settings);
        var saveExisting = queueId is { } existingId
            ? settings.Queues.FirstOrDefault(queue => queue.Id == existingId)
            : null;
        if (queueId is not null && saveExisting is null)
        {
            await ShowMessageAsync("Queue", "This queue was removed before the changes could be saved.");
            return false;
        }

        var useQuota = quotaEnabled.IsChecked == true;
        var reply = await _connection.QueueActionAsync(new QueueActionRequest
        {
            QueueId = saveExisting?.Id ?? 0,
            Action = saveExisting is null ? "create" : "policy",
            Name = name.Text.Trim(),
            MaxConcurrentDownloads = parsedConcurrent,
            QuotaEnabled = useQuota,
            QuotaBytes = useQuota ? parsedQuotaBytes : null,
            QuotaWindowHours = useQuota ? parsedHours : null,
        });
        var result = reply.GetPayload<SettingsResult>();
        if (!result.Ok)
        {
            await ShowMessageAsync("Could not save queue", result.Error ?? "The host rejected the change.");
            return false;
        }

        _settings = result.Settings;
        await RefreshAsync();
        return true;
    }

    private void MenuScheduler_Click(object sender, RoutedEventArgs e)
    {
        if (_schedulerWindow is null)
        {
            _schedulerWindow = new SchedulerWindow();
            _schedulerWindow.Closed += (_, _) => _schedulerWindow = null;
        }
        _schedulerWindow.ShowWindow();
    }

    private sealed record ScheduleTarget(int QueueId, string Label);

    private async Task ShowSchedulerAsync()
    {
        var snapshot = await _connection.RefreshAsync();
        _settings = snapshot.Settings;

        var settings = CloneSettings(_settings);
        var panel = new StackPanel { Spacing = 8 };
        if (settings.Schedules.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No schedules yet.",
                Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", RootGrid),
            });
        }

        var action = string.Empty;
        Guid? actionId = null;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Scheduler",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = Math.Max(240, Math.Min(540, RootGrid.ActualHeight - 220)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            PrimaryButtonText = "Add schedule",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
        };

        foreach (var schedule in settings.Schedules.OrderBy(schedule => schedule.StartTime))
        {
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var target = settings.Queues.FirstOrDefault(queue => queue.Id == schedule.QueueId)?.Name ?? "missing queue";
            var info = new StackPanel { Spacing = 2 };
            info.Children.Add(new TextBlock
            {
                Text = $"{schedule.Name} — {(schedule.Enabled ? "enabled" : "disabled")}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            info.Children.Add(new TextBlock
            {
                Text = (schedule.TimedStartEnabled ? $"{schedule.Kind} {schedule.StartTime}" : "Startup trigger") +
                       (schedule.StopTime is { } stop ? $"–{stop}" : string.Empty) +
                       $" [{schedule.Days}] → {target}",
                FontSize = 11,
                Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", RootGrid),
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            info.Children.Add(new TextBlock { Text =
                (schedule.StartOnHostStartup ? "On Host startup · " : string.Empty) +
                (schedule.RepeatEveryMinutes is { } minutes ? $"Synchronize every {minutes} min · " : string.Empty) +
                (schedule.LastRunAt is { } run ? $"Last run {run.ToLocalTime():g}" : "Not run yet"),
                TextWrapping = TextWrapping.Wrap, FontSize = 11 });
            if (!string.IsNullOrWhiteSpace(schedule.LastRunError))
                info.Children.Add(new TextBlock { Text = schedule.LastRunError, TextWrapping = TextWrapping.Wrap, FontSize = 11 });
            var edit = new Button { Content = "Edit" };
            edit.Click += (_, _) =>
            {
                action = "edit";
                actionId = schedule.Id;
                dialog.Hide();
            };
            Grid.SetColumn(edit, 1);
            row.Children.Add(edit);

            var toggle = new Button { Content = schedule.Enabled ? "Disable" : "Enable" };
            toggle.Click += (_, _) =>
            {
                action = "toggle";
                actionId = schedule.Id;
                dialog.Hide();
            };
            Grid.SetColumn(toggle, 2);
            row.Children.Add(toggle);

            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) =>
            {
                action = "remove";
                actionId = schedule.Id;
                dialog.Hide();
            };
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);

            panel.Children.Add(row);
            panel.Children.Add(new Border
            {
                Height = 1,
                Background = InboundTheme.Brush("InboundHighlightBrush", RootGrid),
            });
        }

        var dialogResult = await dialog.ShowAsync();
        if (action == "edit" && actionId is Guid editId)
        {
            if (await ShowScheduleEditorAsync(editId))
                await ShowSchedulerAsync();
            return;
        }

        if (actionId is Guid scheduleId && (action == "toggle" || action == "remove"))
        {
            var latest = await _connection.RefreshAsync();
            var updated = CloneSettings(latest.Settings);
            var existing = updated.Schedules.FirstOrDefault(schedule => schedule.Id == scheduleId);
            if (existing is not null)
            {
                if (action == "toggle")
                {
                    existing.Enabled = !existing.Enabled;
                }
                else
                {
                    updated.Schedules.Remove(existing);
                }

                var reply = await _connection.UpdateSettingsAsync(updated);
                var result = reply.GetPayload<SettingsResult>();
                if (!result.Ok)
                {
                    await ShowMessageAsync("Scheduler", result.Error ?? "The host rejected the change.");
                    return;
                }
            }

            await ShowSchedulerAsync();
            return;
        }

        if (dialogResult != ContentDialogResult.Primary)
            return;
        if (await ShowScheduleEditorAsync())
            await ShowSchedulerAsync();
    }

    private async Task<bool> ShowScheduleEditorAsync(Guid? scheduleId = null)
    {
        var snapshot = await _connection.RefreshAsync();
        var settings = CloneSettings(snapshot.Settings);
        var existing = scheduleId is { } id
            ? settings.Schedules.FirstOrDefault(schedule => schedule.Id == id)
            : null;
        if (scheduleId is not null && existing is null)
        {
            await ShowMessageAsync("Scheduler", "This schedule no longer exists.");
            return false;
        }

        var targets = settings.Queues
            .OrderBy(queue => queue.Order)
            .Select(queue => new ScheduleTarget(queue.Id, $"Queue: {queue.Name}"))
            .ToList();
        if (targets.Count == 0)
        {
            await ShowMessageAsync("Scheduler", "Create a queue before adding a schedule.");
            return false;
        }

        var nameBox = new TextBox
        {
            Header = "Name",
            PlaceholderText = "Nightly queue",
            Text = existing?.Name ?? string.Empty,
            MaxLength = 80,
        };
        var targetBox = new ComboBox { Header = "Target" };
        foreach (var target in targets)
            targetBox.Items.Add(target.Label);
        var targetIndex = existing is null
            ? 0
            : targets.FindIndex(target => target.QueueId == existing.QueueId);
        targetBox.SelectedIndex = targetIndex >= 0 ? targetIndex : 0;

        var runOptions = new SchedulerRunOptionsEditor(existing,
            settings.Queues.FirstOrDefault(queue => queue.Id == (existing?.QueueId ?? targets[0].QueueId))?.RetryLimit);
        targetBox.SelectionChanged += (_, _) =>
        {
            if (targetBox.SelectedIndex >= 0 && targetBox.SelectedIndex < targets.Count)
                runOptions.SetRetryLimit(settings.Queues.FirstOrDefault(queue => queue.Id == targets[targetBox.SelectedIndex].QueueId)?.RetryLimit);
        };
        var startBox = new TextBox { Header = "Start (HH:mm)", Text = existing?.StartTime ?? "09:00" };
        var stopBox = new TextBox { Header = "Stop (HH:mm, optional)", Text = existing?.StopTime ?? string.Empty };
        var daysBox = new TextBox
        {
            Header = "Days (daily schedules; comma-separated)",
            Text = existing?.Days ?? "Mon,Tue,Wed,Thu,Fri,Sat,Sun",
        };
        var onceBox = new TextBox
        {
            Header = "One-time date (yyyy-MM-dd, optional)",
            Text = existing?.Kind == "oneTime" && existing.StartDate is { } date
                ? date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty,
        };

        var openWhenDone = new CheckBox
        {
            Content = "Open a file when the queue finishes",
            IsChecked = existing?.OpenFileWhenDone ?? false,
        };
        var exitWhenDone = new CheckBox
        {
            Content = "Exit Download Manager when the queue finishes",
            IsChecked = existing?.ExitWhenDone ?? false,
        };
        var shutdownWhenDone = new CheckBox
        {
            Content = "Turn off the computer when the queue finishes",
            IsChecked = existing?.ShutdownWhenDone ?? false,
        };
        var shutdownMode = new ComboBox
        {
            Header = "Computer action",
            ItemsSource = new[] { "Shut down", "Restart", "Log off" },
            SelectedIndex = (existing?.ShutdownMode ?? "shutdown") switch
            {
                "restart" => 1,
                "logoff" => 2,
                _ => 0,
            },
        };
        var forceShutdown = new CheckBox
        {
            Content = "Force the selected computer action",
            IsChecked = existing?.ForceShutdown ?? false,
        };
        var allowSystemActions = new CheckBox
        {
            Content = "Allow system completion actions (global opt-in)",
            IsChecked = settings.AllowSystemCompletionActions,
        };
        var initialAllowSystemActions = settings.AllowSystemCompletionActions;
        var systemActionsNote = new TextBlock
        {
            Text = settings.AllowSystemCompletionActions
                ? "Global system actions are enabled. Queue exit and computer actions will be allowed when configured."
                : "Global system actions are disabled. Exit and computer actions saved below will be skipped until this opt-in is enabled.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = InboundTheme.Brush("InboundSecondaryTextBrush", RootGrid),
        };
        allowSystemActions.Checked += (_, _) => systemActionsNote.Text =
            "Global system actions are enabled. Queue exit and computer actions will be allowed when configured.";
        allowSystemActions.Unchecked += (_, _) => systemActionsNote.Text =
            "Global system actions are disabled. Exit and computer actions saved below will be skipped until this opt-in is enabled.";

        void UpdateShutdownControls()
        {
            shutdownMode.IsEnabled = shutdownWhenDone.IsChecked == true;
            forceShutdown.IsEnabled = shutdownWhenDone.IsChecked == true;
        }
        shutdownWhenDone.Checked += (_, _) => UpdateShutdownControls();
        shutdownWhenDone.Unchecked += (_, _) => UpdateShutdownControls();
        UpdateShutdownControls();

        var error = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            Foreground = InboundTheme.Brush("InboundErrorTextBrush", RootGrid),
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(nameBox);
        panel.Children.Add(targetBox);
        panel.Children.Add(runOptions.Content);
        panel.Children.Add(startBox);
        panel.Children.Add(stopBox);
        panel.Children.Add(daysBox);
        panel.Children.Add(onceBox);
        panel.Children.Add(new TextBlock { Text = "When the queue finishes", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(openWhenDone);
        panel.Children.Add(exitWhenDone);
        panel.Children.Add(shutdownWhenDone);
        panel.Children.Add(shutdownMode);
        panel.Children.Add(forceShutdown);
        panel.Children.Add(allowSystemActions);
        panel.Children.Add(systemActionsNote);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = existing is null ? "Add schedule" : "Edit schedule",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = Math.Max(240, Math.Min(600, RootGrid.ActualHeight - 200)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            PrimaryButtonText = existing is null ? "Add schedule" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        TimeOnly parsedStart = default;
        TimeOnly? parsedStop = null;
        DateOnly? parsedDate = null;
        bool startOnStartup = false, timedStart = true;
        int? repeatMinutes = null, retryLimit = null;
        var valid = false;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var message = string.Empty;
            parsedStop = null;
            parsedDate = null;
            if (string.IsNullOrWhiteSpace(nameBox.Text))
                message = "Enter a schedule name.";
            else if (targetBox.SelectedIndex < 0 || targetBox.SelectedIndex >= targets.Count)
                message = "Choose a queue.";
            else if (!TimeOnly.TryParseExact(startBox.Text.Trim(), "HH:mm",
                         System.Globalization.CultureInfo.InvariantCulture,
                         System.Globalization.DateTimeStyles.None, out parsedStart))
                message = "Start time must use HH:mm.";
            else if (!string.IsNullOrWhiteSpace(stopBox.Text))
            {
                if (!TimeOnly.TryParseExact(stopBox.Text.Trim(), "HH:mm",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var stop))
                    message = "Stop time must use HH:mm.";
                else
                    parsedStop = stop;
            }

            if (message.Length == 0 && !string.IsNullOrWhiteSpace(onceBox.Text))
            {
                if (!DateOnly.TryParseExact(onceBox.Text.Trim(), "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var date))
                    message = "One-time date must use yyyy-MM-dd.";
                else
                    parsedDate = date;
            }

            if (message.Length == 0 && !runOptions.TryGet(out startOnStartup, out timedStart, out repeatMinutes, out retryLimit, out var runError))
                message = runError ?? "Invalid scheduler options.";
            valid = message.Length == 0;
            error.Text = message;
            error.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
            args.Cancel = !valid;
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !valid)
            return false;

        var latest = await _connection.RefreshAsync();
        var updated = CloneSettings(latest.Settings);
        if (targetBox.SelectedIndex < 0 || targetBox.SelectedIndex >= targets.Count)
        {
            await ShowMessageAsync("Scheduler", "Select a target queue.");
            return false;
        }
        var selectedQueueId = targets[targetBox.SelectedIndex].QueueId;
        if (!updated.Queues.Any(queue => queue.Id == selectedQueueId))
        {
            await ShowMessageAsync("Scheduler", "The selected queue was removed before the schedule could be saved.");
            return false;
        }
        ScheduleDto edited;
        if (scheduleId is { } editingId)
        {
            var currentSchedule = updated.Schedules.FirstOrDefault(schedule => schedule.Id == editingId);
            if (currentSchedule is null)
            {
                await ShowMessageAsync("Scheduler", "This schedule was removed before the changes could be saved.");
                return false;
            }
            edited = currentSchedule;
        }
        else
        {
            edited = new ScheduleDto
            {
                Id = Guid.NewGuid(),
                Enabled = true,
            };
            updated.Schedules.Add(edited);
        }

        edited.StartOnHostStartup = startOnStartup;
        edited.TimedStartEnabled = timedStart;
        edited.RepeatEveryMinutes = repeatMinutes;
        updated.Queues.First(queue => queue.Id == selectedQueueId).RetryLimit = retryLimit;
        edited.Name = nameBox.Text.Trim();
        edited.QueueId = selectedQueueId;
        edited.Kind = parsedDate is null ? "daily" : "oneTime";
        edited.StartDate = parsedDate;
        edited.Days = string.IsNullOrWhiteSpace(daysBox.Text)
            ? "Mon,Tue,Wed,Thu,Fri,Sat,Sun"
            : daysBox.Text.Trim();
        edited.StartTime = parsedStart.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        edited.StopTime = parsedStop?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        edited.OpenFileWhenDone = openWhenDone.IsChecked == true;
        edited.ExitWhenDone = exitWhenDone.IsChecked == true;
        edited.ShutdownWhenDone = shutdownWhenDone.IsChecked == true;
        edited.ShutdownMode = shutdownMode.SelectedIndex switch
        {
            1 => "restart",
            2 => "logoff",
            _ => "shutdown",
        };
        edited.ForceShutdown = forceShutdown.IsChecked == true;
        if ((allowSystemActions.IsChecked == true) != initialAllowSystemActions)
            updated.AllowSystemCompletionActions = allowSystemActions.IsChecked == true;

        var reply = await _connection.UpdateSettingsAsync(updated);
        var result = reply.GetPayload<SettingsResult>();
        if (!result.Ok)
        {
            await ShowMessageAsync("Scheduler", result.Error ?? "The host rejected the schedule.");
            return false;
        }

        _settings = result.Settings;
        await RefreshAsync();
        return true;
    }

    private JobViewModel? FindExistingJob(string url)
    {
        // The Host stores a provider link with its key masked, so that is what the list holds.
        var listed = DownloadManager.Core.Providers.ProviderLinks.IsProviderLink(url)
            ? DownloadManager.Core.Providers.ProviderLinks.MaskToken(url)
            : url;
        return _jobsById.Values.FirstOrDefault(job =>
            string.Equals(job.Url, listed, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(job.State, "cancelled", StringComparison.OrdinalIgnoreCase));
    }

    private async Task ShowSettingsDialogAsync()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        if (_settings is null) return;

        var settings = CloneSettings(_settings);
        var window = new SettingsWindow(RootGrid.ActualTheme);
        _settingsWindow = window;
        window.Closed += (_, _) => _settingsWindow = null;
        try
        {
            // Which remote and extraction pages exist depends on what the Host said it can do.
            if (!_remote.State.IsReady) await _remote.WaitUntilReadyAsync(TimeSpan.FromSeconds(1.5));
            var (directoryRow, directory) = window.FolderField("Default download folder", settings.DefaultDownloadDirectory);
            var maxConcurrent = SettingsWindow.Number("Maximum simultaneous downloads", settings.MaxConcurrentDownloads, 1, 16);
            var connections = SettingsWindow.Number("Connections per download", settings.DefaultConnections, 1, 32);
            var speedLimit = SettingsWindow.Number("Global speed limit (KB/s)",
                settings.GlobalSpeedLimitBytesPerSecond is > 0 ? settings.GlobalSpeedLimitBytesPerSecond.Value / 1024 : null,
                1, 10_000_000, "Unlimited");
            speedLimit.SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden;
            var profilesBox = new TextBox
            {
                Header = "One host pattern=maximum connections per line",
                Text = string.Join(
                    Environment.NewLine,
                    settings.ConnectionProfiles.Select(profile => $"{profile.Pattern}={profile.MaxConnections}")),
                AcceptsReturn = true,
                Height = 96,
                PlaceholderText = "*.example.com=2",
            };
            var launch = new CheckBox { Content = "Start Inbound when I sign in to Windows", IsChecked = settings.LaunchAtStartup };
            var clipboard = new CheckBox { Content = "Watch the clipboard for download links", IsChecked = settings.ClipboardMonitoring };
            var integration = new CheckBox { Content = "Capture downloads from Chrome", IsChecked = settings.BrowserIntegrationEnabled };
            var notify = new CheckBox { Content = "Show a notification", IsChecked = settings.NotifyOnCompletion };
            var sound = new CheckBox { Content = "Play a sound", IsChecked = settings.PlayCompletionSound };
            var completionDialog = new CheckBox { Content = "Show the download-complete window", IsChecked = settings.ShowCompletionDialog };
            var autoResume = new CheckBox { Content = "Resume unfinished downloads when Inbound starts", IsChecked = settings.AutoResumeUnfinishedOnStartup };
            var postDownload = new TextBox
            {
                Header = "Run this program after each download (optional)",
                Text = settings.PostDownloadCommand ?? string.Empty,
                PlaceholderText = @"C:\Program Files\Scanner\scan.exe",
            };
            var updateUrl = new TextBox { Header = "Update manifest URL (HTTPS)", Text = settings.UpdateManifestUrl ?? string.Empty };
            var updateKey = new TextBox
            {
                Header = "Update public key (PEM)",
                Text = settings.UpdatePublicKey ?? string.Empty,
                AcceptsReturn = true,
                Height = 96,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
            };
            var updateChannel = new TextBox { Header = "Update channel", Text = settings.UpdateChannel, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 200 };
            var autoUpdate = new CheckBox
            {
                Content = "Check for and install updates automatically while no downloads are running",
                IsChecked = settings.AutoCheckUpdates,
            };
            var updateInterval = SettingsWindow.Number("Check every (hours)", settings.UpdateCheckIntervalHours, 1, 168);

            var (proxyProtocolIndex, proxyHostText) = ProxyAddress.Split(settings.Proxy.Address);
            var proxyMode = new RadioButtons
            {
                Header = "Connection",
                ItemsSource = new[] { "No proxy", "Use the Windows proxy settings", "Use this proxy server:" },
                SelectedIndex = settings.Proxy.Mode switch { "system" => 1, "manual" => 2, _ => 0 },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(proxyMode, "Proxy mode");
            var proxyProtocol = new ComboBox
            {
                Header = "Type",
                MinWidth = 140,
                ItemsSource = ProxyAddress.ProtocolNames,
                SelectedIndex = proxyProtocolIndex,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(proxyProtocol, "Proxy type");
            var proxyAddress = new TextBox
            {
                Header = "Address (host:port)",
                Text = proxyHostText,
                PlaceholderText = "proxy.example.com:8080",
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(proxyAddress, "Proxy address");
            var proxyUser = new TextBox
            {
                Header = "User name (optional; SOCKS4/4a send it as the user ID)",
                Text = settings.Proxy.Username ?? string.Empty,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(proxyUser, "Proxy user name");
            var proxyPassword = new PasswordBox
            {
                Header = settings.Proxy.HasPassword
                    ? "Password (saved - leave empty to keep it)"
                    : "Password (optional)",
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(proxyPassword, "Proxy password");
            var forgetProxyPassword = new CheckBox
            {
                Content = "Forget the saved proxy password",
                Visibility = settings.Proxy.HasPassword ? Visibility.Visible : Visibility.Collapsed,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(forgetProxyPassword, "Forget the saved proxy password");
            var proxyServer = new Grid { ColumnSpacing = 8 };
            proxyServer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            proxyServer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            proxyServer.Children.Add(proxyProtocol);
            Grid.SetColumn(proxyAddress, 1);
            proxyServer.Children.Add(proxyAddress);
            var manualProxy = new StackPanel { Spacing = 12, Margin = new Thickness(28, 0, 0, 0) };
            manualProxy.Children.Add(proxyServer);
            manualProxy.Children.Add(proxyUser);
            manualProxy.Children.Add(proxyPassword);
            manualProxy.Children.Add(forgetProxyPassword);
            void UpdateProxyFields()
            {
                var manual = proxyMode.SelectedIndex == 2;
                proxyProtocol.IsEnabled = proxyAddress.IsEnabled = proxyUser.IsEnabled = manual;
                proxyPassword.IsEnabled = manual && forgetProxyPassword.IsChecked != true;
                forgetProxyPassword.IsEnabled = manual;
            }
            proxyMode.SelectionChanged += (_, _) => UpdateProxyFields();
            forgetProxyPassword.Checked += (_, _) => UpdateProxyFields();
            forgetProxyPassword.Unchecked += (_, _) => UpdateProxyFields();
            UpdateProxyFields();

            var loginRows = new List<(SiteLoginDto Original, TextBox Site, TextBox User, PasswordBox Password)>();
            var loginPanel = new StackPanel { Spacing = 10 };
            void AddLoginRow(SiteLoginDto original)
            {
                var site = new TextBox { Header = "Site or URL prefix", Text = original.Site,
                    PlaceholderText = "https://example.com/private/ or example.com" };
                var user = new TextBox { Header = "User name", Text = original.Username };
                var password = new PasswordBox { Header = original.HasPassword
                    ? "Password (saved; leave empty to keep)" : "Password" };
                var remove = new Button { Content = "Remove", HorizontalAlignment = HorizontalAlignment.Right };
                var credentials = new Grid { ColumnSpacing = 8 };
                credentials.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                credentials.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                credentials.Children.Add(user);
                Grid.SetColumn(password, 1);
                credentials.Children.Add(password);
                var row = new StackPanel { Spacing = 8 };
                row.Children.Add(site);
                row.Children.Add(credentials);
                row.Children.Add(remove);
                var border = new Border { Child = row, Padding = new Thickness(12), BorderThickness = new Thickness(1),
                    BorderBrush = InboundTheme.Brush("InboundDividerBrush", RootGrid),
                    CornerRadius = new CornerRadius(4) };
                var entry = (original, site, user, password);
                loginRows.Add(entry);
                loginPanel.Children.Add(border);
                remove.Click += (_, _) =>
                {
                    password.Password = string.Empty;
                    loginRows.Remove(entry);
                    loginPanel.Children.Remove(border);
                };
            }
            foreach (var login in settings.SiteLogins) AddLoginRow(login);
            var addLogin = new Button { Content = "Add site login" };
            addLogin.Click += (_, _) => AddLoginRow(new SiteLoginDto { Id = Guid.NewGuid() });
            var loginHelp = SettingsWindow.Hint("Credentials are saved securely by Windows and used for matching HTTP or FTP downloads. Re-enter the password when changing a saved site's address or user name.");

            var categoryBoxes = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
            var categoryRows = new List<UIElement>();
            foreach (var category in FileCategories.Names.Append("other"))
            {
                var (row, box) = window.FolderField($"{char.ToUpperInvariant(category[0])}{category[1..]}",
                    settings.CategoryFolders.TryGetValue(category, out var folder) ? folder : null, "Use the default folder", inlineLabel: true);
                categoryBoxes[category] = box;
                categoryRows.Add(row);
            }

            var customCategoryEditors = new List<(CategoryDefinitionDto Category, TextBox Name, TextBox Extensions, TextBox Folder)>();
            var customCategoryControls = new List<UIElement>();
            var stagedCategoryRemovals = new HashSet<string>(StringComparer.Ordinal);
            foreach (var category in settings.CustomCategories)
            {
                var name = new TextBox { Header = "Name", Text = category.Name };
                var extensions = new TextBox { Header = "File extensions (space separated)", Text = string.Join(" ", category.Extensions) };
                var (folderRow, folder) = window.FolderField("Destination folder (optional)", category.DestinationDirectory);
                customCategoryEditors.Add((category, name, extensions, folder));
                var remove = new Button { Content = "Remove category", HorizontalAlignment = HorizontalAlignment.Right };
                var fields = new StackPanel { Spacing = 8 };
                fields.Children.Add(name);
                fields.Children.Add(extensions);
                fields.Children.Add(folderRow);
                fields.Children.Add(remove);
                var expander = new Expander { Header = category.Name, Content = fields, HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                remove.Click += (_, _) =>
                {
                    if (!stagedCategoryRemovals.Add(category.Id))
                    {
                        stagedCategoryRemovals.Remove(category.Id);
                        remove.Content = "Remove category";
                        expander.Opacity = 1;
                    }
                    else
                    {
                        remove.Content = "Undo removal";
                        expander.Opacity = 0.55;
                    }
                };
                customCategoryControls.Add(expander);
            }

            const int ConnectionPage = 3, ProxyPage = 4, SiteLoginsPage = 5, UpdatesPage = 6, SoundsPage = 7;
            window.AddPage("General", "\uE80F",
                SettingsWindow.Section("Startup"), launch, autoResume,
                SettingsWindow.Section("Capturing downloads"), integration, clipboard);
            var saveTo = new List<UIElement> { directoryRow, SettingsWindow.Section("Folders by category"),
                SettingsWindow.Hint("Downloads of these types go to their own folder. Leave a field empty to use the default folder.") };
            saveTo.AddRange(categoryRows);
            if (customCategoryControls.Count > 0)
            {
                saveTo.Add(SettingsWindow.Section("Custom categories"));
                saveTo.AddRange(customCategoryControls);
            }
            window.AddPage("Save to", "\uE8B7", saveTo.ToArray());
            window.AddPage("Downloads", "\uE896",
                SettingsWindow.Section("When a download finishes"), completionDialog, notify, sound, postDownload);
            window.AddPage("Connection", "\uE774",
                maxConcurrent, connections, speedLimit,
                SettingsWindow.Hint("Leave the speed limit empty for unlimited."),
                SettingsWindow.Section("Per-site limits"), profilesBox);
            window.AddPage("Proxy", "\uE968", proxyMode, manualProxy);
            window.AddPage("Site logins", "\uE8D7", loginHelp, loginPanel, addLogin);
            // The update source (channel, manifest URL, signing key) is set by the installer and is
            // not user-editable; the hidden boxes only carry the saved values through Save.
            window.AddPage("Updates", "\uE895", autoUpdate, updateInterval);
            var soundEditor = new SoundOptionsEditor(settings.EventSoundFiles, window);
            window.AddPage("Sounds", "\uE767", soundEditor.Content);

            var nextPage = SoundsPage + 1;
            var accountPages = new List<(ProviderAccountPage Page, int Index)>();
            if (_remote.State.RemoteSupported)
            {
                foreach (var account in _remote.State.Accounts.Where(account => account.Capabilities.Contains("account", StringComparer.OrdinalIgnoreCase)))
                {
                    var page = new ProviderAccountPage(_remote, account);
                    page.KeyBoxEmptied += window.ClearMessage;
                    window.AddPage(page.Title, "\uE77B", page.Controls);
                    accountPages.Add((page, nextPage++));
                }
            }

            ExtractionOptionsPage? extraction = null;
            var extractionPageIndex = nextPage;
            if (_remote.State.ArchivesSupported)
            {
                extraction = new ExtractionOptionsPage(_remote, window);
                window.AddPage("Extraction", "\uE7B8", extraction.Controls);
            }

            window.Closed += (_, _) =>
            {
                foreach (var (page, _) in accountPages) page.Close();
                extraction?.Close();
            };

            static int? WholeNumber(NumberBox box) => double.IsNaN(box.Value) ? null : (int)Math.Round(box.Value);

            window.Save = async () =>
            {
                foreach (var (page, index) in accountPages)
                {
                    if (page.HasUnsavedKey)
                        return ($"The {page.ProviderName} API key you typed isn't saved. Select Save key, or empty the box.", index);
                }
                if (extraction?.Validate() is { } extractionProblem)
                    return (extractionProblem, extractionPageIndex);
                if (!soundEditor.TryApplyTo(settings.EventSoundFiles, out var soundError))
                    return (soundError ?? "Invalid sound choice.", SoundsPage);
                foreach (var row in loginRows)
                {
                    if (string.IsNullOrWhiteSpace(row.Site.Text) || string.IsNullOrWhiteSpace(row.User.Text))
                        return ("Every site login needs a site and user name. Remove unused rows before saving.", SiteLoginsPage);
                    if (row.Original.HasPassword && string.IsNullOrEmpty(row.Password.Password) &&
                        (!string.Equals(row.Original.Site, row.Site.Text.Trim(), StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(row.Original.Username, row.User.Text.Trim(), StringComparison.Ordinal)))
                        return ("Re-enter the password for a login whose site or user name changed.", SiteLoginsPage);
                }
                if (WholeNumber(maxConcurrent) is not { } max || max is < 1 or > 16)
                    return ("Maximum simultaneous downloads must be between 1 and 16.", ConnectionPage);
                if (WholeNumber(connections) is not { } connectionsValue || connectionsValue is < 1 or > 32)
                    return ("Connections per download must be between 1 and 32.", ConnectionPage);
                long? limit = WholeNumber(speedLimit) is { } kb ? kb * 1024L : null;
                if (limit is <= 0) return ("Speed limit must be a positive number of KB/s.", ConnectionPage);
                if (WholeNumber(updateInterval) is not { } checkInterval || checkInterval is < 1 or > 168)
                    return ("Update interval must be 1-168 hours.", UpdatesPage);

                var proxyModeValue = proxyMode.SelectedIndex switch { 1 => "system", 2 => "manual", _ => "none" };
                string? composedProxyAddress = null;
                if (proxyModeValue == "manual")
                {
                    composedProxyAddress = ProxyAddress.Compose(proxyProtocol.SelectedIndex, proxyAddress.Text);
                    if (composedProxyAddress is null)
                        return ("Enter the proxy address as host:port (port 1-65535).", ProxyPage);
                }

                var profiles = new List<ConnectionProfileDto>();
                foreach (var line in profilesBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    var separator = trimmed.LastIndexOf('=');
                    if (separator <= 0 ||
                        !int.TryParse(trimmed[(separator + 1)..].Trim(), out var profileLimit) ||
                        profileLimit < 1 || profileLimit > 32)
                        return ($"Per-site limit '{trimmed}' must use pattern=max (1-32).", ConnectionPage);
                    profiles.Add(new ConnectionProfileDto { Pattern = trimmed[..separator].Trim(), MaxConnections = profileLimit });
                }

                settings.Proxy = new ProxyDto
                {
                    Mode = proxyModeValue,
                    Address = composedProxyAddress,
                    Username = string.IsNullOrWhiteSpace(proxyUser.Text) ? null : proxyUser.Text.Trim(),
                    // Empty keeps the stored password (the host re-applies it for an unchanged address/user)
                    // unless "Forget" is checked.
                    Password = forgetProxyPassword.IsChecked == true || string.IsNullOrEmpty(proxyPassword.Password)
                        ? null
                        : proxyPassword.Password,
                    ClearPassword = forgetProxyPassword.IsChecked == true,
                    HasPassword = settings.Proxy.HasPassword && forgetProxyPassword.IsChecked != true,
                };
                settings.DefaultDownloadDirectory = directory.Text.Trim();
                settings.SiteLogins = loginRows.Select(row => new SiteLoginDto
                {
                    Id = row.Original.Id,
                    Site = row.Site.Text.Trim(),
                    Username = row.User.Text.Trim(),
                    Password = string.IsNullOrEmpty(row.Password.Password) ? null : row.Password.Password,
                    HasPassword = row.Original.HasPassword,
                }).ToList();
                settings.MaxConcurrentDownloads = max;
                settings.DefaultConnections = connectionsValue;
                settings.GlobalSpeedLimitBytesPerSecond = limit;
                settings.ConnectionProfiles = profiles;
                settings.LaunchAtStartup = launch.IsChecked == true;
                settings.ClipboardMonitoring = clipboard.IsChecked == true;
                settings.BrowserIntegrationEnabled = integration.IsChecked == true;
                settings.NotifyOnCompletion = notify.IsChecked == true;
                settings.PlayCompletionSound = sound.IsChecked == true;
                settings.ShowCompletionDialog = completionDialog.IsChecked == true;
                settings.AutoResumeUnfinishedOnStartup = autoResume.IsChecked == true;
                settings.PostDownloadCommand = string.IsNullOrWhiteSpace(postDownload.Text) ? null : postDownload.Text.Trim();
                settings.UpdateManifestUrl = string.IsNullOrWhiteSpace(updateUrl.Text) ? null : updateUrl.Text.Trim();
                settings.UpdatePublicKey = string.IsNullOrWhiteSpace(updateKey.Text) ? null : updateKey.Text.Trim();
                settings.UpdateChannel = string.IsNullOrWhiteSpace(updateChannel.Text) ? "stable" : updateChannel.Text.Trim();
                settings.AutoCheckUpdates = autoUpdate.IsChecked == true;
                settings.UpdateCheckIntervalHours = checkInterval;
                settings.CategoryFolders = categoryBoxes
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Value.Text))
                    .ToDictionary(pair => pair.Key, pair => pair.Value.Text.Trim(), StringComparer.OrdinalIgnoreCase);
                foreach (var editor in customCategoryEditors)
                {
                    editor.Category.Name = editor.Name.Text.Trim();
                    editor.Category.Extensions = editor.Extensions.Text.Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
                    editor.Category.DestinationDirectory = string.IsNullOrWhiteSpace(editor.Folder.Text) ? null : editor.Folder.Text.Trim();
                }
                settings.CustomCategories = settings.CustomCategories
                    .Where(category => !stagedCategoryRemovals.Contains(category.Id))
                    .ToList();
                settings.DeleteCategoryIds = stagedCategoryRemovals.ToList();

                var reply = await _connection.UpdateSettingsAsync(settings);
                var update = reply.GetPayload<SettingsResult>();
                if (!update.Ok)
                    return (update.Error ?? "The host rejected the settings.", -1);

                _settings = update.Settings;
                var extractionSaveProblem = extraction is null ? null : await extraction.SaveAsync();
                await RefreshAsync();
                return extractionSaveProblem is null ? (null, 0) : (extractionSaveProblem, extractionPageIndex);
            };

            window.Activate();
            await window.WaitClosedAsync();
        }
        catch (Exception ex)
        {
            window.Close();
            await ShowMessageAsync("Options", ex.Message);
        }
    }

    private async Task ShowQueueFilesAsync(int queueId)
    {
        var snapshot = await _connection.RefreshAsync();
        var queue = snapshot.Settings.Queues.FirstOrDefault(item => item.Id == queueId);
        if (queue is null) return;
        var jobs = new ObservableCollection<JobViewModel>(snapshot.Jobs.Where(job => job.IsQueued && job.QueueId == queueId)
            .OrderByDescending(job => job.Priority)
            .ThenBy(job => { var index = queue.JobOrder.IndexOf(job.Id); return index < 0 ? int.MaxValue : index; })
            .ThenBy(job => job.CreatedAt).Select(job => { var row = new JobViewModel(); row.Update(job); return row; }));
        var queues = snapshot.Settings.Queues.Where(item => item.Id != queueId).OrderBy(item => item.Order).ToList();
        var stagedMembership = new Dictionary<Guid, int?>();
        var list = new ListBox { ItemsSource = jobs, ItemTemplate = (DataTemplate)RootGrid.Resources["QueueFileTemplate"],
            SelectionMode = SelectionMode.Single, MinHeight = 120,
            MaxHeight = Math.Max(160, Math.Min(320, RootGrid.ActualHeight - 350)),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "Files in download order");
        var up = new Button { Content = "Move up", IsEnabled = false };
        var down = new Button { Content = "Move down", IsEnabled = false };
        var remove = new Button { Content = "Remove from queue", IsEnabled = false };
        var target = new ComboBox { PlaceholderText = "Move to queue", MinWidth = 150, IsEnabled = false };
        foreach (var item in queues) target.Items.Add(item.Name);
        var moveToQueue = new Button { Content = "Move to queue", IsEnabled = false };
        void UpdateButtons()
        {
            var index = list.SelectedIndex;
            up.IsEnabled = index > 0 && jobs[index].Priority == jobs[index - 1].Priority;
            down.IsEnabled = index >= 0 && index < jobs.Count - 1 && jobs[index].Priority == jobs[index + 1].Priority;
            remove.IsEnabled = list.SelectedItem is JobViewModel;
            target.IsEnabled = list.SelectedItem is JobViewModel && queues.Count > 0;
            moveToQueue.IsEnabled = list.SelectedItem is JobViewModel && queues.Count > 0 && target.SelectedIndex >= 0;
        }
        void Move(int direction)
        {
            var index = list.SelectedIndex;
            var destinationIndex = index + direction;
            if (index < 0 || destinationIndex < 0 || destinationIndex >= jobs.Count || jobs[index].Priority != jobs[destinationIndex].Priority) return;
            jobs.Move(index, destinationIndex);
            list.SelectedIndex = destinationIndex;
            UpdateButtons();
        }
        void StageMembershipChange(int? destinationQueueId)
        {
            if (list.SelectedItem is not JobViewModel selected) return;
            var index = list.SelectedIndex;
            stagedMembership[selected.Id] = destinationQueueId;
            jobs.RemoveAt(index);
            list.SelectedIndex = Math.Min(index, jobs.Count - 1);
            UpdateButtons();
        }
        list.SelectionChanged += (_, _) => UpdateButtons();
        target.SelectionChanged += (_, _) => UpdateButtons();
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);
        remove.Click += (_, _) => StageMembershipChange(null);
        moveToQueue.Click += (_, _) =>
        {
            if (target.SelectedIndex >= 0 && target.SelectedIndex < queues.Count)
                StageMembershipChange(queues[target.SelectedIndex].Id);
        };
        var orderButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        orderButtons.Children.Add(up);
        orderButtons.Children.Add(down);
        var membershipButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        membershipButtons.Children.Add(remove);
        membershipButtons.Children.Add(target);
        membershipButtons.Children.Add(moveToQueue);
        var panel = new StackPanel { Spacing = 12, MinWidth = 460 };
        panel.Children.Add(new TextBlock { Text = "Choose the order of waiting downloads within the same priority. Transfers already running continue. Status is shown as of opening this window.", TextWrapping = TextWrapping.Wrap });
        var headings = new Grid { ColumnSpacing = 12, Margin = new Thickness(12, 0, 12, 0) };
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), new GridLength(80), new GridLength(90), new GridLength(75) })
            headings.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var labels = new[] { "File name", "Size", "Status", "Time left" };
        for (var column = 0; column < labels.Length; column++)
        {
            var heading = new TextBlock { Text = labels[column], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            Grid.SetColumn(heading, column);
            headings.Children.Add(heading);
        }
        panel.Children.Add(headings);
        panel.Children.Add(list);
        panel.Children.Add(orderButtons);
        panel.Children.Add(membershipButtons);
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = $"Files in {queue.Name}",
            RequestedTheme = RootGrid.ActualTheme,
            Content = panel, PrimaryButtonText = "Save", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var failures = new List<string>();
        foreach (var change in stagedMembership)
        {
            IpcMessage response;
            try
            {
                response = change.Value is { } targetQueueId
                    ? await _connection.MoveToQueueAsync(change.Key, targetQueueId)
                    : await _connection.DetachFromQueueAsync(change.Key);
            }
            catch (Exception ex)
            {
                failures.Add($"{change.Key}: {ex.Message}");
                continue;
            }
            var action = response.GetPayload<JobActionResponse>();
            if (response.Type != MessageTypes.JobActionResult || !action.Ok)
                failures.Add($"{change.Key}: {action.Error ?? "The host rejected the change."}");
        }
        if (failures.Count > 0)
        {
            await ShowMessageAsync("Queue files", "Some queue changes could not be applied:\n" + string.Join("\n", failures));
            await RefreshAsync();
            return;
        }

        var refreshed = await _connection.RefreshAsync();
        if (!refreshed.Settings.Queues.Any(item => item.Id == queueId))
        {
            await ShowMessageAsync("Queue file order", "This queue was removed before its file order could be saved.");
            await RefreshAsync();
            return;
        }
        var reply = await _connection.QueueActionAsync(new QueueActionRequest
        {
            QueueId = queueId, Action = "reorderfiles", JobOrder = jobs.Select(job => job.Id).ToList(),
        });
        var result = reply.GetPayload<SettingsResult>();
        if (reply.Type != MessageTypes.SettingsResult || !result.Ok)
            await ShowMessageAsync("Queue file order", result.Error ?? "Could not save the file order.");
    }

    private static SettingsDto CloneSettings(SettingsDto source)
        => new()
        {
            DefaultDownloadDirectory = source.DefaultDownloadDirectory,
            MaxConcurrentDownloads = source.MaxConcurrentDownloads,
            DefaultConnections = source.DefaultConnections,
            MaxConnectionsPerHost = source.MaxConnectionsPerHost,
            GlobalSpeedLimitBytesPerSecond = source.GlobalSpeedLimitBytesPerSecond,
            Proxy = new ProxyDto
            {
                Mode = source.Proxy.Mode,
                Address = source.Proxy.Address,
                Username = source.Proxy.Username,
                Password = null,
                HasPassword = source.Proxy.HasPassword,
            },
            SiteLogins = source.SiteLogins.Select(login => new SiteLoginDto
            {
                Id = login.Id,
                Site = login.Site,
                Username = login.Username,
                Password = null,
                HasPassword = login.HasPassword,
            }).ToList(),
            DefaultQueueId = source.DefaultQueueId,
            Queues = source.Queues.Select(queue => new QueueDto
            {
                Id = queue.Id,
                Name = queue.Name,
                MaxConcurrentDownloads = queue.MaxConcurrentDownloads,
                IsRunning = queue.IsRunning,
                Order = queue.Order,
                JobOrder = queue.JobOrder.ToList(),
                RetryLimit = queue.RetryLimit,
                QuotaEnabled = queue.QuotaEnabled,
                QuotaBytes = queue.QuotaBytes,
                QuotaWindowHours = queue.QuotaWindowHours,
                QuotaWindowStart = queue.QuotaWindowStart,
                QuotaBytesUsed = queue.QuotaBytesUsed,
            }).ToList(),
            Schedules = source.Schedules.Select(schedule => new ScheduleDto
            {
                Id = schedule.Id,
                Name = schedule.Name,
                QueueId = schedule.QueueId,
                Enabled = schedule.Enabled,
                Kind = schedule.Kind,
                StartOnHostStartup = schedule.StartOnHostStartup, TimedStartEnabled = schedule.TimedStartEnabled,
                RepeatEveryMinutes = schedule.RepeatEveryMinutes, LastRunAt = schedule.LastRunAt, LastRunError = schedule.LastRunError,
                StartDate = schedule.StartDate,
                Days = schedule.Days,
                StartTime = schedule.StartTime,
                StopTime = schedule.StopTime,
                OpenFileWhenDone = schedule.OpenFileWhenDone,
                ExitWhenDone = schedule.ExitWhenDone,
                ShutdownWhenDone = schedule.ShutdownWhenDone,
                ShutdownMode = schedule.ShutdownMode,
                ForceShutdown = schedule.ForceShutdown,
                LastStartDate = schedule.LastStartDate,
                LastStopDate = schedule.LastStopDate,
            }).ToList(),
            LaunchAtStartup = source.LaunchAtStartup,
            ClipboardMonitoring = source.ClipboardMonitoring,
            BrowserIntegrationEnabled = source.BrowserIntegrationEnabled,
            NotifyOnCompletion = source.NotifyOnCompletion,
            PlayCompletionSound = source.PlayCompletionSound,
            EventSoundFiles = new Dictionary<string, string>(source.EventSoundFiles, StringComparer.OrdinalIgnoreCase),
            ShowCompletionDialog = source.ShowCompletionDialog,
            AutoResumeUnfinishedOnStartup = source.AutoResumeUnfinishedOnStartup,
            AllowLocalNetwork = source.AllowLocalNetwork,
            PreventSleepWhileDownloading = source.PreventSleepWhileDownloading,
            AllowSystemCompletionActions = source.AllowSystemCompletionActions,
            FfmpegPath = source.FfmpegPath,
            Theme = source.Theme,
            LastDestinationDirectory = source.LastDestinationDirectory,
            RecentUrls = new List<string>(source.RecentUrls),
            LastUpdateCheck = source.LastUpdateCheck,
            PostDownloadCommand = source.PostDownloadCommand,
            UpdateManifestUrl = source.UpdateManifestUrl,
            UpdatePublicKey = source.UpdatePublicKey,
            UpdateChannel = source.UpdateChannel,
            AutoCheckUpdates = source.AutoCheckUpdates,
            UpdateCheckIntervalHours = source.UpdateCheckIntervalHours,
            SkippedUpdateVersion = source.SkippedUpdateVersion,
            ConnectionProfiles = source.ConnectionProfiles.Select(profile => new ConnectionProfileDto
            {
                Pattern = profile.Pattern,
                MaxConnections = profile.MaxConnections,
            }).ToList(),
            CustomCategories = source.CustomCategories.Select(category => new CategoryDefinitionDto { Id = category.Id, Name = category.Name,
                DestinationDirectory = category.DestinationDirectory, Extensions = new(category.Extensions) }).ToList(),
            CategoryFolders = new Dictionary<string, string>(source.CategoryFolders, StringComparer.OrdinalIgnoreCase),
            ExcludedFileExtensions = new List<string>(source.ExcludedFileExtensions),
            ExcludedHosts = new List<string>(source.ExcludedHosts),
            InterceptedFileExtensions = new List<string>(source.InterceptedFileExtensions),
        };

    private async void StartQueueButton_Click(object sender, RoutedEventArgs e)
    {
        await RunQueueActionAsync("start");
    }

    private async void StopQueueButton_Click(object sender, RoutedEventArgs e)
    {
        await RunQueueActionAsync("stop");
    }

    private async Task RunQueueActionAsync(string action)
    {
        if (_settings is null)
        {
            return;
        }

        var queueId = SelectedJob?.QueueId ?? SelectedPackage?.SharedQueueId ?? _settings.DefaultQueueId;
        try
        {
            var reply = await _connection.QueueActionAsync(new QueueActionRequest
            {
                QueueId = queueId,
                Action = action,
            });
            var result = reply.GetPayload<SettingsResult>();
            if (!result.Ok)
            {
                await ShowMessageAsync("Queue action failed", result.Error ?? "The host rejected the queue action.");
                return;
            }

            _settings = result.Settings;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Queue action failed", ex.Message);
        }
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOnSelectionAsync("Stop", new GroupActionRequest { Action = "pause" },
            job => job.CanPause, id => _connection.PauseAsync(id));
    }

    private async void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOnSelectionAsync("Resume", new GroupActionRequest { Action = "resume" },
            job => job.CanResume, id => _connection.ResumeAsync(id));
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var selection = SelectedJobs;
        if (selection.Count == 0)
        {
            if (FirstLocalRow is null) await RemoveSideRowsAsync();
            return;
        }

        var deleteFiles = new CheckBox
        {
            Content = "Also delete the downloaded files and any partial data from disk",
            IsChecked = true,
        };
        // Unfinished downloads only have partial data, which is removed anyway.
        var anyCompleted = selection.Any(job => job.State == "completed");
        var question = selection.Count == 1
            ? $"Remove '{selection[0].FileName}' from the list?"
            : $"Remove {selection.Count} downloads from the list?";
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = anyCompleted ? question : question + " Partial data is always removed.",
            TextWrapping = TextWrapping.Wrap,
        });
        if (anyCompleted) panel.Children.Add(deleteFiles);
        else deleteFiles.IsChecked = true;

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = selection.Count == 1 ? "Delete download?" : $"Delete {selection.Count} downloads?",
            Content = panel,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var includeFiles = deleteFiles.IsChecked == true;
        if (selection.Count > 1)
        {
            // One Host operation: every file stops together and none restarts while others are removed.
            StatusText.Text = $"Deleting {selection.Count} downloads…";
            try
            {
                var result = (await _connection.RemoveManyAsync(selection.Select(job => job.Id).ToList(), includeFiles))
                    .GetPayload<JobActionResponse>();
                await RefreshAsync();
                if (!result.Ok) await ShowMessageAsync("Delete incomplete", result.Error ?? "Some downloads could not be removed.");
            }
            catch (Exception ex) { await RefreshAsync(); await ShowMessageAsync("Delete incomplete", ex.Message); }
            return;
        }
        await RunBulkActionAsync(
            selection,
            id => _connection.RemoveAsync(id, deleteFile: includeFiles),
            "Delete");
    }

    private async Task RunBulkActionAsync(
        IReadOnlyList<JobViewModel> jobs,
        Func<Guid, Task<IpcMessage>> action,
        string label)
    {
        var failures = new List<string>();
        foreach (var job in jobs)
        {
            try
            {
                var reply = await action(job.Id);
                if (reply.Type == MessageTypes.JobActionResult)
                {
                    var result = reply.GetPayload<JobActionResponse>();
                    if (!result.Ok)
                    {
                        failures.Add($"{job.FileName}: {result.Error}");
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{job.FileName}: {ex.Message}");
            }
        }

        await RefreshAsync();
        if (failures.Count > 0)
        {
            await ShowMessageAsync($"{label} incomplete",
                string.Join(Environment.NewLine, failures.Take(5)));
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var snapshot = await _connection.RefreshAsync();
            ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Refresh failed: {ex.Message}";
        }
    }

    private async Task RunActionAsync(Func<Task<IpcMessage>> action)
    {
        try
        {
            var reply = await action();
            if (reply.Type == MessageTypes.JobActionResult)
            {
                var result = reply.GetPayload<JobActionResponse>();
                if (!result.Ok)
                {
                    await ShowMessageAsync("Action failed", result.Error ?? "The host rejected the action.");
                }
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Action failed", ex.Message);
        }
    }

    private async Task OpenJobAsync(JobViewModel job)
    {
        if (!string.IsNullOrWhiteSpace(job.FinalFilePath) && File.Exists(job.FinalFilePath))
        {
            try
            {
                Process.Start(ShellActions.OpenFile(job.FinalFilePath));
                return;
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("Could not open file", ex.Message);
                return;
            }
        }

        OpenContainingFolder(job);
    }

    private void OpenContainingFolder(JobViewModel job)
    {
        try
        {
            var path = !string.IsNullOrWhiteSpace(job.FinalFilePath)
                ? job.FinalFilePath
                : job.Destination;
            if (!string.IsNullOrWhiteSpace(path))
            {
                Process.Start(ShellActions.RevealFile(path));
            }
        }
        catch (Exception ex)
        {
            _ = ShowMessageAsync("Could not open folder", ex.Message);
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            ShowPassingStatus($"{title}: {message}");
        }
    }
}
