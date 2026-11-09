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
    private ObservableCollection<JobViewModel> _visibleJobs = new();
    private readonly Dictionary<Guid, JobViewModel> _jobsById = new();
    private HostConnection _connection;
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
    private string _search = string.Empty;

    /// <summary>U-WL-7: how small the window can be dragged: the whole toolbar fits, and the list scrolls sideways for its columns. Cancel Package, when it shows, can scroll.</summary>
    private const int MinimumWindowWidth = 1000;
    private const int MinimumWindowHeight = 560;
    private readonly PassingLine _passing;
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
        // The progress, package and completion windows open on this window's screen (U-WL-4).
        WindowCascade.MarkMain(this);
        // This PC's Host, always: its settings drive the theme and the window's own choices, and its updates are the
        // ones shown. The list shows _connection, which is this one unless another server is chosen (MainWindow.Servers).
        _local = new HostConnection();
        _connection = _local;

        // Its one timer is kept with it for the window's life. See PassingLine for why that matters.
        _passing = new PassingLine(tick => new DispatcherOneShot(DispatcherQueue, PassingLine.Time, tick));
        _passing.Ended += UpdateStatusText;
        FolderChooser.Measure = paths => _connection.GetFolderSpaceAsync(paths);
        FolderChooser.Settings = () => _settings;
        FolderChooser.SaveFavorites = SaveFavoritesAsync;
        // UI.R23: the settings' Theme is the source; until they arrive, the theme last seen.
        AppTheme.Track(RootGrid);
        AppTheme.Changed += () => _uiState.Theme = AppTheme.Choice;
        SpeedGraph.Visibility = _uiState.ShowSpeedGraph ? Visibility.Visible : Visibility.Collapsed;
        ShowSpeedGraphMenuItem.IsChecked = _uiState.ShowSpeedGraph;
        SpeedGraph.IsExpanded = _uiState.SpeedGraphExpanded;
        SpeedGraph.ExpandedChanged += (_, _) => SaveUiState();
        _speedHistoryTimer.Tick += (_, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            // A gap means "no data" (Host disconnected). A download that has gone quiet (stalled,
            // connecting, waiting to retry) is transferring nothing, so it counts as 0, not unknown.
            // UI.B20: Progress Details samples its one download by the same rule.
            var rate = SpeedSample.Total(_graphConnected && _graphSnapshotReady,
                _jobsById.Values.Select(job => SpeedSample.JobRate(job.State, job.BytesPerSecond, job.LastUpdated, now)));
            SpeedGraph.IsSeen = AppWindow.IsVisible
                && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized }
                && SpeedGraph.Visibility == Visibility.Visible;
            SpeedGraph.AddSample(now, rate);
        };
        _speedHistoryTimer.Start();
        InitializeLimitMenus();
        WindowLayout.UseBackdrop(this);
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            DarkModeMenuItem.IsChecked = RootGrid.ActualTheme == ElementTheme.Dark;
            ApplyTitleBarTheme(DarkModeMenuItem.IsChecked);
            StatusBrushes.UseTheme(RootGrid.ActualTheme);
        };
        DarkModeMenuItem.IsChecked = RootGrid.ActualTheme == ElementTheme.Dark;
        StatusBrushes.UseTheme(RootGrid.ActualTheme);
        JobsList.ItemsSource = _visibleJobs;
        BuildJobsContextMenu();
        MenuBarHover.Attach(MainMenuBar);
#if HAS_UNO
        // LightDismissOverlayMode="Off" leaves the list undimmed on Windows; Uno ignores it and dims. Its dismiss layer
        // stays (a click beside the drawer still closes it), only unseen.
        ProviderSplit.PaneOpening += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() => ClearDismissLayer(ProviderSplit));
            // On a reopen Uno draws the pane in place for a frame before its slide starts: hidden for two frames.
            // The pane's contents are laid out at the drawer's width only after it opens (a first open builds them, and
            // from the full view they reflow): hidden until then (a few frames at most), then faded in as it slides.
            // On the first open the template's pane is built later than this, so it is looked for on each frame too.
            ProviderSplit.ApplyTemplate();
            var pane = FindNamed(ProviderSplit, "PaneRoot") as UIElement;
            ProviderDrawerFrame.Opacity = 0;
            if (pane is not null) pane.Opacity = 0;
            var frames = 0;
            long? fadeStart = null;
            void Reveal(object? sender, object e)
            {
                frames++;
                if (pane is null && FindNamed(ProviderSplit, "PaneRoot") is UIElement late)
                {
                    pane = late;
                    pane.Opacity = 0;
                }

                if (fadeStart is null)
                {
                    var laidOut = ProviderDrawerHost.Children.FirstOrDefault() is FrameworkElement content &&
                                  content.ActualWidth > 0 && Math.Abs(content.ActualWidth - ProviderDrawerHost.ActualWidth) < 2;
                    if (frames < 3 || (!laidOut && frames < 20)) return;
                    fadeStart = Environment.TickCount64;
                }

                var progress = Math.Min(1, (Environment.TickCount64 - fadeStart.Value) / 150d);
                if (pane is not null) pane.Opacity = progress;
                ProviderDrawerFrame.Opacity = progress;
                if (progress >= 1) Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= Reveal;
            }
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += Reveal;
        };
#endif
        UiTrace.Write("MainWindow collections initialized");

        try
        {
            // U-WL-7: set before the remembered size comes back, as SetMinimumSize asks.
            WindowLayout.SetMinimumSize(this, MinimumWindowWidth, MinimumWindowHeight);
            var width = Math.Clamp(_uiState.WindowWidth, MinimumWindowWidth, 8000);
            var height = Math.Clamp(_uiState.WindowHeight, MinimumWindowHeight, 8000);

            // A size saved at close is real pixels. The first one isn't, or a scaled screen would get a tiny window.
            if (width == UiState.FirstWindowWidth && height == UiState.FirstWindowHeight) WindowLayout.ResizeForDpi(this, width, height);
            else AppWindow.Resize(new SizeInt32 { Width = width, Height = height });
            WindowPlacement.Track(this, "main");
            var icon = WindowLayout.AppIconPath;
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(WindowLayout.IconArgument(icon));
            }

            ApplyTitleBarTheme(DarkModeMenuItem.IsChecked);
        }
        catch (Exception)
        {
            // Window chrome is cosmetic; startup must not depend on it.
        }

        UiTrace.Write("MainWindow chrome initialized");
        AppWindow.Closing += OnWindowClosing;

        WireConnection(_local);
        _local.Start();
        UiTrace.Write("MainWindow connection started");

        InitializeListHeader();

        // On connections of its own: nothing it waits for can hold up the one above.
        _remote = new RemoteClient(action => DispatcherQueue.TryEnqueue(() => action()));
        InitializeRemoteUi();
        InitializeArchiveUi();
        _remote.Start();
        InitializeServers();

        // Otherwise WinUI floats a shortcut's name over the window whenever the pointer rests on the root.
        RootGrid.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
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
            () => ApplySort("size", ascending: false)));
        // UI.R26: Ctrl+Shift+O opens the server switcher; Ctrl+0 shows this PC and Ctrl+1 to Ctrl+9 the saved servers.
        RootGrid.KeyboardAccelerators.Add(CreateAccelerator(
            Windows.System.VirtualKey.O,
            Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
            OpenServerMenuFromKeyboard));
        InitializeProviderViews();
        foreach (var accelerator in ProviderAccelerators()) RootGrid.KeyboardAccelerators.Add(accelerator);
        for (var number = 0; number <= ServerSwitch.MostNumbered; number++)
        {
            var chosen = number;
            RootGrid.KeyboardAccelerators.Add(CreateAccelerator(
                Windows.System.VirtualKey.Number0 + number,
                Windows.System.VirtualKeyModifiers.Control,
                () => SwitchToNumber(chosen)));
        }

        _sortKey = ListColumnSets.IsDownloadSort(_uiState.SortKey) ? _uiState.SortKey : "added";
        _sortAscending = _uiState.SortAscending;
        _jobsHeader.ShowSort(_sortKey, _sortAscending);
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    public void ShowFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        if (OperatingSystem.IsWindows()) SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        SpeedGraph.IsSeen = SpeedGraph.Visibility == Visibility.Visible;
    }

#if HAS_UNO
    private static DependencyObject? FindNamed(DependencyObject parent, string name)
    {
        for (var at = 0; at < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); at++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, at);
            if (child is FrameworkElement element && element.Name == name) return child;
            if (FindNamed(child, name) is { } found) return found;
        }

        return null;
    }

    private static void ClearDismissLayer(DependencyObject parent)
    {
        for (var at = 0; at < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); at++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, at);
            if (child is FrameworkElement { Name: { } name } element && name.Contains("Dismiss", StringComparison.OrdinalIgnoreCase))
            {
                element.Opacity = 0;
            }

            ClearDismissLayer(child);
        }
    }
