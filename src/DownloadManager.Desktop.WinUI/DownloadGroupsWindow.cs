using System.Collections.ObjectModel;
using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Host-backed management window for persistent download groups.</summary>
public sealed class DownloadGroupsWindow : Window
{
    private readonly HostConnection _connection = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly StackPanel _groupsPanel = new() { Spacing = 8 };
    private readonly ListView _ungrouped = new() { SelectionMode = ListViewSelectionMode.Multiple, Height = 150 };
    private readonly ComboBox _targetGroup = new() { MinWidth = 0, PlaceholderText = "Choose a group" };
    private readonly TextBox _newGroupName = new() { PlaceholderText = "New group name", MinWidth = 0 };
    private readonly Button _create = new() { Content = "Create group" };
    private readonly Button _add = new() { Content = "Add to selected group" };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer _groupsScroll;
    private readonly Dictionary<Guid, JobViewModel> _viewModels = new();
    private readonly Dictionary<Guid, ChildControls> _childControls = new();
    private readonly Dictionary<Guid, GroupSummaryControls> _summaries = new();
    private readonly HashSet<Button> _actionButtons = new();
    private readonly List<DownloadWindow> _detailWindows = new();
    private string _structure = string.Empty;
    private bool _busy;
    private bool _closed;
    private bool _refreshing;

