using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Windows.Graphics;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T1: one file of a torrent in Progress Details' Files tab. Public because the list's bindings read it by reflection.
/// The window updates it in place, so a refresh redraws only what changed.
/// </summary>
public sealed class TorrentFileRow : INotifyPropertyChanged
{
    private TorrentFileDto _file;
    private bool _complete;
    private bool _checked;
    private bool _canCheck;

    internal TorrentFileRow(TorrentFileDto file, bool complete, bool isChecked, bool canCheck)
    {
        _file = file;
        _complete = complete;
        _checked = isChecked;
        _canCheck = canCheck;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal int Index => _file.Index;

    public string Name => ProgressOverviewText.FilePath(_file.Path);

    public string Size => UiFormat.Bytes(_file.Size);

    /// <summary>"71%", "Done" or "Skipped", by the box as it is ticked now.</summary>
    public string Progress => ProgressOverviewText.FileProgress(Shown, _complete);

    public string AccessibleName => ProgressOverviewText.FileRowName(Shown, _complete);

    /// <summary>The box. A click writes here through the binding; the window then decides and calls <see cref="ShowChecked"/>.</summary>
    public bool Checked
    {
        get => _checked;
        set => _checked = value;
    }

    public bool CanCheck => _canCheck;

    private string _priority = TorrentPriorities.Normal;
    private bool _showPriority;

    /// <summary>T3: High, Normal or Low while ticked; Skip while not.</summary>
    public string Priority => TorrentPriorities.Name(_checked ? _priority : TorrentPriorities.Skip);

    public Visibility PriorityVisibility => _showPriority ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>T3: the priority it has while ticked (never Skip).</summary>
    internal string PriorityValue => _priority;

    /// <summary>T3: the file's priority (Skip keeps the one it had for when it is ticked again) and whether the column shows.</summary>
    internal void SetPriority(string priority, bool shown)
    {
        var value = priority is TorrentPriorities.Skip || !TorrentPriorities.IsKnown(priority) ? _priority : priority;
        if (value == _priority && shown == _showPriority) return;
        var visibilityChanged = shown != _showPriority;
        _priority = value;
        _showPriority = shown;
        Raise(nameof(Priority));
        if (visibilityChanged) Raise(nameof(PriorityVisibility));
    }

    private TorrentFileDto Shown => _file.Wanted == _checked ? _file
        : new TorrentFileDto { Index = _file.Index, Path = _file.Path, Size = _file.Size, Wanted = _checked, Done = _file.Done };

    internal void Apply(TorrentFileDto file, bool complete, bool isChecked)
    {
        // Compared by field, not by text: a refresh of 100,000 rows makes no strings for rows that didn't change.
        var old = _file;
        var changed = old.Path != file.Path || old.Size != file.Size || old.Wanted != file.Wanted || old.Done != file.Done
            || _complete != complete || _checked != isChecked || old.Priority != file.Priority;
        var checkedChanged = isChecked != _checked;
        _file = file;
        _complete = complete;
        _checked = isChecked;
        if (!changed) return;
        if (checkedChanged) Raise(nameof(Checked));
        if (old.Path != file.Path) Raise(nameof(Name));
        if (old.Size != file.Size) Raise(nameof(Size));
        Raise(nameof(Progress));
        Raise(nameof(AccessibleName));
        if (checkedChanged) Raise(nameof(Priority));
    }

    /// <summary>Puts the box to this, even when the value is the same: a click may have moved the box alone.</summary>
    internal void ShowChecked(bool value)
    {
        _checked = value;
        Raise(nameof(Checked));
        Raise(nameof(Progress));
        Raise(nameof(AccessibleName));
        Raise(nameof(Priority));
    }

    internal void SetCanCheck(bool value)
    {
        if (_canCheck == value) return;
        _canCheck = value;
        Raise(nameof(CanCheck));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => AccessibleName;
}

/// <summary>A replaceable, per-job frontend. Closing it never stops the Host.</summary>
public sealed partial class DownloadWindow : Window
{
    private readonly Guid _jobId;
    private readonly HostConnection _connection;
    private readonly JobViewModel _iconJob = new();
    private JobDto? _job;
    private SettingsDto? _settings;
    private bool _globalShowCompletion = true;
    private bool _connected;
    private bool _closed;
    private bool _updatingControls;
    private readonly bool _manual;
    private bool _completedLayout;
    private bool _refreshingCompletion;
    private long? _displayedSpeedLimit;
    // UI.B20: the graph takes one sample a second on this window's clock, as the main window's does, not one per update.
    private readonly DispatcherTimer _chartTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // T1: a torrent's Files tab. Read when the tab opens and again when something changes the list: the job's state, its
    // file count or metadata, the file choice, or a reconnect, and while it downloads on its progress updates (at most every
    // TorrentRules.FileProgressReadMs, for each file's done bytes). No timer.
    private List<TorrentFileRow> _fileRows = [];
    /// <summary>The files shown ticked: the Host's choice, or the user's while the torrent waits for one.</summary>
    private HashSet<int> _wanted = [];
    private bool _isTorrent;
    private bool _filesLoaded;
    private bool _loadingFiles;
    /// <summary>A reload was asked for while a read was out: that read's answer is old, and another read follows it.</summary>
    private bool _filesStale;
    /// <summary><see cref="Environment.TickCount64"/> when the last read began.</summary>
    private long _filesReadAt;
    private bool _filesShown;
    private string? _filesState;
    private (int Count, bool HasMetadata, int Wanted, long WantedBytes)? _filesShape;
    private bool _savingFiles;
    /// <summary>Counts choices sent, so a list read before one doesn't put back the files it changed.</summary>
    private int _choiceVersion;
    /// <summary>The torrent waits for a choice of files: ticks stay here until Start sends them.</summary>
    private bool _choosing;
    private bool _filesReadOnly;
    private bool _openedOnFiles;
    private bool _filesNoteFromLoad;

    /// <param name="target">The Host the download is on: this PC, or a saved server (<c>--server</c>).</param>
    public DownloadWindow(Guid jobId, bool manual = false, ServerTarget? target = null)
    {
        _jobId = jobId;
        _connection = new HostConnection(target);
        ShownServer.Target = _connection.Target;
        _manual = manual;
        InitializeComponent();
        FileIconHeader.DataContext = _iconJob;
        AppTheme.Track(Root);
        WindowLayout.ResizeForDpi(this, 640, 640);
        WindowPlacement.Track(this, "progress", restoreSize: false);
        var icon = WindowLayout.AppIconPath;
        if (File.Exists(icon)) AppWindow.SetIcon(WindowLayout.IconArgument(icon));
        Title = ServerNames.ForWindow("Progress Details — Inbound Download Manager", _connection.Target);
        WindowLayout.ApplyChrome(this, Root);
        // U-WL-17: Enter opens the finished file, the window's default action.
        var enter = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Enter };
        enter.Invoked += (_, args) =>
        {
            if (_job?.State != "completed" || OpenButton.Visibility != Visibility.Visible) return;
            args.Handled = true;
            OpenPath(false);
        };
        Root.KeyboardAccelerators.Add(enter);
        Root.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        _connection.HostExiting += () => DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
        _connection.SnapshotReceived += snapshot => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _settings = snapshot.Settings;
            if (_connection.IsRemote) ShownServer.Describe(_connection.Hello?.HostKind, snapshot.Settings.DefaultDownloadDirectory);
            // Another server's theme is its own; this window keeps this PC's.
            if (!_connection.IsRemote) AppTheme.FollowSettings(snapshot.Settings);
            _globalShowCompletion = snapshot.Settings.ShowCompletionDialog;
            var job = snapshot.Jobs.FirstOrDefault(candidate => candidate.Id == _jobId);
            if (job is null) ShowRemoved();
            else
            {
                Update(job, completionSettingsRefreshed: true);
                _ = LoadSpeedHistoryAsync();
            }
        });
        _connection.JobChanged += (job, type) =>
        {
            if (job.Id != _jobId) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed) return;
                if (type == "removed") ShowRemoved();
                else Update(job);
            });
        };
        _connection.ConnectionChanged += (connected, error) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            var reconnected = connected && !_connected;
            _connected = connected;
            Details.SetConnected(connected);
            if (!connected) Notice.Text = $"Host disconnected. Reconnecting… {error}";
            else if (Notice.Text.StartsWith("Host disconnected", StringComparison.Ordinal)) Notice.Text = string.Empty;
            // Back from a drop the files may have changed; a Files tab opened while the Host was away reads them now.
            if (reconnected && _isTorrent) ReloadFiles();
            // A drop ends a paused download's flat line (audit M11); the snapshot after a reconnect starts it again.
            UpdateChartTimer();
            UpdateButtons();
        });
        Details.UseTableOnly();
        Details.Attach(_connection, SegmentMap, OverallFooter, Root);
        Details.AddMap(OverviewMap);
        Details.Problem += words => Notice.Text = words;
        InitializeTorrentTabs();
        InitializeTorrentControls();
        // The graph draws and slides only while it can be seen; samples still go into its history.
        Chart.Heading = "Speed";
        Chart.PeriodSeconds = 60;
        VisibilityChanged += (_, _) => UpdateChartSeen();
        Activated += (_, _) => UpdateChartSeen();
        AppWindow.Changed += (_, change) => { if (change.DidPresenterChange || change.DidVisibilityChange) UpdateChartSeen(); };
        _chartTimer.Tick += (_, _) => SampleChart();
        Closed += async (_, _) =>
        {
            _closed = true;
            _chartTimer.Stop();
            Details.Stop();
            await _connection.DisposeAsync();
        };
        UseSpeedLimit.Click += (_, _) => SpeedLimitInput.IsEnabled = UseSpeedLimit.IsChecked == true && _job?.CanChangeSpeedLimit == true;
        UpdateButtons();
        _connection.Start();
    }

    /// <summary>
    /// Brought to the front by another launch for this download, or by the main window when a torrent's file list came
    /// in (<see cref="TorrentRules.NextFileChoice"/>): a torrent waiting for its files shows them, since this window is
    /// then where they are chosen.
    /// </summary>
    /// <summary>The main window of this Windows account runs (shown or hidden). A failure to look counts as none.</summary>
    private static bool MainWindowRuns()
    {
        try
        {
            var name = WindowInstance.EventName(PipeNames.CurrentUserKey(), update: false, job: null, package: null, serverId: null);
#if HAS_UNO
            name += ".Uno";
#endif
            return ActivationSignal.IsOwned(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    public void ShowWindow()
    {
        if (_choosing && FilesTab.Visibility == Visibility.Visible) Tabs.SelectedItem = FilesTab;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowSelectedPage();

    private void ShowSelectedPage()
    {
        var selected = Tabs.SelectedItem;
        // A finished download shows its overview only; a finished torrent keeps its Files tab too.
        var files = _isTorrent && selected == FilesTab;
        // T2: and its Peers, Trackers and Pieces & Speed, finished or not (a finished torrent may seed).
        var peers = _isTorrent && selected == PeersTab;
        var trackers = _isTorrent && selected == TrackersTab;
        var pieces = _isTorrent && selected == PiecesTab;
        // T3: a finished torrent keeps its Options tab while the Host has the torrent controls (its seeding goal and upload limit).
        var options = selected == OptionsTab && (!_completedLayout || (_isTorrent && Controls));
        var torrentPage = files || peers || trackers || pieces || options;
        OverviewPage.Visibility = !torrentPage && (selected == OverviewTab || _completedLayout) ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsPage.Visibility = selected == ConnectionsTab && !_completedLayout && !_isTorrent ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        PeersPage.Visibility = peers ? Visibility.Visible : Visibility.Collapsed;
        TrackersPage.Visibility = trackers ? Visibility.Visible : Visibility.Collapsed;
        PiecesPage.Visibility = pieces ? Visibility.Visible : Visibility.Collapsed;
        OptionsPage.Visibility = options ? Visibility.Visible : Visibility.Collapsed;
        UpdateChartSeen();
        // Opening the tab reads the files afresh.
        var opened = files && !_filesShown;
        _filesShown = files;
        if (opened) ReloadFiles();
        OptionsTabShown(options);
        ShowControlButtons();
        if (_job is { Torrent: not null } job) ReadDetailsIfDue(job);
    }

    /// <summary>
    /// UI.B20: the same rule as the main window's graph. The last update's rate is held until the next one (or until
    /// it is 10 seconds old), so irregular updates draw one steady line instead of separate strokes.
    /// </summary>
    private void SampleChart(DateTimeOffset? at = null)
    {
        if (_closed || _job is not { } job) return;
        var now = at ?? DateTimeOffset.UtcNow;
        Chart.AddSample(now, SpeedSample.Total(_connected, [SpeedSample.JobRate(job.State, job.BytesPerSecond, job.UpdatedAt, now)]));
    }

    /// <summary>
    /// F-12: on each connection, the Host's figures for this download since it started running, so a window opened
    /// mid-download shows what came before instead of an empty graph. An older Host answers with the total only
    /// (no <c>jobId</c>), which is not this download's, so it is left out.
    /// </summary>
    private async Task LoadSpeedHistoryAsync()
    {
        try
        {
            var reply = await _connection.GetSpeedHistoryAsync(_jobId);
            if (_closed || reply.Type != MessageTypes.SpeedHistoryResult) return;
            var history = reply.GetPayload<SpeedHistoryDto>();
            if (history?.JobId != _jobId) return;
            Chart.LoadHistory(history.Start, history.IntervalSeconds, history.BytesPerSecond);
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Download speed history not loaded: {ex.Message}");
        }
    }

    private void UpdateChartSeen()
    {
        var shown = !_closed && AppWindow.IsVisible
            && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        Chart.IsSeen = shown && OverviewPage.Visibility == Visibility.Visible && SpeedPanel.Visibility == Visibility.Visible;
        Details.SetShown(shown);
        UpdateChartTimer();
        // T2: the torrent tabs read their lists on the same terms: shown, and not minimized.
        SyncTorrentPolling();
        SyncWatch();
    }

    private long? _chartStoppedAt;
    private readonly ChartIdle _chartIdle = new();

    /// <summary>
    /// Audit M11: the chart's one-second sample runs only while the chart can be seen and the download transfers. While
    /// it waits or is paused nothing runs: <see cref="ChartIdle"/> adds the wait's zeros when it moves again, so the line
    /// is a flat 0 rather than a gap. Seen again after a stop of more than a few seconds, it reads the Host's history.
    /// </summary>
    private void UpdateChartTimer()
    {
        var job = _job;
        var now = DateTimeOffset.UtcNow;
        var moving = job is not null && SpeedSample.IsActive(job.State);
        var waiting = job is not null && !moving && job.State is not ("completed" or "failed" or "cancelled");
        var (zeros, begins) = _chartIdle.Update(waiting, _connected, now);
        if (zeros.Count > 0) Chart.AddSamples(zeros.Select(at => (at, (double?)0)));
        var run = Chart.IsSeen && moving;
        if (!run)
        {
            if (_chartTimer.IsEnabled)
            {
                _chartTimer.Stop();
                _chartStoppedAt = Environment.TickCount64;
            }
            // The wait's first figure: 0 from now until it moves again.
            if (begins) SampleChart(now);
            return;
        }

        if (_chartTimer.IsEnabled) return;
        if (_chartStoppedAt is { } stoppedAt && Environment.TickCount64 - stoppedAt > 5_000 && _connected) _ = LoadSpeedHistoryAsync();
        _chartStoppedAt = null;
        SampleChart();
        _chartTimer.Start();
    }

    private async Task RefreshCompletionAsync(JobDto completed)
    {
        if (_refreshingCompletion) return;
        _refreshingCompletion = true;
        try
        {
            // Another window may have changed the default while this transfer
            // was running. Resolve that preference at completion, not startup.
            var latest = await _connection.RefreshAsync();
            if (_closed) return;
            _settings = latest.Settings;
            _globalShowCompletion = latest.Settings.ShowCompletionDialog;
            var current = latest.Jobs.FirstOrDefault(candidate => candidate.Id == _jobId);
            if (current is null) ShowRemoved();
            else Update(current, completionSettingsRefreshed: true);
        }
        catch (Exception)
        {
            if (!_closed) Update(completed, completionSettingsRefreshed: true);
        }
        finally { _refreshingCompletion = false; }
    }

    private void ShowRemoved()
    {
        _job = null;
        Heading.Text = "Removed";
        SetChip("InboundStatusNeutralBrush");
        Details.ShowNothing("Removed");
        FileNameText.Text = "Download removed";
        SubText.Text = string.Empty;
        Tabs.Visibility = OverviewPage.Visibility = ConnectionsPage.Visibility = OptionsPage.Visibility = FilesPage.Visibility = Visibility.Collapsed;
        PeersPage.Visibility = TrackersPage.Visibility = PiecesPage.Visibility = Visibility.Collapsed;
        _torrentTimer.Stop();
        _filesShown = false;
        DontShowAgain.Visibility = Visibility.Collapsed;
        Notice.Text = "This download is no longer in the list.";
        CloseButton.Content = "Close";
        UpdateChartTimer();
        UpdateButtons();
    }

    private void SetChip(string brushKey)
    {
        var brush = InboundTheme.Brush(brushKey, Root);
        Heading.Foreground = brush;
        StateChip.BorderBrush = brush;
    }

    private static string StateBrush(JobDto job) => job.State switch
    {
        "completed" => "InboundStatusDoneBrush",
        "failed" => "InboundStatusFailedBrush",
        "paused" or "queued" or "retryWaiting" or "retrying" => "InboundStatusWaitingBrush",
        "cancelled" => "InboundStatusNeutralBrush",
        _ => "InboundStatusActiveBrush",
    };

    private static void Tile(TextBlock label, Run value, Run unit, string labelText, string valueText, string unitText = "")
    {
        label.Text = labelText;
        value.Text = valueText;
        unit.Text = unitText.Length > 0 ? " " + unitText : string.Empty;
    }

    /// <summary>Where the download comes from, for the line under its name: the site, never the address itself.</summary>
    private static string Origin(JobDto job)
    {
        // A torrent comes from its peers: say what it holds instead (never its magnet).
        if (job.Torrent is { } torrent) return $"Torrent · {TorrentRules.FilesSummary(torrent)}";
        if (!JobAddress.HasAddress(job)) return job.RemoteProvider is { Length: > 0 } provider ? $"through {provider}" : string.Empty;
        var shown = JobAddress.Shown(job);
        return Uri.TryCreate(shown, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : shown;
    }

    private void Update(JobDto job, bool completionSettingsRefreshed = false)
    {
        _job = job;
        _isTorrent = job.Torrent is not null;
        _iconJob.Update(job);
        var complete = job.State == "completed";
        if (complete && !_completedLayout && !completionSettingsRefreshed)
        {
            _ = RefreshCompletionAsync(job);
            return;
        }
        var showCompletion = job.ShowCompletionDialog ?? _globalShowCompletion;
        // A user who disables future completion windows from this already-open
        // completion window should not have that same window disappear.
        if (complete && !showCompletion && !_manual && !_completedLayout)
        {
            Close();
            return;
        }
        if (complete && !_completedLayout)
        {
            _completedLayout = true;
            ConnectionsPage.Visibility = OptionsPage.Visibility = Visibility.Collapsed;
            if (job.Torrent is null)
            {
                Tabs.Visibility = Visibility.Collapsed;
                OverviewPage.Visibility = Visibility.Visible;
            }
            else
            {
                // T1: a finished torrent keeps Overview and its Files, read-only.
                ConnectionsTab.Visibility = OptionsTab.Visibility = Visibility.Collapsed;
                if (!IsTorrentTab(Tabs.SelectedItem)) Tabs.SelectedItem = OverviewTab;
                ShowSelectedPage();
            }

            WindowLayout.ResizeForDpi(this, 600, 500);
            // The default action, ready for Enter or Space (U-WL-17).
            if (OpenButton.Visibility == Visibility.Visible || RemoteDataRules.MayUseFiles(_connection.IsRemote))
                DispatcherQueue.TryEnqueue(() => { if (OpenButton.Visibility == Visibility.Visible) OpenButton.Focus(FocusState.Programmatic); });
        }

        var percent = JobDetailsPanel.Percent(job);
        var failed = job.State == "failed";
        var paused = job.State == "paused";
        var moving = ProgressBarRules.ShowsMovingBar(job.State, job.TotalBytes);
        var torrent = job.Torrent;
        var choosingFiles = torrent is { AwaitingFileChoice: true } && !FilesFixed(job);
        var displayName = !string.IsNullOrWhiteSpace(job.FileName) ? job.FileName
            : torrent is { Name.Length: > 0 } ? torrent.Name
            : torrent is not null ? "Torrent" : "Download";
        var stateWord = complete ? "Complete" : JobViewModel.ToDisplayState(job.State);
        Heading.Text = choosingFiles ? "Choose files" : TorrentSeedRules.IsSeeding(job) ? "Seeding" : stateWord;
        SetChip(StateBrush(job));
        // U-WL-16: a percentage only while it moves and the size is known, otherwise the state; named for another server.
        Title = ServerNames.ForWindow(ShellWords.ProgressTitle(job.State, complete, job.TotalBytes is > 0 ? percent : null, displayName, JobViewModel.ToDisplayState(job.State)), _connection.Target);
        FileNameText.Text = displayName;
        SubText.Text = complete ? Path.GetDirectoryName(job.FinalFilePath) ?? string.Empty
            : paused && job.ReceivedBytes > 0 ? $"Paused · {Bytes(job.ReceivedBytes)} kept"
            : failed && job.Failure is { } stopped ? $"Stopped at {stopped.OccurredAt.ToLocalTime():t}"
            : Origin(job);

        // A file from a provider account has no address of its own: only a reference to the account's item.
        // T1: a torrent's address is its short magnet (no trackers), and only once its info hash is known.
        var hasAddress = torrent is not null ? torrent.InfoHash.Length > 0 : JobAddress.HasAddress(job);
        UrlHeader.Visibility = UrlText.Visibility = hasAddress ? Visibility.Visible : Visibility.Collapsed;
        var addressLabel = torrent is not null ? "Magnet" : JobAddress.Label(job);
        UrlHeader.Text = addressLabel;
        AutomationProperties.SetName(UrlText, addressLabel);
        UrlText.Text = torrent is not null ? (hasAddress ? TorrentRules.ShortMagnet(torrent) : string.Empty) : JobAddress.Shown(job);
        ToolTipService.SetToolTip(UrlText, UrlText.Text.Length > 0 ? UrlText.Text : null);
        SavedHeader.Text = complete ? "Saved as" : "Save to";
        SavedText.Text = complete || string.IsNullOrEmpty(job.DestinationDirectory) ? job.FinalFilePath : job.DestinationDirectory;
        AutomationProperties.SetName(SavedText, complete ? "The file saved as" : "Save to");
        CompletionPath.Text = job.FinalFilePath;
        var resume = job.ResumeSupported switch { true => "Supported", false => "Not supported", null => "Unknown" };
        ResumeText.Text = resume;
        // A failure has Resume as a tile of its own. A torrent always picks up where it was, so it has none.
        ResumeHeader.Visibility = ResumeText.Visibility = complete || failed || torrent is not null ? Visibility.Collapsed : Visibility.Visible;
        ShowTorrent(job, choosingFiles);
        Details.Show(job);
        // A finished file has no progress to show: the full bar only filled the middle (U-WL-17).
        if (complete) SegmentMap.Visibility = OverallFooter.Visibility = OverviewMap.Visibility = Visibility.Collapsed;

        // The ring: blue while it runs, amber paused, red failed, green with a check when done.
        Ring.Foreground = InboundTheme.Brush(complete ? "InboundStatusDoneBrush" : failed ? "InboundStatusFailedBrush"
            : paused ? "InboundStatusWaitingBrush" : "InboundProgressFillBrush", Root);
        Ring.IsIndeterminate = moving;
        Ring.Value = complete ? 100 : percent;
        RingGlyph.Visibility = complete ? Visibility.Visible : Visibility.Collapsed;
        RingGlyph.Glyph = "";
        RingGlyph.Foreground = InboundTheme.Brush("InboundStatusDoneBrush", Root);
        RingValue.Visibility = complete ? Visibility.Collapsed : Visibility.Visible;
        RingValue.Text = job.TotalBytes is > 0 || job.Progress is not null ? $"{percent:0}%" : Bytes(job.ReceivedBytes);
        if (failed) RingValue.Foreground = InboundTheme.Brush("InboundStatusFailedBrush", Root);
        else RingValue.ClearValue(TextBlock.ForegroundProperty);
        RingCaption.Text = complete ? "complete" : failed || paused ? "kept" : "downloaded";
        AutomationProperties.SetName(Ring, complete ? "Download complete" : $"{percent:0} percent downloaded");

        var rate = job.State == "downloading" && job.BytesPerSecond is > 0 ? job.BytesPerSecond.Value : 0;
        var size = job.TotalBytes is > 0 ? job.TotalBytes.Value : job.ReceivedBytes;
        var received = job.TotalBytes is > 0 ? ProgressOverviewText.Of(job.ReceivedBytes, job.TotalBytes.Value) : ProgressOverviewText.Split(job.ReceivedBytes);
        if (torrent is not null)
        {
            // T1: received of the wanted files' size, as "392 MB of 1.03 GB".
            var wanted = job.TotalBytes is > 0 ? job.TotalBytes.Value : torrent.WantedBytes;
            received = wanted > 0 ? ProgressOverviewText.OfWords(job.ReceivedBytes, wanted) : ProgressOverviewText.Split(job.ReceivedBytes);
        }

        if (complete)
        {
            var (number, unit) = ProgressOverviewText.Split(size);
            var took = ProgressOverviewText.Took(job.StartedAt, job.CompletedAt);
            Tile(Tile1Label, Tile1Value, Tile1Unit, "Size", number, unit);
            Tile(Tile2Label, Tile2Value, Tile2Unit, "Took", ProgressOverviewText.Duration(took));
            if (took is { TotalSeconds: > 0 } span && size > 0)
            {
                var (average, averageUnit) = ProgressOverviewText.Split((long)(size / span.TotalSeconds));
                Tile(Tile3Label, Tile3Value, Tile3Unit, "Average speed", average, averageUnit + "/s");
            }
            else Tile(Tile3Label, Tile3Value, Tile3Unit, "Average speed", "—");
            if (torrent is not null) Tile(Tile4Label, Tile4Value, Tile4Unit, "Files", TorrentRules.FilesSummary(torrent));
            else Tile(Tile4Label, Tile4Value, Tile4Unit, "Connections", job.RequestedConnections > 0 ? job.RequestedConnections.ToString(CultureInfo.CurrentCulture) : "—");
        }
        else if (failed)
        {
            var failure = job.Failure;
            Tile(Tile1Label, Tile1Value, Tile1Unit, "Error", ProgressOverviewText.ErrorName(failure?.HttpStatus, failure?.Kind ?? string.Empty));
            // The Host counts the download's retries, not each connection's.
            Tile(Tile2Label, Tile2Value, Tile2Unit, "Retries", job.QueueRetryCount.ToString(CultureInfo.CurrentCulture));
            Tile(Tile3Label, Tile3Value, Tile3Unit, "Kept", received.Number, received.Unit);
            if (torrent is not null) Tile(Tile4Label, Tile4Value, Tile4Unit, "Files", TorrentRules.FilesSummary(torrent));
            else Tile(Tile4Label, Tile4Value, Tile4Unit, "Resume", resume);
        }
        else if (torrent is not null)
        {
            // T1: Downloaded, Rate, Time left and Peers; a torrent has peers, not connections.
            Tile(Tile1Label, Tile1Value, Tile1Unit, "Downloaded", received.Number, received.Unit);
            if (rate > 0)
            {
                var (speed, speedUnit) = ProgressOverviewText.Split((long)rate);
                Tile(Tile2Label, Tile2Value, Tile2Unit, "Rate", speed, speedUnit + "/s");
            }
            else Tile(Tile2Label, Tile2Value, Tile2Unit, "Rate", "—");
            var running = TorrentRules.IsActive(job);
            Tile(Tile3Label, Tile3Value, Tile3Unit, "Time left", job.State == "downloading" ? TimeLeft(job.EstimatedRemaining) : "—");
            if (running)
            {
                var (peers, seeds) = ProgressOverviewText.Peers(torrent.Peers, torrent.Seeds);
                Tile(Tile4Label, Tile4Value, Tile4Unit, "Peers", peers, seeds);
            }
            else Tile(Tile4Label, Tile4Value, Tile4Unit, "Peers", "—");
        }
        else
        {
            Tile(Tile1Label, Tile1Value, Tile1Unit, "Downloaded", received.Number, received.Unit);
            if (rate > 0)
            {
                var (speed, speedUnit) = ProgressOverviewText.Split((long)rate);
                Tile(Tile2Label, Tile2Value, Tile2Unit, "Speed", speed, speedUnit + "/s");
            }
            else Tile(Tile2Label, Tile2Value, Tile2Unit, "Speed", "—");
            var running = job.State == "downloading";
            // U-WL-15: the list's own words for time left, such as 12m 4s.
            Tile(Tile3Label, Tile3Value, Tile3Unit, "Time left", running ? TimeLeft(job.EstimatedRemaining) : "—");
            Tile(Tile4Label, Tile4Value, Tile4Unit, "Connections", running ? job.ActiveConnections.ToString(CultureInfo.CurrentCulture) : "—",
                running && job.RequestedConnections > 0 ? $"of {job.RequestedConnections} busy" : string.Empty);
        }
        if (failed) Tile1Value.Foreground = InboundTheme.Brush("InboundStatusFailedBrush", Root);
        else Tile1Value.ClearValue(TextElement.ForegroundProperty);

        // The failure first: what went wrong, then what can be done about it.
        // U-WL-3: the Host's reason with any address taken out.
        var failureMessage = JobNotes.FailureMessage(job);
        FailureBar.IsOpen = failureMessage is not null;
        if (failureMessage is not null && job.Failure is { } reason)
        {
            FailureBar.Title = failureMessage;
            var after = new List<string>();
            if (job.QueueRetryCount > 0) after.Add(job.QueueRetryCount == 1 ? "It was retried once." : $"It was retried {job.QueueRetryCount} times.");
            if (job.CanResume && job.ReceivedBytes > 0 && reason.IsResumable) after.Add($"Retry continues from {percent:0}%.");
            else if (job.CanResume) after.Add("Retry tries the download again.");
            FailureBar.Message = string.Join(" ", after);
        }

        // The speed for the last minute, from the updates this window gets anyway.
        SpeedPanel.Visibility = complete || failed ? Visibility.Collapsed : Visibility.Visible;
        UpdateChartSeen();
        CompletedActions.Visibility = complete && !job.FileRemovedAfterExtraction && RemoteDataRules.MayUseFiles(_connection.IsRemote)
            ? Visibility.Visible : Visibility.Collapsed;

        // The other tabs carry a one-line summary of the same progress.
        var line = string.Join(" · ", new[]
        {
            rate > 0 ? $"{Bytes((long)rate)}/s" : null,
            job.State == "downloading" && job.EstimatedRemaining is { } left ? TimeLeft(left) : null,
            choosingFiles ? "Choose files to download"
                : paused && torrent is not null ? "Paused"
                : paused ? $"Paused · resume {(job.ResumeSupported == true ? "supported" : job.ResumeSupported == false ? "not supported" : "unknown")}" : null,
            failed || job.State is "queued" or "connecting" or "cancelled" ? stateWord : null,
        }.Where(part => part is not null));
        var tone = paused ? "InboundStatusWaitingBrush" : failed ? "InboundStatusFailedBrush" : null;
        foreach (var (percentText, bar, words) in new[] { (ConnectionsPercent, ConnectionsBar, ConnectionsLine), (OptionsPercent, OptionsBar, OptionsLine) })
        {
            percentText.Text = $"{percent:0}%";
            if (tone is null) percentText.ClearValue(TextBlock.ForegroundProperty);
            else percentText.Foreground = InboundTheme.Brush(tone, Root);
            bar.IsIndeterminate = moving;
            bar.Value = percent;
            bar.Foreground = InboundTheme.Brush(tone ?? "InboundProgressFillBrush", Root);
            words.Text = line;
        }
        ConnectionsTab.Text = job.RequestedConnections > 0 ? $"Connections ({job.RequestedConnections})" : "Connections";
        ConnectionsNote.Visibility = job.IsTerminal ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsNote.Text = "Connections are listed while the download runs or is paused.";

        // Finished: where the file is, and for another server that it is on that computer (U-WR-1).
        var completionText = complete && job.FileRemovedAfterExtraction ? JobNotes.NoFile(JobNotes.Note(job))
            : complete ? ServerNames.Completed(_connection.Target)
            : failed ? (failureMessage is null && job.CanResume ? "Retry tries the download again." : string.Empty)
            : choosingFiles ? "The file list is in. Choose the files on the Files tab, then Start."
            : paused ? "The download is paused. Resume it or close this window."
            : "Closing this window will not stop the download.";
        // A failure's reason is in the bar above, so the notice doesn't repeat it.
        if (_connected) Notice.Text = failureMessage is not null ? completionText : job.Failure?.Message ?? completionText;

        _updatingControls = true;
        try
        {
            ShowCompletionForJob.IsChecked = job.ShowCompletionDialog ?? _globalShowCompletion;
            DontShowAgain.IsChecked = !_globalShowCompletion;
            DontShowAgain.Visibility = complete ? Visibility.Visible : Visibility.Collapsed;
            SpeedLimitInput.IsEnabled = job.CanChangeSpeedLimit && UseSpeedLimit.IsChecked == true;
            UseSpeedLimit.IsEnabled = job.CanChangeSpeedLimit;
            ApplySpeedLimitButton.IsEnabled = _connected && job.CanChangeSpeedLimit;
            SpeedLimitNotice.Text = job.CanChangeSpeedLimit
                ? "Applies to ordinary HTTP/HTTPS and FTP file downloads. HLS and DASH transfers do not support this control."
                : "Speed limiting is available for unfinished HTTP/HTTPS and FTP file downloads. It is unavailable for HLS, DASH, and finished transfers.";
            CurrentRateText.Text = job.BytesPerSecond is > 0 ? $"{Bytes((long)job.BytesPerSecond.Value)}/s" : "—";
            if (_displayedSpeedLimit != job.SpeedLimitBytesPerSecond)
            {
                _displayedSpeedLimit = job.SpeedLimitBytesPerSecond;
                UseSpeedLimit.IsChecked = job.SpeedLimitBytesPerSecond is > 0;
                SpeedLimitInput.Text = job.SpeedLimitBytesPerSecond is > 0
                    ? (job.SpeedLimitBytesPerSecond.Value / 1024m).ToString("0.##", CultureInfo.CurrentCulture)
                    : string.Empty;
                SpeedLimitInput.IsEnabled = job.CanChangeSpeedLimit && UseSpeedLimit.IsChecked == true;
            }
        }
        finally { _updatingControls = false; }

        CloseButton.Content = job.IsTerminal ? "Close" : "Hide";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var complete = _job?.State == "completed";
        PauseButton.Visibility = _job is null || _job.IsTerminal || _job.State is "failed" or "paused" ? Visibility.Collapsed : Visibility.Visible;
        // T1: a torrent waiting for its files goes on with Start on the Files tab, which sends the choice.
        var choosingFiles = _job?.Torrent is { AwaitingFileChoice: true } && !FilesFixed(_job);
        ResumeButton.Visibility = _job is null || (_job.IsTerminal && !_job.CanResume) || choosingFiles ? Visibility.Collapsed : Visibility.Visible;
        StartFilesButton.IsEnabled = _connected && _choosing && !_savingFiles;
        CancelButton.Visibility = _job?.CanCancel == true ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.IsEnabled = _connected && _job?.CanPause == true;
        ResumeButton.IsEnabled = _connected && _job?.CanResume == true;
        CancelButton.IsEnabled = _connected && _job?.CanCancel == true;
        // Resume is the way on from a pause or a failure, so it is the primary button then.
        var resumeLeads = _job is { CanResume: true, State: "paused" or "failed" or "cancelled" };
        if (resumeLeads) ResumeButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        else ResumeButton.ClearValue(FrameworkElement.StyleProperty);
        ResumeLabel.Text = _job?.State == "failed" ? "Retry" : "Resume";
        AutomationProperties.SetName(ResumeButton, ResumeLabel.Text);
        ResumeGlyph.Glyph = _job?.State == "failed" ? "" : "";
        if (resumeLeads) ResumeGlyph.ClearValue(FontIcon.ForegroundProperty);
        else ResumeGlyph.Foreground = InboundTheme.Brush("InboundIconStartBrush", Root);
        ShowCompletionForJob.IsEnabled = _connected && _job is not null;
        DeleteButton.Visibility = _job is null ? Visibility.Collapsed : Visibility.Visible;
        DeleteButton.IsEnabled = _connected && _job is not null;
        // A download on another server: its file is on that machine, so this PC can't open or show it.
        var mayOpen = complete && _job?.FileRemovedAfterExtraction != true && RemoteDataRules.MayUseFiles(_connection.IsRemote);
        OpenButton.Visibility = OpenWithButton.Visibility = FolderButton.Visibility = mayOpen ? Visibility.Visible : Visibility.Collapsed;
        if (!OperatingSystem.IsWindows()) OpenWithButton.Visibility = Visibility.Collapsed; // Windows' chooser only
        // A torrent of several files is a folder: Open shows it, and there is no app to open it with.
        if (_job?.Torrent is { FileCount: > 1 }) OpenWithButton.Visibility = Visibility.Collapsed;
        // The completion window's own default is this PC's choice, kept on this PC's Host; this window talks to the server.
        if (_connection.IsRemote) DontShowAgain.Visibility = Visibility.Collapsed;
        if (_job is { Torrent: not null } torrentJob) ShowSeedButton(torrentJob);
        else SeedButton.Visibility = Visibility.Collapsed;
        ShowControlButtons();
        SyncTorrentPolling();
    }

    /// <summary>
    /// The box beside the buttons, or on its own line above them when a torrent's extra buttons show (walk 2026-11-12:
    /// beside Stop seeding and Force recheck it wrapped to three lines).
    /// </summary>
    private void LayOutFooter()
    {
        var crowded = DontShowAgain.Visibility == Visibility.Visible
            && (SeedButton.Visibility == Visibility.Visible || RecheckButton.Visibility == Visibility.Visible || AnnounceButton.Visibility == Visibility.Visible);
        var row = crowded ? 1 : 0;
        if (Grid.GetRow(ActionRow) == row) return;
        Grid.SetRow(ActionRow, row);
        Grid.SetColumn(ActionRow, crowded ? 0 : 1);
        Grid.SetColumnSpan(ActionRow, crowded ? 2 : 1);
        ActionRow.HorizontalAlignment = crowded ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        ActionRow.Margin = crowded ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }

    /// <summary>A torrent's files can't change once it has finished or been cancelled.</summary>
    private static bool FilesFixed(JobDto job) => job.State is "completed" or "cancelled";

    /// <summary>T1: the torrent's facts, its Files tab instead of Connections, and its file choice.</summary>
    private void ShowTorrent(JobDto job, bool choosingFiles)
    {
        var torrent = job.Torrent;
        ShowTorrentTabs(job);
        ShowTorrentControls(job);
        TorrentFacts.Visibility = torrent is not null ? Visibility.Visible : Visibility.Collapsed;
        FilesTab.Visibility = torrent is not null ? Visibility.Visible : Visibility.Collapsed;
        if (torrent is null)
        {
            if (!_completedLayout) ConnectionsTab.Visibility = Visibility.Visible;
            if (Tabs.SelectedItem == FilesTab) Tabs.SelectedItem = OverviewTab;
            FileChoiceBar.Visibility = Visibility.Collapsed;
            return;
        }

        ConnectionsTab.Visibility = Visibility.Collapsed;
        if (Tabs.SelectedItem == ConnectionsTab) Tabs.SelectedItem = OverviewTab;
        TorrentStatusText.Text = choosingFiles ? "Choose files to download" : TorrentRules.StatusText(job) ?? JobViewModel.ToDisplayState(job.State);
        TorrentFilesText.Text = TorrentRules.FilesSummary(torrent);
        // T3: with the v2 hash a details read gave (kept, as the progress updates don't carry it).
        InfoHashText.Text = TorrentControlRules.HashText(torrent.InfoHash, _infoHashV2);
        TorrentRatioText.Text = TorrentListRules.RatioLine(torrent);
        var goal = TorrentListRules.GoalLine(job, _settings, DateTimeOffset.UtcNow);
        SeedGoalText.Text = goal ?? string.Empty;
        SeedGoalHeader.Visibility = SeedGoalText.Visibility = goal is null ? Visibility.Collapsed : Visibility.Visible;

        // The choice in the list is the user's while the torrent waits for it, the Host's otherwise.
        // Entering or leaving the choice, a new state, the file list arriving (a magnet's metadata) or a choice saved
        // anywhere (the main window, the web UI) reads the files again.
        var shape = (torrent.FileCount, torrent.HasMetadata, torrent.WantedCount, torrent.WantedBytes);
        var reload = choosingFiles != _choosing
            || (_filesState is not null && _filesState != job.State)
            || (_filesShape is { } before && before != shape);
        _choosing = choosingFiles;
        _filesState = job.State;
        _filesShape = shape;
        if (reload) ReloadFiles();
        // While it downloads, its progress updates read the files again for each one's done bytes (no timer).
        else if (_filesLoaded && !_loadingFiles && FilesPage.Visibility == Visibility.Visible
            && TorrentRules.FileProgressDue(job, listShown: true, Environment.TickCount64 - _filesReadAt)) _ = LoadFilesAsync();

        FileChoiceBar.Visibility = choosingFiles ? Visibility.Visible : Visibility.Collapsed;
        // T1: a torrent that waits for its files opens on them, once, but only when no main window runs: one that does
        // either opens its own Choose files or brings this window forward on the Files tab (ShowWindow), so the choice
        // is asked in one place.
        if (choosingFiles && !_openedOnFiles && !MainWindowRuns())
        {
            _openedOnFiles = true;
            Tabs.SelectedItem = FilesTab;
        }

        // Finished or cancelled: the files stay to look at, read-only, with their last word on progress.
        // A refusal while it was verifying holds until it moves on (cancelled and resumed, it can be changed again).
        var terminal = FilesFixed(job);
        if (terminal) SetFilesReadOnly(true);
        else if (job.State != "verifying") SetFilesReadOnly(false);
    }

    /// <summary>
    /// The shown files may be out of date: read them again now if the tab is open, otherwise when it opens. A read
    /// already out began before this, so its answer is dropped and another read follows it.
    /// </summary>
    private void ReloadFiles()
    {
        _filesLoaded = false;
        _filesStale = true;
        if (FilesPage.Visibility == Visibility.Visible) _ = LoadFilesAsync();
    }

    /// <summary>Reads the torrent's files from the Host, one read at a time.</summary>
    private async Task LoadFilesAsync()
    {
        if (_closed || _loadingFiles || !_connected || !_isTorrent) return;
        _loadingFiles = true;
        _filesStale = false;
        _filesReadAt = Environment.TickCount64;
        var version = _choiceVersion;
        try
        {
            var result = await _connection.GetTorrentFilesAsync(_jobId);
            // Asked again while this read was out: its answer is older than that, so the read in finally replaces it.
            if (_closed || !_isTorrent || _filesStale) return;
            if (!result.Ok)
            {
                ShowFilesNote(result.Error ?? "The file list could not be read.", fromLoad: true);
                return;
            }

            _filesLoaded = true;
            // A choice sent while this read was on its way is newer than what it says.
            ApplyFiles(result.Files, takeHostChoice: !_choosing && !_savingFiles && version == _choiceVersion);
        }
        catch (Exception ex)
        {
            if (!_closed && !_filesStale) ShowFilesNote($"The file list could not be read. {ex.Message}", fromLoad: true);
        }
        finally
        {
            _loadingFiles = false;
            if (_filesStale && !_closed && FilesPage.Visibility == Visibility.Visible) _ = LoadFilesAsync();
        }
    }

    /// <summary>Updates the rows in place when the files are the same ones, so the list keeps its place and its boxes.</summary>
    private void ApplyFiles(IReadOnlyList<TorrentFileDto> files, bool takeHostChoice)
    {
        var complete = _job?.State == "completed";
        // The first read while choosing starts from the Host's choice too.
        if (takeHostChoice || (_choosing && _fileRows.Count == 0))
        {
            _wanted = files.Where(file => file.Wanted).Select(file => file.Index).ToHashSet();
            // T3: the priorities the Host has (Normal for a file an older Host sent without one).
            _priorities = files.Where(file => file.Wanted && TorrentPriorities.Of(file) != TorrentPriorities.Normal)
                .ToDictionary(file => file.Index, TorrentPriorities.Of);
        }

        var same = files.Count == _fileRows.Count;
        for (var index = 0; same && index < files.Count; index++) same = files[index].Index == _fileRows[index].Index;
        if (same)
        {
            for (var index = 0; index < files.Count; index++)
                _fileRows[index].Apply(files[index], complete, _wanted.Contains(files[index].Index));
        }
        else
        {
            _fileRows = files.Select(file => new TorrentFileRow(file, complete, _wanted.Contains(file.Index), !_filesReadOnly)).ToList();
            FilesList.ItemsSource = _fileRows;
        }

        ShowFilePriorities();

        if (files.Count == 0) ShowFilesNote("The file list is not in yet. It comes from the peers.", fromLoad: true);
        else if (_filesNoteFromLoad) ClearFilesNote();
    }

    private void SetFilesReadOnly(bool readOnly)
    {
        if (_filesReadOnly == readOnly) return;
        _filesReadOnly = readOnly;
        foreach (var row in _fileRows) row.SetCanCheck(!readOnly);
    }

    private void ShowFilesNote(string words, bool fromLoad = false)
    {
        _filesNoteFromLoad = fromLoad;
        FilesNote.Text = words;
        FilesNote.Visibility = Visibility.Visible;
    }

    private void ClearFilesNote()
    {
        _filesNoteFromLoad = false;
        FilesNote.Text = string.Empty;
        FilesNote.Visibility = Visibility.Collapsed;
    }

    private async void FileBox_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is CheckBox { DataContext: TorrentFileRow row } box) await ToggleFileAsync(row, box.IsChecked == true);
        }
        catch (Exception ex)
        {
            if (!_closed) ShowFilesNote(ex.Message);
        }
    }

    /// <summary>Space on a focused row ticks it, as on its box.</summary>
    private async void FilesList_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        try
        {
            if (e.Key != Windows.System.VirtualKey.Space) return;
            // A focused box takes Space for itself; a focused row is a container whose content is the row.
            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) is not FrameworkElement focused || focused is CheckBox) return;
            if (((focused as ContentControl)?.Content ?? focused.DataContext) is not TorrentFileRow row) return;
            e.Handled = true;
            if (row.CanCheck) await ToggleFileAsync(row, !row.Checked);
        }
        catch (Exception ex)
        {
            if (!_closed) ShowFilesNote(ex.Message);
        }
    }

    /// <summary>
    /// A file ticked or cleared. While the torrent waits for its choice the change stays here until Start; otherwise the
    /// whole wanted set goes to the Host at once, and the box goes back if the Host refuses.
    /// </summary>
    private async Task ToggleFileAsync(TorrentFileRow row, bool want)
    {
        var had = _wanted.Contains(row.Index);
        if (_filesReadOnly || _job?.Torrent is null)
        {
            row.ShowChecked(had);
            return;
        }

        if (_savingFiles)
        {
            row.ShowChecked(had);
            ShowFilesNote("The last change is still being saved.");
            return;
        }

        var next = new HashSet<int>(_wanted);
        if (want) next.Add(row.Index);
        else next.Remove(row.Index);
        if (next.Count == 0)
        {
            row.ShowChecked(had);
            ShowFilesNote("Keep at least one file.");
            return;
        }

        if (_choosing)
        {
            _wanted = next;
            row.ShowChecked(want);
            ClearFilesNote();
            return;
        }

        if (!_connected)
        {
            row.ShowChecked(had);
            ShowFilesNote("The Host is not connected.");
            return;
        }

        _savingFiles = true;
        _choiceVersion++;
        row.ShowChecked(want);
        UpdateButtons();
        try
        {
            var result = await _connection.SetTorrentFilesAsync(_jobId, next.Order());
            if (_closed) return;
            if (result.Ok)
            {
                _wanted = next;
                ClearFilesNote();
                ReloadFiles();
            }
            else
            {
                row.ShowChecked(_wanted.Contains(row.Index));
                ShowFilesNote(result.Error ?? "The Host did not change the files.");
                // A torrent that has finished, or begun to, keeps the files it has.
                if (_job is { } job && (FilesFixed(job) || job.State == "verifying")) SetFilesReadOnly(true);
            }
        }
        catch (Exception ex)
        {
            if (_closed) return;
            row.ShowChecked(_wanted.Contains(row.Index));
            ShowFilesNote(ex.Message);
        }
        finally
        {
            _savingFiles = false;
            if (!_closed) UpdateButtons();
        }
    }

    /// <summary>The torrent waits for its files: send the ticked ones, and the Host starts it.</summary>
    private async void StartFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_choosing || _savingFiles || !_connected) return;
            if (!_filesLoaded)
            {
                // A read that failed is tried again from here; nothing else would until the torrent changes.
                if (_loadingFiles) ShowFilesNote("The file list is still loading.");
                else
                {
                    ShowFilesNote("Reading the file list again.");
                    ReloadFiles();
                }

                return;
            }

            if (_wanted.Count == 0)
            {
                ShowFilesNote("Choose at least one file.");
                return;
            }

            _savingFiles = true;
            _choiceVersion++;
            UpdateButtons();
            try
            {
                // T3: with the priorities chosen meanwhile, for the ticked files.
                var priorities = Controls
                    ? TorrentPriorities.ToSend(_priorities.Where(pair => _wanted.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value))
                    : null;
                var result = await _connection.SetTorrentFilesAsync(_jobId, _wanted.Order(), priorities);
                if (_closed) return;
                if (result.Ok)
                {
                    ClearFilesNote();
                    ReloadFiles();
                }
                else ShowFilesNote(result.Error ?? "The Host did not take the choice.");
            }
            finally
            {
                _savingFiles = false;
                if (!_closed) UpdateButtons();
            }
        }
        catch (Exception ex)
        {
            if (!_closed) ShowFilesNote(ex.Message);
        }
    }

    private static string Bytes(long bytes) => JobDetailsPanel.Bytes(bytes);

    private static string TimeLeft(TimeSpan? duration) => DurationText.TimeLeft(duration) is { Length: > 0 } words ? words : "—";

    private async Task RunAsync(Func<Task<IpcMessage>> action)
    {
        try
        {
            var reply = await action();
            if (reply.Type == MessageTypes.JobActionResult && reply.GetPayload<JobActionResponse>() is { Ok: false } result)
                Notice.Text = result.Error ?? "The host rejected the action.";
            else if (reply.Type == MessageTypes.JobActionResult && reply.GetPayload<JobActionResponse>() is { Job: { } updated })
                Update(updated);
        }
        catch (Exception ex) { if (!_closed) Notice.Text = ex.Message; }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _connection.PauseAsync(_jobId));
    private async void Resume_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _connection.ResumeAsync(_jobId));
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Open_Click(object sender, RoutedEventArgs e) => OpenPath(false);
    private void OpenWith_Click(object sender, RoutedEventArgs e) => OpenPathWith();
    private void Folder_Click(object sender, RoutedEventArgs e) => OpenPath(true);

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null) return;
        var name = string.IsNullOrWhiteSpace(_job.FileName) ? "this download" : $"“{_job.FileName}”";
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = "Delete download?",
            Content = _job.FileRemovedAfterExtraction
                ? $"Remove {name} from the list? {JobNotes.NothingToDelete(1)}"
                : _job.Torrent is not null
                    ? $"Remove {name} from the list and delete its downloaded files? This cannot be undone."
                    : $"Remove {name} from the list and delete its downloaded file? This cannot be undone.",
            PrimaryButtonText = "Delete",
            PrimaryButtonStyle = InboundUi.DangerDialogButton,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await DialogGuard.ShowAsync(confirm) != ContentDialogResult.Primary || _closed || _job is null) return;
        try
        {
            var reply = await _connection.RemoveAsync(_jobId, deleteFile: !_job.FileRemovedAfterExtraction);
            if (reply.Type == MessageTypes.JobActionResult && reply.GetPayload<JobActionResponse>() is { Ok: false } result)
            {
                Notice.Text = result.Error ?? "The host could not delete this download.";
                return;
            }
            Close();
        }
        catch (Exception ex) { if (!_closed) Notice.Text = ex.Message; }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = "Cancel download?",
            Content = _job?.Torrent is not null ? "Cancel this torrent and delete its partial files?" : "Cancel this transfer and delete its partial file?",
            PrimaryButtonText = "Cancel download",
            PrimaryButtonStyle = InboundUi.DangerDialogButton,
            CloseButtonText = _job?.State is "paused" or "queued" or "failed" ? "Keep it" : "Keep downloading",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await DialogGuard.ShowAsync(confirm) == ContentDialogResult.Primary && _job?.CanCancel == true)
            await RunAsync(() => _connection.CancelAsync(_jobId));
    }

    private async void ApplySpeedLimit_Click(object sender, RoutedEventArgs e)
    {
        if (_job?.CanChangeSpeedLimit != true)
        {
            SpeedLimitNotice.Text = "Speed limiting is unavailable for this transfer.";
            return;
        }

        long limit = 0;
        if (UseSpeedLimit.IsChecked == true)
        {
            if (!decimal.TryParse(SpeedLimitInput.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var kilobytes) ||
                kilobytes <= 0 || kilobytes > long.MaxValue / 1024m)
            {
                SpeedLimitNotice.Text = "Enter a positive speed in KB/s, or turn the limiter off.";
                return;
            }

            limit = (long)Math.Round(kilobytes * 1024m, MidpointRounding.AwayFromZero);
            if (limit < 1)
            {
                SpeedLimitNotice.Text = "The speed limit must be at least one byte per second.";
                return;
            }
        }

        ApplySpeedLimitButton.IsEnabled = false;
        try
        {
            var reply = await _connection.UpdateTransferOptionsAsync(_jobId, speedLimitBytesPerSecond: limit);
            var result = reply.GetPayload<JobActionResponse>();
            if (!result.Ok)
            {
                SpeedLimitNotice.Text = result.Error ?? "The host rejected the speed limit.";
                return;
            }

            if (result.Job is { } updated) Update(updated);
            SpeedLimitNotice.Text = limit == 0 ? "Speed limiter is off for this download." : "Speed limit applied to this download.";
        }
        catch (Exception ex)
        {
            SpeedLimitNotice.Text = ex.Message;
        }
        finally { ApplySpeedLimitButton.IsEnabled = _connected && _job?.CanChangeSpeedLimit == true; }
    }

    private async void ShowCompletionForJob_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingControls || _job is not { } before) return;
        var requested = ShowCompletionForJob.IsChecked == true;
        // Audit L3: _job is null once the download is removed while the request is out; the box goes back to what it
        // was for the job as it was then.
        bool Previous() => (_job ?? before).ShowCompletionDialog ?? _globalShowCompletion;
        try
        {
            var reply = await _connection.UpdateTransferOptionsAsync(_jobId, showCompletionDialog: requested);
            var result = reply.GetPayload<JobActionResponse>();
            if (!result.Ok)
            {
                Notice.Text = result.Error ?? "The host rejected the completion preference.";
                ShowCompletionForJob.IsChecked = Previous();
                return;
            }

            if (result.Job is { } updated && _job is not null) Update(updated);
        }
        catch (Exception ex)
        {
            Notice.Text = ex.Message;
            ShowCompletionForJob.IsChecked = Previous();
        }
    }

    private async void DontShowAgain_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingControls) return;
        var previous = _globalShowCompletion;
        var requested = DontShowAgain.IsChecked != true;
        DontShowAgain.IsEnabled = false;
        try
        {
            // Refresh immediately before the full-settings update so unrelated
            // settings are copied from the Host's current persisted snapshot.
            var latest = await _connection.RefreshAsync();
            _settings = latest.Settings;
            _settings.ShowCompletionDialog = requested;
            var reply = await _connection.UpdateSettingsAsync(_settings);
            var result = reply.GetPayload<SettingsResult>();
            if (!result.Ok)
            {
                _globalShowCompletion = previous;
                Notice.Text = result.Error ?? "Could not save the completion preference.";
            }
            else
            {
                _settings = result.Settings;
                _globalShowCompletion = result.Settings.ShowCompletionDialog;
                Notice.Text = _globalShowCompletion
                    ? "Completion windows will continue to appear."
                    : "The default completion window is disabled. Individual download choices still apply.";
            }
        }
        catch (Exception ex)
        {
            _globalShowCompletion = previous;
            Notice.Text = ex.Message;
        }
        finally
        {
            _updatingControls = true;
            DontShowAgain.IsChecked = !_globalShowCompletion;
            DontShowAgain.IsEnabled = true;
            _updatingControls = false;
        }
    }

    private void OpenPath(bool folder)
    {
        if (_job?.State != "completed" || _job.FileRemovedAfterExtraction || !RemoteDataRules.MayUseFiles(_connection.IsRemote)) return;
        try
        {
            if (folder)
                Process.Start(ShellActions.RevealFile(_job.FinalFilePath));
            else if (File.Exists(_job.FinalFilePath))
                Process.Start(ShellActions.OpenFile(_job.FinalFilePath));
            else if (_job.Torrent is not null && Directory.Exists(_job.FinalFilePath))
                Process.Start(ShellActions.OpenFolder(_job.FinalFilePath));
            else Notice.Text = _job.Torrent is not null ? "The downloaded files could not be found." : "The downloaded file could not be found.";
        }
        catch (Exception ex) { Notice.Text = ex.Message; }
    }

    private void OpenPathWith()
    {
        if (_job?.State != "completed" || _job.FileRemovedAfterExtraction || !RemoteDataRules.MayUseFiles(_connection.IsRemote)) return;
        try
        {
            if (File.Exists(_job.FinalFilePath))
                Process.Start(ShellActions.OpenWith(_job.FinalFilePath));
            else Notice.Text = "The downloaded file could not be found.";
        }
        catch (Exception ex) { Notice.Text = ex.Message; }
    }
}