#endif

    public void RequestSettingsPage()
    {
        ShowFromTray();
        _pendingSettingsOpen = true;
        if (_settings is not null)
        {
            _pendingSettingsOpen = false;
            _ = OpenOptionsAsync();
        }
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        UiTrace.Write($"MainWindow closing completely={LocalSettings?.CloseWindowCompletely == true}");
        // Closing the window hides it, and by default (PERF.5) also ends this process. Downloads and the browser
        // bridge stay alive in the background host; Exit is an explicit tray/menu action.
        SaveUiState(sender);
        args.Cancel = true;
        // PERF.5 option: end the window's process, so the GPU driver's threads and the window's memory go with it.
        // The Host keeps downloading; the tray, a second launch or a download window starts it again.
        if (LocalSettings?.CloseWindowCompletely == true)
        {
            sender.Hide();
            Application.Current.Exit();
#if HAS_UNO
            // Uno's X11 head leaves the process running after Exit.
            Environment.Exit(0);
#endif
            return;
        }
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
            // AppTheme writes the start-up hint itself; this keeps a save from here putting an older one back.
            _uiState.Theme = AppTheme.Choice;
            UiStateStore.Save(_uiState);
        }
        catch (Exception ex)
        {
            UiTrace.Write($"UI state save failed: {ex.Message}");
        }
    }

    private void ApplySnapshot(SnapshotDto snapshot)
    {
        if (!_graphSnapshotReady) _ = LoadSpeedHistoryAsync();
        _graphSnapshotReady = true;
        _settings = snapshot.Settings;
        // A server's path style, for every folder check and hint while it is shown.
        if (_connection.IsRemote) ShownServer.Describe(_connection.Hello?.HostKind, snapshot.Settings.DefaultDownloadDirectory);
        if (!_connection.IsRemote)
        {
            _localSettings = snapshot.Settings;
            _localStatus = snapshot.Status;
            FollowThemeSetting(snapshot.Settings);
        }
        UpdateLimitButtons();
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
            if (_connection.IsRemote) _remoteCompletions.Saw(job.Id, job.State);
        }

        RebuildVisible(selectedId);
        UpdateStatusText();
        TraceFirstList(snapshot.Jobs.Count);

        if (_pendingSettingsOpen)
        {
            _pendingSettingsOpen = false;
            _ = OpenOptionsAsync();
        }
    }

    private bool _firstListTraced;

    /// <summary>
    /// PERF.5: the cold-open time is measured to the first frame drawn with the list in it, so a start is timed the
    /// same way however many jobs there are. Two lines in ui-trace.log, once per process.
    /// </summary>
    private void TraceFirstList(int jobs)
    {
        if (_firstListTraced) return;
        _firstListTraced = true;
        UiTrace.Write($"First snapshot applied: {jobs} jobs, {_visibleJobs.Count} rows");
        void OnRendering(object? sender, object e)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            UiTrace.Write("First frame with the list");
        }
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
    }

    private void ApplyJobEvent(JobDto job, string changeType)
    {
        if (_connection.IsRemote && changeType != "completed") _remoteCompletions.Saw(job.Id, job.State);
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

        // Preserve notifications for headless jobs when the main UI is already open. Only a single CLI add: a CLI
        // batch or import ("cliBatch", "cliImport") opens no completion window per job (UI.B14).
        // Another server's completions are shown from these events, while this window is open (design D9, section 5):
        // whether to is this PC's setting alone, and only for a download seen unfinished here, a few a minute at most
        // (RemoteCompletionGate). This PC's Host opens its own.
        // A package opens one window, when its last file is done, as the Host does for the other sources; finishes
        // close together are gathered first (CompletionBurst), so one package never opens a window per file.
        // A package's last file can fail rather than finish: its package still gets its window, with the counts (U-WR-16).
        // A lone failure opens none, and nor does a cancel, which the user chose.
        var finishes = changeType == "completed" || (changeType == "failed" && PackageOf(job.Id) is not null);
        if (finishes && RemoteDataRules.WindowOpensCompletion(_connection.IsRemote, job.Source, job.ShowCompletionDialog,
                (_connection.IsRemote ? LocalSettings : _settings)?.ShowCompletionDialog == true) &&
            (!_connection.IsRemote || _remoteCompletions.Finished(job.Id)))
        {
            QueueCompletionWindow(job.Id);
        }

        if (changeType == "completed") NoteRemoteCompletion(job);
    }

    private void ApplyConnection(bool connected, string? error)
    {
        _graphConnected = connected;
        _graphSnapshotReady = false;
        var target = _connection.Target;
        ConnectionText.Text = target.IsLocal
            ? connected ? "Host connected" : $"Host unavailable{(error is null ? string.Empty : $": {error}")}"
            : connected ? $"Connected to {target.Name}" : error ?? $"Disconnected from {target.Name}";
        NoteShownConnection(connected, error);
        UpdateStatusText();

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
        CancelPackageGroup.Visibility = rows.Any(row => row.IsPackage) ? Visibility.Visible : Visibility.Collapsed;
        CancelPackageButton.IsEnabled = connected && rows.Any(row => row.CanCancelPackage);
        var sideRows = rows.Count(row => row.IsSideRow);
        var removableSideRows = FirstLocalRow is null && sideRows > 0 ? SelectedRemoteJobs.Count + CurrentArchivePlan().Remove.Count : 0;
        DeleteButton.IsEnabled = connected && (selection.Count > 0 || removableSideRows > 0);
        var deleteToolTip = FirstLocalRow is null && sideRows > 0 ? "Remove the selected rows from the list" : "Delete the selected download and its partial data";
        if (deleteToolTip != _deleteToolTip) ToolTipService.SetToolTip(DeleteButton, _deleteToolTip = deleteToolTip);
        SyncFileMenu(sideRows, removableSideRows);
        StopAllButton.IsEnabled = connected && _jobsById.Values.Any(j => j.CanPause);
        DeleteCompletedButton.IsEnabled = connected && _jobsById.Values.Any(j => j.State == "completed");
        SyncQueueCommands();
        SyncArchiveMenus();
    }

    private string _deleteToolTip = string.Empty;

    /// <summary>A command with nothing to act on is dimmed and says why in its own words, like Extract.</summary>
    private void SyncFileMenu(int sideRows, int removableSideRows)
    {
        var plan = FileMenuRules.Plan(SelectedJob is not null, SelectedPackage is not null, sideRows, removableSideRows, SelectedJob?.FileRemovedAfterExtraction == true, _connection.IsRemote,
            SelectedJob?.State == "completed");
        Apply(FileOpenItem, "Open", plan.LaunchReason);
        Apply(FileOpenFolderItem, "Open Folder", OpenFolderReason(plan));
        Apply(FileMoveRenameItem, "Move/Rename…", plan.OpenReason);
        Apply(FileProgressItem, "Progress Details…", plan.LocalReason);
        var rows = JobsList.SelectedItems.OfType<JobViewModel>().ToList();
        Apply(FileDownloadNowItem, "Download Now", plan.LocalReason, rows.Any(row => row.CanResume));
        Apply(FileStopItem, "Stop Download", plan.LocalReason, rows.Any(row => row.CanPause));
        Apply(FileRedownloadItem, "Redownload", plan.FileReason);
        Apply(FileDeleteItem, "Delete…", plan.RemoveReason);

        static void Apply(MenuFlyoutItem item, string text, string? reason, bool canAct = true)
        {
            var label = FileMenuRules.Label(text, reason);
            if (item.Text != label) item.Text = label;
            var on = reason is null && canAct;
            if (item.IsEnabled != on) item.IsEnabled = on;
        }
    }

    /// <summary>A message about something just done. The usual counts come back by themselves.</summary>
    private void ShowPassingStatus(string words)
    {
        _passing.Show(words);
        StatusText.Text = words;
        StatusAnnouncer.Text = words;

        // A set name replaces the text for a screen reader, so the words have to be the name.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StatusAnnouncer, words);
        LiveRegion.Announce(StatusAnnouncer);
    }

    /// <summary>Puts the counts back now, rather than when a passing line's time is up.</summary>
    private void EndPassingStatus()
    {
        _passing.End();
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        var active = _jobsById.Values.Count(j => j.State is "connecting" or "downloading" or "verifying");
        var retrying = _jobsById.Values.Count(j => j.State == "retryWaiting");
        var queued = _jobsById.Values.Count(j => j.State == "queued");
        var host = _status?.HostVersion;
        StatusText.Text = $"{_jobsById.Count} download{(_jobsById.Count == 1 ? string.Empty : "s")} · {active} active · {queued} queued" +
                          (retrying > 0 ? $" · {retrying} retrying" : string.Empty);
        // A server that can't be reached has no counts to give; "0 downloads" would say it has none (U-WR-1). One still
        // connecting says so, rather than "Not connected".
        if (_connection.IsRemote && !_banner.Connected)
            StatusText.Text = _banner.Status ?? $"Not connected to {_connection.Target.Name}";
        if (_graphConnected)
        {
            ConnectionText.Text = _connection.IsRemote
                ? $"Connected to {_connection.Target.Name}" + (host is null ? string.Empty : $" · {host}")
                : "Host connected" + (host is null ? string.Empty : $" · {host}")
                    + (ShellWords.BrowserIntegration(_status?.BrowserIntegration) is { } integration ? $" · {integration}" : string.Empty);
        }
        var repair = !_connection.IsRemote && _graphConnected && ShellWords.IntegrationNeedsRepair(_status?.BrowserIntegration);
        RepairBrowserButton.Visibility = repair ? Visibility.Visible : Visibility.Collapsed;
        if (ShellWords.FindCount(_search, _visibleJobs.Count(row => row.PackageId is null)) is { } shown) StatusText.Text += $" · {shown}";
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

        if (_passing.Words is { } passing) StatusText.Text = passing;

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

    /// <param name="sorting">The rows are being put in a new order, and the caller then shows where the list should be.</param>
    private void RebuildVisible(Guid? selectedId = null, bool sorting = false)
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

        var ordered = Sorted(top);

        // On top and in their own order: a sort is about local files, and these have no size or rate.
        target.AddRange(RemoteRowsInView());
        target.AddRange(ArchiveRowsInView());

        foreach (var row in ordered)
        {
            target.Add(row);
            if (row.IsPackage && row.IsExpanded)
                foreach (var child in _sortKey == "added" ? row.Children.Where(Matches) : Sorted(row.Children.Where(Matches))) target.Add(child);
        }

        // Sync in place instead of Clear + Add: rows that stay keep their containers, so
        // progress bars do not replay from another row's value and nothing jumps.
        // A sort can put most of a long list out of place, and one move at a time would crawl.
        if (ListSort.ReplacesWholeList(sorting, _visibleJobs, target))
        {
            _visibleJobs = new ObservableCollection<JobViewModel>(target);
            JobsList.ItemsSource = _visibleJobs;
        }

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
        UpdateFindBar();
        UpdateButtons();
    }

    /// <summary>U-WL-8: a filtered list says so, with how many rows it shows, until the filter is cleared.</summary>
    private void UpdateFindBar()
    {
        var words = ShellWords.FindFilter(_search, _visibleJobs.Count(row => row.PackageId is null));
        FindBar.Visibility = words is null ? Visibility.Collapsed : Visibility.Visible;
        if (words is not null && FindBarText.Text != words) FindBarText.Text = words;
    }

    private void FindBarClear_Click(object sender, RoutedEventArgs e)
    {
        _search = string.Empty;
        RebuildVisible();
        UpdateStatusText();
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
        return default;
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
        // One box can change many rows. It says so itself as it goes, and when it ends.
        if (_regionKeep is not null) return;
        UpdateButtons();
        UpdateStatusText();
    }

    private void JobsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && SelectedJobs.Count > 0)
        {
            e.Handled = true;
            // Shift+Delete asks with "Delete from disk" ticked; plain Delete leaves it unticked (UI.R20).
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            _ = ConfirmDeleteAsync(deleteFromDisk: shift);
        }
    }

#if HAS_UNO
    private bool _fixingCategoryHighlight;
#endif

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
#if HAS_UNO
        // Uno's ListBox leaves the earlier item looking selected: clear every other item's own flag.
        if (_fixingCategoryHighlight) return;
        if (CategoryList.SelectedItem is ListBoxItem chosen)
        {
            _fixingCategoryHighlight = true;
            try
            {
                foreach (var other in CategoryList.Items.OfType<ListBoxItem>())
                {
                    if (!ReferenceEquals(other, chosen) && other.IsSelected) other.IsSelected = false;
                }

                if (!chosen.IsSelected) chosen.IsSelected = true;
                if (!ReferenceEquals(CategoryList.SelectedItem, chosen)) CategoryList.SelectedItem = chosen;
            }
            finally
            {
                _fixingCategoryHighlight = false;
            }
        }
