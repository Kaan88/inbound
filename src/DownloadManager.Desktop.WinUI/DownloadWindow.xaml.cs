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
            _connected = connected;
            Details.SetConnected(connected);
            if (!connected) Notice.Text = $"Host disconnected. Reconnecting… {error}";
            else if (Notice.Text.StartsWith("Host disconnected", StringComparison.Ordinal)) Notice.Text = string.Empty;
            UpdateButtons();
        });
        Details.UseTableOnly();
        Details.Attach(_connection, SegmentMap, OverallFooter, Root);
        Details.AddMap(OverviewMap);
        Details.Problem += words => Notice.Text = words;
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

    public void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var selected = sender.SelectedItem;
        OverviewPage.Visibility = selected == OverviewTab || _completedLayout ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsPage.Visibility = selected == ConnectionsTab && !_completedLayout ? Visibility.Visible : Visibility.Collapsed;
        OptionsPage.Visibility = selected == OptionsTab && !_completedLayout ? Visibility.Visible : Visibility.Collapsed;
        UpdateChartSeen();
    }

    /// <summary>
    /// UI.B20: the same rule as the main window's graph. The last update's rate is held until the next one (or until
    /// it is 10 seconds old), so irregular updates draw one steady line instead of separate strokes.
    /// </summary>
    private void SampleChart()
    {
        if (_closed || _job is not { } job) return;
        var now = DateTimeOffset.UtcNow;
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

    private void UpdateChartSeen() => Chart.IsSeen = !_closed && AppWindow.IsVisible && OverviewPage.Visibility == Visibility.Visible
        && SpeedPanel.Visibility == Visibility.Visible
        && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

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
        Tabs.Visibility = OverviewPage.Visibility = ConnectionsPage.Visibility = OptionsPage.Visibility = Visibility.Collapsed;
        DontShowAgain.Visibility = Visibility.Collapsed;
        Notice.Text = "This download is no longer in the list.";
        CloseButton.Content = "Close";
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
        if (!JobAddress.HasAddress(job)) return job.RemoteProvider is { Length: > 0 } provider ? $"through {provider}" : string.Empty;
        var shown = JobAddress.Shown(job);
        return Uri.TryCreate(shown, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : shown;
    }

    private void Update(JobDto job, bool completionSettingsRefreshed = false)
    {
        _job = job;
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
            Tabs.Visibility = ConnectionsPage.Visibility = OptionsPage.Visibility = Visibility.Collapsed;
            OverviewPage.Visibility = Visibility.Visible;
            WindowLayout.ResizeForDpi(this, 600, 500);
            // The default action, ready for Enter or Space (U-WL-17).
            if (OpenButton.Visibility == Visibility.Visible || RemoteDataRules.MayUseFiles(_connection.IsRemote))
                DispatcherQueue.TryEnqueue(() => { if (OpenButton.Visibility == Visibility.Visible) OpenButton.Focus(FocusState.Programmatic); });
        }

        var percent = JobDetailsPanel.Percent(job);
        var failed = job.State == "failed";
        var paused = job.State == "paused";
        var moving = ProgressBarRules.ShowsMovingBar(job.State, job.TotalBytes);
        var displayName = string.IsNullOrWhiteSpace(job.FileName) ? "Download" : job.FileName;
        var stateWord = complete ? "Complete" : JobViewModel.ToDisplayState(job.State);
        Heading.Text = stateWord;
        SetChip(StateBrush(job));
        // U-WL-16: a percentage only while it moves and the size is known, otherwise the state; named for another server.
        Title = ServerNames.ForWindow(ShellWords.ProgressTitle(job.State, complete, job.TotalBytes is > 0 ? percent : null, displayName, JobViewModel.ToDisplayState(job.State)), _connection.Target);
        FileNameText.Text = displayName;
        SubText.Text = complete ? Path.GetDirectoryName(job.FinalFilePath) ?? string.Empty
            : paused && job.ReceivedBytes > 0 ? $"Paused · {Bytes(job.ReceivedBytes)} kept"
            : failed && job.Failure is { } stopped ? $"Stopped at {stopped.OccurredAt.ToLocalTime():t}"
            : Origin(job);

        // A file from a provider account has no address of its own: only a reference to the account's item.
        var hasAddress = JobAddress.HasAddress(job);
        UrlHeader.Visibility = UrlText.Visibility = hasAddress ? Visibility.Visible : Visibility.Collapsed;
        UrlHeader.Text = JobAddress.Label(job);
        AutomationProperties.SetName(UrlText, JobAddress.Label(job));
        UrlText.Text = JobAddress.Shown(job);
        ToolTipService.SetToolTip(UrlText, UrlText.Text.Length > 0 ? UrlText.Text : null);
        SavedHeader.Text = complete ? "Saved as" : "Save to";
        SavedText.Text = complete || string.IsNullOrEmpty(job.DestinationDirectory) ? job.FinalFilePath : job.DestinationDirectory;
        AutomationProperties.SetName(SavedText, complete ? "The file saved as" : "Save to");
        CompletionPath.Text = job.FinalFilePath;
        var resume = job.ResumeSupported switch { true => "Supported", false => "Not supported", null => "Unknown" };
        ResumeText.Text = resume;
        // A failure has Resume as a tile of its own.
        ResumeHeader.Visibility = ResumeText.Visibility = complete || failed ? Visibility.Collapsed : Visibility.Visible;
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
            Tile(Tile4Label, Tile4Value, Tile4Unit, "Connections", job.RequestedConnections > 0 ? job.RequestedConnections.ToString(CultureInfo.CurrentCulture) : "—");
        }
        else if (failed)
        {
            var failure = job.Failure;
            Tile(Tile1Label, Tile1Value, Tile1Unit, "Error", ProgressOverviewText.ErrorName(failure?.HttpStatus, failure?.Kind ?? string.Empty));
            // The Host counts the download's retries, not each connection's.
            Tile(Tile2Label, Tile2Value, Tile2Unit, "Retries", job.QueueRetryCount.ToString(CultureInfo.CurrentCulture));
            Tile(Tile3Label, Tile3Value, Tile3Unit, "Kept", received.Number, received.Unit);
            Tile(Tile4Label, Tile4Value, Tile4Unit, "Resume", resume);
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
        if (!complete && !failed)
        {
            UpdateChartSeen();
            if (!_chartTimer.IsEnabled)
            {
                SampleChart();
                _chartTimer.Start();
            }
        }
        else _chartTimer.Stop();
        CompletedActions.Visibility = complete && !job.FileRemovedAfterExtraction && RemoteDataRules.MayUseFiles(_connection.IsRemote)
            ? Visibility.Visible : Visibility.Collapsed;

        // The other tabs carry a one-line summary of the same progress.
        var line = string.Join(" · ", new[]
        {
            rate > 0 ? $"{Bytes((long)rate)}/s" : null,
            job.State == "downloading" && job.EstimatedRemaining is { } left ? TimeLeft(left) : null,
            paused ? $"Paused · resume {(job.ResumeSupported == true ? "supported" : job.ResumeSupported == false ? "not supported" : "unknown")}" : null,
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
        ResumeButton.Visibility = _job is null || (_job.IsTerminal && !_job.CanResume) ? Visibility.Collapsed : Visibility.Visible;
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
        // The completion window's own default is this PC's choice, kept on this PC's Host; this window talks to the server.
        if (_connection.IsRemote) DontShowAgain.Visibility = Visibility.Collapsed;
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
            Content = "Cancel this transfer and delete its partial file?",
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
        if (_updatingControls || _job is null) return;
        var requested = ShowCompletionForJob.IsChecked == true;
        try
        {
            var reply = await _connection.UpdateTransferOptionsAsync(_jobId, showCompletionDialog: requested);
            var result = reply.GetPayload<JobActionResponse>();
            if (!result.Ok)
            {
                Notice.Text = result.Error ?? "The host rejected the completion preference.";
                ShowCompletionForJob.IsChecked = _job.ShowCompletionDialog ?? _globalShowCompletion;
                return;
            }

            if (result.Job is { } updated) Update(updated);
        }
        catch (Exception ex)
        {
            Notice.Text = ex.Message;
            ShowCompletionForJob.IsChecked = _job.ShowCompletionDialog ?? _globalShowCompletion;
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
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_job.FinalFilePath}\"") { UseShellExecute = true });
            else if (File.Exists(_job.FinalFilePath))
                Process.Start(ShellActions.OpenFile(_job.FinalFilePath));
            else Notice.Text = "The downloaded file could not be found.";
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