    public DownloadGroupsWindow()
    {
        Title = "Download Groups — Inbound Download Manager";
        WindowLayout.ResizeForDpi(this, 850, 650);
        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        var root = new Grid { Margin = new Thickness(16), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RequestedTheme = UiStateStore.Load().DarkMode switch
        {
            true => ElementTheme.Dark,
            false => ElementTheme.Light,
            null => ElementTheme.Default
        };

        void ApplyBackground() => root.Background = InboundTheme.Brush("InboundSurfaceBrush", root);
        root.ActualThemeChanged += (_, _) => ApplyBackground();
        root.Loaded += (_, _) => ApplyBackground();

        var heading = new TextBlock { Text = "Download groups", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        root.Children.Add(heading);

        var selectionPanel = new StackPanel { Spacing = 6 };
        selectionPanel.Children.Add(new TextBlock { Text = "Ungrouped downloads" });
        _ungrouped.DisplayMemberPath = nameof(JobViewModel.FileName);
        _ungrouped.SelectionChanged += (_, _) => UpdateButtons();
        _targetGroup.SelectionChanged += (_, _) => UpdateButtons();
        selectionPanel.Children.Add(_ungrouped);
        var createRow = new StackPanel { Spacing = 6 };
        createRow.Children.Add(_newGroupName);
        createRow.Children.Add(_create);
        selectionPanel.Children.Add(createRow);
        var addRow = new StackPanel { Spacing = 6 };
        addRow.Children.Add(_targetGroup);
        addRow.Children.Add(_add);
        selectionPanel.Children.Add(addRow);
        _create.Click += async (_, _) => await CreateGroupAsync();
        _add.Click += async (_, _) => await AddToGroupAsync();
        var selectionExpander = new Expander { Header = "Create a group or add downloads", HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer { Content = selectionPanel, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        Grid.SetRow(selectionExpander, 1);
        root.Children.Add(selectionExpander);

        _groupsScroll = new ScrollViewer { Content = _groupsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(_groupsScroll, 2);
        root.Children.Add(_groupsScroll);
        _notice.Opacity = 0.9;
        Grid.SetRow(_notice, 3);
        root.Children.Add(_notice);
        root.Children.Add(new TextBlock
        {
            Text = "Groups keep downloads independent. Ungrouping never removes files.",
            Opacity = 0.7,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetRow((FrameworkElement)root.Children[^1], 4);

        Content = root;
        Closed += async (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _connection.SnapshotReceived -= OnSnapshotReceived;
            await _connection.DisposeAsync();
        };
        _connection.SnapshotReceived += OnSnapshotReceived;
        _timer.Tick += async (_, _) => await RefreshAsync();
        _connection.Start();
        _timer.Start();
        UpdateButtons();
    }

    public void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private void OnSnapshotReceived(SnapshotDto snapshot) => DispatcherQueue.TryEnqueue(() => ApplySnapshot(snapshot));

    private async Task RefreshAsync()
    {
        if (_closed || _refreshing) return;
        _refreshing = true;
        try
        {
            var snapshot = await _connection.RefreshAsync();
            if (!_closed) ApplySnapshot(snapshot);
        }
        catch (Exception ex) { if (!_closed) _notice.Text = "Could not refresh downloads: " + ex.Message; }
        finally { _refreshing = false; }
    }

    private void ApplySnapshot(SnapshotDto snapshot)
    {
        if (_closed) return;
        var jobs = snapshot.Jobs.ToDictionary(job => job.Id);
        foreach (var job in snapshot.Jobs)
        {
            if (!_viewModels.TryGetValue(job.Id, out var vm)) _viewModels[job.Id] = vm = new JobViewModel();
            vm.Update(job);
        }
        foreach (var id in _viewModels.Keys.Where(id => !jobs.ContainsKey(id)).ToArray()) _viewModels.Remove(id);

        var groups = snapshot.Settings.DownloadGroups ?? new List<DownloadGroupDto>();
        var memberIds = groups.SelectMany(group => group.JobIds).ToHashSet();
        var ungroupedIds = snapshot.Jobs.Where(job => !memberIds.Contains(job.Id)).Select(job => job.Id).ToArray();
        var selectedUngrouped = _ungrouped.SelectedItems.OfType<JobViewModel>().Select(vm => vm.Id).ToHashSet();
        var selectedGroupId = (_targetGroup.SelectedItem as GroupChoice)?.Id;
        var signature = string.Join("|", groups.OrderBy(group => group.Id)
            .Select(group => $"{group.Id:N}:{group.Name}:{string.Join(',', group.JobIds)}")) +
            "#" + string.Join(',', ungroupedIds);

        if (!string.Equals(signature, _structure, StringComparison.Ordinal))
        {
            var previousScroll = _groupsScroll.VerticalOffset;
            var previousGroups = _groupsPanel.Children.OfType<GroupVisual>().ToArray();
            var expandNewGroups = previousGroups.Length == 0;
            var expanded = previousGroups.Where(g => g.Expander.IsExpanded)
                .Select(g => g.GroupId).ToHashSet();
            _structure = signature;
            _childControls.Clear();
            _summaries.Clear();
            _actionButtons.Clear();
            _groupsPanel.Children.Clear();
            foreach (var group in groups)
                _groupsPanel.Children.Add(BuildGroup(group, jobs, expandNewGroups || expanded.Contains(group.Id)));

            _ungrouped.ItemsSource = null;
            _ungrouped.ItemsSource = new ObservableCollection<JobViewModel>(ungroupedIds
                .Where(_viewModels.ContainsKey).Select(id => _viewModels[id]));
            foreach (var item in _ungrouped.Items.OfType<JobViewModel>().Where(vm => selectedUngrouped.Contains(vm.Id)))
                _ungrouped.SelectedItems.Add(item);
            _targetGroup.ItemsSource = null;
            _targetGroup.ItemsSource = groups.Select(group => new GroupChoice(group.Id, group.Name)).ToList();
            _targetGroup.SelectedItem = _targetGroup.Items.OfType<GroupChoice>().FirstOrDefault(choice => choice.Id == selectedGroupId)
                ?? _targetGroup.Items.FirstOrDefault();
            DispatcherQueue.TryEnqueue(() => { if (!_closed) _groupsScroll.ChangeView(null, previousScroll, null, disableAnimation: true); });
        }
        else
        {
            foreach (var group in groups)
            {
                var members = group.JobIds.Where(jobs.ContainsKey).Select(id => jobs[id]).ToArray();
                if (_summaries.TryGetValue(group.Id, out var summaryControls))
                    UpdateSummary(summaryControls, DownloadGroupSummary.Create(members));
                foreach (var id in group.JobIds)
                    if (_childControls.TryGetValue(id, out var controls) && jobs.TryGetValue(id, out var job))
                        UpdateChild(controls, _viewModels[id], job);
            }
        }
        UpdateButtons();
    }

    private GroupVisual BuildGroup(DownloadGroupDto group, IReadOnlyDictionary<Guid, JobDto> jobs, bool expanded)
    {
        var visual = new GroupVisual(group.Id);
        var children = group.JobIds.Where(jobs.ContainsKey).Select(id => jobs[id]).ToArray();
        var summary = DownloadGroupSummary.Create(children);
        var summaryLine = new TextBlock { Text = SummaryText(summary), Opacity = 0.82 };
        summaryLine.TextWrapping = TextWrapping.Wrap;
        var progressBar = InboundTheme.Styled(new ProgressBar { Minimum = 0, Maximum = 1, Width = 180, Height = 6, Margin = new Thickness(0, 2, 0, 2) });
        var summaryControls = new GroupSummaryControls(summaryLine, progressBar);
        UpdateSummary(summaryControls, summary);
        var body = new StackPanel { Spacing = 6, Margin = new Thickness(12, 4, 0, 8) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var resume = Button("Resume"); var pause = Button("Pause"); var cancel = Button("Cancel");
        var rename = Button("Rename"); var ungroup = Button("Ungroup");
        toolbar.Children.Add(resume); toolbar.Children.Add(pause); toolbar.Children.Add(cancel);
        var management = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        management.Children.Add(rename); management.Children.Add(ungroup);
        resume.Click += async (_, _) => await GroupActionAsync(group.Id, "resume");
        pause.Click += async (_, _) => await GroupActionAsync(group.Id, "pause");
        cancel.Click += async (_, _) =>
        {
            if (await ConfirmAsync("Cancel all unfinished downloads in this group? Downloaded files will be kept.", "Cancel group downloads"))
                await GroupActionAsync(group.Id, "cancel");
        };
        rename.Click += async (_, _) => await RenameGroupAsync(group);
        ungroup.Click += async (_, _) =>
        {
            if (await ConfirmAsync($"Remove the group ‘{group.Name}’? Its downloads and files will be kept.", "Ungroup downloads"))
                await GroupActionAsync(group.Id, "delete");
        };

        foreach (var job in children)
        {
            var row = BuildChild(group.Id, job);
            body.Children.Add(row.Root);
            _childControls[job.Id] = row;
        }
        _summaries[group.Id] = summaryControls;
        foreach (var button in toolbar.Children.OfType<Button>().Concat(management.Children.OfType<Button>())) _actionButtons.Add(button);
        var header = new StackPanel { Spacing = 3 };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleLine.Children.Add(new TextBlock { Text = group.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        titleLine.Children.Add(new TextBlock { Text = $"{children.Length} downloads", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(titleLine);
        header.Children.Add(summaryLine);
        var progressActions = new StackPanel { Spacing = 6 };
        progressActions.Children.Add(progressBar);
        progressActions.Children.Add(toolbar);
        progressActions.Children.Add(management);
        header.Children.Add(progressActions);
        visual.Expander = new Expander { Header = header, Content = body, IsExpanded = expanded };
        visual.Content = visual.Expander;
        return visual;
    }

    private ChildControls BuildChild(Guid groupId, JobDto job)
    {
        var vm = _viewModels[job.Id];
        var row = new Grid { RowSpacing = 4, Margin = new Thickness(6, 4, 6, 4) };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var icon = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        icon.SetBinding(Image.SourceProperty, new Binding { Path = new PropertyPath(nameof(JobViewModel.FileIcon)) });
        icon.SetBinding(UIElement.VisibilityProperty, new Binding { Path = new PropertyPath(nameof(JobViewModel.FileIconVisibility)) });
        row.Children.Add(icon);
        var glyph = new TextBlock { Width = 20, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe MDL2 Assets"), Margin = new Thickness(0, 0, 8, 0) };
        glyph.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(nameof(JobViewModel.FileGlyph)) });
        glyph.SetBinding(UIElement.VisibilityProperty, new Binding { Path = new PropertyPath(nameof(JobViewModel.FileGlyphVisibility)) });
        row.Children.Add(glyph);
        var file = BoundText(nameof(JobViewModel.FileName), 260);
        file.Width = double.NaN;
        file.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(file);
        Grid.SetColumn(file, 1);
        var metadata = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var size = BoundText(nameof(JobViewModel.SizeText), 80); metadata.Children.Add(size);
        var state = BoundText(nameof(JobViewModel.StateText), 90); metadata.Children.Add(state);
        var speed = BoundText(nameof(JobViewModel.SpeedText), 80); metadata.Children.Add(speed);
        row.Children.Add(metadata); Grid.SetRow(metadata, 1); Grid.SetColumnSpan(metadata, 2);
        var details = Button("Details"); var pauseResume = Button(string.Equals(job.State, "paused", StringComparison.OrdinalIgnoreCase) ? "Resume" : "Pause"); var move = Button("Move"); var detach = Button("Detach");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(details); actions.Children.Add(pauseResume); actions.Children.Add(move); actions.Children.Add(detach);
        row.Children.Add(actions); Grid.SetRow(actions, 2); Grid.SetColumnSpan(actions, 2);
        _actionButtons.Add(details); _actionButtons.Add(pauseResume); _actionButtons.Add(move); _actionButtons.Add(detach);
        details.Click += (_, _) =>
        {
            var window = new DownloadWindow(job.Id, manual: true);
            _detailWindows.Add(window);
            window.Closed += (_, _) => _detailWindows.Remove(window);
            window.ShowWindow();
        };
        pauseResume.Click += async (_, _) => await JobActionAsync(job.Id, vm.CanResume ? "resume" : "pause");
        move.Click += async (_, _) =>
        {
            if (_targetGroup.SelectedItem is not GroupChoice choice) { _notice.Text = "Choose a destination group first."; return; }
            if (choice.Id == groupId) { _notice.Text = "Choose a different destination group."; return; }
            await GroupActionAsync(choice.Id, "add", new List<Guid> { job.Id });
        };
        detach.Click += async (_, _) => await GroupActionAsync(groupId, "detach", new List<Guid> { job.Id });
        row.DataContext = vm;
        var controls = new ChildControls(row, state, speed, pauseResume, job.CanPause || job.CanResume);
        UpdateChild(controls, vm, job);
        return controls;
    }

    private void UpdateChild(ChildControls controls, JobViewModel vm, JobDto job)
    {
        controls.Root.DataContext = vm;
        controls.State.Text = vm.StateText;
        controls.Speed.Text = vm.SpeedText;
        controls.PauseResume.Content = job.CanResume ? "Resume" : "Pause";
        controls.CanPauseResume = job.CanPause || job.CanResume;
        controls.Root.Opacity = 1;
    }

    private async Task CreateGroupAsync()
    {
        var ids = SelectedUngroupedIds();
        if (ids.Count == 0) { _notice.Text = "Select one or more ungrouped downloads first."; return; }
        var name = _newGroupName.Text.Trim();
        if (name.Length == 0) { _notice.Text = "Enter a name for the group."; return; }
        await GroupActionAsync(Guid.Empty, "create", ids, name);
        if (string.IsNullOrEmpty(_notice.Text)) _newGroupName.Text = string.Empty;
    }

    private async Task AddToGroupAsync()
    {
        if (_targetGroup.SelectedItem is not GroupChoice choice) { _notice.Text = "Choose a group first."; return; }
        var ids = SelectedUngroupedIds();
        if (ids.Count == 0) { _notice.Text = "Select one or more ungrouped downloads first."; return; }
        await GroupActionAsync(choice.Id, "add", ids);
    }

    private async Task RenameGroupAsync(DownloadGroupDto group)
    {
        var input = new TextBox { Text = group.Name, MaxLength = 100, MinWidth = 280 };
        var dialog = Dialog("Rename group", input, "Save", "Cancel");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await GroupActionAsync(group.Id, "rename", name: input.Text.Trim());
    }

    private async Task<bool> ConfirmAsync(string message, string title)
    {
        var dialog = Dialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, "Continue", "Cancel");
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private ContentDialog Dialog(string title, UIElement content, string primary, string close) => new()
    {
        XamlRoot = Content.XamlRoot,
        RequestedTheme = ((FrameworkElement)Content).ActualTheme,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = close,
        DefaultButton = ContentDialogButton.Primary
    };

    private async Task GroupActionAsync(Guid groupId, string action, List<Guid>? ids = null, string? name = null)
    {
        if (_busy) return;
        await RunBusyAsync(async () =>
        {
            var reply = await _connection.GroupActionAsync(new GroupActionRequest { GroupId = groupId, Action = action, Name = name, JobIds = ids });
            if (reply.Type != MessageTypes.GroupActionResult) throw new InvalidDataException("Host returned an unexpected group response.");
            var result = reply.GetPayload<GroupActionResult>();
            var latest = await _connection.RefreshAsync();
            if (_closed) return;
            ApplySnapshot(latest);
            if (!result.Ok)
            {
                var failures = result.FailedJobs.Count == 0 ? string.Empty : " " + string.Join(" ", result.FailedJobs.Select(pair => $"{pair.Key}: {pair.Value}"));
                throw new InvalidOperationException((result.Error ?? "The group action failed.") + failures);
            }
        });
    }

    private async Task JobActionAsync(Guid id, string action)
    {
        if (_busy) return;
        await RunBusyAsync(async () =>
        {
            var reply = action == "resume" ? await _connection.ResumeAsync(id) : await _connection.PauseAsync(id);
            var result = reply.GetPayload<JobActionResponse>();
            if (!result.Ok) throw new InvalidOperationException(result.Error ?? "The download action failed.");
            var latest = await _connection.RefreshAsync();
            if (!_closed) ApplySnapshot(latest);
        });
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        _busy = true; UpdateButtons();
        try { _notice.Text = string.Empty; await action(); }
        catch (Exception ex) { if (!_closed) _notice.Text = ex.Message; }
        finally { _busy = false; if (!_closed) UpdateButtons(); }
    }

    private List<Guid> SelectedUngroupedIds() => _ungrouped.SelectedItems.OfType<JobViewModel>().Select(vm => vm.Id).ToList();

    private void UpdateButtons()
    {
        if (_closed) return;
        _create.IsEnabled = !_busy;
        _add.IsEnabled = !_busy && _targetGroup.SelectedItem is GroupChoice && _ungrouped.SelectedItems.Count > 0;
        _ungrouped.IsEnabled = !_busy;
        _targetGroup.IsEnabled = !_busy;
        _newGroupName.IsEnabled = !_busy;
        foreach (var button in _actionButtons) button.IsEnabled = !_busy;
        foreach (var child in _childControls.Values)
            child.PauseResume.IsEnabled = !_busy && child.CanPauseResume;
    }

    private static Button Button(string label) => new() { Content = label, Padding = new Thickness(8, 3, 8, 3), MinWidth = 0 };
    private static TextBlock BoundText(string path, double width)
    {
        var text = new TextBlock { Width = width, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        text.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(path) });
        return text;
    }

    private static string SummaryText(DownloadGroupSummary summary)
    {
        var total = summary.TotalBytes is long totalBytes ? FormatBytes(totalBytes) : "unknown size";
        var progress = summary.Progress is double fraction ? $"{fraction:P0}" : "—";
        var speedBytes = summary.BytesPerSecond >= long.MaxValue ? long.MaxValue : (long)summary.BytesPerSecond;
        var speed = speedBytes > 0 ? $"{FormatBytes(speedBytes)}/s" : "—";
        var eta = summary.EstimatedRemaining is TimeSpan remaining ? FormatEta(remaining) : "—";
        return $"{summary.State}  ·  {summary.CompletedCount}/{summary.MemberCount} completed  ·  {FormatBytes(summary.ReceivedBytes)} / {total}  ·  {progress}  ·  {speed}  ·  {eta} left";
    }

    private static void UpdateSummary(GroupSummaryControls controls, DownloadGroupSummary summary)
    {
        controls.Text.Text = SummaryText(summary);
        controls.Progress.IsIndeterminate = summary.Progress is null && summary.State == "active";
        controls.Progress.Value = summary.Progress ?? 0;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes); var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return index == 0 ? $"{bytes} B" : $"{value:0.0} {units[index]}";
    }

    private static string FormatEta(TimeSpan eta) => eta.TotalHours >= 1
        ? $"{(long)eta.TotalHours}h {eta.Minutes}m"
        : eta.TotalMinutes >= 1 ? $"{(int)eta.TotalMinutes}m {eta.Seconds}s" : $"{eta.Seconds}s";

    private sealed record GroupChoice(Guid Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed class GroupVisual(Guid groupId) : ContentControl
    {
        public Guid GroupId { get; } = groupId;
        public Expander Expander { get; set; } = null!;
    }

    private sealed class ChildControls(Grid root, TextBlock state, TextBlock speed, Button pauseResume, bool canPauseResume)
    {
        public Grid Root { get; } = root;
        public TextBlock State { get; } = state;
        public TextBlock Speed { get; } = speed;
        public Button PauseResume { get; } = pauseResume;
        public bool CanPauseResume { get; set; } = canPauseResume;
    }

    private sealed record GroupSummaryControls(TextBlock Text, ProgressBar Progress);
}