#endif
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
                // UI.R32, A: My provider takes the list's place; the category shown before stays the one to go back to.
                OpenProviderWindow(tag[RemoteBrowseTag.Length..]);
                return;
            }

            LeaveProviderFull(selectCategory: false);
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
        flyout.Items.Add(_menuOpen = CreateContextItem("Open", MenuOpen_Click));
        flyout.Items.Add(_menuOpenWith = CreateContextItem("Open With…", MenuOpenWith_Click));
        flyout.Items.Add(_menuOpenFolder = CreateContextItem("Open Folder", MenuOpenFolder_Click));
        flyout.Items.Add(_menuShowDownload = CreateContextItem("Show Download", (_, _) => ShowDownloadsOfSelectedRecords()));
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
            // A removed archive gets its reason from there, in the status bar.
            if (SelectedJob is { State: "completed" }) MenuMoveRename_Click(moveRename, new RoutedEventArgs());
        };
        JobsList.KeyboardAccelerators.Add(moveShortcut);
        flyout.Items.Add(moveRename);
        _menuMoveRename = moveRename;
        flyout.Items.Add(_separatorAfterOpen = new MenuFlyoutSeparator());
        flyout.Items.Add(_menuResume = CreateContextItem("Resume", MenuResume_Click));
        flyout.Items.Add(_menuStop = CreateContextItem("Stop", MenuPause_Click));
        flyout.Items.Add(_menuCancelPackage = CreateContextItem("Cancel Package…", MenuCancelPackage_Click));
        flyout.Items.Add(_menuRedownload = CreateContextItem("Redownload", MenuRedownload_Click));
        flyout.Items.Add(_menuRefreshUrl = CreateContextItem("Refresh Download Address…", MenuRefreshUrl_Click));
        flyout.Items.Add(new MenuFlyoutSeparator());
        _addToQueueMenu = new MenuFlyoutSubItem { Text = "Add to Queue" };
        flyout.Items.Add(_addToQueueMenu);
        flyout.Items.Add(_menuUnqueue = CreateContextItem("Remove from Queue", MenuDeleteFromQueue_Click));
        _priorityMenu = new MenuFlyoutSubItem { Text = "Priority" };
        foreach (var priority in new[] { JobPriorities.High, JobPriorities.Normal, JobPriorities.Low })
        {
            var item = new ToggleMenuFlyoutItem { Text = JobPriorities.Name(priority), Tag = priority };
            item.Click += MenuPriority_Click;
            _priorityMenu.Items.Add(item);
        }
        flyout.Items.Add(_priorityMenu);
        flyout.Items.Add(new MenuFlyoutSeparator());
        // One way to take rows off the list: the Delete dialog, as the toolbar and the Delete key do (owner, 2026-11-08:
        // "just delete? seems remove seems redundant").
        flyout.Items.Add(_menuDelete = CreateContextItem("Delete…", MenuContextDelete_Click));
        _menuDelete.KeyboardAcceleratorTextOverride = "Delete";
        var separatorAfterRemove = new MenuFlyoutSeparator();
        flyout.Items.Add(separatorAfterRemove);
        var progressDetails = CreateContextItem("Progress Details…", MenuProgressDetails_Click);
        flyout.Items.Add(progressDetails);
        flyout.Items.Add(_menuGroup = CreateContextItem("Group into Package…", MenuGroupSelected_Click));
        flyout.Items.Add(_menuUngroup = CreateContextItem("Remove from Package", MenuUngroup_Click));
        flyout.Items.Add(_menuRenamePackage = CreateContextItem("Rename Package…", MenuRenamePackage_Click));
        flyout.Items.Add(CreateContextItem("Change Category…", MenuChangeCategory_Click));
        flyout.Items.Add(_menuProperties = CreateContextItem("Properties", MenuProperties_Click));
        _recordFileItems.AddRange([_menuOpen, _menuOpenWith, _menuOpenFolder, _separatorAfterOpen, _menuDelete, separatorAfterRemove, progressDetails, _menuProperties]);
        AddRemoteContextItems(flyout);
        AddShowInProviderItem(flyout);
        JobsList.ContextFlyout = flyout;
    }

    private static MenuFlyoutItem CreateContextItem(string text, RoutedEventHandler handler)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += handler;
        return item;
    }

    private MenuFlyoutItem? _menuOpen;
    private MenuFlyoutItem? _menuOpenWith;
    private MenuFlyoutItem? _menuOpenFolder;
    private MenuFlyoutItem? _menuShowDownload;
    private MenuFlyoutItem? _menuDelete;
    private MenuFlyoutSeparator? _separatorAfterOpen;

    /// <summary>What a record's own row keeps for the one download behind it.</summary>
    private readonly List<MenuFlyoutItemBase> _recordFileItems = [];
    private MenuFlyoutItem? _menuMoveRename;
    private MenuFlyoutItem? _menuGroup;
    private MenuFlyoutItem? _menuUngroup;
    private MenuFlyoutItem? _menuRenamePackage;
    private MenuFlyoutItem? _menuCancelPackage;
    private MenuFlyoutItem? _menuRedownload;
    private MenuFlyoutItem? _menuRefreshUrl;
    private MenuFlyoutItem? _menuResume;
    private MenuFlyoutItem? _menuStop;
    private MenuFlyoutItem? _menuUnqueue;
    private MenuFlyoutItem? _menuProperties;
    private MenuFlyoutSubItem? _priorityMenu;

    private void JobsContextMenu_Opening(object? sender, object e)
    {
        // Offer only what applies to the current selection.
        var selection = SelectedJobs;
        ShowContextItemsForSelection();

        // The same words as the File menu: a package row has no one file for these to act on.
        var rows = JobsList.SelectedItems.OfType<JobViewModel>().ToList();
        var plan = FileMenuRules.Plan(CommandJob is not null, SelectedPackage is not null, rows.Count(row => row.IsSideRow), 0, CommandJob?.FileRemovedAfterExtraction == true, _connection.IsRemote,
            CommandJob?.State == "completed");
        SayIfNoFile(_menuOpen, "Open", plan.LaunchReason);
        SayIfNoFile(_menuOpenWith, "Open With…", plan.LaunchReason);
        SayIfNoFile(_menuOpenFolder, "Open Folder", OpenFolderReason(plan));
        SayIfNoFile(_menuMoveRename, "Move/Rename…", plan.OpenReason);
        if (_menuMoveRename is not null) _menuMoveRename.IsEnabled = SelectedJob?.State == "completed" && plan.OpenReason is null;
        SayIfNoFile(_menuRedownload, "Redownload", plan.FileReason);
        SayIfNoFile(_menuRefreshUrl, "Refresh Download Address…", plan.FileReason);
        SayIfNoFile(_menuProperties, "Properties", plan.FileReason);
        if (_menuResume is not null) _menuResume.IsEnabled = rows.Any(row => row.CanResume);
        if (_menuStop is not null) _menuStop.IsEnabled = rows.Any(row => row.CanPause);
        if (_menuUnqueue is not null) _menuUnqueue.IsEnabled = selection.Any(job => !string.IsNullOrEmpty(job.QueueText));
        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        if (_menuGroup is not null) _menuGroup.Visibility = Show(FileMenuRules.CanGroup(selection.Count, rows.Count(row => !row.IsSideRow)));
        // U-WL-14: a package row isn't in a package; only its files can be taken out of it.
        if (_menuUngroup is not null) _menuUngroup.Visibility = Show(rows.Any(row => !row.IsPackage && row.PackageId is not null));
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
        ShowProviderContextItem();

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

    private static void SayIfNoFile(MenuFlyoutItem? item, string text, string? reason)
    {
        if (item is null) return;
        item.Text = FileMenuRules.Label(text, reason);
        item.IsEnabled = reason is null;
    }

    private async void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (CommandJob is { } job)
        {
            await OpenJobAsync(job);
        }
    }

    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (CommandJob is { } job)
        {
            OpenContainingFolder(job);
        }
        else if (SelectedPackage is { } package && PackageFolder(package) is { } folder && !SayIfOnAnotherServer())
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _ = ShowMessageAsync("Could not open folder", ex.Message);
            }
        }
    }

    /// <summary>U-WL-14: a package's folder is the one all of its files are in, below which each may have its own.</summary>
    private static string? PackageFolder(JobViewModel package)
    {
        var folder = ShellWords.CommonFolder(package.Children.Select(child =>
            !string.IsNullOrWhiteSpace(child.FinalFilePath) ? Path.GetDirectoryName(child.FinalFilePath) : child.Destination));
        return folder is not null && Directory.Exists(folder) ? folder : null;
    }

    /// <summary>Open Folder works for a package too, on the folder its files share.</summary>
    private string? OpenFolderReason(FileMenuPlan plan)
    {
        if (SelectedJob is not null || SelectedPackage is not { } package) return plan.ShellReason;
        if (!RemoteDataRules.MayUseFiles(_connection.IsRemote)) return RemoteDataRules.OnAnotherServer;
        return PackageFolder(package) is null ? "its folder isn't there" : null;
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
        if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary) return;
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
        if (SayIfOnAnotherServer()) return;
        if (CommandJob is { FileRemovedAfterExtraction: true } removed)
        {
            ShowPassingStatus(JobNotes.NoFile(removed.StatusNote));
            return;
        }

        if (CommandJob is not { } job || string.IsNullOrWhiteSpace(job.FinalFilePath) ||
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

        if (job.FileRemovedAfterExtraction)
        {
            ShowPassingStatus(JobNotes.NoFile(job.StatusNote));
            return;
        }

        if (job.State != "completed")
        {
            await ShowMessageAsync("Move/Rename", "Move/Rename is available after the download completes.");
            return;
        }

        // Another server's path is split and joined with its own separator: a Linux server's paths use '/'.
        var remote = _connection.IsRemote;
        var nameBox = new TextBox { Header = "File name", Text = remote ? ServerPaths.FileName(job.FinalFilePath) : Path.GetFileName(job.FinalFilePath) };
        var folderBox = new TextBox { Header = "Folder", Text = (remote ? ServerPaths.Folder(job.FinalFilePath) : Path.GetDirectoryName(job.FinalFilePath)) ?? string.Empty };
        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        browse.Click += async (_, _) =>
        {
            try
            {
                // For another server this is its folder browser, starting at the download's folder.
                if (await Pickers.PickFolderAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), start: remote ? folderBox.Text : null) is { } folder) folderBox.Text = folder;
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
        panel.Children.Add(folderRow);
        // A refusal shows here, and the dialog stays open with what was typed (U-WR-12).
        var problem = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = InboundTheme.Brush("InboundErrorTextBrush", RootGrid),
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(problem);
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
        var moved = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var outcome = await MoveOrRenameAsync(job, nameBox.Text, folderBox.Text, remote);
                if (outcome.Error is { } error)
                {
                    problem.Text = error;
                    problem.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
                else moved = outcome.Moved;
            }
            finally
            {
                deferral.Complete();
            }
        };
        await DialogGuard.ShowAsync(dialog);
        if (moved) await RefreshAsync();
    }

    /// <summary>
    /// Checks and sends one Move/Rename. Returns the problem in words, shown in the dialog, or null with whether
    /// anything was sent (nothing is when the name and folder are unchanged).
    /// </summary>
    private async Task<(string? Error, bool Moved)> MoveOrRenameAsync(JobViewModel job, string typedName, string typedFolder, bool remote)
    {
        if (string.IsNullOrWhiteSpace(typedName)) return ("Type a file name.", false);
        if (string.IsNullOrWhiteSpace(typedFolder)) return ("Choose a folder.", false);
        var newName = typedName.Trim();
        if (remote && newName.IndexOfAny(['/', '\\']) >= 0) return ("Type the file name without a folder; choose the folder above it.", false);

        // A server's folder by its own rules: /downloads on a Linux server, never this PC's D:\ ones. This PC's Host
        // checks its own as before.
        if (remote && !ServerPaths.IsFull(typedFolder, ShownServer.PathStyle))
            return ($"The folder has to be {ServerPaths.FullFolderWords(ShownServer.PathStyle)}.", false);

        var target = remote ? ServerPaths.Join(typedFolder.Trim(), newName) : Path.Combine(typedFolder.Trim(), newName);
        if (string.Equals(target, job.FinalFilePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(remote ? ServerPaths.FileName(target) : Path.GetFileName(target),
                remote ? ServerPaths.FileName(job.FinalFilePath) : Path.GetFileName(job.FinalFilePath), StringComparison.Ordinal))
        {
            return (null, false);
        }

        try
        {
            var reply = await _connection.MoveJobAsync(job.Id, target);
            var result = reply.GetPayload<JobActionResponse>();
            if (result.Ok) return (null, true);
            var error = result.Error ?? "The host rejected the move.";
            // Another server's own folders, so one can be typed or chosen without leaving the dialog.
            if (remote && MoveRefusal.IsOutsideRoots(result)) return (MoveRefusal.Text(error, await ServerRootsAsync(), _connection.Target.Name), false);
            return (error, false);
        }
        catch (Exception ex)
        {
            return (ex.Message, false);
        }
    }

    /// <summary>The folders the shown server may use, from its folder browser; null when it doesn't say.</summary>
    private async Task<IReadOnlyList<string>?> ServerRootsAsync()
    {
        try
        {
            var reply = await ServerConnector.SendOnceAsync(_connection.Target,
                IpcMessage.Create(MessageTypes.FoldersBrowse, new FolderBrowseRequest()), TimeSpan.FromSeconds(10));
            return reply.Type == MessageTypes.FoldersBrowseResult ? reply.GetPayload<FolderBrowseResult>().Roots : null;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
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
                RememberDestination = false,
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
        _queueMenuItems.Clear();
        foreach (var queue in _settings.Queues.OrderBy(queue => queue.Order))
        {
            var start = new MenuFlyoutItem { Text = $"Start {queue.Name} download queue" };
            start.Click += async (_, _) => await QueueActionFromMenuAsync(queue.Id, "start");
            StartQueueMenu.Items.Add(start);

            var stop = new MenuFlyoutItem { Text = $"Stop {queue.Name} download queue" };
            stop.Click += async (_, _) => await QueueActionFromMenuAsync(queue.Id, "stop");
            StopQueueMenu.Items.Add(stop);
            _queueMenuItems[queue.Id] = (start, stop);
        }

        SyncQueueCommands();
    }

    private readonly Dictionary<int, (MenuFlyoutItem Start, MenuFlyoutItem Stop)> _queueMenuItems = new();

    /// <summary>
    /// U-WL-1: the menu, the toolbar and the Queues dialog offer Start for a stopped queue and for a running one whose
    /// "later" or queue-paused downloads wait for it, and Stop for a running one, from the same rule.
    /// </summary>
    private void SyncQueueCommands()
    {
        if (_settings is null) return;
        var jobs = _jobsById.Values.Select(row => row.Dto).OfType<JobDto>().ToList();
        foreach (var queue in _settings.Queues)
        {
            if (!_queueMenuItems.TryGetValue(queue.Id, out var items)) continue;
            items.Start.IsEnabled = QueueCommands.CanStart(queue, jobs);
            items.Stop.IsEnabled = QueueCommands.CanStop(queue);
        }

        var connected = _connection?.IsConnected ?? false;
        var targetId = SelectedJob?.QueueId ?? SelectedPackage?.SharedQueueId ?? _settings.DefaultQueueId;
        var target = _settings.Queues.FirstOrDefault(queue => queue.Id == targetId);
        StartQueueButton.IsEnabled = connected && target is not null && QueueCommands.CanStart(target, jobs);
        StopQueueButton.IsEnabled = connected && target is not null && QueueCommands.CanStop(target);
        var startTip = target is null ? "Start the selected queue" : $"Start the {target.Name} queue ({QueueCommands.State(target, jobs)})";
        if (startTip != _startQueueToolTip) ToolTipService.SetToolTip(StartQueueButton, _startQueueToolTip = startTip);
        var stopTip = target is null ? "Stop the selected queue and pause its downloads" : $"Stop the {target.Name} queue and pause its downloads";
        if (stopTip != _stopQueueToolTip) ToolTipService.SetToolTip(StopQueueButton, _stopQueueToolTip = stopTip);
    }

    private string _startQueueToolTip = string.Empty;
    private string _stopQueueToolTip = string.Empty;

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

            UpdateLimitButtons();
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

        var hidden = job.Dto is { } dto && JobAddress.IsHidden(dto);
        var input = new TextBox
        {
            Text = job.Dto is { } current ? JobAddress.RefreshStart(current) : job.Url,
            Header = "New download address",
            PlaceholderText = hidden ? "Paste the new link, or leave empty to try the saved one" : string.Empty,
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

        if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary ||
            (string.IsNullOrWhiteSpace(input.Text) && !hidden))
        {
            return;
        }

        // The address it already has stays usable: refusing that would strand a retry of a direct download.
        // A hidden one isn't in the box, so an empty box asks for the same.
        var newAddress = string.IsNullOrWhiteSpace(input.Text) ? job.Url : input.Text.Trim();
        var chosenDirect = false;
        if (newAddress != job.Url && LinkExtractor.IsWebLink(newAddress) && await ProviderAddAppliesAsync())
        {
            var address = await CheckGivenAddressAsync("Refresh download address", newAddress);
            if (address.Way == AddressWay.Cancelled) return;
            if (address.Way == AddressWay.Provider)
            {
                await ShowMessageAsync("Refresh download address", RemoteText.RefreshRefused(ProviderNameFor(address.Result)));
                return;
            }

            chosenDirect = address.ChosenDirect;
        }

        await RunActionAsync(() => newAddress == job.Url
            ? _connection.RefreshUrlAsync(job.Id, newAddress)
            : _connection.RefreshChangedUrlAsync(job.Id, newAddress, chosenDirect));
    }

    private void MenuProgressDetails_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPackage is { } package) { OpenPackageWindow(package.Id); return; }
        if (CommandJob is not { } job) return;
        OpenDownloadWindow(job.Id);
    }

    private void OpenPackageWindow(Guid packageId)
    {
        // A package of one is an ordinary row on the list, so it gets an ordinary file's window.
        if (ProgressDetailRules.OnlyFile(_groups.FirstOrDefault(group => group.Id == packageId), _jobsById.ContainsKey) is { } only)
        {
            OpenDownloadWindow(only);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, WindowInstance.Arguments("package", packageId, _connection.Target)) { UseShellExecute = false });
        }
        catch (Exception ex) { _ = ShowMessageAsync("Could not open progress", ex.Message); }
    }

    private void OpenDownloadWindow(Guid jobId)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, WindowInstance.Arguments("job", jobId, _connection.Target, manual: true))
            {
                UseShellExecute = false,
            });
        }
        catch (Exception ex) { _ = ShowMessageAsync("Could not open progress", ex.Message); }
    }

    private void MenuSpeedGraph_Click(object sender, RoutedEventArgs e)
    {
        SpeedGraph.Visibility = ShowSpeedGraphMenuItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        SpeedGraph.IsSeen = ShowSpeedGraphMenuItem.IsChecked;
        SaveUiState();
    }

    private void MenuStatistics_Click(object sender, RoutedEventArgs e)
    {
        if (_statisticsWindow is null)
        {
            _statisticsWindow = new StatisticsWindow(_connection.Target);
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
        return await DialogGuard.ShowAsync(dialog) == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(name.Text)
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
        if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary || categoryBox.SelectedItem is not ComboBoxItem { Tag: string categoryId }) return;
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
        if (CommandJob is not { } job || _settings is null)
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
            ("File", job.FileName), ("Status", job.StateText), ("Note", job.StatusNote), ("Size", job.SizeText),
            ("Queue", string.IsNullOrWhiteSpace(job.QueueName) ? "None" : job.QueueName),
            ("Source", job.IsProviderFile ? ProviderDisplayName(job.Dto?.RemoteProvider) : job.SourceText),
            ("Package", job.IsProviderFile ? _groups.FirstOrDefault(group => group.JobIds.Contains(job.Id))?.Name : null),
            (job.Dto is { } shown ? JobAddress.Label(shown) : "Address", job.IsProviderFile || job.Dto is null ? null : JobAddress.Shown(job.Dto)),
            ("Saved to", job.FinalFilePath), ("Last try", job.LastTryText),
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
        // Another server's download: its program is a path there, never looked for on this PC, and only what its hello
        // allows can be changed.
        var capabilities = _connection.Hello?.Capabilities ?? [];
        var remoteScope = _connection.IsRemote
            ? new RemoteCompletionScope(_connection.Target.Name, capabilities.Contains("programSettings"), capabilities.Contains("completionActions"))
            : null;
        var completionEditor = new JobCompletionEditor(job.CompletionOptions, this, _settings.AllowSystemCompletionActions, remoteScope);
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
            Content = new ScrollViewer { Padding = InboundTheme.DialogScrollPadding, Content = panel, MaxHeight = Math.Max(180, RootGrid.ActualHeight - 220), VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
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

        if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary)
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
        if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary ||
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
        if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary ||
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
        if (await DialogGuard.ShowAsync(confirm) != ContentDialogResult.Primary)
        {
            return;
        }

        Dictionary<string, RemoteClassifyResult>? answers = null;
        if (await ProviderAddAppliesAsync())
        {
            ShowPassingStatus($"Checking {entries.Count} addresses…");
            answers = await ImportRouting.ClassifyAllAsync(entries.Select(entry => entry.Url).Where(LinkExtractor.IsDownloadableLink), () => _remote.OpenClassifier());
        }

        var accepted = 0;
        var failed = 0;
        string? firstError = null;
        var toReview = new List<(string Input, RemoteClassifyResult Result)>();
        foreach (var entry in entries)
        {
            if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https" or "ftp"))
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
                    RememberDestination = false,
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
        if (toReview.Count > 0 && !await ShowProviderAddAsync(AddFocus.ImportedLinks, answered: toReview))
        {
            ShowPassingStatus(RemoteText.ImportReviewNotOpened(accepted, entries.Count, failed, toReview.Count));
        }
    }

    private void MenuAdd_Click(object sender, RoutedEventArgs e) => AddButton_Click(sender, e);

    private async void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _local.ShutdownAsync();
        }
        catch (Exception)
        {
            // The host may already be exiting.
        }

        Application.Current.Exit();
