using System.Collections.ObjectModel;
using System.Text.Json;
using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Queue, schedule, file order and limit editor with explicit unsaved settings.</summary>
public sealed class SchedulerWindow : Window
{
    private readonly ContentControl _editorSurface = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly HostConnection _connection = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly ListBox _queues = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _start = new() { Content = "Start Now" };
    private readonly Button _stop = new() { Content = "Stop", IsEnabled = false };
    private readonly Button _apply = new() { Content = "Apply" };
    private readonly Button _close = new() { Content = "Close" };

    private readonly ComboBox _schedulePicker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _scheduleTarget = new() { Header = "Queue", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _scheduleName = new() { Header = "Schedule name", MaxLength = 80 };
    private readonly TimeField _startTime = new("Start time", optional: false);
    private readonly TimeField _stopTime = new("Stop time (optional)", optional: true);
    private readonly DaysField _days = new("Days");
    private readonly DateField _oneTimeDate = new("One-time date (optional)", "Repeat on the days above");
    private readonly CheckBox _scheduleEnabled = new() { Content = "Enable schedule" };
    private readonly CheckBox _openWhenDone = new() { Content = "Open a file when the queue finishes" };
    private readonly CheckBox _exitWhenDone = new() { Content = "Exit Download Manager when the queue finishes" };
    private readonly CheckBox _shutdownWhenDone = new() { Content = "Turn off the computer when the queue finishes" };
    private readonly ComboBox _shutdownMode = new() { Header = "Computer action", ItemsSource = new[] { "Shut down", "Restart", "Log off" } };
    private readonly CheckBox _forceShutdown = new() { Content = "Force the selected computer action" };
    private readonly CheckBox _allowSystemActions = new() { Content = "Allow system completion actions (global opt-in)" };
    private readonly TextBlock _scheduleHint = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly StackPanel _runOptionsHost = new() { Spacing = 8 };
    private readonly Button _newSchedule = new() { Content = "New schedule" };
    private readonly Button _removeSchedule = new() { Content = "Remove schedule", IsEnabled = false };

    private readonly ListView _files = new() { SelectionMode = ListViewSelectionMode.Single, MinHeight = 150 };
    private readonly Button _moveUp = new() { Content = "Move up", IsEnabled = false };
    private readonly Button _moveDown = new() { Content = "Move down", IsEnabled = false };
    private readonly Button _detachFile = new() { Content = "Remove from queue", IsEnabled = false };
    private readonly ComboBox _moveTarget = new() { Header = "Move to queue", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _moveFile = new() { Content = "Move", IsEnabled = false };
    private readonly TextBlock _fileHint = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };

    private readonly NumberBox _maxConcurrent = new() { Header = "Files at the same time", Minimum = 1, Maximum = 16, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160 };
    private readonly CheckBox _quotaEnabled = new() { Content = "Enable download limit" };
    private readonly NumberBox _quotaMb = new() { Header = "Download limit (MB)", Minimum = 1, Maximum = 10_485_760, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 200 };
    private readonly NumberBox _quotaHours = new() { Header = "Per (hours)", Minimum = 1, Maximum = 8_760, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160 };

    private readonly Dictionary<int, QueuePolicyDraft> _queueDrafts = new();
    private readonly Dictionary<int, QueueDto> _queuePolicyBaselines = new();
    private readonly Dictionary<int, QueueFileDraft> _fileDrafts = new();
    private readonly Dictionary<Guid, ScheduleDto> _scheduleDrafts = new();
    private readonly Dictionary<Guid, ScheduleDto> _scheduleBaselines = new();
    private readonly HashSet<Guid> _newSchedules = new();
    private readonly HashSet<Guid> _dirtySchedules = new();
    private readonly HashSet<Guid> _removedSchedules = new();
    private SnapshotDto? _snapshot;
    private SchedulerRunOptionsEditor? _runOptions;
    private Guid? _displayedScheduleId;
    private bool _globalOptInBaseline;
    private bool _globalOptInDirty;
    private bool _suppressSchedulePicker;
    private bool _scheduleEditorEnabled;
    private bool _closed;
    private bool _refreshing;
    private bool _draftsInitialized;
    private int? _lastSelectedQueueId;

    private string _scheduleLoadedKey = string.Empty;
    private string _limitsLoadedKey = string.Empty;
    private bool _capturingEditors;
    private bool _restoringQueue;

    private string ScheduleFormKey() => JsonSerializer.Serialize(new object?[] { _scheduleName.Text,
        _scheduleEnabled.IsChecked, _startTime.Text, _stopTime.Text, _days.Text, _oneTimeDate.Text,
        _openWhenDone.IsChecked, _exitWhenDone.IsChecked, _shutdownWhenDone.IsChecked, _shutdownMode.SelectedIndex,
        _forceShutdown.IsChecked, (_scheduleTarget.SelectedItem as ComboBoxItem)?.Tag, _runOptions?.StateKey });
    private string LimitsFormKey() => JsonSerializer.Serialize(new object?[] { _maxConcurrent.Text,
        _quotaEnabled.IsChecked, _quotaMb.Text, _quotaHours.Text });
    private bool CaptureEditors(int? queueId)
    {
        if (_capturingEditors || _snapshot is null) return true;
        _capturingEditors = true;
        try
        {
            if (_scheduleEditorEnabled && ScheduleFormKey() != _scheduleLoadedKey && !StageScheduleDraft()) return false;
            if (queueId is not null && _limitsLoadedKey.Length > 0 && LimitsFormKey() != _limitsLoadedKey && !StageQueueLimits(queueId)) return false;
            return true;
        }
        finally { _capturingEditors = false; }
    }

    public SchedulerWindow()
    {
        Title = "Scheduler — Inbound Download Manager";
        WindowLayout.ResizeForDpi(this, 1000, 720);
        WindowPlacement.Track(this, "scheduler");
        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        var root = new Grid { Margin = new Thickness(14), ColumnSpacing = 14, RowSpacing = 10 };
        root.RequestedTheme = UiStateStore.Load().DarkMode switch
        {
            true => ElementTheme.Dark,
            false => ElementTheme.Light,
            null => ElementTheme.Default,
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var queuePanel = new Grid { RowSpacing = 8 };
        queuePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        queuePanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        queuePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        queuePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        queuePanel.Children.Add(new TextBlock
        {
            Text = "Queues", FontSize = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        _queues.DisplayMemberPath = nameof(QueueDto.Name);
        _queues.SelectionChanged += (_, _) => OnQueueSelectionChanged();
        Grid.SetRow(_queues, 1);
        queuePanel.Children.Add(_queues);
        var queueCrud = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var createQueue = new Button { Content = "New…" };
        var renameQueue = new Button { Content = "Rename…" };
        var deleteQueue = new Button { Content = "Delete" };
        ToolTipService.SetToolTip(createQueue, "New queue");
        ToolTipService.SetToolTip(renameQueue, "Rename the selected queue");
        ToolTipService.SetToolTip(deleteQueue, "Delete the selected queue");
        createQueue.Click += async (_, _) => await CreateQueueAsync();
        renameQueue.Click += async (_, _) => await RenameQueueAsync();
        deleteQueue.Click += async (_, _) => await DeleteQueueAsync();
        queueCrud.Children.Add(createQueue);
        queueCrud.Children.Add(renameQueue);
        queueCrud.Children.Add(deleteQueue);
        Grid.SetRow(queueCrud, 2);
        queuePanel.Children.Add(queueCrud);
        var queueButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _start.Click += async (_, _) => await RunQueueActionAsync("start");
        _stop.Click += async (_, _) => await RunQueueActionAsync("stop");
        queueButtons.Children.Add(_start);
        queueButtons.Children.Add(_stop);
        Grid.SetRow(queueButtons, 3);
        queuePanel.Children.Add(queueButtons);
        Grid.SetColumn(queuePanel, 0);
        root.Children.Add(queuePanel);

        // Flat selector bar instead of browser-style tabs (see docs/design-guidelines.md, dialogs).
        var tabs = new Grid { RowSpacing = 6 };
        tabs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tabs.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var tabBar = new SelectorBar();
        var tabPage = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(tabPage, 1);
        var tabPages = new List<UIElement> { BuildScheduleTab(), BuildFilesTab(), BuildLimitsTab() };
        foreach (var header in new[] { "Schedule", "Files", "Limits" }) tabBar.Items.Add(new SelectorBarItem { Text = header });
        tabBar.SelectionChanged += (_, _) => tabPage.Content = tabPages[Math.Max(0, tabBar.Items.IndexOf(tabBar.SelectedItem))];
        tabBar.SelectedItem = tabBar.Items[0];
        tabPage.Content = tabPages[0];
        tabs.Children.Add(tabBar);
        tabs.Children.Add(tabPage);
        Grid.SetColumn(tabs, 1);
        root.Children.Add(tabs);

        var bottom = new Grid { ColumnSpacing = 12 };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.Text = "Loading queues…";
        Grid.SetColumn(_status, 0);
        bottom.Children.Add(_status);
        _apply.Click += async (_, _) => await ApplyAsync();
        Grid.SetColumn(_apply, 1);
        bottom.Children.Add(_apply);
        _close.Click += (_, _) => Close();
        Grid.SetColumn(_close, 2);
        bottom.Children.Add(_close);
        Grid.SetRow(bottom, 1);
        Grid.SetColumnSpan(bottom, 2);
        root.Children.Add(bottom);

        _editorSurface.Content = root;
        _editorSurface.RequestedTheme = root.RequestedTheme;
        var windowFrame = new Border { Child = _editorSurface, RequestedTheme = root.RequestedTheme };
        Content = windowFrame;
        WindowLayout.ApplyChrome(this, root);
        void ApplyBackground()
        {
            var brush = InboundTheme.Brush("InboundSurfaceBrush", root);
            root.Background = brush;
            windowFrame.Background = brush;
        }
        root.ActualThemeChanged += (_, _) => ApplyBackground();
        root.Loaded += (_, _) => ApplyBackground();
        Closed += async (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _connection.ConnectionChanged -= OnConnectionChanged;
            await _connection.DisposeAsync();
        };
        _connection.ConnectionChanged += OnConnectionChanged;
        _connection.HostExiting += () => DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
        _timer.Tick += async (_, _) => await RefreshAsync();
        _connection.Start();
        _timer.Start();
        _ = RefreshAsync();
    }

    public void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private UIElement BuildScheduleTab()
    {
        var panel = new StackPanel { Spacing = 9, Padding = new Thickness(10) };
        panel.Children.Add(new TextBlock { Text = "Schedules", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 17 });
        panel.Children.Add(_schedulePicker);
        var scheduleActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _newSchedule.Click += (_, _) => BeginNewSchedule();
        _removeSchedule.Click += (_, _) => StageScheduleRemoval();
        scheduleActions.Children.Add(_newSchedule);
        scheduleActions.Children.Add(_removeSchedule);
        panel.Children.Add(scheduleActions);
        panel.Children.Add(_scheduleHint);
        panel.Children.Add(_scheduleTarget);
        panel.Children.Add(_scheduleName);
        panel.Children.Add(_scheduleEnabled);
        var timing = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timing.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        timing.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        timing.Children.Add(_startTime);
        timing.Children.Add(_stopTime); Grid.SetColumn(_stopTime, 1);
        timing.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        timing.Children.Add(_days); Grid.SetRow(_days, 1); Grid.SetColumnSpan(_days, 2);
        timing.Children.Add(_oneTimeDate); Grid.SetRow(_oneTimeDate, 2);
        panel.Children.Add(timing);
        var completion = new StackPanel { Spacing = 8 };
        completion.Children.Add(_openWhenDone); completion.Children.Add(_exitWhenDone);
        completion.Children.Add(_shutdownWhenDone); completion.Children.Add(_shutdownMode);
        completion.Children.Add(_forceShutdown); completion.Children.Add(_allowSystemActions);
        panel.Children.Add(new Expander { Header = "When the queue finishes", Content = completion,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        panel.Children.Add(_runOptionsHost);
        _schedulePicker.SelectionChanged += (_, _) =>
        {
            if (_suppressSchedulePicker) return;
            if (_schedulePicker.SelectedItem is ComboBoxItem { Tag: Guid id })
            {
                if (!CaptureEditors(SelectedQueue?.Id))
                {
                    _suppressSchedulePicker = true;
                    _schedulePicker.SelectedItem = _schedulePicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is Guid previous && previous == _displayedScheduleId);
                    _suppressSchedulePicker = false;
                    return;
                }
                LoadSchedule(id);
            }
        };
        _scheduleTarget.SelectionChanged += (_, _) => UpdateRunOptionsRetryLimit();
        _allowSystemActions.Checked += (_, _) => MarkGlobalOptInDirty();
        _allowSystemActions.Unchecked += (_, _) => MarkGlobalOptInDirty();
        _shutdownWhenDone.Checked += (_, _) => UpdateShutdownControls();
        _shutdownWhenDone.Unchecked += (_, _) => UpdateShutdownControls();
        UpdateShutdownControls();
        _scheduleHint.Text = "Edits are preserved when you switch schedules. Apply saves them; Close discards unsaved changes.";
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private UIElement BuildFilesTab()
    {
        var panel = new StackPanel { Spacing = 9, Padding = new Thickness(10) };
        panel.Children.Add(new TextBlock { Text = "Queued downloads", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 17 });
        panel.Children.Add(new TextBlock { Text = "Choose the order of waiting downloads within the same priority. Active transfers continue. Membership and order are saved when you click Apply.", TextWrapping = TextWrapping.Wrap });
        _files.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid ColumnSpacing="10" Padding="4">
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width="28"/><ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>
                <Grid VerticalAlignment="Center" HorizontalAlignment="Center">
                  <Image Source="{Binding FileIcon}" Visibility="{Binding FileIconVisibility}" Width="24" Height="24"/>
                  <TextBlock Text="{Binding FileGlyph}" FontFamily="Segoe MDL2 Assets" Visibility="{Binding FileGlyphVisibility}" VerticalAlignment="Center" HorizontalAlignment="Center"/>
                </Grid>
                <StackPanel Grid.Column="1" Spacing="2">
                  <TextBlock Text="{Binding FileName}" TextTrimming="CharacterEllipsis"/>
                  <StackPanel Orientation="Horizontal" Spacing="8">
                    <TextBlock Text="{Binding SizeText}" Visibility="{Binding SizeVisibility}" FontSize="12" Opacity="0.75"/>
                    <TextBlock Text="{Binding StateText}" FontSize="12" Opacity="0.75" TextTrimming="CharacterEllipsis"/>
                  </StackPanel>
                </StackPanel>
              </Grid>
            </DataTemplate>
            """);
        _files.SelectionChanged += (_, _) => UpdateFileButtons();
        panel.Children.Add(_files);
        var ordering = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _moveUp.Click += (_, _) => MoveFile(-1);
        _moveDown.Click += (_, _) => MoveFile(1);
        _detachFile.Click += (_, _) => StageFileMembership(null);
        _moveFile.Click += (_, _) =>
        {
            if (_moveTarget.SelectedItem is ComboBoxItem { Tag: int queueId }) StageFileMembership(queueId);
        };
        ordering.Children.Add(_moveUp);
        ordering.Children.Add(_moveDown);
        panel.Children.Add(ordering);
        var moveRow = new StackPanel { Spacing = 8 };
        moveRow.Children.Add(_detachFile);
        moveRow.Children.Add(_moveTarget);
        moveRow.Children.Add(_moveFile);
        panel.Children.Add(moveRow);
        panel.Children.Add(_fileHint);
        _fileHint.Text = "Save applies membership changes first, then writes an exact order only if the current queue membership still matches the unsaved result.";
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private UIElement BuildLimitsTab()
    {
        // Left-aligned like the other tabs; MaxWidth alone centred it in a wide window.
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(10), MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = "Queue limits", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 17 });
        panel.Children.Add(_maxConcurrent);
        panel.Children.Add(_quotaEnabled);
        panel.Children.Add(_quotaMb);
        panel.Children.Add(_quotaHours);
        panel.Children.Add(new TextBlock { Text = "Quota use and its current window are owned by the Host; Apply changes only the queue's limit policy.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        _quotaEnabled.Checked += (_, _) => UpdateQuotaControls();
        _quotaEnabled.Unchecked += (_, _) => UpdateQuotaControls();
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private QueueDto? SelectedQueue
    {
        get
        {
            var id = (_queues.SelectedItem as QueueDto)?.Id;
            return _snapshot?.Settings.Queues.FirstOrDefault(queue => queue.Id == id);
        }
    }

    private async Task RefreshAsync()
    {
        if (_closed || _refreshing) return;
        _refreshing = true;
        try
        {
            var snapshot = await _connection.RefreshAsync();
            if (_closed) return;
            _snapshot = snapshot;
            if (!_draftsInitialized)
            {
                _draftsInitialized = true;
                _globalOptInBaseline = snapshot.Settings.AllowSystemCompletionActions;
                _allowSystemActions.IsChecked = _globalOptInBaseline;
                foreach (var schedule in snapshot.Settings.Schedules)
                {
                    _scheduleDrafts[schedule.Id] = Clone(schedule);
                    _scheduleBaselines[schedule.Id] = Clone(schedule);
                }
            }
            UpdateQueueList(SelectedQueue?.Id);
            if (_status.Text == "Loading queues…" ||
                _status.Text.StartsWith("Could not refresh queues: ", StringComparison.Ordinal) ||
                _status.Text == "Waiting for the background Host…") _status.Text = "Ready";
        }
        catch (Exception ex) { if (!_closed) _status.Text = "Could not refresh queues: " + ex.Message; }
        finally { _refreshing = false; }
    }

    private void OnConnectionChanged(bool connected, string? error)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            if (!connected) _status.Text = string.IsNullOrWhiteSpace(error) ? "Waiting for the background Host…" : error;
            else _ = RefreshAsync();
        });
    }

    private void UpdateQueueList(int? selectedId, bool force = false)
    {
        if (_snapshot is null) return;
        var queues = _snapshot.Settings.Queues.OrderBy(queue => queue.Order).ToArray();
        var visibleQueues = _queues.Items.OfType<QueueDto>().ToArray();
        if (!force && visibleQueues.Length == queues.Length && visibleQueues.Select(queue => (queue.Id, queue.Name, queue.Order))
                .SequenceEqual(queues.Select(queue => (queue.Id, queue.Name, queue.Order))))
        {
            var selectedLive = queues.FirstOrDefault(queue => queue.Id == selectedId);
            if (selectedLive is not null)
            {
                _start.IsEnabled = !selectedLive.IsRunning;
                _stop.IsEnabled = selectedLive.IsRunning;
                var liveJobs = _snapshot.Jobs.ToDictionary(job => job.Id);
                foreach (var row in _files.Items.OfType<JobViewModel>())
                    if (liveJobs.TryGetValue(row.Id, out var job)) row.Update(job);
            }
            return;
        }
        _queues.ItemsSource = queues;
        var selected = queues.FirstOrDefault(queue => queue.Id == selectedId) ?? queues.FirstOrDefault();
        _queues.SelectedItem = selected;
    }

    private void OnQueueSelectionChanged()
    {
        var queue = SelectedQueue;
        if (queue is null || _snapshot is null || _restoringQueue) return;
        if (_lastSelectedQueueId is { } previousQueue && !CaptureEditors(previousQueue))
        {
            _restoringQueue = true;
            _queues.SelectedItem = _queues.Items.OfType<QueueDto>().FirstOrDefault(item => item.Id == previousQueue);
            _restoringQueue = false;
            return;
        }
        _lastSelectedQueueId = queue.Id;
        _start.IsEnabled = !queue.IsRunning;
        _stop.IsEnabled = queue.IsRunning;
        if (!_queueDrafts.ContainsKey(queue.Id)) _queuePolicyBaselines[queue.Id] = Clone(queue);
        LoadQueueLimits(queue);
        LoadQueueFiles(queue);
        LoadScheduleChoices();
        UpdateScheduleTargetChoices();
    }

    private void LoadQueueLimits(QueueDto queue)
    {
        if (_queueDrafts.TryGetValue(queue.Id, out var draft))
        {
            _maxConcurrent.Text = draft.DesiredMaxConcurrent.ToString();
            _quotaEnabled.IsChecked = draft.DesiredQuotaEnabled;
            _quotaMb.Text = (draft.DesiredQuotaBytes / (1024m * 1024m)).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
            _quotaHours.Text = draft.DesiredQuotaHours.ToString();
        }
        else
        {
            _maxConcurrent.Text = queue.MaxConcurrentDownloads.ToString();
            _quotaEnabled.IsChecked = queue.QuotaEnabled;
            _quotaMb.Text = (queue.QuotaBytes / (1024m * 1024m)).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
            _quotaHours.Text = queue.QuotaWindowHours.ToString();
        }
        UpdateQuotaControls();
        _limitsLoadedKey = LimitsFormKey();
    }

    private void UpdateQuotaControls()
    {
        var enabled = _quotaEnabled.IsChecked == true;
        _quotaMb.IsEnabled = enabled;
        _quotaHours.IsEnabled = enabled;
    }

    private bool StageQueueLimits(int? queueId = null)
    {
        var queue = queueId is { } id ? _snapshot?.Settings.Queues.FirstOrDefault(item => item.Id == id)
            ?? _queuePolicyBaselines.GetValueOrDefault(id) : SelectedQueue;
        if (queue is null) return false;
        var current = _queueDrafts.GetValueOrDefault(queue.Id);
        var baseline = current?.Baseline ?? _queuePolicyBaselines.GetValueOrDefault(queue.Id) ?? Clone(queue);
        if (!int.TryParse(_maxConcurrent.Text, out var concurrent) || concurrent is < 1 or > 16)
        { _status.Text = "Simultaneous downloads must be between 1 and 16."; return false; }
        var useQuota = _quotaEnabled.IsChecked == true;
        var quotaMb = 0m;
        var hours = current?.DesiredQuotaHours ?? baseline.QuotaWindowHours;
        long quotaBytes = current?.DesiredQuotaBytes ?? baseline.QuotaBytes;
        if (useQuota && (!decimal.TryParse(_quotaMb.Text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out quotaMb) || quotaMb <= 0 || quotaMb > 10_485_760m))
        { _status.Text = "The quota must be a positive number of MB, up to 10,485,760."; return false; }
        if (useQuota && !int.TryParse(_quotaHours.Text, out hours) || useQuota && hours is < 1 or > 8_760)
        { _status.Text = "The quota window must be between 1 and 8,760 hours."; return false; }
        if (useQuota)
        {
            var bytes = quotaMb * 1024m * 1024m;
            if (bytes < 1 || bytes > long.MaxValue) { _status.Text = "The quota must represent at least one byte."; return false; }
            quotaBytes = (long)Math.Round(bytes, MidpointRounding.AwayFromZero);
        }
        _queueDrafts[queue.Id] = new QueuePolicyDraft(baseline, concurrent, useQuota, quotaBytes, hours,
            current?.DesiredRetryLimit, current?.RetryBaseline ?? baseline.RetryLimit);
        _limitsLoadedKey = LimitsFormKey();
        return true;
    }

    private void LoadQueueFiles(QueueDto queue)
    {
        if (_snapshot is null) return;
        var candidate = _snapshot.Jobs.Where(job => job.IsQueued && job.QueueId == queue.Id).ToArray();
        if (!_fileDrafts.TryGetValue(queue.Id, out var draft))
        {
            var byId = candidate.ToDictionary(job => job.Id);
            var order = candidate.OrderByDescending(job => job.Priority)
                .ThenBy(job => { var index = queue.JobOrder.IndexOf(job.Id); return index < 0 ? int.MaxValue : index; })
                .ThenBy(job => job.CreatedAt).Select(job => job.Id).ToList();
            draft = new QueueFileDraft(candidate.Select(job => (job.Id, job.QueueId)).ToDictionary(p => p.Id, p => p.QueueId), order);
            _fileDrafts[queue.Id] = draft;
        }
        var viewModels = draft.OrderIds.Where(id => byIdContains(candidate, id)).Select(id =>
        {
            var dto = candidate.First(job => job.Id == id);
            var vm = new JobViewModel();
            vm.Update(dto);
            return vm;
        }).ToList();
        _files.ItemsSource = new ObservableCollection<JobViewModel>(viewModels);
        _moveTarget.Items.Clear();
        foreach (var other in _snapshot.Settings.Queues.Where(item => item.Id != queue.Id).OrderBy(item => item.Order))
            _moveTarget.Items.Add(new ComboBoxItem { Content = other.Name, Tag = other.Id });
        _moveTarget.SelectedIndex = _moveTarget.Items.Count > 0 ? 0 : -1;
        UpdateFileButtons();
        _fileHint.Text = draft.MembershipChanges.Count == 0 && !draft.OrderDirty
            ? $"{candidate.Length} queued download(s). Order changes apply within matching priority groups."
            : "Membership/order changes are unsaved. Close discards them; Apply saves them.";
    }

    private static bool byIdContains(JobDto[] jobs, Guid id) => jobs.Any(job => job.Id == id);

    private void UpdateFileButtons()
    {
        var jobs = (_files.ItemsSource as ObservableCollection<JobViewModel>);
        var index = _files.SelectedIndex;
        var valid = jobs is not null && index >= 0 && index < jobs.Count;
        _moveUp.IsEnabled = valid && index > 0 && jobs![index].Priority == jobs[index - 1].Priority;
        _moveDown.IsEnabled = valid && index < jobs!.Count - 1 && jobs[index].Priority == jobs[index + 1].Priority;
        _detachFile.IsEnabled = valid;
        _moveTarget.IsEnabled = valid && _moveTarget.Items.Count > 0;
        _moveFile.IsEnabled = valid && _moveTarget.SelectedItem is ComboBoxItem;
    }

    private void MoveFile(int delta)
    {
        var jobs = _files.ItemsSource as ObservableCollection<JobViewModel>;
        var index = _files.SelectedIndex;
        if (jobs is null || index < 0 || index + delta < 0 || index + delta >= jobs.Count || jobs[index].Priority != jobs[index + delta].Priority) return;
        var queue = SelectedQueue;
        if (queue is null || !_fileDrafts.TryGetValue(queue.Id, out var draft)) return;
        var moved = jobs[index];
        var newIndex = index + delta;
        jobs.Move(index, newIndex);
        draft.OrderIds = jobs.Select(item => item.Id).ToList();
        draft.OrderDirty = true;
        _files.SelectedIndex = newIndex;
        _fileHint.Text = "File order unsaved. Click Apply to save.";
        UpdateFileButtons();
    }

    private void StageFileMembership(int? destinationQueueId)
    {
        var queue = SelectedQueue;
        if (queue is null || _snapshot is null || _files.SelectedItem is not JobViewModel selected || !_fileDrafts.TryGetValue(queue.Id, out var draft)) return;
        var originalQueueId = draft.OriginalMembership.TryGetValue(selected.Id, out var id) ? id : queue.Id;
        draft.MembershipChanges[selected.Id] = new MembershipChange(originalQueueId, destinationQueueId);
        draft.OrderIds.Remove(selected.Id);
        var jobs = _files.ItemsSource as ObservableCollection<JobViewModel>;
        jobs?.Remove(selected);
        if (_files.ItemsSource is ObservableCollection<JobViewModel> remaining) _files.SelectedIndex = Math.Min(_files.SelectedIndex, remaining.Count - 1);
        _fileHint.Text = destinationQueueId is null ? "Removal from this queue unsaved; the download and file will be kept." : "Queue move unsaved; the download and file will be kept.";
        UpdateFileButtons();
    }

    private void LoadScheduleChoices(Guid? selectedId = null)
    {
        if (_snapshot is null) return;
        _suppressSchedulePicker = true;
        _schedulePicker.Items.Clear();
        var queuesById = _snapshot.Settings.Queues.ToDictionary(queue => queue.Id);
        foreach (var schedule in _scheduleDrafts.Values.Where(schedule => !_removedSchedules.Contains(schedule.Id) && schedule.QueueId == SelectedQueue?.Id)
                     .OrderBy(schedule => schedule.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var queueName = queuesById.TryGetValue(schedule.QueueId, out var queue) ? queue.Name : "missing queue";
            _schedulePicker.Items.Add(new ComboBoxItem { Content = $"{schedule.Name} — {queueName}", Tag = schedule.Id });
        }
        var preferred = selectedId ?? _displayedScheduleId;
        var item = _schedulePicker.Items.OfType<ComboBoxItem>().FirstOrDefault(entry => entry.Tag is Guid id && id == preferred);
        _schedulePicker.SelectedItem = item ?? _schedulePicker.Items.FirstOrDefault();
        _suppressSchedulePicker = false;
        if (_schedulePicker.SelectedItem is ComboBoxItem { Tag: Guid selected }) LoadSchedule(selected);
        else ClearScheduleForm();
    }

    private void UpdateScheduleTargetChoices(int? selectedQueueId = null)
    {
        if (_snapshot is null) return;
        var oldId = selectedQueueId ?? (_scheduleTarget.SelectedItem as ComboBoxItem)?.Tag as int? ?? SelectedQueue?.Id;
        _scheduleTarget.Items.Clear();
        foreach (var queue in _snapshot.Settings.Queues.OrderBy(queue => queue.Order))
            _scheduleTarget.Items.Add(new ComboBoxItem { Content = queue.Name, Tag = queue.Id });
        _scheduleTarget.SelectedItem = _scheduleTarget.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is int id && id == oldId)
            ?? _scheduleTarget.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _scheduleTarget.IsEnabled = _scheduleTarget.Items.Count > 1;
        UpdateRunOptionsRetryLimit();
    }

    private void LoadSchedule(Guid id)
    {
        if (!_scheduleDrafts.TryGetValue(id, out var schedule)) return;
        _displayedScheduleId = id;
        _scheduleName.Text = schedule.Name;
        _scheduleEnabled.IsChecked = schedule.Enabled;
        _startTime.Text = schedule.StartTime;
        _stopTime.Text = schedule.StopTime ?? string.Empty;
        _days.Text = schedule.Days;
        _oneTimeDate.Text = schedule.Kind == "oneTime" && schedule.StartDate is { } date ? date.ToString("yyyy-MM-dd") : string.Empty;
        _openWhenDone.IsChecked = schedule.OpenFileWhenDone;
        _exitWhenDone.IsChecked = schedule.ExitWhenDone;
        _shutdownWhenDone.IsChecked = schedule.ShutdownWhenDone;
        _shutdownMode.SelectedIndex = schedule.ShutdownMode switch { "restart" => 1, "logoff" => 2, _ => 0 };
        _forceShutdown.IsChecked = schedule.ForceShutdown;
        UpdateScheduleTargetChoices(schedule.QueueId);
        var retryLimit = RetryLimitFor(schedule.QueueId);
        _runOptions = new SchedulerRunOptionsEditor(schedule, retryLimit);
        _runOptionsHost.Children.Clear();
        _runOptionsHost.Children.Add(_runOptions.Content);
        _removeSchedule.IsEnabled = true;
        _scheduleEditorEnabled = true;
        _scheduleHint.Text = "Apply saves your changes; Close discards unsaved edits.";
        _scheduleLoadedKey = ScheduleFormKey();
        UpdateScheduleEditorEnabled();
    }

    private void UpdateScheduleEditorEnabled()
    {
        foreach (var control in new Control[] { _scheduleName, _scheduleEnabled, _scheduleTarget,
            _startTime, _stopTime, _days, _oneTimeDate, _openWhenDone, _exitWhenDone, _shutdownWhenDone,
            _shutdownMode, _forceShutdown }) control.IsEnabled = _scheduleEditorEnabled;
        if (_runOptions?.Content is Control options) options.IsEnabled = _scheduleEditorEnabled;
        if (_scheduleEditorEnabled) UpdateShutdownControls();
    }

    private void ClearScheduleForm()
    {
        _displayedScheduleId = null;
        _scheduleName.Text = "";
        _scheduleEnabled.IsChecked = true;
        _startTime.Text = "09:00";
        _stopTime.Text = "";
        _days.Text = "Mon,Tue,Wed,Thu,Fri,Sat,Sun";
        _oneTimeDate.Text = "";
        _openWhenDone.IsChecked = false;
        _exitWhenDone.IsChecked = false;
        _shutdownWhenDone.IsChecked = false;
        _shutdownMode.SelectedIndex = 0;
        _forceShutdown.IsChecked = false;
        _removeSchedule.IsEnabled = false;
        _scheduleEditorEnabled = false;
        _runOptions = new SchedulerRunOptionsEditor(null, null);
        _runOptionsHost.Children.Clear();
        _runOptionsHost.Children.Add(_runOptions.Content);
        _scheduleHint.Text = "Click New schedule to add one.";
        _scheduleLoadedKey = ScheduleFormKey();
        UpdateScheduleEditorEnabled();
    }

    private void BeginNewSchedule()
    {
        if (!CaptureEditors(SelectedQueue?.Id)) return;
        if (_snapshot is null || _snapshot.Settings.Queues.Count == 0) { _status.Text = "Create a queue before adding a schedule."; return; }
        _displayedScheduleId = null;
        _schedulePicker.SelectedItem = null;
        _scheduleName.Text = "New schedule";
        _scheduleEnabled.IsChecked = true;
        _startTime.Text = "09:00";
        _stopTime.Text = "";
        _days.Text = "Mon,Tue,Wed,Thu,Fri,Sat,Sun";
        _oneTimeDate.Text = "";
        _openWhenDone.IsChecked = false;
        _exitWhenDone.IsChecked = false;
        _shutdownWhenDone.IsChecked = false;
        _shutdownMode.SelectedIndex = 0;
        _forceShutdown.IsChecked = false;
        UpdateScheduleTargetChoices(SelectedQueue?.Id);
        var queueId = (_scheduleTarget.SelectedItem as ComboBoxItem)?.Tag as int?;
        var retryLimit = RetryLimitFor(queueId);
        _runOptions = new SchedulerRunOptionsEditor(null, retryLimit);
        _runOptionsHost.Children.Clear();
        _runOptionsHost.Children.Add(_runOptions.Content);
        _scheduleEditorEnabled = true;
        _removeSchedule.IsEnabled = false;
        _scheduleHint.Text = "Apply saves this new schedule.";
        _scheduleLoadedKey = string.Empty;
        UpdateScheduleEditorEnabled();
    }

    private int? RetryLimitFor(int? queueId)
    {
        if (queueId is not { } id) return null;
        return _queueDrafts.TryGetValue(id, out var draft) ? draft.DesiredRetryLimit
            : _snapshot?.Settings.Queues.FirstOrDefault(queue => queue.Id == id)?.RetryLimit;
    }

    private void UpdateRunOptionsRetryLimit()
    {
        var targetId = (_scheduleTarget.SelectedItem as ComboBoxItem)?.Tag as int?;
        var retry = RetryLimitFor(targetId);
        _runOptions?.SetRetryLimit(retry);
    }

    private bool StageScheduleDraft()
    {
        if (_snapshot is null || _runOptions is null) return false;
        if (_scheduleTarget.SelectedItem is not ComboBoxItem { Tag: int queueId } || !_snapshot.Settings.Queues.Any(queue => queue.Id == queueId))
        { _status.Text = "Choose an existing target queue."; return false; }
        var name = _scheduleName.Text.Trim();
        if (name.Length is < 1 or > 80) { _status.Text = "Enter a schedule name of 1–80 characters."; return false; }
        if (!TimeOnly.TryParseExact(_startTime.Text.Trim(), "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start))
        { _status.Text = "Start time must use HH:mm."; return false; }
        TimeOnly? stop = null;
        if (!string.IsNullOrWhiteSpace(_stopTime.Text))
        {
            if (!TimeOnly.TryParseExact(_stopTime.Text.Trim(), "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedStop))
            { _status.Text = "Stop time must use HH:mm."; return false; }
            stop = parsedStop;
        }
        DateOnly? date = null;
        if (!string.IsNullOrWhiteSpace(_oneTimeDate.Text))
        {
            if (!DateOnly.TryParseExact(_oneTimeDate.Text.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDate))
            { _status.Text = "One-time date must use yyyy-MM-dd."; return false; }
            date = parsedDate;
        }
        if (!_runOptions.TryGet(out var startup, out var timed, out var repeat, out var retry, out var runError))
        { _status.Text = runError ?? "Check scheduler run options."; return false; }

        ScheduleDto? current = null;
        ScheduleDto draft;
        if (_displayedScheduleId is { } selected && _scheduleDrafts.TryGetValue(selected, out var found))
        { current = found; draft = Clone(current); }
        else
        {
            draft = new ScheduleDto { Id = Guid.NewGuid(), Enabled = true };
            _newSchedules.Add(draft.Id);
        }
        draft.Name = name;
        draft.QueueId = queueId;
        draft.Enabled = _scheduleEnabled.IsChecked == true;
        draft.Kind = date is null ? "daily" : "oneTime";
        draft.StartDate = date;
        draft.Days = string.IsNullOrWhiteSpace(_days.Text) ? "Mon,Tue,Wed,Thu,Fri,Sat,Sun" : _days.Text.Trim();
        draft.StartTime = start.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        draft.StopTime = stop?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        draft.StartOnHostStartup = startup;
        draft.TimedStartEnabled = timed;
        draft.RepeatEveryMinutes = repeat;
        draft.OpenFileWhenDone = _openWhenDone.IsChecked == true;
        draft.ExitWhenDone = _exitWhenDone.IsChecked == true;
        draft.ShutdownWhenDone = _shutdownWhenDone.IsChecked == true;
        draft.ShutdownMode = _shutdownMode.SelectedIndex switch { 1 => "restart", 2 => "logoff", _ => "shutdown" };
        draft.ForceShutdown = _forceShutdown.IsChecked == true;
        _scheduleDrafts[draft.Id] = draft;
        _dirtySchedules.Add(draft.Id);
        _displayedScheduleId = draft.Id;
        if (current is not null) _scheduleBaselines.TryAdd(draft.Id, Clone(current));

        var queue = _snapshot.Settings.Queues.First(item => item.Id == queueId);
        var existingQueueDraft = _queueDrafts.GetValueOrDefault(queueId);
        var queueBaseline = existingQueueDraft?.Baseline ?? _queuePolicyBaselines.GetValueOrDefault(queueId) ?? Clone(queue);
        if (retry != (existingQueueDraft?.DesiredRetryLimit ?? queue.RetryLimit))
            _queueDrafts[queueId] = new QueuePolicyDraft(existingQueueDraft?.Baseline ?? queueBaseline,
                existingQueueDraft?.DesiredMaxConcurrent ?? queue.MaxConcurrentDownloads,
                existingQueueDraft?.DesiredQuotaEnabled ?? queue.QuotaEnabled,
                existingQueueDraft?.DesiredQuotaBytes ?? queue.QuotaBytes,
                existingQueueDraft?.DesiredQuotaHours ?? queue.QuotaWindowHours,
                retry, existingQueueDraft?.RetryBaseline ?? queue.RetryLimit);
        LoadScheduleChoices(draft.Id);
        _scheduleLoadedKey = ScheduleFormKey();
        return true;
    }

    private void StageScheduleRemoval()
    {
        if (_displayedScheduleId is not { } id || !_scheduleDrafts.Remove(id, out var schedule)) return;
        if (_newSchedules.Remove(id))
        {
            _dirtySchedules.Remove(id);
            _scheduleBaselines.Remove(id);
        }
        else
        {
            _removedSchedules.Add(id);
            _dirtySchedules.Remove(id);
        }
        _displayedScheduleId = null;
        LoadScheduleChoices();
        _status.Text = $"Removal of '{schedule.Name}' unsaved. Click Apply to save.";
    }

    private void MarkGlobalOptInDirty()
    {
        if (_snapshot is null) return;
        _globalOptInDirty = _allowSystemActions.IsChecked != _globalOptInBaseline;
    }

    private void UpdateShutdownControls()
    {
        var enabled = _shutdownWhenDone.IsChecked == true;
        _shutdownMode.IsEnabled = enabled;
        _forceShutdown.IsEnabled = enabled;
    }

    private async Task CreateQueueAsync()
    {
        var name = await AskTextAsync("New queue", "Queue name", "Create");
        if (name is null) return;
        await QueueCrudAsync(new QueueActionRequest { Action = "create", Name = name, MaxConcurrentDownloads = 1, QuotaEnabled = false });
    }

    private async Task RenameQueueAsync()
    {
        if (SelectedQueue is not { } selected) return;
        var name = await AskTextAsync("Rename queue", "Queue name", "Save", selected.Name);
        if (name is null) return;
        var latest = await _connection.RefreshAsync();
        var queue = latest.Settings.Queues.FirstOrDefault(item => item.Id == selected.Id);
        if (queue is null) { _status.Text = "This queue was removed before it could be renamed."; await RefreshAsync(); return; }
        await QueueCrudAsync(new QueueActionRequest
        {
            Action = "policy", QueueId = queue.Id, Name = name,
            MaxConcurrentDownloads = queue.MaxConcurrentDownloads,
            QuotaEnabled = queue.QuotaEnabled, QuotaBytes = queue.QuotaBytes, QuotaWindowHours = queue.QuotaWindowHours,
        });
    }

    private async Task DeleteQueueAsync()
    {
        if (SelectedQueue is not { } queue || _snapshot is null) return;
        if (queue.Id == _snapshot.Settings.DefaultQueueId) { _status.Text = "The default queue cannot be deleted."; return; }
        var targets = _snapshot.Settings.Queues.Where(item => item.Id != queue.Id).OrderBy(item => item.Order).ToArray();
        if (targets.Length == 0) { _status.Text = "Create another queue before deleting this one."; return; }
        var choice = new ComboBox { Header = "Move downloads and schedules to", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var target in targets) choice.Items.Add(new ComboBoxItem { Content = target.Name, Tag = target.Id });
        var defaultTarget = targets.FirstOrDefault(item => item.Id == _snapshot.Settings.DefaultQueueId) ?? targets[0];
        choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().First(item => item.Tag is int id && id == defaultTarget.Id);
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Downloads and schedules will move to the selected queue. Downloaded files stay in their current folders. This keeps all download files.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(choice);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Delete {queue.Name}?",
            Content = panel,
            PrimaryButtonText = "Delete queue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ((FrameworkElement)Content).ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || choice.SelectedItem is not ComboBoxItem { Tag: int targetId }) return;
        await QueueCrudAsync(new QueueActionRequest { Action = "delete", QueueId = queue.Id, MoveContentsToQueueId = targetId });
    }

    private async Task<string?> AskTextAsync(string title, string header, string accept, string initial = "")
    {
        var text = new TextBox { Header = header, Text = initial, MaxLength = 80 };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = text,
            PrimaryButtonText = accept,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ((FrameworkElement)Content).ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var value = text.Text.Trim();
        if (value.Length is < 1 or > 80) { _status.Text = "Queue names must contain 1–80 characters."; return null; }
        return value;
    }

    private async Task QueueCrudAsync(QueueActionRequest request)
    {
        try
        {
            var reply = await _connection.QueueActionAsync(request);
            var result = reply.GetPayload<SettingsResult>();
            if (reply.Type != MessageTypes.SettingsResult || !result.Ok) { _status.Text = result.Error ?? "The Host rejected the queue change."; return; }
            await RefreshAsync();
            UpdateQueueList(request.Action == "create" ? result.Settings.Queues.OrderByDescending(item => item.Order).FirstOrDefault()?.Id : request.Action == "delete" ? request.MoveContentsToQueueId : request.QueueId);
            _status.Text = $"Queue {request.Action}d.";
        }
        catch (Exception ex) { _status.Text = "Could not update queue: " + ex.Message; }
    }

    private async Task RunQueueActionAsync(string action)
    {
        if (SelectedQueue is not { } queue) return;
        try
        {
            var reply = await _connection.QueueActionAsync(new QueueActionRequest { QueueId = queue.Id, Action = action });
            var result = reply.GetPayload<SettingsResult>();
            if (reply.Type != MessageTypes.SettingsResult || !result.Ok) { _status.Text = result.Error ?? $"Could not {action} queue."; return; }
            await RefreshAsync();
            _status.Text = action == "start" ? $"{queue.Name} started now." : $"{queue.Name} stopped.";
        }
        catch (Exception ex) { _status.Text = $"Could not {action} queue: {ex.Message}"; }
    }

    private async Task ApplyAsync()
    {
        if (_snapshot is null) { _status.Text = "Queue settings are not loaded."; return; }
        if (!CaptureEditors(SelectedQueue?.Id)) return;
        _apply.IsEnabled = false;
        _editorSurface.IsEnabled = false;
        var errors = new List<string>();
        var appliedQueueIds = new List<int>();
        var appliedSchedules = new List<Guid>();
        var removedSchedules = new List<Guid>();
        var newScheduleIds = new List<Guid>();
        var optInApplied = false;
        try
        {
            var latest = await _connection.RefreshAsync();
            var merged = Clone(latest.Settings);
            var scheduleEditsToApply = new Dictionary<Guid, ScheduleDto>();

            foreach (var (queueId, draft) in _queueDrafts)
            {
                var current = merged.Queues.FirstOrDefault(queue => queue.Id == queueId);
                if (current is null) { errors.Add($"Queue '{draft.Baseline.Name}' was removed; its unsaved limits were not applied."); continue; }
                var original = draft.Baseline;
                if (current.MaxConcurrentDownloads != original.MaxConcurrentDownloads || current.QuotaEnabled != original.QuotaEnabled || current.QuotaBytes != original.QuotaBytes || current.QuotaWindowHours != original.QuotaWindowHours)
                { errors.Add($"Queue '{current.Name}' limit policy changed elsewhere; its unsaved limits were not applied."); continue; }
                if (draft.DesiredRetryLimit != draft.RetryBaseline && current.RetryLimit != draft.RetryBaseline)
                { errors.Add($"Queue '{current.Name}' retry policy changed elsewhere; its unsaved queue edits were not applied."); continue; }
                current.MaxConcurrentDownloads = draft.DesiredMaxConcurrent;
                current.QuotaEnabled = draft.DesiredQuotaEnabled;
                current.QuotaBytes = draft.DesiredQuotaBytes;
                current.QuotaWindowHours = draft.DesiredQuotaHours;
                if (draft.DesiredRetryLimit != draft.RetryBaseline)
                    current.RetryLimit = draft.DesiredRetryLimit;
                appliedQueueIds.Add(queueId);
            }

            foreach (var id in _dirtySchedules)
            {
                if (_newSchedules.Contains(id))
                {
                    if (merged.Schedules.Any(schedule => schedule.Id == id)) { errors.Add($"New schedule ID {id} already exists."); continue; }
                    if (!_scheduleDrafts.TryGetValue(id, out var added) || !merged.Queues.Any(queue => queue.Id == added.QueueId))
                    { errors.Add("A new schedule's target queue was removed; the schedule was not applied."); continue; }
                    merged.Schedules.Add(Clone(added));
                    newScheduleIds.Add(id);
                    continue;
                }
                if (!_scheduleDrafts.TryGetValue(id, out var edited) || !_scheduleBaselines.TryGetValue(id, out var baseline)) continue;
                var current = merged.Schedules.FirstOrDefault(schedule => schedule.Id == id);
                if (current is null) { errors.Add($"Schedule '{baseline.Name}' was removed elsewhere; its unsaved edit was not applied."); continue; }
                if (!SameScheduleConfig(current, baseline)) { errors.Add($"Schedule '{baseline.Name}' changed elsewhere; its unsaved edit was not applied."); continue; }
                if (!merged.Queues.Any(queue => queue.Id == edited.QueueId)) { errors.Add($"The target queue for schedule '{edited.Name}' was removed; its unsaved edit was not applied."); continue; }
                CopyScheduleConfig(edited, current);
                scheduleEditsToApply[id] = Clone(current);
                appliedSchedules.Add(id);
            }

            foreach (var id in _removedSchedules)
            {
                if (!_scheduleBaselines.TryGetValue(id, out var baseline)) continue;
                var current = merged.Schedules.FirstOrDefault(schedule => schedule.Id == id);
                if (current is null) { errors.Add($"Schedule '{baseline.Name}' was already removed elsewhere."); continue; }
                if (!SameScheduleConfig(current, baseline)) { errors.Add($"Schedule '{baseline.Name}' changed elsewhere; it was not removed."); continue; }
                merged.Schedules.Remove(current);
                removedSchedules.Add(id);
            }

            if (_globalOptInDirty)
            {
                if (merged.AllowSystemCompletionActions != _globalOptInBaseline)
                    errors.Add("The global system-action setting changed elsewhere; your unsaved opt-in was not applied.");
                else { merged.AllowSystemCompletionActions = _allowSystemActions.IsChecked == true; optInApplied = true; }
            }

            var hasSettingsChanges = appliedQueueIds.Count > 0 || appliedSchedules.Count > 0 || removedSchedules.Count > 0 || newScheduleIds.Count > 0 || optInApplied;
            if (hasSettingsChanges)
            {
                var settingsReply = await _connection.UpdateSettingsAsync(merged);
                var result = settingsReply.GetPayload<SettingsResult>();
                if (settingsReply.Type != MessageTypes.SettingsResult || !result.Ok)
                {
                    errors.Add("Settings were not saved: " + (result.Error ?? "The Host rejected the update."));
                    appliedQueueIds.Clear(); appliedSchedules.Clear(); removedSchedules.Clear(); newScheduleIds.Clear(); optInApplied = false;
                }
                else
                {
                    _snapshot = new SnapshotDto { Settings = result.Settings, Jobs = latest.Jobs };
                    foreach (var id in appliedSchedules)
                    {
                        _scheduleDrafts[id] = Clone(scheduleEditsToApply[id]);
                        _scheduleBaselines[id] = Clone(scheduleEditsToApply[id]);
                    }
                    foreach (var id in removedSchedules) { _scheduleDrafts.Remove(id); _scheduleBaselines.Remove(id); _dirtySchedules.Remove(id); _removedSchedules.Remove(id); }
                    foreach (var id in newScheduleIds) { _scheduleBaselines[id] = Clone(_scheduleDrafts[id]); _newSchedules.Remove(id); _dirtySchedules.Remove(id); }
                    foreach (var id in appliedSchedules) _dirtySchedules.Remove(id);
                    foreach (var id in appliedQueueIds) _queueDrafts.Remove(id);
                    if (optInApplied) { _globalOptInBaseline = _snapshot.Settings.AllowSystemCompletionActions; _globalOptInDirty = false; }
                }
            }

            await ApplyFileDraftsAsync(errors);
            await RefreshAsync();
            UpdateQueueList(SelectedQueue?.Id);
            LoadScheduleChoices(_displayedScheduleId);
            if (errors.Count == 0) _status.Text = "Changes saved.";
            else _status.Text = string.Join(" ", errors);
        }
        catch (Exception ex) { _status.Text = "Could not apply unsaved changes: " + ex.Message; }
        finally { if (!_closed) { _apply.IsEnabled = true; _editorSurface.IsEnabled = true; } }
    }

    private async Task ApplyFileDraftsAsync(List<string> errors)
    {
        foreach (var (queueId, draft) in _fileDrafts.ToArray())
        {
            if (draft.MembershipChanges.Count == 0 && !draft.OrderDirty) continue;
            var queueName = _snapshot?.Settings.Queues.FirstOrDefault(queue => queue.Id == queueId)?.Name ?? $"Queue {queueId}";
            var membershipFailures = new HashSet<Guid>();
            foreach (var (jobId, change) in draft.MembershipChanges.ToArray())
            {
                try
                {
                    var latest = await _connection.RefreshAsync();
                    var job = latest.Jobs.FirstOrDefault(item => item.Id == jobId);
                    if (job is null || job.QueueId != change.ExpectedSourceQueueId)
                    { errors.Add($"A download unsaved for {queueName} membership changed elsewhere; it was left untouched."); membershipFailures.Add(jobId); continue; }
                    var reply = change.TargetQueueId is { } destination
                        ? await _connection.MoveToQueueAsync(jobId, destination)
                        : await _connection.DetachFromQueueAsync(jobId);
                    var result = reply.GetPayload<JobActionResponse>();
                    if (reply.Type != MessageTypes.JobActionResult || !result.Ok)
                    { errors.Add($"Could not update queue membership for {job.FileName}: {result.Error ?? "The Host rejected the change."}"); membershipFailures.Add(jobId); continue; }
                    draft.MembershipChanges.Remove(jobId);
                    draft.OriginalMembership.Remove(jobId);
                }
                catch (Exception ex) { errors.Add($"Could not update a download's queue membership: {ex.Message}"); membershipFailures.Add(jobId); }
            }

            if (draft.OrderDirty && membershipFailures.Count == 0)
            {
                var latest = await _connection.RefreshAsync();
                var currentQueue = latest.Settings.Queues.FirstOrDefault(item => item.Id == queueId);
                if (currentQueue is null)
                { errors.Add($"{queueName} was removed before file order could be saved."); continue; }
                var actualMembers = latest.Jobs.Where(job => job.IsQueued && job.QueueId == queueId).Select(job => job.Id).ToHashSet();
                var expected = draft.OrderIds.ToHashSet();
                if (!actualMembers.SetEquals(expected))
                { errors.Add($"{queueName} membership changed while applying order. The order was left untouched; refresh and stage it again."); continue; }
                var reply = await _connection.QueueActionAsync(new QueueActionRequest { QueueId = queueId, Action = "reorderfiles", JobOrder = draft.OrderIds.ToList() });
                var result = reply.GetPayload<SettingsResult>();
                if (reply.Type != MessageTypes.SettingsResult || !result.Ok)
                { errors.Add($"Could not save file order for {queueName}: {result.Error ?? "The Host rejected the order."}"); continue; }
                draft.OrderDirty = false;
            }
            if (draft.MembershipChanges.Count == 0 && !draft.OrderDirty) _fileDrafts.Remove(queueId);
        }
    }

    private static bool SameScheduleConfig(ScheduleDto left, ScheduleDto right)
        => left.Name == right.Name && left.QueueId == right.QueueId && left.Enabled == right.Enabled && left.Kind == right.Kind &&
           left.StartDate == right.StartDate && left.Days == right.Days && left.StartTime == right.StartTime && left.StopTime == right.StopTime &&
           left.StartOnHostStartup == right.StartOnHostStartup && left.TimedStartEnabled == right.TimedStartEnabled &&
           left.RepeatEveryMinutes == right.RepeatEveryMinutes && left.OpenFileWhenDone == right.OpenFileWhenDone &&
           left.ExitWhenDone == right.ExitWhenDone && left.ShutdownWhenDone == right.ShutdownWhenDone &&
           left.ShutdownMode == right.ShutdownMode && left.ForceShutdown == right.ForceShutdown;

    private static void CopyScheduleConfig(ScheduleDto source, ScheduleDto target)
    {
        target.Name = source.Name; target.QueueId = source.QueueId; target.Enabled = source.Enabled; target.Kind = source.Kind;
        target.StartDate = source.StartDate; target.Days = source.Days; target.StartTime = source.StartTime; target.StopTime = source.StopTime;
        target.StartOnHostStartup = source.StartOnHostStartup; target.TimedStartEnabled = source.TimedStartEnabled;
        target.RepeatEveryMinutes = source.RepeatEveryMinutes; target.OpenFileWhenDone = source.OpenFileWhenDone;
        target.ExitWhenDone = source.ExitWhenDone; target.ShutdownWhenDone = source.ShutdownWhenDone;
        target.ShutdownMode = source.ShutdownMode; target.ForceShutdown = source.ForceShutdown;
    }

    private static T Clone<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value)) ?? throw new InvalidDataException("Could not copy unsaved scheduler data.");

    private sealed record QueuePolicyDraft(QueueDto Baseline, int DesiredMaxConcurrent, bool DesiredQuotaEnabled,
        long DesiredQuotaBytes, int DesiredQuotaHours, int? DesiredRetryLimit, int? RetryBaseline);

    private sealed class QueueFileDraft(Dictionary<Guid, int> originalMembership, List<Guid> orderIds)
    {
        public Dictionary<Guid, int> OriginalMembership { get; } = originalMembership;
        public List<Guid> OrderIds { get; set; } = orderIds;
        public Dictionary<Guid, MembershipChange> MembershipChanges { get; } = new();
        public bool OrderDirty { get; set; }
    }

    private sealed record MembershipChange(int ExpectedSourceQueueId, int? TargetQueueId);
}
