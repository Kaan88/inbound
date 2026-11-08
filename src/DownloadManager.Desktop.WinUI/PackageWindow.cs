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
    private readonly HostConnection _connection = new();

    // Archive state comes over the connection above. The Host has eight pipe instances for every client on
    // the machine, and each of these windows is a process of its own, so a second connection here adds up.
    private readonly RemoteState _archives = new();
    private readonly Dictionary<Guid, JobDto> _jobs = [];
    private readonly Dictionary<Guid, Row> _rows = [];
    private List<Guid> _members = [];
    private bool _closed;
    private bool _groupKnown;
    private bool _passwordSavedHere;
    private bool _showingSaved;
    private bool _hostConnected;
    private bool _archivesRead;
    private int _archiveRead;

    private readonly Grid _root = new() { Padding = new Thickness(18), RowSpacing = 10 };
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _summary = new() { Opacity = 0.8 };
    private readonly ProgressBar _overall = InboundTheme.Styled(new ProgressBar { Minimum = 0, Maximum = 100 });
    private readonly TextBlock _detail = new() { Opacity = 0.8 };
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection(),
    };
    private readonly TextBlock _fileHeading = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly SegmentMapBar _fileMap = new() { Visibility = Visibility.Collapsed };
    private readonly ProgressBar _fileMoving = InboundTheme.Styled(new ProgressBar { Maximum = 100, Height = 14, MinHeight = 14, CornerRadius = new CornerRadius(2), Visibility = Visibility.Collapsed });
    private readonly JobDetailsPanel _fileDetails = new() { ShowSavePath = true };
    private Guid? _pickedFile;
    private Guid? _shownFile;
    private bool _selectingFile;
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly Button _pause = new() { Content = "Pause all", MinWidth = 100 };
    private readonly Button _resume = new() { Content = "Resume all", MinWidth = 100 };
    private readonly Button _cancel = new() { Content = "Cancel package", MinWidth = 100 };
    private readonly Button _openFolder = new() { Content = "Open folder", MinWidth = 100, Visibility = Visibility.Collapsed };
    private readonly Button _close = new() { Content = "Hide", MinWidth = 92 };
    private readonly StackPanel _archivePanel = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly PasswordBox _archivePassword = new() { Header = "Archive password", PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly Button _savePassword = new() { Content = "Save Password", MinWidth = 100, IsEnabled = false, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _clearPassword = new() { Content = "Clear Password", MinWidth = 100, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly StatusLine _passwordStatus = new("Archive password status");
    private readonly StatusLine _extractionStatus = new("Extraction");

    private sealed record Row(ListViewItem Item, Guid JobId, TextBlock Name, TextBlock Status, ProgressBar Bar, TextBlock Size);

    public PackageWindow(Guid packageId)
    {
        _packageId = packageId;
        AppTheme.Track(_root);
        Title = "Package status — Inbound Download Manager";
        WindowLayout.ResizeForDpi(this, 720, 820);
        WindowPlacement.Track(this, "package");
        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        // The chosen file's details are the longer of the two, and the list scrolls well in less room.
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(2, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            _root.RowDefinitions.Add(new RowDefinition { Height = height, MinHeight = height.IsStar ? 110 : 0 });
        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(_title);
        header.Children.Add(_summary);
        _root.Children.Add(header);
        Add(_overall, 1);
        Add(_detail, 2);
        var listFrame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(4), Child = _list };
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
        Add(BuildFileDetails(), 4);
        BuildArchivePanel();
        Add(_archivePanel, 5);
        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 5; i++) footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_notice);
        var column = 1;
        foreach (var button in new[] { _openFolder, _pause, _resume, _cancel, _close })
        {
            Grid.SetColumn(button, column++);
            footer.Children.Add(button);
        }
        Add(footer, 6);

        void ApplyColors()
        {
            _root.Background = InboundTheme.Brush("InboundSurfaceBrush", _root);
            listFrame.BorderBrush = InboundTheme.Brush("InboundDividerBrush", _root);
        }
        _root.Loaded += (_, _) => ApplyColors();
        _root.ActualThemeChanged += (_, _) => ApplyColors();

        _pause.Click += async (_, _) => await RunPackageActionAsync("pause");
        _resume.Click += async (_, _) => await RunPackageActionAsync("resume");
        _cancel.Click += async (_, _) => await ConfirmCancelAsync();
        _openFolder.Click += (_, _) => OpenFolder();
        _close.Click += (_, _) => Close();

        _archives.ArchiveSets.CollectionChanged += (_, _) => { if (!_closed) RenderArchives(); };
        _archives.PropertyChanged += (_, _) => { if (!_closed) RenderArchives(); };
        _connection.ArchiveChanged += change => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _archives.ArchivesSupported = true;
            _archives.Apply(change);
        });

        _connection.HostExiting += () => DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
        _connection.SnapshotReceived += snapshot => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            AppTheme.FollowSettings(snapshot.Settings);
            _jobs.Clear();
            foreach (var job in snapshot.Jobs) _jobs[job.Id] = job;
            SetMembers(snapshot.Settings.DownloadGroups);
            _ = ReadArchivesAsync();
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
            if (_members.Contains(job.Id)) Render();
        });
        _connection.ConnectionChanged += (connected, error) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _hostConnected = connected;
            _fileDetails.SetConnected(connected);
            _notice.Text = connected ? string.Empty : $"Host disconnected. Reconnecting… {error}";
        });
        Closed += async (_, _) =>
        {
            _closed = true;
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

    /// <summary>The same segment map and figures as a file's own window, for the selected file.</summary>
    private FrameworkElement BuildFileDetails()
    {
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
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
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
            _title.Text = "Package removed";
            _notice.Text = "This package no longer exists. Its files are listed individually in the main window.";
        }
        else
        {
            _title.Text = group.Name;
            _groupKnown = true;
            _members = [.. group.JobIds];
        }
        Render();
        RenderArchives();
    }

    // ---- Archives: the password for what this package brings, and how far extraction has got ----

    private void BuildArchivePanel()
    {
        var hint = new TextBlock { Text = ArchiveText.PackagePasswordHint, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
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
        _archives.ApplyArchiveList(reply.Sets);
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
        _archivePanel.Visibility = _archives.ArchivesSupported && _groupKnown ? Visibility.Visible : Visibility.Collapsed;
        var sets = PackageSets();

        // The Host says whether a password is held only through a set, and a new package may have none yet.
        _clearPassword.IsEnabled = _passwordSavedHere || sets.Count == 0 || sets.Any(set => set.HasPassword);

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
        _passwordStatus.Show("Clearing the archive password…");
        var result = await SendArchivePasswordAsync(string.Empty);
        if (_closed) return;
        if (result.Ok)
        {
            _passwordSavedHere = false;
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
        for (var index = 0; index < members.Count; index++)
        {
            var job = members[index];
            if (!_rows.TryGetValue(job.Id, out var row)) _rows[job.Id] = row = CreateRow(job.Id);
            var at = _list.Items.IndexOf(row.Item);
            if (at != index)
            {
                if (at >= 0) _list.Items.RemoveAt(at);
                _list.Items.Insert(Math.Min(index, _list.Items.Count), row.Item);
            }
            row.Name.Text = job.FileName;
            AutomationProperties.SetName(row.Item, $"{job.FileName}, {StateText(job)}");
            row.Status.Text = StateText(job);
            row.Bar.IsIndeterminate = job.TotalBytes is not > 0 && job.State is "connecting" or "downloading";
            row.Bar.Value = job.State == "completed" ? 100 : Percent(job.ReceivedBytes, job.TotalBytes);
            row.Size.Text = job.TotalBytes is > 0 ? $"{Bytes(job.ReceivedBytes)} / {Bytes(job.TotalBytes.Value)}" : Bytes(job.ReceivedBytes);
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
        _summary.Text = members.Count == 0 ? string.Empty : $"{completed} of {members.Count} files complete";
        _overall.Value = members.Count > 0 && completed == members.Count ? 100 : allKnown ? Percent(received, total) : 0;
        _overall.IsIndeterminate = !allKnown && completed < members.Count && speed > 0;
        var remaining = allKnown && speed > 0 ? TimeSpan.FromSeconds((total - received) / speed) : (TimeSpan?)null;
        _detail.Text = members.Count == 0 ? string.Empty : string.Join("   ·   ", new[]
        {
            allKnown ? $"{Bytes(received)} of {Bytes(total)}" : Bytes(received),
            speed > 0 ? $"{Bytes((long)speed)}/s" : null,
            remaining is { } left ? $"{FormatDuration(left)} left" : null,
        }.Where(part => part is not null));

        var allDone = members.Count > 0 && completed == members.Count;
        _openFolder.Visibility = allDone ? Visibility.Visible : Visibility.Collapsed;
        _pause.IsEnabled = PackageRules.PauseTargets(members).Count > 0;
        _resume.IsEnabled = PackageRules.ResumeTargets(_packageId, members).Count > 0;
        _cancel.IsEnabled = PackageRules.CancelTargets(members).Count > 0;
        _close.Content = allDone ? "Close" : "Hide";
        if (allDone) Title = "Package complete — Inbound Download Manager";
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
        var status = new TextBlock { Opacity = 0.8 };
        var size = new TextBlock { Opacity = 0.8, HorizontalAlignment = HorizontalAlignment.Right };
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
            PrimaryButtonText = "Cancel Package",
            CloseButtonText = "Keep Downloading",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await DialogGuard.ShowAsync(dialog) == ContentDialogResult.Primary) await RunPackageActionAsync("cancel");
    }

    private void OpenFileWindow(Guid jobId)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--job {jobId:D} --manual") { UseShellExecute = false });
        }
        catch (Exception ex) { _notice.Text = $"Could not open progress details: {ex.Message}"; }
    }

    private void OpenFolder()
    {
        var folder = _members.Select(id => _jobs.TryGetValue(id, out var job) ? Path.GetDirectoryName(job.FinalFilePath) : null)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
        if (folder is null) { _notice.Text = "The download folder was not found."; return; }
        try { Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); }
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

    private static string Bytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):N2} GB" : bytes >= 1024L * 1024
        ? $"{bytes / (1024.0 * 1024):N1} MB" : bytes >= 1024 ? $"{bytes / 1024.0:N0} KB" : $"{bytes:N0} B";

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}d {duration.Hours}h";
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}h {duration.Minutes:D2}m";
        return duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m {duration.Seconds:D2}s" : $"{duration.Seconds}s";
    }
}
