using System.Diagnostics;
using System.Globalization;
using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace DownloadManager.Desktop.WinUI;

/// <summary>A replaceable, per-job frontend. Closing it never stops the Host.</summary>
public sealed partial class DownloadWindow : Window
{
    private readonly Guid _jobId;
    private readonly HostConnection _connection = new();
    private readonly JobViewModel _iconJob = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private JobDto? _job;
    private SettingsDto? _settings;
    private bool _globalShowCompletion = true;
    private bool _connected;
    private bool _closed;
    private bool _loadingSegments;
    private bool _haveSegmentMap;
    // Highest completed byte count shown per segment start; display never moves backwards between refreshes.
    private readonly Dictionary<long, long> _segmentHighWater = [];
    // Exponentially smoothed per-segment speed; raw per-stream rates sawtooth between reads.
    private readonly Dictionary<long, double> _segmentRate = [];
    private bool _updatingControls;
    private readonly bool _manual;
    private bool _completedLayout;
    private bool _refreshingCompletion;
    private long? _displayedSpeedLimit;

    public DownloadWindow(Guid jobId, bool manual = false)
    {
        _jobId = jobId;
        _manual = manual;
        InitializeComponent();
        FileIconHeader.DataContext = _iconJob;
        var theme = UiStateStore.Load().DarkMode;
        Root.RequestedTheme = theme is { } dark ? (dark ? ElementTheme.Dark : ElementTheme.Light) : ElementTheme.Default;
        WindowLayout.ResizeForDpi(this, 720, 700);
        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        Title = "Download status — Rill Download Manager";
        _connection.SnapshotReceived += snapshot => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _settings = snapshot.Settings;
            _globalShowCompletion = snapshot.Settings.ShowCompletionDialog;
            var job = snapshot.Jobs.FirstOrDefault(candidate => candidate.Id == _jobId);
            if (job is null) ShowRemoved();
            else Update(job, completionSettingsRefreshed: true);
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
            if (!connected) Notice.Text = $"Host disconnected. Reconnecting… {error}";
            else if (Notice.Text.StartsWith("Host disconnected", StringComparison.Ordinal)) Notice.Text = string.Empty;
            UpdateButtons();
        });
        _timer.Tick += async (_, _) => await LoadSegmentsAsync();
        Closed += async (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            await _connection.DisposeAsync();
        };
        UseSpeedLimit.Click += (_, _) => SpeedLimitInput.IsEnabled = UseSpeedLimit.IsChecked == true && _job?.CanChangeSpeedLimit == true;
        UpdateButtons();
        _connection.Start();
        _timer.Start();
    }

    public void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
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
        Heading.Text = "Download removed";
        OverallFooter.IsIndeterminate = false;
        OverallFooter.Value = 0;
        StatusValue.Text = "Removed";
        FileNameText.Text = string.Empty;
        DetailsTabs.Visibility = CompletionSummary.Visibility = Visibility.Collapsed;
        DontShowAgain.Visibility = Visibility.Collapsed;
        Notice.Text = "This download is no longer in the list.";
        Segments.Children.Clear();
        CloseButton.Content = "Close";
        UpdateButtons();
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
            DetailsTabs.Visibility = Visibility.Collapsed;
            CompletionSummary.Visibility = Visibility.Visible;
            WindowLayout.ResizeForDpi(this, 600, 400);
        }

        var percent = job.TotalBytes is > 0
            ? Math.Clamp(job.ReceivedBytes * 100.0 / job.TotalBytes.Value, 0, 100)
            : (job.Progress is { } progress ? Math.Clamp(progress * 100, 0, 100) : 0);
        var displayName = string.IsNullOrWhiteSpace(job.FileName) ? "Download" : job.FileName;
        Heading.Text = complete ? "Download complete" : JobViewModel.ToDisplayState(job.State);
        Title = complete ? $"Download complete — {displayName}" : $"{percent:0.0}% — {displayName}";
        FileNameText.Text = displayName;
        UrlText.Text = job.Url;
        CompletionPath.Text = job.FinalFilePath;
        CompletedUrl.Text = job.Url;
        CompletedPath.Text = job.FinalFilePath;
        CompletedSize.Text = $"Downloaded {Bytes(job.ReceivedBytes)} ({job.ReceivedBytes:N0} bytes)";
        OverallFooter.IsIndeterminate = job.TotalBytes is not > 0 && !job.IsTerminal;
        OverallFooter.Value = complete ? 100 : percent;
        // One main bar: the segment map when the size is known, the indeterminate bar only for unknown sizes.
        var sized = job.TotalBytes is > 0 || complete;
        OverallFooter.Visibility = sized ? Visibility.Collapsed : Visibility.Visible;
        SegmentMap.Visibility = sized ? Visibility.Visible : Visibility.Collapsed;
        if (complete) SegmentMap.SetFraction(1);
        else if (!_haveSegmentMap) SegmentMap.SetFraction(percent / 100);
        StatusValue.Text = JobViewModel.ToDisplayState(job.State);
        SizeValue.Text = job.TotalBytes is >= 0 ? Bytes(job.TotalBytes.Value) : "Unknown";
        DownloadedValue.Text = job.TotalBytes is > 0
            ? $"{Bytes(job.ReceivedBytes)} ({percent:0.00}%)"
            : Bytes(job.ReceivedBytes);
        RateValue.Text = job.BytesPerSecond is > 0 ? $"{Bytes((long)job.BytesPerSecond.Value)}/s" : "—";
        TimeLeftValue.Text = job.EstimatedRemaining is { } eta && !complete
            ? FormatDuration(eta)
            : complete ? "Complete" : "—";
        ResumeValue.Text = job.ResumeSupported switch
        {
            true => "Yes",
            false => "No",
            null => "Unknown",
        };
        var completionText = complete
            ? "The file has been saved. Choose an action below."
            : job.State == "paused" ? "The download is paused. Resume it or close this window."
            : "Closing this window will not stop the download.";
        if (_connected) Notice.Text = job.Failure?.Message ?? completionText;

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
                ? "Applies to ordinary HTTP/HTTPS file downloads. FTP, HLS, and DASH transfers do not support this control."
                : "Speed limiting is available for unfinished HTTP/HTTPS file downloads. It is unavailable for FTP, HLS, DASH, and finished transfers.";
            CurrentRateText.Text = job.BytesPerSecond is > 0
                ? $"Current transfer rate: {Bytes((long)job.BytesPerSecond.Value)}/s"
                : "Current transfer rate: —";
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
        if (job.IsTerminal) Segments.Children.Clear();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var complete = _job?.State == "completed";
        PauseButton.Visibility = _job is null || _job.IsTerminal ? Visibility.Collapsed : Visibility.Visible;
        ResumeButton.Visibility = _job is null || (_job.IsTerminal && !_job.CanResume) ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = _job?.CanCancel == true ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.IsEnabled = _connected && _job?.CanPause == true;
        ResumeButton.IsEnabled = _connected && _job?.CanResume == true;
        CancelButton.IsEnabled = _connected && _job?.CanCancel == true;
        ShowCompletionForJob.IsEnabled = _connected && _job is not null;
        OpenButton.Visibility = OpenWithButton.Visibility = FolderButton.Visibility =
            complete ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadSegmentsAsync()
    {
        if (_closed || _loadingSegments || !_connected || _job is null || _job.IsTerminal) return;
        _loadingSegments = true;
        try
        {
            var reply = await _connection.GetSegmentsAsync(_jobId);
            if (_closed || _job?.IsTerminal != false) return;
            var segments = reply.GetPayload<List<SegmentDto>>();
            var connectionsReply = await _connection.GetConnectionsAsync(_jobId);
            if (_closed || _job?.IsTerminal != false) return;
            var connections = connectionsReply.GetPayload<List<ConnectionTelemetryDto>>();
            var job = _job!;
            var total = job.TotalBytes ?? 0;
            // Single-stream downloads have no saved ranges; show them as one segment covering the file.
            if (segments.Count == 0 && total > 0)
                segments = [new SegmentDto { Start = 0, End = total - 1, Completed = job.ReceivedBytes,
                    IsComplete = job.ReceivedBytes >= total, Fraction = job.ReceivedBytes / (double)total }];
            // Checkpoints lag the live streams. Each live range starts at segment.Start + (bytes already saved),
            // so its true position is RangeStart - Start + ReceivedBytes. Take the furthest known point.
            foreach (var segment in segments)
            {
                var live = connections
                    .Where(c => c.RangeStart is { } from && from >= segment.Start && from <= segment.End)
                    .Select(c => c.RangeStart!.Value - segment.Start + c.ReceivedBytes)
                    .DefaultIfEmpty(0).Max();
                var length = Math.Max(1, segment.End - segment.Start + 1);
                _segmentHighWater.TryGetValue(segment.Start, out var shown);
                segment.Completed = Math.Min(length, Math.Max(Math.Max(segment.Completed, live), shown));
                _segmentHighWater[segment.Start] = segment.Completed;
                segment.IsComplete |= segment.Completed >= length;
            }
            if (segments.Count > 0) { SegmentMap.SetSegments(segments, total); _haveSegmentMap = true; }
            else SegmentMap.SetFraction(0);
            RenderSegmentTable(segments, connections);
        }
        catch (Exception ex)
        {
            if (!_closed) Notice.Text = $"Saved segment details unavailable: {ex.Message}";
        }
        finally { _loadingSegments = false; }
    }

    private void RenderSegmentTable(IReadOnlyList<SegmentDto> segments, IReadOnlyList<ConnectionTelemetryDto> connections)
    {
        Segments.Children.Clear();
        var secondary = RillTheme.Brush("RillSecondaryTextBrush", Root);
        Grid NewRow()
        {
            var row = new Grid { ColumnSpacing = 10, MinHeight = 24, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var width in new[] { 28d, -1d, 170d, 90d, 120d })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = width < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
            return row;
        }
        void Cell(Grid row, int column, UIElement element)
        {
            Grid.SetColumn((FrameworkElement)element, column);
            ((FrameworkElement)element).VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(element);
        }
        TextBlock Text(string value, bool muted = false, bool header = false)
        {
            var block = new TextBlock
            {
                Text = value, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = header ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            };
            // Leave the inherited theme foreground alone for normal cells; a null brush renders invisible text.
            if (muted || header) block.Foreground = secondary;
            return block;
        }

        if (segments.Count == 0)
        {
            Segments.Children.Add(Text("No segment information yet.", muted: true));
            return;
        }
        var head = NewRow();
        Cell(head, 0, Text("N.", header: true)); Cell(head, 1, Text("Progress", header: true));
        Cell(head, 2, Text("Downloaded", header: true)); Cell(head, 3, Text("Speed", header: true));
        Cell(head, 4, Text("Info", header: true));
        Segments.Children.Add(head);
        Segments.Children.Add(new Border { Height = 1, Background = RillTheme.Brush("RillDividerBrush", Root) });

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var length = Math.Max(1, segment.End - segment.Start + 1);
            var live = connections.Where(c => c.RangeStart is { } from ? from >= segment.Start && from <= segment.End
                : segments.Count == 1).ToList();
            var raw = live.Sum(c => Math.Max(0, c.BytesPerSecond));
            var rate = _segmentRate.TryGetValue(segment.Start, out var previous) && raw > 0 ? previous * 0.75 + raw * 0.25 : raw;
            _segmentRate[segment.Start] = rate;
            var row = NewRow();
            Cell(row, 0, Text((index + 1).ToString(CultureInfo.CurrentCulture), muted: true));
            Cell(row, 1, RillTheme.Styled(new ProgressBar { Maximum = 100, Value = Math.Clamp(segment.Completed * 100.0 / length, 0, 100) }));
            Cell(row, 2, Text($"{Bytes(segment.Completed)} / {Bytes(length)}"));
            Cell(row, 3, Text(rate > 0 ? $"{Bytes((long)rate)}/s" : "—", muted: rate <= 0));
            Cell(row, 4, Text(segment.IsComplete ? "Complete" : live.Count > 0 ? "Receiving data…" : "Waiting", muted: live.Count == 0));
            Segments.Children.Add(row);
        }
    }

    private static string Bytes(long bytes) => bytes >= 1024L * 1024
        ? $"{bytes / (1024.0 * 1024):N2} MB" : bytes >= 1024 ? $"{bytes / 1024.0:N1} KB" : $"{bytes:N0} B";

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) return "—";
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}d {duration.Hours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
        return duration.ToString(@"hh\:mm\:ss", CultureInfo.CurrentCulture);
    }

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

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = "Cancel download?",
            Content = "Cancel this transfer and delete its partial file?",
            PrimaryButtonText = "Cancel download",
            CloseButtonText = "Keep downloading",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary && _job?.CanCancel == true)
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
        if (_job?.State != "completed") return;
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
        if (_job?.State != "completed") return;
        try
        {
            if (File.Exists(_job.FinalFilePath))
                Process.Start(ShellActions.OpenWith(_job.FinalFilePath));
            else Notice.Text = "The downloaded file could not be found.";
        }
        catch (Exception ex) { Notice.Text = ex.Message; }
    }
}
