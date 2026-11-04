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
    private MenuFlyoutSubItem? _addToQueueMenu;
    private SettingsDto? _settings;
    private HostStatusDto? _status;
    private bool _settingsDialogOpen;
    private bool _pendingSettingsOpen;
    private bool _completionDialogOpen;
    private readonly Queue<JobDto> _completionDialogs = new();
    private string _category = "all";
    private string _search = string.Empty;
    private string _sortKey = "added";
    private bool _sortAscending;
    private bool _toolbarLarge = true;
    private bool _hideCategories;
    private UiState _uiState = new();

    public MainWindow()
    {
        UiTrace.Write("MainWindow ctor begin");
        _uiState = UiStateStore.Load();
        InitializeComponent();
        UiTrace.Write("MainWindow InitializeComponent complete");
        RootGrid.RequestedTheme = ElementTheme.Dark;
        JobsList.ItemsSource = _visibleJobs;
        BuildJobsContextMenu();
        UiTrace.Write("MainWindow collections initialized");

        try
        {
            var width = Math.Clamp(_uiState.WindowWidth, 900, 8000);
            var height = Math.Clamp(_uiState.WindowHeight, 560, 8000);
            AppWindow.Resize(new SizeInt32(width, height));
            var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }

            ApplyTitleBarTheme(dark: true);
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
        _connection.ConnectionChanged += (connected, error) =>
            DispatcherQueue.TryEnqueue(() => ApplyConnection(connected, error));
        _connection.Start();
        UiTrace.Write("MainWindow connection started");

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
                saved.Tag is not ("grabber" or "queues"))
            {
                CategoryList.SelectedItem = saved;
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
        Activate();
    }

    public void RequestSettingsPage()
    {
        _pendingSettingsOpen = true;
        if (_settings is not null)
        {
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
            var size = (window ?? AppWindow).Size;
            UiStateStore.Save(new UiState
            {
                WindowWidth = size.Width,
                WindowHeight = size.Height,
                Category = _category,
                SortKey = _sortKey,
                SortAscending = _sortAscending,
                ToolbarLarge = _toolbarLarge,
                HideCategories = _hideCategories,
            });
        }
        catch (Exception ex)
        {
            UiTrace.Write($"UI state save failed: {ex.Message}");
        }
    }

    private void ApplySnapshot(SnapshotDto snapshot)
    {
        _settings = snapshot.Settings;
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
            }
        }
        else
        {
            if (!_jobsById.TryGetValue(job.Id, out var vm))
            {
                vm = new JobViewModel();
                _jobsById[job.Id] = vm;
            }

            vm.Update(job);
            var isVisible = _visibleJobs.Contains(vm);
            if (Matches(vm))
            {
                if (!isVisible)
                {
                    _visibleJobs.Insert(0, vm);
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

        if (changeType == "completed" && _settings?.ShowCompletionDialog == true)
        {
            _completionDialogs.Enqueue(job);
            _ = ShowNextCompletionDialogAsync();
        }
    }

    private async Task ShowNextCompletionDialogAsync()
    {
        if (_completionDialogOpen)
        {
            return;
        }

        _completionDialogOpen = true;
        try
        {
            while (_completionDialogs.Count > 0)
            {
                var job = _completionDialogs.Dequeue();
                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = "Download complete",
                    Content = new TextBlock
                    {
                        Text = $"{job.FileName}\n{job.FinalFilePath}",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = "Open file",
                    SecondaryButtonText = "Open folder",
                    CloseButtonText = "Close",
                    DefaultButton = ContentDialogButton.Primary,
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary && File.Exists(job.FinalFilePath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(job.FinalFilePath) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        await ShowMessageAsync("Could not open file", ex.Message);
                    }
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    try
                    {
                        var argument = File.Exists(job.FinalFilePath)
                            ? $"/select,\"{job.FinalFilePath}\""
                            : $"\"{Path.GetDirectoryName(job.FinalFilePath)}\"";
                        Process.Start(new ProcessStartInfo("explorer.exe", argument)
                        {
                            UseShellExecute = true,
                        });
                    }
                    catch (Exception ex)
                    {
                        await ShowMessageAsync("Could not open folder", ex.Message);
                    }
                }
            }
        }
        finally
        {
            _completionDialogOpen = false;
        }
    }

    private void ApplyConnection(bool connected, string? error)
    {
        ConnectionText.Text = connected
            ? "Host connected"
            : $"Host unavailable{(error is null ? string.Empty : $": {error}")}";
        if (!connected)
        {
            EmptyText.Visibility = Visibility.Visible;
        }

        UpdateButtons();
    }

    private JobViewModel? SelectedJob => JobsList.SelectedItem as JobViewModel;

    private List<JobViewModel> SelectedJobs =>
        JobsList.SelectedItems.OfType<JobViewModel>().ToList();

    private void UpdateButtons()
    {
        var connected = _connection?.IsConnected ?? false;
        var selection = SelectedJobs;
        AddButton.IsEnabled = connected;
        PauseButton.IsEnabled = connected && selection.Any(job => job.CanPause);
        ResumeButton.IsEnabled = connected && selection.Any(job => job.CanResume);
        DeleteButton.IsEnabled = connected && selection.Count > 0;
        StopAllButton.IsEnabled = connected && _jobsById.Values.Any(j => j.CanPause);
        DeleteCompletedButton.IsEnabled = connected && _jobsById.Values.Any(j => j.State == "completed");
        StartQueueButton.IsEnabled = connected && _settings is not null;
        StopQueueButton.IsEnabled = connected && _settings is not null;
    }

    private void UpdateStatusText()
    {
        var active = _jobsById.Values.Count(j => j.State is "connecting" or "downloading" or "retryWaiting" or "verifying");
        var queued = _jobsById.Values.Count(j => j.State == "queued");
        var host = _status?.HostVersion;
        StatusText.Text = $"{_jobsById.Count} downloads Â· {active} active Â· {queued} queued" +
                          (host is null ? string.Empty : $" Â· host {host}")
                          + (_status?.BrowserIntegration is { Length: > 0 } integration
                              ? $" | browser {integration}"
                              : string.Empty);
        var selected = SelectedJobs.Count;
        if (selected > 1)
        {
            StatusText.Text += $"  ({selected} selected)";
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
                job.DetailText.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
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
            "compressed" => FileCategories.For(job.FileName) == "compressed",
            "documents" => FileCategories.For(job.FileName) == "documents",
            "music" => FileCategories.For(job.FileName) == "music",
            "programs" => FileCategories.For(job.FileName) == "programs",
            "video" => FileCategories.For(job.FileName) == "video",
            _ => true,
        };
    }

    private void RebuildVisible(Guid? selectedId = null)
    {
        selectedId ??= SelectedJob?.Id;
        _visibleJobs.Clear();

        var filtered = _jobsById.Values.Where(Matches);
        var ordered = _sortKey switch
        {
            "name" => _sortAscending ? filtered.OrderBy(j => j.FileName) : filtered.OrderByDescending(j => j.FileName),
            "size" => _sortAscending ? filtered.OrderBy(j => j.TotalSize) : filtered.OrderByDescending(j => j.TotalSize),
            "status" => _sortAscending ? filtered.OrderBy(j => j.State) : filtered.OrderByDescending(j => j.State),
            "rate" => _sortAscending ? filtered.OrderBy(j => j.BytesPerSecond) : filtered.OrderByDescending(j => j.BytesPerSecond),
            "lastTry" => _sortAscending ? filtered.OrderBy(j => j.LastUpdated) : filtered.OrderByDescending(j => j.LastUpdated),
            _ => _sortAscending ? filtered.OrderBy(j => j.CreatedAt) : filtered.OrderByDescending(j => j.CreatedAt),
        };

        foreach (var job in ordered)
        {
            _visibleJobs.Add(job);
        }

        if (selectedId is not null && _jobsById.TryGetValue(selectedId.Value, out var selected))
        {
            JobsList.SelectedItem = selected;
        }

        EmptyText.Visibility = _visibleJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void JobsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        UpdateStatusText();
    }

    private async void JobsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && SelectedJobs.Count > 0)
        {
            e.Handled = true;
            DeleteButton_Click(sender, e);
        }
        else if (e.Key == Windows.System.VirtualKey.Enter && SelectedJob is { } job)
        {
            e.Handled = true;
            await OpenJobAsync(job);
        }
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is ListBoxItem { Tag: string tag })
        {
            if (tag == "grabber")
            {
                _ = ShowGrabberProjectsAsync();
                return;
            }

            if (tag == "queues")
            {
                _ = ShowQueuesAsync();
                return;
            }

            _category = tag;
            RebuildVisible();
        }
    }

    private async void JobsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SelectedJob is { } job)
        {
            await OpenJobAsync(job);
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
        flyout.Items.Add(CreateContextItem("Open with...", MenuOpenWith_Click));
        flyout.Items.Add(CreateContextItem("Open folder", MenuOpenFolder_Click));
        var moveRename = CreateContextItem("Move/Rename...", MenuMoveRename_Click);
        moveRename.KeyboardAccelerators.Add(new KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.M,
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
        });
        flyout.Items.Add(moveRename);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateContextItem("Redownload", MenuRedownload_Click));
        flyout.Items.Add(CreateContextItem("Resume", MenuResume_Click));
        flyout.Items.Add(CreateContextItem("Stop", MenuPause_Click));
        flyout.Items.Add(CreateContextItem("Refresh download address...", MenuRefreshUrl_Click));
        flyout.Items.Add(CreateContextItem("Remove", MenuRemove_Click));
        _addToQueueMenu = new MenuFlyoutSubItem { Text = "Add to queue" };
        flyout.Items.Add(_addToQueueMenu);
        flyout.Items.Add(CreateContextItem("Delete from queue", MenuDeleteFromQueue_Click));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateContextItem("Progress details...", MenuProgressDetails_Click));
        flyout.Items.Add(CreateContextItem("Properties", MenuProperties_Click));
        JobsList.ContextFlyout = flyout;
    }

    private static MenuFlyoutItem CreateContextItem(string text, RoutedEventHandler handler)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += handler;
        return item;
    }

    private void JobsContextMenu_Opening(object? sender, object e)
    {
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
        if (SelectedJob is { } job && sender is MenuFlyoutItem { Tag: int queueId })
        {
            await RunActionAsync(() => _connection.MoveToQueueAsync(job.Id, queueId));
        }
    }

    private async void MenuDeleteFromQueue_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is { } job && _settings is { } settings)
        {
            await RunActionAsync(() => _connection.MoveToQueueAsync(job.Id, settings.DefaultQueueId));
        }
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

        var pathBox = new TextBox
        {
            Header = "New full path",
            Text = job.FinalFilePath,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Move/Rename",
            Content = pathBox,
            PrimaryButtonText = "Move",
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
            var reply = await _connection.MoveJobAsync(job.Id, pathBox.Text.Trim());
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

        try
        {
            var reply = await _connection.AddAsync(new AddJobRequest
            {
                Url = job.Url,
                DestinationDirectory = job.Destination,
                FileName = job.FileName,
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

        var input = new TextBox
        {
            Text = job.Url,
            Header = "New download address",
            AcceptsReturn = false,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
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

        await RunActionAsync(() => _connection.RefreshUrlAsync(job.Id, input.Text.Trim()));
    }

    private async void MenuProgressDetails_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is not { } selected)
        {
            return;
        }

        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var overall = new ProgressBar { Maximum = 100, Height = 8 };
        var segmentsPanel = new StackPanel { Spacing = 4 };
        var status = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150)),
        };
        var refresh = new Button { Content = "Refresh" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(summary);
        panel.Children.Add(overall);
        panel.Children.Add(refresh);
        panel.Children.Add(segmentsPanel);
        panel.Children.Add(status);

        async Task LoadAsync()
        {
            try
            {
                var snapshot = await _connection.RefreshAsync();
                var job = snapshot.Jobs.FirstOrDefault(candidate => candidate.Id == selected.Id);
                if (job is null)
                {
                    summary.Text = "This download is no longer in the list.";
                    segmentsPanel.Children.Clear();
                    return;
                }

                var received = job.ReceivedBytes;
                var total = job.TotalBytes;
                var percent = total is > 0 ? received * 100.0 / total.Value : 0;
                overall.Value = Math.Clamp(percent, 0, 100);
                summary.Text =
                    $"File: {job.FileName}\nState: {job.State}\n" +
                    $"Bytes: {received:N0}" + (total is > 0 ? $" / {total.Value:N0} ({percent:0.0}%)" : string.Empty) +
                    $"\nConnections: {job.ActiveConnections}/{job.RequestedConnections}\n" +
                    $"Speed: {job.BytesPerSecond:0} B/s\nETA: {job.EstimatedRemaining}\n" +
                    $"Part: {job.PartFilePath}";

                var reply = await _connection.GetSegmentsAsync(job.Id);
                var segments = reply.GetPayload<List<SegmentDto>>();
                segmentsPanel.Children.Clear();
                if (segments.Count == 0)
                {
                    status.Text = job.State == "completed"
                        ? "Completed; the checkpoint was removed after the final rename."
                        : "No segment checkpoint yet (the transfer may still be connecting).";
                    return;
                }

                status.Text = $"{segments.Count} segment{(segments.Count == 1 ? "" : "s")} from the engine checkpoint.";
                foreach (var segment in segments)
                {
                    var length = Math.Max(1, segment.End - segment.Start + 1);
                    var row = new StackPanel { Spacing = 1 };
                    row.Children.Add(new TextBlock
                    {
                        Text = $"Bytes {segment.Start:N0}â€“{segment.End:N0}  " +
                               $"{segment.Completed:N0}/{length:N0}",
                        FontSize = 11,
                    });
                    row.Children.Add(new ProgressBar
                    {
                        Maximum = 100,
                        Height = 5,
                        Value = Math.Clamp(segment.Fraction * 100, 0, 100),
                    });
                    segmentsPanel.Children.Add(row);
                }
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
        }

        refresh.Click += async (_, _) => await LoadAsync();
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Download progress",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        await LoadAsync();
        await dialog.ShowAsync();
    }

    private async void MenuProperties_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        var info = new TextBlock
        {
            Text = $"File: {job.FileName}\nQueue: {job.QueueName}\nState: {job.StateText}\n" +
                   $"Size: {job.SizeText}\nSource: {job.SourceText}\nURL: {job.Url}\n" +
                   $"Destination: {job.Destination}\nFinal path: {job.FinalFilePath}\n" +
                   $"Last try: {job.LastTryText}",
            TextWrapping = TextWrapping.Wrap,
        };
        var descriptionBox = new TextBox
        {
            Header = "Description",
            Text = job.RawDescription,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Height = 80,
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(info);
        panel.Children.Add(descriptionBox);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Download properties",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var reply = await _connection.UpdateJobAsync(job.Id, descriptionBox.Text);
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
            Title = "Import downloads?",
            Content = new TextBlock
            {
                Text = $"Add {entries.Count} downloads from this file?",
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

        var accepted = 0;
        var failed = 0;
        string? firstError = null;
        foreach (var entry in entries)
        {
            if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                failed++;
                firstError ??= $"Invalid URL: {entry.Url}";
                continue;
            }

            try
            {
                var reply = await _connection.AddAsync(new AddJobRequest
                {
                    Url = entry.Url,
                    FileName = entry.FileName,
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
        await ShowMessageAsync("Import download list",
            $"Added {accepted} of {entries.Count} downloads" +
            (failed > 0 ? $"; {failed} failed. {firstError}" : "."));
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
            button.Width = large ? 86 : 34;
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
        ApplyTitleBarTheme(dark);
    }

    private void ApplyTitleBarTheme(bool dark)
    {
        var titleBar = AppWindow.TitleBar;
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var background = dark ? Windows.UI.Color.FromArgb(255, 27, 27, 27) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        var foreground = dark ? Windows.UI.Color.FromArgb(255, 235, 235, 235) : Windows.UI.Color.FromArgb(255, 30, 30, 30);
        var hover = dark ? Windows.UI.Color.FromArgb(255, 45, 45, 48) : Windows.UI.Color.FromArgb(255, 225, 225, 225);
        var pressed = dark ? Windows.UI.Color.FromArgb(255, 60, 60, 63) : Windows.UI.Color.FromArgb(255, 210, 210, 210);
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = foreground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonInactiveForegroundColor = foreground;
    }

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
        await ShowMessageAsync("Download Manager",
            "Download Manager 0.1.0\n\n" +
            "Private download manager built around a durable background host, a replaceable WinUI 3 frontend, " +
            "and a Chrome Manifest V3 bridge.\n\n" +
            "Reference behavior studied from another download manager 6.43.12.3. This project is not affiliated " +
            "with the vendor and does not include reference app code, assets, or licensing.");
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

        var urlBox = new ComboBox
        {
            Header = "URL",
            PlaceholderText = "https://example.com/file.zip",
            IsEditable = true,
        };
        foreach (var recent in _settings.RecentUrls.Take(20))
        {
            urlBox.Items.Add(recent);
        }
        var nameBox = new TextBox
        {
            Header = "File name (optional)",
            PlaceholderText = "Resolved from the URL or server",
        };
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

        var conflictBox = new ComboBox { Header = "If the file exists" };
        conflictBox.Items.Add("Rename the new file");
        conflictBox.Items.Add("Overwrite the existing file");
        conflictBox.Items.Add("Fail the new download");
        conflictBox.SelectedIndex = 0;

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(urlBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(destinationBox);
        panel.Children.Add(startMode);
        panel.Children.Add(queueBox);
        panel.Children.Add(conflictBox);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Add download",
            Content = panel,
            PrimaryButtonText = "Download",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var url = urlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            await ShowMessageAsync("Invalid URL", "Enter an absolute http or https URL.");
            return;
        }

        var existing = FindExistingJob(url);
        if (existing is not null)
        {
            var duplicateDialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
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
            DestinationDirectory = string.IsNullOrWhiteSpace(destinationBox.Text) ? null : destinationBox.Text.Trim(),
            StartMode = startMode.SelectedIndex == 1 ? "later" : "now",
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

        var patternBox = new TextBox
        {
            Header = "URL pattern",
            PlaceholderText = "https://example.com/file[001-010].zip",
            TextWrapping = TextWrapping.Wrap,
        };
        var hint = new TextBlock
        {
            Text = "Ranges: [1-10], [001-010], [a-z], or lists [1,3,5]. Multiple ranges expand together.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150)),
            FontSize = 12,
        };
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
        panel.Children.Add(patternBox);
        panel.Children.Add(hint);
        panel.Children.Add(destinationBox);
        panel.Children.Add(startMode);
        panel.Children.Add(queueBox);
        panel.Children.Add(conflictBox);
        panel.Children.Add(skipDuplicates);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Add batch download",
            Content = panel,
            PrimaryButtonText = "Add batch",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        IReadOnlyList<string> urls;
        try
        {
            urls = BatchUrlExpander.Expand(patternBox.Text);
        }
        catch (FormatException ex)
        {
            await ShowMessageAsync("Invalid batch pattern", ex.Message);
            return;
        }

        urls = urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (urls.Count == 0)
        {
            await ShowMessageAsync("Batch download", "The pattern produced no URLs.");
            return;
        }

        if (urls.Count > 25)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Add many downloads?",
                Content = new TextBlock
                {
                    Text = $"This pattern expands to {urls.Count} downloads. Add them all?",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Add all",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        var queueId = _settings.Queues.OrderBy(q => q.Order)
            .ElementAtOrDefault(Math.Max(0, queueBox.SelectedIndex))?.Id ?? _settings.DefaultQueueId;
        var destination = string.IsNullOrWhiteSpace(destinationBox.Text)
            ? null
            : destinationBox.Text.Trim();
        var start = startMode.SelectedIndex == 1 ? "later" : "now";
        var accepted = 0;
        var duplicates = 0;
        var skipped = 0;
        var failed = 0;
        string? firstError = null;

        foreach (var url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                failed++;
                firstError ??= $"Invalid URL: {url}";
                continue;
            }

            if (skipDuplicates.IsChecked == true && FindExistingJob(url) is not null)
            {
                skipped++;
                continue;
            }

            try
            {
                var reply = await _connection.AddAsync(new AddJobRequest
                {
                    Url = url,
                    DestinationDirectory = destination,
                    StartMode = start,
                    ConflictPolicy = conflictBox.SelectedIndex switch
                    {
                        1 => "overwrite",
                        2 => "fail",
                        _ => "rename",
                    },
                    Source = "batch",
                    QueueId = queueId,
                });
                var add = reply.GetPayload<AddJobResponse>();
                if (add.Accepted)
                {
                    accepted++;
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

        await RefreshAsync();
        var summary = $"Added {accepted} of {urls.Count} downloads" +
                      (duplicates > 0 ? $" ({duplicates} already queued)" : string.Empty) +
                      (skipped > 0 ? $"; {skipped} skipped as already listed" : string.Empty) +
                      (failed > 0 ? $"; {failed} failed. {firstError}" : ".");
        await ShowMessageAsync("Batch download", summary);
    }

    private async void MenuGrabber_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        var urlBox = new TextBox
        {
            Header = "Start URL",
            PlaceholderText = "https://example.com/docs/",
        };
        var destinationBox = new TextBox
        {
            Header = "Mirror folder",
            Text = Path.Combine(_settings.DefaultDownloadDirectory, "mirror"),
        };
        var nameBox = new TextBox
        {
            Header = "Project name (optional)",
            PlaceholderText = "Docs mirror",
        };
        var saveProject = new CheckBox { Content = "Save as a reusable project" };
        var depthBox = new TextBox { Header = "Maximum depth", Text = "2" };
        var pagesBox = new TextBox { Header = "Maximum pages", Text = "50" };
        var extensionsBox = new TextBox
        {
            Header = "File extensions (comma separated, empty for all)",
            PlaceholderText = "zip, pdf, mp3",
        };
        var includeBox = new TextBox
        {
            Header = "Include URL patterns (comma separated, optional)",
            PlaceholderText = "*docs*",
        };
        var excludeBox = new TextBox
        {
            Header = "Exclude URL patterns (comma separated, optional)",
            PlaceholderText = "*/logout*",
        };
        var sameHost = new CheckBox { Content = "Stay on the start host", IsChecked = true };
        var savePages = new CheckBox { Content = "Save HTML pages for offline viewing", IsChecked = true };
        var rewrite = new CheckBox { Content = "Rewrite links to local files", IsChecked = true };
        var robots = new CheckBox { Content = "Respect the site's robots.txt", IsChecked = true };
        var renderJs = new CheckBox
        {
            Content = "Render JavaScript (uses the installed Edge or Chrome, slower)",
            IsChecked = false,
        };
        var startMode = new ComboBox { Header = "Download files" };
        startMode.Items.Add("Start now");
        startMode.Items.Add("Download later");
        startMode.SelectedIndex = 0;
        var queueBox = new ComboBox { Header = "Queue" };
        foreach (var queue in _settings.Queues.OrderBy(q => q.Order))
        {
            queueBox.Items.Add(queue.Name);
        }

        queueBox.SelectedIndex = 0;

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(urlBox);
        panel.Children.Add(destinationBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(saveProject);
        panel.Children.Add(depthBox);
        panel.Children.Add(pagesBox);
        panel.Children.Add(extensionsBox);
        panel.Children.Add(includeBox);
        panel.Children.Add(excludeBox);
        panel.Children.Add(sameHost);
        panel.Children.Add(savePages);
        panel.Children.Add(rewrite);
        panel.Children.Add(robots);
        panel.Children.Add(renderJs);
        panel.Children.Add(startMode);
        panel.Children.Add(queueBox);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Run site grabber",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = "Start",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (!Uri.TryCreate(urlBox.Text.Trim(), UriKind.Absolute, out var startUri) ||
            startUri.Scheme is not ("http" or "https"))
        {
            await ShowMessageAsync("Invalid URL", "Enter an absolute http or https start URL.");
            return;
        }

        if (!int.TryParse(depthBox.Text, out var depth) || depth < 0 || depth > 5 ||
            !int.TryParse(pagesBox.Text, out var maxPages) || maxPages < 1 || maxPages > 500)
        {
            await ShowMessageAsync("Invalid grabber settings", "Depth must be 0-5 and pages 1-500.");
            return;
        }

        var request = new SiteGrabberRequest
        {
            StartUrl = startUri.AbsoluteUri,
            DestinationDirectory = string.IsNullOrWhiteSpace(destinationBox.Text)
                ? null
                : destinationBox.Text.Trim(),
            MaxDepth = depth,
            MaxPages = maxPages,
            SameHostOnly = sameHost.IsChecked == true,
            SavePages = savePages.IsChecked == true,
            RewriteLinks = rewrite.IsChecked == true,
            RespectRobotsTxt = robots.IsChecked == true,
            RenderJavaScript = renderJs.IsChecked == true,
            StartMode = startMode.SelectedIndex == 1 ? "later" : "now",
            QueueId = _settings.Queues.OrderBy(q => q.Order)
                .ElementAtOrDefault(Math.Max(0, queueBox.SelectedIndex))?.Id ?? _settings.DefaultQueueId,
            Extensions = extensionsBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            IncludePatterns = includeBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            ExcludePatterns = excludeBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
        };

        StatusText.Text = "Site grabber is crawling...";
        try
        {
            SiteGrabberResult result;
            if (saveProject.IsChecked == true)
            {
                var createdReply = await _connection.CreateGrabberProjectAsync(new GrabberProjectCreateRequest
                {
                    Name = nameBox.Text.Trim(),
                    Request = request,
                });
                var created = createdReply.GetPayload<GrabberProjectResult>();
                if (!created.Ok || created.Project is null)
                {
                    await ShowMessageAsync("Site grabber", created.Error ?? "Could not create the project.");
                    return;
                }

                StatusText.Text = $"Running grabber project '{created.Project.Name}'...";
                var runReply = await _connection.RunGrabberProjectAsync(created.Project.Id);
                var run = runReply.GetPayload<GrabberProjectResult>();
                if (!run.Ok || run.Project is null)
                {
                    await ShowMessageAsync("Site grabber", run.Error ?? "The crawl failed.");
                    return;
                }

                result = run.Run ?? new SiteGrabberResult
                {
                    Ok = true,
                    PagesCrawled = run.Project.PagesCrawled,
                    FilesQueued = run.Project.FilesQueued,
                    FilesSkipped = run.Project.FilesSkipped,
                };
            }
            else
            {
                var reply = await _connection.RunGrabberAsync(request);
                result = reply.GetPayload<SiteGrabberResult>();
            }

            if (!result.Ok)
            {
                await ShowMessageAsync("Site grabber", result.Error ?? "The crawl failed.");
                return;
            }

            await RefreshAsync();
            var summary = $"Crawled {result.PagesCrawled} pages; queued {result.FilesQueued} files; " +
                          $"skipped {result.FilesSkipped}; blocked by robots.txt {result.RobotsBlocked}; " +
                          $"rendered {result.PagesRendered}.";
            if (saveProject.IsChecked == true)
            {
                summary += "\n\nThe project was saved under Grabber Projects.";
            }

            if (result.Errors.Count > 0)
            {
                summary += $"\n\nFirst warning: {result.Errors[0]}";
            }

            await ShowMessageAsync("Site grabber", summary);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Site grabber", ex.Message);
        }
        finally
        {
            UpdateStatusText();
        }
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
            Title = "Queues",
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        foreach (var queue in _settings.Queues.OrderBy(queue => queue.Order))
        {
            var members = snapshot.Jobs.Count(job => job.QueueId == queue.Id);
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 2 };
            info.Children.Add(new TextBlock
            {
                Text = $"{queue.Name} â€” {(queue.IsRunning ? "running" : "stopped")}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{members} download{(members == 1 ? "" : "s")}; " +
                       $"max {queue.MaxConcurrentDownloads} concurrent" +
                       (queue.QuotaEnabled ? $"; quota {queue.QuotaBytes:N0} bytes" : string.Empty),
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150)),
            });
            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            var start = new Button { Content = "Start", IsEnabled = !queue.IsRunning };
            start.Click += (_, _) =>
            {
                action = "start";
                actionQueue = queue.Id;
                dialog.Hide();
            };
            Grid.SetColumn(start, 1);
            row.Children.Add(start);

            var stop = new Button { Content = "Stop", IsEnabled = queue.IsRunning };
            stop.Click += (_, _) =>
            {
                action = "stop";
                actionQueue = queue.Id;
                dialog.Hide();
            };
            Grid.SetColumn(stop, 2);
            row.Children.Add(stop);

            panel.Children.Add(row);
            panel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 59, 76)),
            });
        }

        await dialog.ShowAsync();
        if (action.Length == 0)
        {
            return;
        }

        var reply = await _connection.QueueActionAsync(new QueueActionRequest
        {
            QueueId = actionQueue,
            Action = action,
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

    private async void MenuScheduler_Click(object sender, RoutedEventArgs e)
        => await ShowSchedulerAsync();

    private sealed record ScheduleTarget(int? QueueId, Guid? ProjectId, string Label);

    private async Task ShowSchedulerAsync()
    {
        var snapshot = await _connection.RefreshAsync();
        _settings = snapshot.Settings;
        var projects = (await _connection.GetGrabberProjectsAsync())
            .GetPayload<GrabberProjectsResult>().Projects;

        var settings = CloneSettings(_settings);
        var targets = new List<ScheduleTarget>();
        targets.AddRange(settings.Queues
            .OrderBy(queue => queue.Order)
            .Select(queue => new ScheduleTarget(queue.Id, null, $"Queue: {queue.Name}")));
        targets.AddRange(projects.Select(project =>
            new ScheduleTarget(null, project.Id, $"Grabber: {project.Name}")));

        var panel = new StackPanel { Spacing = 8 };
        if (settings.Schedules.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No schedules yet.",
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150)),
            });
        }

        var action = string.Empty;
        Guid? actionId = null;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Scheduler",
            Content = new ScrollViewer { Content = panel, MaxHeight = 540 },
            PrimaryButtonText = "Add schedule",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        foreach (var schedule in settings.Schedules.OrderBy(schedule => schedule.StartTime))
        {
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var target = schedule.GrabberProjectId is { } projectId
                ? projects.FirstOrDefault(project => project.Id == projectId)?.Name ?? "missing grabber"
                : settings.Queues.FirstOrDefault(queue => queue.Id == schedule.QueueId)?.Name ?? "missing queue";
            var info = new StackPanel { Spacing = 2 };
            info.Children.Add(new TextBlock
            {
                Text = $"{schedule.Name} â€” {(schedule.Enabled ? "enabled" : "disabled")}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{schedule.Kind} {schedule.StartTime}" +
                       (schedule.StopTime is { } stop ? $"â€“{stop}" : string.Empty) +
                       $" [{schedule.Days}] â†’ {target}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150)),
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            var toggle = new Button { Content = schedule.Enabled ? "Disable" : "Enable" };
            toggle.Click += (_, _) =>
            {
                action = "toggle";
                actionId = schedule.Id;
                dialog.Hide();
            };
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);

            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) =>
            {
                action = "remove";
                actionId = schedule.Id;
                dialog.Hide();
            };
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);

            panel.Children.Add(row);
            panel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 59, 76)),
            });
        }

        var nameBox = new TextBox { Header = "Name", PlaceholderText = "Nightly queue" };
        var targetBox = new ComboBox { Header = "Target" };
        foreach (var target in targets)
        {
            targetBox.Items.Add(target.Label);
        }

        targetBox.SelectedIndex = targets.Count > 0 ? 0 : -1;
        var startBox = new TextBox { Header = "Start (HH:mm)", Text = "09:00" };
        var stopBox = new TextBox { Header = "Stop (HH:mm, optional)" };
        var daysBox = new TextBox
        {
            Header = "Days",
            Text = "Mon,Tue,Wed,Thu,Fri,Sat,Sun",
        };
        var onceBox = new TextBox
        {
            Header = "One-time date (yyyy-MM-dd, optional)",
        };

        panel.Children.Add(new TextBlock { Text = "Add schedule", Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(nameBox);
        panel.Children.Add(targetBox);
        panel.Children.Add(startBox);
        panel.Children.Add(stopBox);
        panel.Children.Add(daysBox);
        panel.Children.Add(onceBox);

        var dialogResult = await dialog.ShowAsync();
        if (actionId is Guid scheduleId && (action == "toggle" || action == "remove"))
        {
            var updated = CloneSettings(settings);
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
        {
            return;
        }

        if (!TimeOnly.TryParse(startBox.Text.Trim(), out var start))
        {
            await ShowMessageAsync("Scheduler", "Start time must be HH:mm.");
            return;
        }

        TimeOnly? stopTime = null;
        if (!string.IsNullOrWhiteSpace(stopBox.Text))
        {
            if (!TimeOnly.TryParse(stopBox.Text.Trim(), out var parsedStop))
            {
                await ShowMessageAsync("Scheduler", "Stop time must be HH:mm.");
                return;
            }

            stopTime = parsedStop;
        }

        DateOnly? once = null;
        var kind = "daily";
        if (!string.IsNullOrWhiteSpace(onceBox.Text))
        {
            if (!DateOnly.TryParse(onceBox.Text.Trim(), out var parsedDate))
            {
                await ShowMessageAsync("Scheduler", "One-time date must be yyyy-MM-dd.");
                return;
            }

            once = parsedDate;
            kind = "oneTime";
        }

        var selectedTarget = targetBox.SelectedIndex >= 0 && targetBox.SelectedIndex < targets.Count
            ? targets[targetBox.SelectedIndex]
            : null;
        if (selectedTarget is null)
        {
            await ShowMessageAsync("Scheduler", "Choose a queue or grabber project.");
            return;
        }

        var added = CloneSettings(settings);
        added.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(nameBox.Text) ? $"Schedule {added.Schedules.Count + 1}" : nameBox.Text.Trim(),
            QueueId = selectedTarget.QueueId ?? 0,
            GrabberProjectId = selectedTarget.ProjectId,
            Enabled = true,
            Kind = kind,
            StartDate = once,
            Days = string.IsNullOrWhiteSpace(daysBox.Text)
                ? "Mon,Tue,Wed,Thu,Fri,Sat,Sun"
                : daysBox.Text.Trim(),
            StartTime = start.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            StopTime = stopTime?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        });

        var addReply = await _connection.UpdateSettingsAsync(added);
        var addResult = addReply.GetPayload<SettingsResult>();
        if (!addResult.Ok)
        {
            await ShowMessageAsync("Scheduler", addResult.Error ?? "The host rejected the schedule.");
            return;
        }

        await ShowSchedulerAsync();
    }

    private async void MenuGrabberProjects_Click(object sender, RoutedEventArgs e)
        => await ShowGrabberProjectsAsync();

    private async Task ShowGrabberProjectsAsync()
    {
        var reply = await _connection.GetGrabberProjectsAsync();
        var result = reply.GetPayload<GrabberProjectsResult>();
        var panel = new StackPanel { Spacing = 8 };
        if (result.Projects.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No grabber projects yet. Use Tasks â†’ Run site grabber and save a project.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150)),
            });
        }

        var action = string.Empty;
        Guid? actionId = null;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Grabber projects",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
            PrimaryButtonText = "New project...",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        foreach (var project in result.Projects)
        {
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 2 };
            info.Children.Add(new TextBlock
            {
                Text = $"{project.Name} â€” {project.Status}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{project.Request.StartUrl}\n{project.PagesCrawled} pages, " +
                       $"{project.FilesQueued} queued, {project.FilesSkipped} skipped",
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150)),
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            var runButton = new Button { Content = "Run", Tag = project.Id };
            runButton.Click += (_, _) =>
            {
                action = "run";
                actionId = project.Id;
                dialog.Hide();
            };
            Grid.SetColumn(runButton, 1);
            row.Children.Add(runButton);

            var removeButton = new Button { Content = "Remove", Tag = project.Id };
            removeButton.Click += (_, _) =>
            {
                action = "remove";
                actionId = project.Id;
                dialog.Hide();
            };
            Grid.SetColumn(removeButton, 2);
            row.Children.Add(removeButton);

            panel.Children.Add(row);
            panel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 59, 76)),
            });
        }

        var dialogResult = await dialog.ShowAsync();
        if (actionId is Guid id)
        {
            if (action == "run")
            {
                StatusText.Text = "Running grabber project...";
                try
                {
                    var runReply = await _connection.RunGrabberProjectAsync(id);
                    var run = runReply.GetPayload<GrabberProjectResult>();
                    if (!run.Ok)
                    {
                        await ShowMessageAsync("Grabber project", run.Error ?? "The crawl failed.");
                    }
                    else
                    {
                        var project = run.Project!;
                        await ShowMessageAsync("Grabber project",
                            $"{project.Name}: {project.Status}\n" +
                            $"{project.PagesCrawled} pages, {project.FilesQueued} queued, " +
                            $"{project.FilesSkipped} skipped.");
                    }
                }
                catch (Exception ex)
                {
                    await ShowMessageAsync("Grabber project", ex.Message);
                }
                finally
                {
                    UpdateStatusText();
                }

                await ShowGrabberProjectsAsync();
            }
            else if (action == "remove")
            {
                var removeReply = await _connection.RemoveGrabberProjectAsync(id);
                var removed = removeReply.GetPayload<GrabberProjectResult>();
                if (!removed.Ok)
                {
                    await ShowMessageAsync("Grabber project", removed.Error ?? "Could not remove the project.");
                }

                await ShowGrabberProjectsAsync();
            }
        }
        else if (dialogResult == ContentDialogResult.Primary)
        {
            MenuGrabber_Click(this, new RoutedEventArgs());
        }
    }

    private JobViewModel? FindExistingJob(string url)
        => _jobsById.Values.FirstOrDefault(job =>
            string.Equals(job.Url, url, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(job.State, "cancelled", StringComparison.OrdinalIgnoreCase));

    private async Task ShowSettingsDialogAsync()
    {
        if (_settingsDialogOpen || _settings is null)
        {
            return;
        }

        _settingsDialogOpen = true;
        try
        {
            var settings = CloneSettings(_settings);
            var directory = new TextBox { Header = "Default download folder", Text = settings.DefaultDownloadDirectory };
            var maxConcurrent = new TextBox { Header = "Maximum simultaneous downloads", Text = settings.MaxConcurrentDownloads.ToString() };
            var connections = new TextBox { Header = "Connections per download (1â€“32)", Text = settings.DefaultConnections.ToString() };
            var speedLimit = new TextBox
            {
                Header = "Global speed limit (KB/s, empty for unlimited)",
                Text = settings.GlobalSpeedLimitBytesPerSecond is > 0
                    ? (settings.GlobalSpeedLimitBytesPerSecond.Value / 1024).ToString()
                    : string.Empty,
            };
            var profilesBox = new TextBox
            {
                Header = "Connection profiles (host-pattern=max-connections per line)",
                Text = string.Join(
                    Environment.NewLine,
                    settings.ConnectionProfiles.Select(profile => $"{profile.Pattern}={profile.MaxConnections}")),
                AcceptsReturn = true,
                Height = 80,
                PlaceholderText = "*.example.com=2",
            };
            var launch = new CheckBox { Content = "Launch Download Manager at sign-in", IsChecked = settings.LaunchAtStartup };
            var clipboard = new CheckBox { Content = "Monitor the clipboard for URLs", IsChecked = settings.ClipboardMonitoring };
            var integration = new CheckBox { Content = "Enable Chrome â†’ Download Manager handoff", IsChecked = settings.BrowserIntegrationEnabled };
            var autoIntercept = new CheckBox { Content = "Automatically take over eligible Chrome downloads", IsChecked = settings.BrowserAutoIntercept };
            var notify = new CheckBox { Content = "Notify when a download completes", IsChecked = settings.NotifyOnCompletion };
            var sound = new CheckBox { Content = "Play a sound when a download completes", IsChecked = settings.PlayCompletionSound };
            var autoResume = new CheckBox { Content = "Resume unfinished downloads after restart", IsChecked = settings.AutoResumeUnfinishedOnStartup };
            var postDownload = new TextBox
            {
                Header = "Run this program after each download (optional)",
                Text = settings.PostDownloadCommand ?? string.Empty,
                PlaceholderText = @"C:\Program Files\Scanner\scan.exe",
            };
            var updateUrl = new TextBox
            {
                Header = "Update manifest URL (HTTPS)",
                Text = settings.UpdateManifestUrl ?? string.Empty,
            };
            var updateKey = new TextBox
            {
                Header = "Update public key (PEM)",
                Text = settings.UpdatePublicKey ?? string.Empty,
                AcceptsReturn = true,
                Height = 70,
            };
            var updateChannel = new TextBox
            {
                Header = "Update channel",
                Text = settings.UpdateChannel,
            };
            var autoUpdate = new CheckBox
            {
                Content = "Automatically check for updates",
                IsChecked = settings.AutoCheckUpdates,
            };
            var updateInterval = new TextBox
            {
                Header = "Update check interval (hours)",
                Text = settings.UpdateCheckIntervalHours.ToString(),
            };

            var (proxyProtocolIndex, proxyHostText) = ProxyAddress.Split(settings.Proxy.Address);
            var proxyMode = new ComboBox
            {
                Header = "Proxy",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new[] { "No proxy", "Use system proxy", "Manual proxy" },
                SelectedIndex = settings.Proxy.Mode switch { "system" => 1, "manual" => 2, _ => 0 },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(proxyMode, "Proxy mode");
            var proxyProtocol = new ComboBox
            {
                Header = "Proxy type",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = ProxyAddress.ProtocolNames,
                SelectedIndex = proxyProtocolIndex,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(proxyProtocol, "Proxy type");
            var proxyAddress = new TextBox
            {
                Header = "Proxy address (host:port)",
                Text = proxyHostText,
                PlaceholderText = "proxy.example.com:8080",
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(proxyAddress, "Proxy address");
            var proxyUser = new TextBox
            {
                Header = "Proxy user name (SOCKS4/4a send it as the user ID)",
                Text = settings.Proxy.Username ?? string.Empty,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(proxyUser, "Proxy user name");
            var proxyPassword = new PasswordBox
            {
                Header = settings.Proxy.HasPassword
                    ? "Proxy password (saved - leave empty to keep it)"
                    : "Proxy password",
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(proxyPassword, "Proxy password");
            var forgetProxyPassword = new CheckBox
            {
                Content = "Forget the saved proxy password",
                Visibility = settings.Proxy.HasPassword ? Visibility.Visible : Visibility.Collapsed,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(forgetProxyPassword, "Forget the saved proxy password");
            forgetProxyPassword.Checked += (_, _) => proxyPassword.IsEnabled = false;
            forgetProxyPassword.Unchecked += (_, _) => proxyPassword.IsEnabled = true;

            var categoryBoxes = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
            foreach (var category in FileCategories.Names)
            {
                categoryBoxes[category] = new TextBox
                {
                    Header = $"{char.ToUpperInvariant(category[0])}{category[1..]} folder (optional)",
                    Text = settings.CategoryFolders.TryGetValue(category, out var folder) ? folder : string.Empty,
                };
            }

            var panel = new StackPanel { Spacing = 9 };
            panel.Children.Add(new TextBlock { Text = "Transfer" });
            panel.Children.Add(directory);
            panel.Children.Add(maxConcurrent);
            panel.Children.Add(connections);
            panel.Children.Add(speedLimit);
            panel.Children.Add(profilesBox);
            panel.Children.Add(new TextBlock { Text = "Behaviour", Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(launch);
            panel.Children.Add(clipboard);
            panel.Children.Add(autoResume);
            panel.Children.Add(postDownload);
            panel.Children.Add(notify);
            panel.Children.Add(sound);
            panel.Children.Add(new TextBlock { Text = "Browser integration", Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(integration);
            panel.Children.Add(autoIntercept);
            panel.Children.Add(new TextBlock { Text = "Proxy", Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(proxyMode);
            panel.Children.Add(proxyProtocol);
            panel.Children.Add(proxyAddress);
            panel.Children.Add(proxyUser);
            panel.Children.Add(proxyPassword);
            panel.Children.Add(forgetProxyPassword);
            panel.Children.Add(new TextBlock { Text = "Category folders", Margin = new Thickness(0, 8, 0, 0) });
            foreach (var box in categoryBoxes.Values)
            {
                panel.Children.Add(box);
            }

            panel.Children.Add(new TextBlock { Text = "Updates", Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(updateUrl);
            panel.Children.Add(updateKey);
            panel.Children.Add(updateChannel);
            panel.Children.Add(autoUpdate);
            panel.Children.Add(updateInterval);

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Options",
                Content = new ScrollViewer { Content = panel, MaxHeight = 540 },
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!int.TryParse(maxConcurrent.Text, out var max) || max < 1 || max > 16)
            {
                await ShowMessageAsync("Invalid setting", "Maximum simultaneous downloads must be between 1 and 16.");
                return;
            }

            if (!int.TryParse(connections.Text, out var connectionsValue) || connectionsValue < 1 || connectionsValue > 32)
            {
                await ShowMessageAsync("Invalid setting", "Connections per download must be between 1 and 32.");
                return;
            }

            long? limit = null;
            if (!string.IsNullOrWhiteSpace(speedLimit.Text))
            {
                if (!long.TryParse(speedLimit.Text, out var kb) || kb <= 0)
                {
                    await ShowMessageAsync("Invalid setting", "Speed limit must be a positive number of KB/s.");
                    return;
                }

                limit = kb * 1024;
            }

            var proxyModeValue = proxyMode.SelectedIndex switch { 1 => "system", 2 => "manual", _ => "none" };
            string? composedProxyAddress = null;
            if (proxyModeValue == "manual")
            {
                composedProxyAddress = ProxyAddress.Compose(proxyProtocol.SelectedIndex, proxyAddress.Text);
                if (composedProxyAddress is null)
                {
                    await ShowMessageAsync("Invalid setting", "Enter the proxy address as host:port (port 1-65535).");
                    return;
                }
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
            settings.MaxConcurrentDownloads = max;
            settings.DefaultConnections = connectionsValue;
            settings.GlobalSpeedLimitBytesPerSecond = limit;
            var profiles = new List<ConnectionProfileDto>();
            foreach (var line in profilesBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var separator = trimmed.LastIndexOf('=');
                if (separator <= 0 ||
                    !int.TryParse(trimmed[(separator + 1)..].Trim(), out var profileLimit) ||
                    profileLimit < 1 || profileLimit > 32)
                {
                    await ShowMessageAsync("Invalid setting",
                        $"Connection profile '{trimmed}' must use pattern=max (1-32).");
                    return;
                }

                profiles.Add(new ConnectionProfileDto
                {
                    Pattern = trimmed[..separator].Trim(),
                    MaxConnections = profileLimit,
                });
            }

            settings.ConnectionProfiles = profiles;
            settings.LaunchAtStartup = launch.IsChecked == true;
            settings.ClipboardMonitoring = clipboard.IsChecked == true;
            settings.BrowserIntegrationEnabled = integration.IsChecked == true;
            settings.BrowserAutoIntercept = autoIntercept.IsChecked == true;
            settings.NotifyOnCompletion = notify.IsChecked == true;
            settings.PlayCompletionSound = sound.IsChecked == true;
            settings.AutoResumeUnfinishedOnStartup = autoResume.IsChecked == true;
            settings.PostDownloadCommand = string.IsNullOrWhiteSpace(postDownload.Text)
                ? null
                : postDownload.Text.Trim();
            settings.UpdateManifestUrl = string.IsNullOrWhiteSpace(updateUrl.Text)
                ? null
                : updateUrl.Text.Trim();
            settings.UpdatePublicKey = string.IsNullOrWhiteSpace(updateKey.Text)
                ? null
                : updateKey.Text.Trim();
            settings.UpdateChannel = string.IsNullOrWhiteSpace(updateChannel.Text)
                ? "stable"
                : updateChannel.Text.Trim();
            settings.AutoCheckUpdates = autoUpdate.IsChecked == true;
            if (!int.TryParse(updateInterval.Text, out var checkInterval) ||
                checkInterval < 1 || checkInterval > 168)
            {
                await ShowMessageAsync("Invalid setting", "Update interval must be 1-168 hours.");
                return;
            }

            settings.UpdateCheckIntervalHours = checkInterval;
            settings.CategoryFolders = categoryBoxes
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value.Text))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Text.Trim(), StringComparer.OrdinalIgnoreCase);

            var reply = await _connection.UpdateSettingsAsync(settings);
            var update = reply.GetPayload<SettingsResult>();
            if (!update.Ok)
            {
                await ShowMessageAsync("Could not save settings", update.Error ?? "The host rejected the settings.");
                return;
            }

            _settings = update.Settings;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Options", ex.Message);
        }
        finally
        {
            _settingsDialogOpen = false;
        }
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
                GrabberProjectId = schedule.GrabberProjectId,
                Enabled = schedule.Enabled,
                Kind = schedule.Kind,
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
            BrowserAutoIntercept = source.BrowserAutoIntercept,
            NotifyOnCompletion = source.NotifyOnCompletion,
            PlayCompletionSound = source.PlayCompletionSound,
            ShowCompletionDialog = source.ShowCompletionDialog,
            AutoResumeUnfinishedOnStartup = source.AutoResumeUnfinishedOnStartup,
            AllowLocalNetwork = source.AllowLocalNetwork,
            PreventSleepWhileDownloading = source.PreventSleepWhileDownloading,
            AllowSystemCompletionActions = source.AllowSystemCompletionActions,
            FfmpegPath = source.FfmpegPath,
            Theme = source.Theme,
            LastDestinationDirectory = source.LastDestinationDirectory,
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

        var queueId = SelectedJob?.QueueId ?? _settings.DefaultQueueId;
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
        var selection = SelectedJobs.Where(job => job.CanPause).ToList();
        if (selection.Count > 0)
        {
            await RunBulkActionAsync(selection, id => _connection.PauseAsync(id), "Stop");
        }
    }

    private async void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        var selection = SelectedJobs.Where(job => job.CanResume).ToList();
        if (selection.Count > 0)
        {
            await RunBulkActionAsync(selection, id => _connection.ResumeAsync(id), "Resume");
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var selection = SelectedJobs;
        if (selection.Count == 0)
        {
            return;
        }

        var deleteFiles = new CheckBox
        {
            Content = "Also delete the downloaded file from disk",
            IsChecked = true,
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = selection.Count == 1
                ? $"Remove '{selection[0].FileName}' from the list? Partial data is always removed."
                : $"Remove {selection.Count} downloads from the list? Partial data is always removed.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(deleteFiles);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
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
            StatusText.Text = $"{title}: {message}";
        }
    }
}