#if HAS_UNO
        // Uno's X11 head leaves the process running after Exit.
        Environment.Exit(0);
#endif
    }

    private void MenuPause_Click(object sender, RoutedEventArgs e) => PauseButton_Click(sender, e);
    private void MenuResume_Click(object sender, RoutedEventArgs e) => ResumeButton_Click(sender, e);

    /// <summary>The File and list menus' Delete: the same dialog as the toolbar's Delete and the Delete key.</summary>
    private async void MenuDelete_Click(object sender, RoutedEventArgs e) => await ConfirmDeleteAsync(deleteFromDisk: false);

    // A record's row in the context menu acts on the download behind it, as the other file commands there do.
    private async void MenuContextDelete_Click(object sender, RoutedEventArgs e) => await ConfirmDeleteAsync(deleteFromDisk: false, fromContextMenu: true);
    private void MenuOptions_Click(object sender, RoutedEventArgs e) => _ = OpenOptionsAsync();

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

        if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary)
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
        // UI.R31: nothing found at the last scan offers nothing to do: a success bar and Close.
        if (known.Count == 0)
        {
            var clean = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = "Clean up leftover partial files",
                Content = new InfoBar
                {
                    Severity = InfoBarSeverity.Success,
                    IsOpen = true,
                    IsClosable = false,
                    Title = "Nothing to clean up",
                    Message = "No leftover partial files were found at the last scan. Partial files of existing downloads are never touched.",
                },
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };
            await DialogGuard.ShowAsync(clean);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "Clean up leftover partial files",
            Content = new ScrollViewer
            {
                Padding = InboundTheme.DialogScrollPadding,
                MaxHeight = 320,
                Content = new TextBlock
                {
                    Text = text + "\n\nQuarantine moves them into a .dm-orphans folder next to where they were " +
                           "found, and the folder goes once it is empty. Partial files of existing downloads are never touched.",
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            },
            PrimaryButtonText = "Quarantine",
            SecondaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var choice = await DialogGuard.ShowAsync(dialog);
        if (choice is null or ContentDialogResult.None)
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

        var result = await DialogGuard.ShowAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            _search = box.Text.Trim();
            RebuildVisible();
            UpdateStatusText();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            _search = string.Empty;
            RebuildVisible();
            UpdateStatusText();
        }
    }

    /// <summary>The secondary text color for an editable box's placeholder, in its own theme and when the theme changes.</summary>
    /// <param name="theme">The window's theme, for before the box is in the tree.</param>
    private static void DimPlaceholder(ComboBox box, ElementTheme theme)
    {
        // The editable box's inner text box draws the placeholder, and with no placeholder color of its own it takes
        // the focused text color while it has the focus. So the color goes on that text box once it exists.
        void Apply(ElementTheme current)
        {
            var brush = InboundTheme.Brush("InboundSecondaryTextBrush", current);
            box.PlaceholderForeground = brush;
            if (FindDescendant<TextBox>(box, "EditableText") is { } text) text.PlaceholderForeground = brush;
        }

        Apply(theme);
        box.Loaded += (_, _) => Apply(box.ActualTheme);
        box.GotFocus += (_, _) => Apply(box.ActualTheme);
        box.ActualThemeChanged += (_, _) => Apply(box.ActualTheme);
    }

    private static T? FindDescendant<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && match.Name == name) return match;
            if (FindDescendant<T>(child, name) is { } deeper) return deeper;
        }

        return null;
    }

    /// <summary>Turns every control inside a panel on or off, however deep the panels go.</summary>
    private static void SetControlsEnabled(UIElement element, bool enabled)
    {
        switch (element)
        {
            case Control control:
                control.IsEnabled = enabled;
                break;
            case Panel panel:
                foreach (var child in panel.Children) SetControlsEnabled(child, enabled);
                break;
            case Border { Child: { } child }:
                SetControlsEnabled(child, enabled);
                break;
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

        var (next, ascending) = ListSort.Next(_sortKey, _sortAscending, key);
        ApplySort(next, ascending);
    }

    private void MenuLargeToolbar_Click(object sender, RoutedEventArgs e) => SetToolbarScale(large: true);
    private void MenuSmallToolbar_Click(object sender, RoutedEventArgs e) => SetToolbarScale(large: false);

    private void SetToolbarScale(bool large)
    {
        _toolbarLarge = large;
        // Some buttons sit in a panel of their own (My TorBox, shown only with an account), one level down.
        var buttons = ToolbarPanel.Children.OfType<Button>()
            .Concat(ToolbarPanel.Children.OfType<Panel>().SelectMany(panel => panel.Children.OfType<Button>()));
        foreach (var button in buttons)
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

        // The server switcher keeps its name with small buttons too, beside the icon: which server is shown is what
        // it is there to say (UI.R26).
        ToolbarServerSwitcher.Height = large ? 58 : 32;
        ToolbarServerSwitcher.Padding = large ? new Thickness(10, 2, 10, 2) : new Thickness(6, 2, 6, 2);
        if (ToolbarServerSwitcher.Content is StackPanel server)
        {
            server.Orientation = large ? Orientation.Vertical : Orientation.Horizontal;
            server.Spacing = large ? 0 : 6;
            ToolbarServerIcon.FontSize = large ? 25 : 16;
            ToolbarServerLabel.Margin = large ? new Thickness(0, 3, 0, 0) : new Thickness(0);
            ToolbarServerLabel.VerticalAlignment = VerticalAlignment.Center;
        }
    }

    /// <summary>View > Dark Mode: a quick way to set the Theme setting to Dark or Light (UI.R23). The choice is kept in
    /// ui-state.json until the Host has saved it, the same way as the old value on upgrade, so a snapshot that arrives
    /// first, or a failed save, cannot put the previous theme back, and a pending older value is never sent over it.</summary>
    private void MenuDarkMode_Click(object sender, RoutedEventArgs e)
    {
        var choice = DarkModeMenuItem.IsChecked ? ThemeChoices.Dark : ThemeChoices.Light;
        AppTheme.Use(choice);
        _uiState.DarkMode = ThemeChoices.ToDarkMode(choice);
        SaveUiState();
        if (!_savingKeptTheme) _ = SaveKeptThemeAsync();
    }

    private bool _savingKeptTheme;

    /// <summary>Uses the settings' Theme. While a View > Dark Mode choice is kept in ui-state.json (from before the
    /// setting was the source, or one the Host has yet to save), that one is used and saved as the setting, so nobody's
    /// look changes on upgrade and a click is not undone by a snapshot.</summary>
    private void FollowThemeSetting(SettingsDto settings)
    {
        if (ThemeChoices.FromDarkMode(_uiState.DarkMode) is { } kept)
        {
            AppTheme.Use(kept);
            if (!_savingKeptTheme) _ = SaveKeptThemeAsync();
            return;
        }

        AppTheme.Use(settings.Theme);
    }

    /// <summary>Saves the kept choice as the Theme setting, then forgets it. A click while a save is under way changes
    /// the kept value, and the loop saves that one too before it stops. UI thread only, like every other use of
    /// _uiState. A failed save keeps the value, and the next snapshot tries again.</summary>
    private async Task SaveKeptThemeAsync()
    {
        _savingKeptTheme = true;
        try
        {
            while (ThemeChoices.FromDarkMode(_uiState.DarkMode) is { } kept)
            {
                if (!await SaveThemeAsync(kept)) return;
                if (ThemeChoices.FromDarkMode(_uiState.DarkMode) != kept) continue;
                _uiState.DarkMode = null;
                SaveUiState();
            }
        }
        finally
        {
            _savingKeptTheme = false;
        }
    }

    /// <summary>Saves <paramref name="theme"/> as the Theme setting, from the Host's latest settings so nothing changed
    /// elsewhere is undone. False when the Host did not take it; the window keeps the theme either way.</summary>
    private async Task<bool> SaveThemeAsync(string theme)
    {
        try
        {
            // The theme is this PC's, whichever server the window shows.
            var latest = await _local.RefreshAsync();
            if (latest.Settings.Theme == theme)
            {
                KeepLocalSettings(latest.Settings);
                return true;
            }

            var updated = CloneSettings(latest.Settings);
            updated.Theme = theme;
            var result = (await _local.UpdateSettingsAsync(updated)).GetPayload<SettingsResult>();
            if (!result.Ok) return false;
            KeepLocalSettings(result.Settings);
            return true;
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Theme save failed: {ex.Message}");
            return false;
        }
    }

    private void ApplyTitleBarTheme(bool dark) => WindowLayout.ApplyTitleBar(this, dark);

    private async void MenuUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var check = (await _local.CheckForUpdatesAsync())
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
            var updateChoice = await DialogGuard.ShowAsync(dialog);
            // Refused because another dialog is open: nothing was asked, so nothing is deferred either.
            if (updateChoice is null) return;
            if (updateChoice != ContentDialogResult.Primary)
            {
                await _local.DeferUpdateAsync(check.AvailableVersion!);
                return;
            }

            var download = (await _local.DownloadUpdateAsync())
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
            var installChoice = await DialogGuard.ShowAsync(installDialog);
            // Refused because another dialog is open: nothing was asked, so nothing is deferred either. The installer
            // stays downloaded, and Check for updates offers it again.
            if (installChoice is null) return;
            if (installChoice != ContentDialogResult.Primary)
            {
                await _local.DeferUpdateAsync(check.AvailableVersion!);
                return;
            }

            var install = (await _local.InstallUpdateAsync())
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
                if (await DialogGuard.ShowAsync(forceDialog) != ContentDialogResult.Primary)
                {
                    return;
                }

                install = (await _local.InstallUpdateAsync(force: true))
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
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "Inbound Download Manager", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        text.Children.Add(new TextBlock { Text = $"Version {version}", IsTextSelectionEnabled = true });
        text.Children.Add(new TextBlock
        {
            // U-WL-26: what it does for the person, not how it is built.
            Text = "Downloads files for you, in parts at once where the server allows, and picks up where it left off after a " +
                "lost connection or a restart. Downloads keep going in the background while this window is closed, and the " +
                "browser extension hands the browser's downloads to it.\n\n" +
                "If something goes wrong, Help > Open Diagnostics Folder has the logs.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        // A grid, not a horizontal stack: a stack gives the text unlimited width, so it would never wrap.
        var row = new Grid { ColumnSpacing = 16, Padding = InboundTheme.DialogScrollPadding, MaxWidth = 520 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "app.png");
            if (File.Exists(icon))
            {
                var image = new Image { Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Top, Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(icon)) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(image, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                row.Children.Add(image);
            }
        }
        catch (Exception)
        {
            // Without the picture the About text still shows.
        }

        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = "About",
            Content = row,
            CloseButtonText = "OK",
        };
        try
        {
            if (await ShowDialogAsync(dialog) is null) ShowPassingStatus($"Inbound Download Manager {version}");
        }
        catch (Exception)
        {
            ShowPassingStatus($"Inbound Download Manager {version}");
        }
    }

    private void MenuDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        // This PC's own folder, whichever server the window shows.
        var directory = _connection.IsRemote ? _localStatus?.StateDirectory : _status?.StateDirectory;
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

        await RefreshSettingsAsync();
        var providerAdd = await ProviderAddAppliesAsync();
        var urlBox = new ComboBox
        {
            Header = "URL",
            PlaceholderText = "https://example.com/file.zip",
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        // U-WL-21: an editable box draws its placeholder in the text color, so it read as a typed address.
        DimPlaceholder(urlBox, RootGrid.ActualTheme);
        foreach (var recent in _settings.RecentUrls.Take(20))
        {
            urlBox.Items.Add(recent);
        }
        // UI.R18: a very long link would make the list as wide as the screen. Cut it short; the tooltip has it all.
        urlBox.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<TextBlock Text='{Binding}' MaxWidth='560' TextTrimming='CharacterEllipsis' ToolTipService.ToolTip='{Binding}' /></DataTemplate>");
        // With no recent addresses the list would open as an empty strip over the dialog.
        urlBox.DropDownOpened += (_, _) => { if (urlBox.Items.Count == 0) urlBox.IsDropDownOpen = false; };
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
            Title = ServerNames.ForWindow("Enter new address to download", _connection.Target),
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
            // U-WL-12: a numbered address is several downloads, which Add Links lists one by one.
            if (LinkExtractor.ExpandNumbered(url) is { Count: > 1 } numbered)
            {
                await ShowProviderAddAsync(AddFocus.Links, numbered);
                return;
            }
        }
        else
        {
            addressDialog.PrimaryButtonClick += (_, args) =>
            {
                if (!Uri.TryCreate(urlBox.Text.Trim(), UriKind.Absolute, out var candidate) ||
                    candidate.Scheme is not ("http" or "https" or "ftp"))
                {
                    addressError.Say("Enter an absolute http, https or ftp URL.", isProblem: true);
                    args.Cancel = true;
                }
            };
            if (await DialogGuard.ShowAsync(addressDialog) != ContentDialogResult.Primary) return;
            url = urlBox.Text.Trim();
            // U-WL-12: a numbered address is several downloads, which Add Links lists one by one.
            if (LinkExtractor.ExpandNumbered(url) is { Count: > 1 } numbered)
            {
                await ShowAddLinksAsync(numbered);
                return;
            }
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
                if (await Pickers.PickFolderAsync(WinRT.Interop.WindowNative.GetWindowHandle(this)) is { } folder)
                    destinationBox.Text = folder;
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
        long? knownSize = null;
        var destinationField = FolderChooser.Attach(destinationRow, destinationBox, () => knownSize, out var refreshSpace);
        var descriptionBox = new TextBox { Header = "Description (optional)" };
        var categoryBox = new ComboBox { Header = "Category", HorizontalAlignment = HorizontalAlignment.Stretch };
        categoryBox.Items.Add(new ComboBoxItem { Content = "Automatic (file extension)" });
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
            var folder = _settings.UseCategoryFolders ? _settings.CustomCategories.FirstOrDefault(item => item.Id == category)?.DestinationDirectory : null;
            if (_settings.UseCategoryFolders && string.IsNullOrWhiteSpace(folder)) _settings.CategoryFolders.TryGetValue(category, out folder);
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
                UpdateLimitButtons();
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
        authenticationPanel.Children.Add(new TextBlock { Text = $"HTTP Basic authentication. Credentials apply only to this download and are {PlatformText.ProtectedBy} when saved.", TextWrapping = TextWrapping.Wrap });
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
        // U-WR-2: the Host said it won't add this address for this PC (another device's rules), so Start would only fail.
        string? addRefusal = null;
        ContentDialog? addDialog = null;
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
                // The Host answered: its refusal holds only while it still gives one, so a later answer without one turns
                // Start and Download later back on.
                var wasRefused = addRefusal is not null;
                addRefusal = AddRules.RefusalAfter(addRefusal, answered: true, refused: !metadata.Ok && metadata.AddRefused, metadata.Error);
                if (addRefusal is not null)
                {
                    metadataText.Text = AddRules.RefusedText(addRefusal, _connection.Target);
                    metadataText.Foreground = InboundTheme.Brush("InboundErrorTextBrush", RootGrid);
                    if (addDialog is not null) addDialog.IsPrimaryButtonEnabled = addDialog.IsSecondaryButtonEnabled = false;
                    return;
                }
                if (wasRefused)
                {
                    metadataText.ClearValue(TextBlock.ForegroundProperty);
                    if (addDialog is not null) addDialog.IsPrimaryButtonEnabled = addDialog.IsSecondaryButtonEnabled = true;
                }
                if (!metadata.Ok) throw new InvalidOperationException(metadata.Error ?? "File information is unavailable.");
                // UI.R31: the list's size format; the exact count is in the tooltip.
                var size = metadata.TotalBytes is { } bytes ? UiFormat.Bytes(bytes) : "Size unknown";
                ToolTipService.SetToolTip(metadataText, metadata.TotalBytes is { } exact ? $"{exact:N0} bytes" : null);
                knownSize = metadata.TotalBytes;
                refreshSpace();
                var serverName = RemoteText.ServerNameToShow(metadata.FileName, url, addressResult);
                metadataText.Text = $"{size} · {metadata.ContentType ?? "Type unknown"}" + (serverName is null ? string.Empty : $"\nServer name: {serverName}");
                if (!nameEdited && serverName is not null)
                {
                    assigningServerName = true;
                    nameBox.Text = serverName;
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
        // Why the last Start didn't add it: shown here, with everything typed kept, rather than after the dialog closed.
        var addProblem = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = InboundTheme.Brush("InboundErrorTextBrush", RootGrid),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(addProblem, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        panel.Children.Add(addProblem);
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
        panel.Children.Add(destinationField);
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
            Title = ServerNames.ForWindow("Download file info", _connection.Target),
            Content = new ScrollViewer { Padding = InboundTheme.DialogScrollPadding, Content = panel, MaxHeight = Math.Max(200, Math.Min(480, RootGrid.ActualHeight - 240)) },
            PrimaryButtonText = "Start download",
            SecondaryButtonText = "Download later",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        fileInfoDialog = dialog;
        addDialog = dialog;
        dialog.Opened += async (_, _) =>
        {
            // Shown again after a failed Start: the check runs again, and its answer counts again.
            metadataClosed = false;
            if (addRefusal is not null) dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
            await PreviewMetadataAsync();
        };
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
        try
        {
            // U-WR-2: a Start the Host refuses shows its reason in this dialog, which opens again with all that was typed.
            // The dialog that just closed is still on its way out for a moment, or another dialog may be open on this
            // window, and DialogGuard refuses to show it until that one is gone; so it is tried again, and never dropped
            // while another dialog is the reason. Only a window that stops showing dialogs at all gives up, to the
            // status bar.
            string? lastProblem = null;
            var failedShows = 0;
            while (true)
            {
                var (shown, problem) = await AddFromFileInfoAsync();
                var step = AddRules.AfterShow(shown, problem, lastProblem, failedShows);
                if (step == ReshowStep.Stop) break;
                if (step == ReshowStep.GiveUp)
                {
                    ShowPassingStatus(lastProblem!);
                    break;
                }

                if (step == ReshowStep.TryAgain)
                {
                    if (!DialogGuard.IsOpen(dialog.XamlRoot)) failedShows++;
                    await Task.Delay(100);
                    continue;
                }

                lastProblem = problem;
                failedShows = 0;
                addProblem.Text = problem;
                addProblem.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            authenticationPassword.Password = string.Empty;
        }

        // Whether the dialog was shown, and the reason it wasn't added, to show in it again; null when it was added or the user stopped.
        async Task<(bool Shown, string? Problem)> AddFromFileInfoAsync()
        {
            var downloadChoice = await DialogGuard.ShowAsync(dialog);
            // Null: it couldn't be shown (another dialog was still open on this window).
            if (downloadChoice is null) return (false, null);
            if (downloadChoice == ContentDialogResult.None) return (true, null);

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
                // The add dialog is still on its way out for a moment.
                for (var wait = 0; wait < 40 && DialogGuard.IsOpen(dialog.XamlRoot); wait++) await Task.Delay(50);
                var duplicateResult = await DialogGuard.ShowAsync(duplicateDialog);
                // The question couldn't be shown: what was typed comes back, with why, rather than being dropped.
                if (duplicateResult is null)
                    return (true, $"{existing.FileName} is already in the list for this URL. Choose Start again to decide whether to download it again.");
                if (duplicateResult == ContentDialogResult.Secondary)
                {
                    JobsList.SelectedItem = existing;
                    return (true, null);
                }

                if (duplicateResult != ContentDialogResult.Primary)
                {
                    return (true, null);
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
                var reply = await _connection.AddAsync(request);
                var add = reply.GetPayload<AddJobResponse>();
                if (!add.Accepted) return (true, AddRules.NotAddedText(add.Error, _connection.Target));
                await RefreshAsync();
                return (true, null);
            }
            catch (Exception ex)
            {
                return (true, AddRules.NotAddedText(ex.Message, _connection.Target));
            }
        }
    }

    private async void MenuAddBatch_Click(object sender, RoutedEventArgs e) => await ShowAddLinksAsync(null);

    /// <param name="links">Links to start from, in place of what the clipboard holds: Add URL's numbered address, expanded.</param>
    private async Task ShowAddLinksAsync(IReadOnlyList<string>? links)
    {
        if (_settings is null)
        {
            return;
        }

        if (await ProviderAddAppliesAsync())
        {
            await ShowProviderAddAsync(AddFocus.Links, links);
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
        if (links is { Count: > 0 }) pasteBox.Text = string.Join(Environment.NewLine, links);
        else try
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
        panel.Children.Add(SettingsWindow.Hint(LinkExtractor.NumberedHint));
        var batchDestinationRow = new Grid { ColumnSpacing = 8 };
        batchDestinationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        batchDestinationRow.Children.Add(destinationBox);
        panel.Children.Add(FolderChooser.Attach(batchDestinationRow, destinationBox));
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
            Content = new ScrollViewer { Padding = InboundTheme.DialogScrollPadding, Content = panel, MaxHeight = 560 },
            PrimaryButtonText = "Review",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var found = LinkExtractor.HttpLinks(pasteBox.Text);
        if (found.Count == 0)
        {
            await ShowMessageAsync("Add links", "No http, https or ftp links were found in the pasted text.");
            return;
        }

        var checks = new List<(CheckBox Box, string Url)>();
        var list = new StackPanel { Spacing = 2 };
        foreach (var link in found)
        {
            var uri = new Uri(link);
            var name = Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty);
            var listed = FindExistingJob(link) is not null;
            var noPath = LinkExtractor.HasNoPath(link);
            var box = new CheckBox
            {
                // U-WL-23: an address that names no file may have been cut short, so it waits to be ticked.
                IsChecked = !(listed && skipDuplicates.IsChecked == true) && !noPath,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = name.Length > 0 ? name : uri.Host, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock
                        {
                            Text = uri.Host + (listed ? " · already in the list" : string.Empty) + (noPath ? $" · {LinkExtractor.NoPathWords}" : string.Empty),
                            TextWrapping = TextWrapping.Wrap,
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
        if (await DialogGuard.ShowAsync(review) != ContentDialogResult.Primary)
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

        // The new rows say a clean add. Only one that failed or was already queued needs an OK.
        if (added.NeedsSaying) await ShowMessageAsync("Add links", added.Summary);
        else if (RemoteText.AddedWords(added.Accepted, 0, string.Empty) is { Length: > 0 } words) ShowPassingStatus(words);
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

        /// <summary>A link failed or was already queued. Anything else the new rows say for themselves.</summary>
        public bool NeedsSaying => Failed > 0 || Duplicates > 0;
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
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp"))
            {
                failed++;
                firstError ??= "That isn't an http, https or ftp address.";
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
        UpdateLimitButtons();
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
                Padding = InboundTheme.DialogScrollPadding,
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
                Text = $"{queue.Name} — {QueueCommands.State(queue, snapshot.Jobs)}",
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

            var start = new Button { Content = "Start", IsEnabled = QueueCommands.CanStart(queue, snapshot.Jobs) };
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

        var dialogResult = await DialogGuard.ShowAsync(dialog);
        if (dialogResult is null) return;
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
            if (await DialogGuard.ShowAsync(confirm) != ContentDialogResult.Primary)
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

        UpdateLimitButtons();
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
                Padding = InboundTheme.DialogScrollPadding,
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

        if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary)
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

        UpdateLimitButtons();
        await RefreshAsync();
        return true;
    }

    private void MenuScheduler_Click(object sender, RoutedEventArgs e)
    {
        if (_schedulerWindow is null)
        {
            _schedulerWindow = new SchedulerWindow(_connection.Target);
            _schedulerWindow.Closed += (_, _) => _schedulerWindow = null;
        }
        _schedulerWindow.ShowWindow();
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

    /// <summary>This PC's Options. Opened through <see cref="OpenOptionsAsync"/>, which keeps it to one window.</summary>
    private async Task ShowSettingsDialogAsync()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        if (_settings is null)
        {
            await ShowMessageAsync("Options", RemoteOptionsText.ThisPcUnreachable);
            return;
        }

        // UI.B6: from the Host's current settings; the window's copy can be stale (folders remembered by an add,
        // changes from the CLI), and Options would show and save the old values.
        await RefreshSettingsAsync();
        var settings = CloneSettings(_settings);
        var window = new SettingsWindow();
        OptionsWindowCreated(window);
        try
        {
            // Which remote and extraction pages exist depends on what the Host said it can do.
            if (!_remote.State.IsReady) await _remote.WaitUntilReadyAsync(TimeSpan.FromSeconds(1.5));
            var (directoryRow, directory) = window.FolderField("Default download folder", settings.DefaultDownloadDirectory);
            var maxConcurrent = SettingsWindow.Number("Maximum simultaneous downloads", settings.MaxConcurrentDownloads, 1, 16);
            var connections = SettingsWindow.Number("Connections per download", settings.DefaultConnections, 1, 32);
            var speedLimit = SettingsWindow.Number("Total speed limit, all downloads (KB/s)",
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
            var launch = new CheckBox { Content = PlatformText.StartAtSignIn, IsChecked = settings.LaunchAtStartup };
            var clipboard = new CheckBox { Content = "Watch the clipboard for download links", IsChecked = settings.ClipboardMonitoring };
            var integration = new CheckBox { Content = "Capture downloads from your browser", IsChecked = settings.BrowserIntegrationEnabled };
            var notify = new CheckBox { Content = "Show a notification", IsChecked = settings.NotifyOnCompletion };
            var sound = new CheckBox { Content = "Play a sound", IsChecked = settings.PlayCompletionSound };
            var completionDialog = new CheckBox { Content = "Show the download-complete window", IsChecked = settings.ShowCompletionDialog };
            var autoResume = new CheckBox { Content = "Resume unfinished downloads when Inbound starts", IsChecked = settings.AutoResumeUnfinishedOnStartup };
            var closeCompletely = new CheckBox { Content = "Free the window's memory when I close it (downloads keep running in the background)", IsChecked = settings.CloseWindowCompletely };
            ToolTipService.SetToolTip(closeCompletely, "Downloads keep running in the background. Opening the window again from the tray takes a moment longer.");
            var providerOpens = ProviderOpensBox();
            var useCategoryFolders = new CheckBox { Content = "Save downloads to a folder for their category", IsChecked = settings.UseCategoryFolders };
            // UI.R23: the source of every window's theme. View > Dark Mode sets it too, so the box follows that.
            string[] themeValues = [ThemeChoices.System, ThemeChoices.Light, ThemeChoices.Dark];
            var themeBox = new ComboBox
            {
                Header = "Theme",
                MinWidth = 200,
                ItemsSource = new[] { PlatformText.FollowSystemTheme, "Light", "Dark" },
                // The theme in use, not only the Host's: a Dark Mode click the Host has yet to save is kept in
                // _uiState, and starting from the Host's value would let OK undo it.
                SelectedIndex = Math.Max(0, Array.IndexOf(themeValues, ThemeChoices.InUse(_uiState.DarkMode, settings.Theme))),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(themeBox, "ThemeBox");
            void FollowThemeMenu() => themeBox.SelectedIndex = Math.Max(0, Array.IndexOf(themeValues, AppTheme.Choice));
            AppTheme.Changed += FollowThemeMenu;
            window.Closed += (_, _) => AppTheme.Changed -= FollowThemeMenu;
            var postDownload = new TextBox
            {
                Header = "Run this program after each download (optional)",
                Text = settings.PostDownloadCommand ?? string.Empty,
                // UI.R31: a hint that can't be taken for a real value.
                PlaceholderText = "Path to a program",
            };
            // The update feed, key and channel are not on any page, and Options leaves them as they are: hidden boxes
            // once wrote them back, and the key's box kept only its first line (UP.B1). The CLI still sets them.
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
                ItemsSource = new[] { "No proxy", PlatformText.SystemProxy, "Use this proxy server:" },
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
            // UI.R31: the page says when there is nothing on it yet.
            var loginEmpty = SettingsWindow.Hint("No site logins yet.");
            void ShowLoginEmpty() => loginEmpty.Visibility = loginRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
                var border = InboundUi.Card(row, new Thickness(12));
                var entry = (original, site, user, password);
                loginRows.Add(entry);
                loginPanel.Children.Add(border);
                ShowLoginEmpty();
                remove.Click += (_, _) =>
                {
                    password.Password = string.Empty;
                    loginRows.Remove(entry);
                    loginPanel.Children.Remove(border);
                    ShowLoginEmpty();
                };
            }
            foreach (var login in settings.SiteLogins) AddLoginRow(login);
            ShowLoginEmpty();
            var addLogin = new Button { Content = "Add site login" };
            addLogin.Click += (_, _) => AddLoginRow(new SiteLoginDto { Id = Guid.NewGuid() });
            var loginHelp = SettingsWindow.Hint((OperatingSystem.IsWindows() ? "Credentials are saved securely by Windows" : "Credentials are saved encrypted") + " and used for matching HTTP or FTP downloads. Re-enter the password when changing a saved site's address or user name.");

            var categoryBoxes = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
            var categoryRows = new List<UIElement>();
            foreach (var category in FileCategories.Names.Append("other"))
            {
                var (row, box) = window.FolderField($"{char.ToUpperInvariant(category[0])}{category[1..]}",
                    settings.CategoryFolders.TryGetValue(category, out var folder) ? folder : null, "Use the default folder", inlineLabel: true);
                categoryBoxes[category] = box;
                categoryRows.Add(row);
            }

            // Favorite folders: name and folder per row, in order. The star beside a folder box saves at once, so OK
            // keeps any saved meanwhile and drops only the rows removed here.
            var favoritesAtOpen = settings.FavoriteFolders.Select(favorite => favorite.Path).ToList();
            var favoriteRows = new List<(TextBox Name, TextBox Folder, UIElement Row)>();
            var favoritesPanel = new StackPanel { Spacing = 8 };
            void ShowFavoriteRows()
            {
                favoritesPanel.Children.Clear();
                foreach (var entry in favoriteRows) favoritesPanel.Children.Add(entry.Row);
                if (favoriteRows.Count == 0) favoritesPanel.Children.Add(SettingsWindow.Hint("No favorites yet."));
            }
            void AddFavoriteRow(string name, string path)
            {
                var nameBox = new TextBox { Header = "Name", Text = name, Width = 160, VerticalAlignment = VerticalAlignment.Bottom };
                var (folderRow, folderBox) = window.FolderField("Folder", path, favorites: false);
                var up = new Button { Content = new FontIcon { Glyph = "\uE70E", FontSize = 12 }, VerticalAlignment = VerticalAlignment.Bottom };
                var down = new Button { Content = new FontIcon { Glyph = "\uE70D", FontSize = 12 }, VerticalAlignment = VerticalAlignment.Bottom };
                var remove = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 }, VerticalAlignment = VerticalAlignment.Bottom };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(up, "Move up");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(down, "Move down");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "Remove favorite");
                ToolTipService.SetToolTip(up, "Move up");
                ToolTipService.SetToolTip(down, "Move down");
                ToolTipService.SetToolTip(remove, "Remove");
                var line = new Grid { ColumnSpacing = 8 };
                foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
                var cells = new UIElement[] { nameBox, folderRow, up, down, remove };
                for (var i = 0; i < cells.Length; i++) { Grid.SetColumn((FrameworkElement)cells[i], i); line.Children.Add(cells[i]); }
                var entry = (nameBox, folderBox, (UIElement)line);
                void Move(int by)
                {
                    var at = favoriteRows.IndexOf(entry);
                    var to = at + by;
                    if (at < 0 || to < 0 || to >= favoriteRows.Count) return;
                    favoriteRows.RemoveAt(at);
                    favoriteRows.Insert(to, entry);
                    ShowFavoriteRows();
                }
                up.Click += (_, _) => Move(-1);
                down.Click += (_, _) => Move(1);
                remove.Click += (_, _) => { favoriteRows.Remove(entry); ShowFavoriteRows(); };
                favoriteRows.Add(entry);
            }
            foreach (var favorite in settings.FavoriteFolders) AddFavoriteRow(favorite.Name, favorite.Path);
            ShowFavoriteRows();
            var addFavorite = new Button { Content = "Add favorite" };
            addFavorite.Click += (_, _) => { AddFavoriteRow(string.Empty, string.Empty); ShowFavoriteRows(); };

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
                SettingsWindow.Section("Window"), themeBox, closeCompletely, providerOpens,
                SettingsWindow.Section("Capturing downloads"), integration, clipboard);
            var saveTo = new List<UIElement> { directoryRow, SettingsWindow.Section("Folders by category"), useCategoryFolders,
                SettingsWindow.Hint("Downloads of these types go to their own folder. Leave a field empty to use the default folder. Turned off, every download goes to the default folder.") };
            saveTo.AddRange(categoryRows);
            void ShowCategoryFoldersEnabled()
            {
                // U-WL-19: every control in a row, Browse and the favourites button inside the folder field included.
                foreach (var row in categoryRows) SetControlsEnabled(row, useCategoryFolders.IsChecked == true);
                foreach (var box in categoryBoxes.Values) box.IsEnabled = useCategoryFolders.IsChecked == true;
            }
            useCategoryFolders.Checked += (_, _) => ShowCategoryFoldersEnabled();
            useCategoryFolders.Unchecked += (_, _) => ShowCategoryFoldersEnabled();
            ShowCategoryFoldersEnabled();
            if (customCategoryControls.Count > 0)
            {
                saveTo.Add(SettingsWindow.Section("Custom categories"));
                saveTo.AddRange(customCategoryControls);
            }
            saveTo.Add(SettingsWindow.Section("Favorite folders"));
            saveTo.Add(SettingsWindow.Hint("Named folders offered first by the star button beside every folder box. A name left empty becomes the folder's own name."));
            saveTo.Add(favoritesPanel);
            saveTo.Add(addFavorite);
            window.AddPage("Save to", "\uE8B7", saveTo.ToArray());
            window.AddPage("Downloads", "\uE896",
                SettingsWindow.Section("When a download finishes"), completionDialog, notify, sound, postDownload);
            window.AddPage("Connection", "\uE774",
                maxConcurrent, connections, speedLimit,
                SettingsWindow.Hint("Leave the speed limit empty for unlimited."),
                SettingsWindow.Section("Per-site limits"), profilesBox);
            window.AddPage("Proxy", "\uE968", proxyMode, manualProxy);
            window.AddPage("Site logins", "\uE8D7", loginHelp, loginEmpty, loginPanel, addLogin);
            // The update source (channel, manifest URL, signing key) is set by the installer and is
            // not user-editable; the hidden boxes only carry the saved values through Save.
            window.AddPage("Updates", "\uE895", SettingsWindow.Section("Updates"), autoUpdate, updateInterval);
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

            var webUi = new WebUiOptionsPage(_remote) { PageIndex = nextPage + (extraction is null ? 0 : 1) };
            // UI.R31: a screen, not Connection's globe.
            window.AddPage("Web UI", "\uE7F4", webUi.Controls);

            window.Closed += (_, _) =>
            {
                foreach (var (page, _) in accountPages) page.Close();
                extraction?.Close();
                webUi.Close();
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
                if (webUi.Validate() is { } webUiProblem)
                    return (webUiProblem, webUi.PageIndex);
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
                settings.CloseWindowCompletely = closeCompletely.IsChecked == true;
                SaveProviderOpens(providerOpens);
                settings.Theme = themeValues[Math.Max(0, themeBox.SelectedIndex)];
                settings.UseCategoryFolders = useCategoryFolders.IsChecked == true;
                settings.ClipboardMonitoring = clipboard.IsChecked == true;
                settings.BrowserIntegrationEnabled = integration.IsChecked == true;
                settings.NotifyOnCompletion = notify.IsChecked == true;
                settings.PlayCompletionSound = sound.IsChecked == true;
                settings.ShowCompletionDialog = completionDialog.IsChecked == true;
                settings.AutoResumeUnfinishedOnStartup = autoResume.IsChecked == true;
                settings.PostDownloadCommand = string.IsNullOrWhiteSpace(postDownload.Text) ? null : postDownload.Text.Trim();
                settings.AutoCheckUpdates = autoUpdate.IsChecked == true;
                settings.UpdateCheckIntervalHours = checkInterval;
                var edited = favoriteRows.Where(entry => !string.IsNullOrWhiteSpace(entry.Folder.Text))
                    .Select(entry => new FavoriteFolderDto { Name = entry.Name.Text.Trim(), Path = entry.Folder.Text.Trim() }).ToList();
                var savedMeanwhile = (_settings?.FavoriteFolders ?? new()).Where(favorite =>
                    !favoritesAtOpen.Contains(favorite.Path, StringComparer.OrdinalIgnoreCase) &&
                    !edited.Any(row => string.Equals(row.Path, favorite.Path, StringComparison.OrdinalIgnoreCase)));
                settings.FavoriteFolders = edited.Concat(savedMeanwhile).ToList();
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
                // The Theme saved here is the user's latest choice, so a View > Dark Mode one still kept is dropped.
                if (_uiState.DarkMode is not null)
                {
                    _uiState.DarkMode = null;
                    SaveUiState();
                }
                AppTheme.Use(update.Settings.Theme);
                UpdateLimitButtons();
                // UI.B6: a new default folder moves the category folders that sat under the old one. Show what the
                // Host saved, or the boxes keep the old paths and the next OK writes them back.
                directory.Text = update.Settings.DefaultDownloadDirectory;
                foreach (var (category, box) in categoryBoxes)
                    box.Text = update.Settings.CategoryFolders.TryGetValue(category, out var saved) ? saved : string.Empty;
                var extractionSaveProblem = extraction is null ? null : await extraction.SaveAsync();
                var webUiSaveProblem = await webUi.SaveAsync();
                await RefreshAsync();
                // Neither problem is dropped: both are said, and the window shows the page of the first.
                if (webUiSaveProblem is not null && extractionSaveProblem is not null)
                    return ($"{extractionSaveProblem} {webUiSaveProblem}", extractionPageIndex);
                if (webUiSaveProblem is not null) return (webUiSaveProblem, webUi.PageIndex);
                return extractionSaveProblem is null ? (null, 0) : (extractionSaveProblem, extractionPageIndex);
            };

            WebUiPageReady(window, webUi.PageIndex);
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
        if (await DialogGuard.ShowAsync(dialog) != ContentDialogResult.Primary) return;

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

    /// <summary>Saves the favorites list onto the Host's latest settings, so nothing else a client changed is undone.</summary>
    private async Task<string?> SaveFavoritesAsync(List<FavoriteFolderDto> favorites)
    {
        var current = await _connection.RefreshAsync();
        var updated = CloneSettings(current.Settings);
        updated.FavoriteFolders = favorites;
        var result = (await _connection.UpdateSettingsAsync(updated)).GetPayload<SettingsResult>();
        if (!result.Ok) return result.Error ?? "Couldn't save the favorites.";
        _settings = result.Settings;
        UpdateLimitButtons();
        return null;
    }

    private static SettingsDto CloneSettings(SettingsDto source)
        => new()
        {
            DefaultDownloadDirectory = source.DefaultDownloadDirectory,
            MaxConcurrentDownloads = source.MaxConcurrentDownloads,
            DefaultConnections = source.DefaultConnections,
            UseCategoryFolders = source.UseCategoryFolders,
            CloseWindowCompletely = source.CloseWindowCompletely,
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
            RecentDestinationDirectories = new List<string>(source.RecentDestinationDirectories),
            FavoriteFolders = source.FavoriteFolders.Select(favorite => new FavoriteFolderDto { Name = favorite.Name, Path = favorite.Path }).ToList(),
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

            UpdateLimitButtons();
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

    private async void DeleteButton_Click(object sender, RoutedEventArgs e) => await ConfirmDeleteAsync(deleteFromDisk: false);

    /// <summary>True while a ContentDialog is open on this window, whether or not it came through ShowDialogAsync.</summary>
    private bool IsContentDialogOpen() => DialogGuard.IsOpen(Content?.XamlRoot);

    /// <summary>Shows <paramref name="dialog"/>, or returns null without showing it while another ContentDialog is open
    /// (UI.B8). See <see cref="DialogGuard"/>.</summary>
    private static Task<ContentDialogResult?> ShowDialogAsync(ContentDialog dialog) => DialogGuard.ShowAsync(dialog);

    /// <summary>Asks before deleting the selection. <paramref name="deleteFromDisk"/> is the box's starting state:
    /// only Shift+Delete starts it ticked, so a plain Delete never removes finished files without a click.</summary>
    private async Task ConfirmDeleteAsync(bool deleteFromDisk, bool fromContextMenu = false)
    {
        // The toolbar can still be invoked behind an open dialog (UI Automation, a screen reader), so a Delete then
        // does nothing (UI.B8); ShowDialogAsync below refuses it too.
        if (IsContentDialogOpen()) return;
        var selection = DeleteRules.Targets(SelectedJobs, RecordDownload, fromContextMenu);
        if (selection.Count == 0)
        {
            if (FirstLocalRow is null) await RemoveSideRowsAsync();
            return;
        }

        var deleteFiles = new CheckBox
        {
            Content = "Delete from disk: the downloaded files",
            IsChecked = deleteFromDisk,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(deleteFiles, "DeleteFromDiskBox");
        // Unfinished downloads only have partial data, which is removed anyway (UI.B19), so the box is about finished files.
        var anyCompleted = selection.Any(job => job.State == "completed" && !job.FileRemovedAfterExtraction);
        var anyUnfinished = selection.Any(job => job.State != "completed");

        // Their files went on purpose after extraction. A box offering to delete them would do nothing, and a new file by that name isn't theirs.
        var noFilesLeft = selection.All(job => job.FileRemovedAfterExtraction);
        var question = selection.Count == 1
            ? $"Remove '{selection[0].FileName}' from the list?"
            : $"Remove {selection.Count} downloads from the list?";
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = noFilesLeft ? question + " " + JobNotes.NothingToDelete(selection.Count)
                : anyCompleted ? (anyUnfinished ? question + " " + DeleteRules.MixedNote : question)
                : question + " Partial data is always removed.",
            TextWrapping = TextWrapping.Wrap,
        });
        if (anyCompleted) panel.Children.Add(deleteFiles);
        else deleteFiles.IsChecked = false;

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

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var includeFiles = deleteFiles.IsChecked == true;
        if (selection.Count > 1)
        {
            // One Host operation per kind: every unfinished file stops together and none restarts while others are
            // removed. The unfinished ones go first, with their partial data; then the finished ones as the box says.
            StatusText.Text = $"Deleting {selection.Count} downloads…";
            var (withData, keepFile) = DeleteRules.Split(selection, job => job.State == "completed", includeFiles);
            var errors = new List<string>();
            try
            {
                foreach (var (part, deleteFile) in new[] { (withData, true), (keepFile, false) })
                {
                    if (part.Count == 0) continue;
                    var result = (await _connection.RemoveManyAsync(part.Select(job => job.Id).ToList(), deleteFile))
                        .GetPayload<JobActionResponse>();
                    if (!result.Ok) errors.Add(result.Error ?? "Some downloads could not be removed.");
                }
                await RefreshAsync();
                if (errors.Count > 0) await ShowMessageAsync("Delete incomplete", string.Join(Environment.NewLine, errors));
            }
            catch (Exception ex) { await RefreshAsync(); await ShowMessageAsync("Delete incomplete", ex.Message); }
            return;
        }
        var single = DeleteRules.DeleteFileFor(selection[0].State == "completed", includeFiles);
        await RunBulkActionAsync(
            selection,
            id => _connection.RemoveAsync(id, deleteFile: single),
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
            var connection = _connection;
            var snapshot = await connection.RefreshAsync();
            // A reply from the Host shown before a switch would put its settings and path style on the new one.
            if (connection != _connection) return;
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
        if (SayIfOnAnotherServer()) return;
        if (job.FileRemovedAfterExtraction)
        {
            ShowPassingStatus(JobNotes.NoFile(job.StatusNote));
            return;
        }

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
        if (SayIfOnAnotherServer()) return;
        if (job.FileRemovedAfterExtraction)
        {
            ShowPassingStatus(JobNotes.NoFile(job.StatusNote));
            return;
        }

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

    /// <summary>A message that may be a long list, such as every link an add refused, so it scrolls rather than being cut off.</summary>
    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = title,
            Content = new ScrollViewer
            {
                Padding = InboundTheme.DialogScrollPadding,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                MaxHeight = Math.Max(160, Math.Min(480, RootGrid.ActualHeight - 240)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            CloseButtonText = "OK",
        };

        try
        {
            // Refused while another dialog is open: the message then goes to the status bar, as when ShowAsync fails.
            if (await ShowDialogAsync(dialog) is null) ShowPassingStatus($"{title}: {message}");
        }
        catch (Exception)
        {
            ShowPassingStatus($"{title}: {message}");
        }
    }
}
