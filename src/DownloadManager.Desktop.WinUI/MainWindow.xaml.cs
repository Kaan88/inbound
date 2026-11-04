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
    private string _category = "all";
    private string _search = string.Empty;
    private string _sortKey = "added";
    private bool _sortAscending;
    private bool _toolbarLarge = true;

    public MainWindow()
    {
        UiTrace.Write("MainWindow ctor begin");
        InitializeComponent();
        UiTrace.Write("MainWindow InitializeComponent complete");
        RootGrid.RequestedTheme = ElementTheme.Dark;
        JobsList.ItemsSource = _visibleJobs;
        BuildJobsContextMenu();
        UiTrace.Write("MainWindow collections initialized");

        try
        {
            AppWindow.Resize(new SizeInt32(1180, 760));
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

        CategoryList.SelectedIndex = 0;
        UpdateButtons();
        UiTrace.Write("MainWindow ctor end");
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
        args.Cancel = true;
        sender.Hide();
    }

    private void ApplySnapshot(SnapshotDto snapshot)
    {
        _settings = snapshot.Settings;
        _status = snapshot.Status;

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

    private void UpdateButtons()
    {
        var connected = _connection?.IsConnected ?? false;
        var job = SelectedJob;
        AddButton.IsEnabled = connected;
        PauseButton.IsEnabled = connected && job?.CanPause == true;
        ResumeButton.IsEnabled = connected && job?.CanResume == true;
        DeleteButton.IsEnabled = connected && job is not null;
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
        StatusText.Text = $"{_jobsById.Count} downloads · {active} active · {queued} queued" +
                          (host is null ? string.Empty : $" · host {host}");
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
            "compressed" => FileCategories.For(job.FileName) == "compressed",
            "documents" => FileCategories.For(job.FileName) == "documents",
            "music" => FileCategories.For(job.FileName) == "music",
            "programs" => FileCategories.For(job.FileName) == "programs",
            "video" => FileCategories.For(job.FileName) == "video",
            "queues" => !string.IsNullOrEmpty(job.QueueText),
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
            "size" => _sortAscending ? filtered.OrderBy(j => j.SizeText.Length) : filtered.OrderByDescending(j => j.SizeText.Length),
            "status" => _sortAscending ? filtered.OrderBy(j => j.State) : filtered.OrderByDescending(j => j.State),
            "rate" => _sortAscending ? filtered.OrderBy(j => j.SpeedText) : filtered.OrderByDescending(j => j.SpeedText),
            "lastTry" => _sortAscending ? filtered.OrderBy(j => j.LastTryText) : filtered.OrderByDescending(j => j.LastTryText),
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

    private void JobsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is ListBoxItem { Tag: string tag })
        {
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

        if (element is ListViewItem { Content: JobViewModel job })
        {
            JobsList.SelectedItem = job;
        }
    }

    private void BuildJobsContextMenu()
    {
        var flyout = new MenuFlyout();
        flyout.Opening += JobsContextMenu_Opening;
        flyout.Items.Add(CreateContextItem("Open", MenuOpen_Click));
        flyout.Items.Add(CreateContextItem("Open folder", MenuOpenFolder_Click));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateContextItem("Remove", MenuRemove_Click));
        flyout.Items.Add(CreateContextItem("Refresh download address...", MenuRefreshUrl_Click));
        _addToQueueMenu = new MenuFlyoutSubItem { Text = "Add to queue" };
        flyout.Items.Add(_addToQueueMenu);
        flyout.Items.Add(CreateContextItem("Delete from queue", MenuDeleteFromQueue_Click));
        flyout.Items.Add(new MenuFlyoutSeparator());
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
    private void MenuRemove_Click(object sender, RoutedEventArgs e) => DeleteButton_Click(sender, e);
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
        CategoryColumn.Width = HideCategoriesMenuItem.IsChecked ? new GridLength(0) : new GridLength(192);
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

        var urlBox = new TextBox
        {
            Header = "URL",
            PlaceholderText = "https://example.com/file.zip",
            TextWrapping = TextWrapping.Wrap,
        };
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
            var connections = new TextBox { Header = "Connections per download (1–32)", Text = settings.DefaultConnections.ToString() };
            var speedLimit = new TextBox
            {
                Header = "Global speed limit (KB/s, empty for unlimited)",
                Text = settings.GlobalSpeedLimitBytesPerSecond is > 0
                    ? (settings.GlobalSpeedLimitBytesPerSecond.Value / 1024).ToString()
                    : string.Empty,
            };
            var launch = new CheckBox { Content = "Launch Download Manager at sign-in", IsChecked = settings.LaunchAtStartup };
            var clipboard = new CheckBox { Content = "Monitor the clipboard for URLs", IsChecked = settings.ClipboardMonitoring };
            var integration = new CheckBox { Content = "Enable Chrome → Download Manager handoff", IsChecked = settings.BrowserIntegrationEnabled };
            var autoIntercept = new CheckBox { Content = "Automatically take over eligible Chrome downloads", IsChecked = settings.BrowserAutoIntercept };
            var notify = new CheckBox { Content = "Notify when a download completes", IsChecked = settings.NotifyOnCompletion };
            var sound = new CheckBox { Content = "Play a sound when a download completes", IsChecked = settings.PlayCompletionSound };
            var autoResume = new CheckBox { Content = "Resume unfinished downloads after restart", IsChecked = settings.AutoResumeUnfinishedOnStartup };

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
            panel.Children.Add(new TextBlock { Text = "Behaviour", Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(launch);
            panel.Children.Add(clipboard);
            panel.Children.Add(autoResume);
            panel.Children.Add(notify);
            panel.Children.Add(sound);
            panel.Children.Add(new TextBlock { Text = "Browser integration", Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(integration);
            panel.Children.Add(autoIntercept);
            panel.Children.Add(new TextBlock { Text = "Category folders", Margin = new Thickness(0, 8, 0, 0) });
            foreach (var box in categoryBoxes.Values)
            {
                panel.Children.Add(box);
            }

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

            settings.DefaultDownloadDirectory = directory.Text.Trim();
            settings.MaxConcurrentDownloads = max;
            settings.DefaultConnections = connectionsValue;
            settings.GlobalSpeedLimitBytesPerSecond = limit;
            settings.LaunchAtStartup = launch.IsChecked == true;
            settings.ClipboardMonitoring = clipboard.IsChecked == true;
            settings.BrowserIntegrationEnabled = integration.IsChecked == true;
            settings.BrowserAutoIntercept = autoIntercept.IsChecked == true;
            settings.NotifyOnCompletion = notify.IsChecked == true;
            settings.PlayCompletionSound = sound.IsChecked == true;
            settings.AutoResumeUnfinishedOnStartup = autoResume.IsChecked == true;
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
            LaunchAtStartup = source.LaunchAtStartup,
            ClipboardMonitoring = source.ClipboardMonitoring,
            BrowserIntegrationEnabled = source.BrowserIntegrationEnabled,
            BrowserAutoIntercept = source.BrowserAutoIntercept,
            NotifyOnCompletion = source.NotifyOnCompletion,
            PlayCompletionSound = source.PlayCompletionSound,
            ShowCompletionDialog = source.ShowCompletionDialog,
            AutoResumeUnfinishedOnStartup = source.AutoResumeUnfinishedOnStartup,
            AllowLocalNetwork = source.AllowLocalNetwork,
            Theme = source.Theme,
            LastDestinationDirectory = source.LastDestinationDirectory,
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
        if (SelectedJob is { } job)
        {
            await RunActionAsync(() => _connection.PauseAsync(job.Id));
        }
    }

    private async void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is { } job)
        {
            await RunActionAsync(() => _connection.ResumeAsync(job.Id));
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Delete download?",
            Content = new TextBlock
            {
                Text = "The list entry, partial data and the downloaded file will be deleted. This cannot be undone.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunActionAsync(() => _connection.RemoveAsync(job.Id, deleteFile: true));
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
                Process.Start(new ProcessStartInfo { FileName = job.FinalFilePath, UseShellExecute = true });
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
            if (!string.IsNullOrWhiteSpace(job.FinalFilePath) && File.Exists(job.FinalFilePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{job.FinalFilePath}\"",
                    UseShellExecute = true,
                });
            }
            else if (Directory.Exists(job.Destination))
            {
                Process.Start(new ProcessStartInfo { FileName = job.Destination, UseShellExecute = true });
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
