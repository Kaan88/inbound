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
    private int? _heightBeforeSegments;

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
        WindowLayout.ResizeForDpi(this, 720, 700);
        WindowPlacement.Track(this, "progress", restoreSize: false);
        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        Title = "Download status — Inbound Download Manager";
        WindowLayout.ApplyChrome(this, Root);
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
            Details.SetConnected(connected);
            if (!connected) Notice.Text = $"Host disconnected. Reconnecting… {error}";
            else if (Notice.Text.StartsWith("Host disconnected", StringComparison.Ordinal)) Notice.Text = string.Empty;
            UpdateButtons();
        });
        Details.Attach(_connection, SegmentMap, OverallFooter, Root);
        Details.SegmentsLayoutChanged += FitSegmentsSoon;
        Details.Problem += words => Notice.Text = words;
        Root.Loaded += (_, _) => FitSegmentsSoon();
        Closed += async (_, _) =>
        {
            _closed = true;
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
        Details.ShowNothing("Removed");
        FileNameText.Text = string.Empty;
        DetailsTabs.Visibility = CompletionSummary.Visibility = Visibility.Collapsed;
        DontShowAgain.Visibility = Visibility.Collapsed;
        Notice.Text = "This download is no longer in the list.";
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
            WindowLayout.ResizeForDpi(this, 600, 480);
        }

        var percent = JobDetailsPanel.Percent(job);
        var displayName = string.IsNullOrWhiteSpace(job.FileName) ? "Download" : job.FileName;
        Heading.Text = complete ? "Download complete" : JobViewModel.ToDisplayState(job.State);
        Title = complete ? $"Download complete — {displayName}" : $"{percent:0.0}% — {displayName}";
        FileNameText.Text = displayName;
        // A file from a provider account has no address of its own: only a reference to the account's item.
        var hasAddress = JobAddress.HasAddress(job);
        UrlHeader.Visibility = UrlText.Visibility = CompletedUrlHeader.Visibility = CompletedUrl.Visibility = hasAddress ? Visibility.Visible : Visibility.Collapsed;
        UrlHeader.Text = CompletedUrlHeader.Text = JobAddress.Label(job);
        UrlText.Text = JobAddress.Shown(job);
        CompletionPath.Text = job.FinalFilePath;
        CompletedUrl.Text = JobAddress.Shown(job);
        CompletedPath.Text = job.FinalFilePath;
        CompletedSize.Text = $"Downloaded {Bytes(job.ReceivedBytes)} ({job.ReceivedBytes:N0} bytes)";
        Details.Show(job);
        var completionText = complete && job.FileRemovedAfterExtraction ? JobNotes.NoFile(JobNotes.Note(job))
            : complete ? "The file has been saved. Choose an action below."
            : job.State == "paused" ? "The download is paused. Resume it or close this window."
            : job.State == "failed" && job.CanResume ? "Resume tries the download again."
            : "Closing this window will not stop the download.";
        var failure = JobNotes.FailureMessage(job);
        FailureText.Text = failure ?? string.Empty;
        FailureText.Visibility = failure is null ? Visibility.Collapsed : Visibility.Visible;
        if (_connected) Notice.Text = failure is null ? job.Failure?.Message ?? completionText : completionText;

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
        PauseButton.Visibility = _job is null || _job.IsTerminal ? Visibility.Collapsed : Visibility.Visible;
        ResumeButton.Visibility = _job is null || (_job.IsTerminal && !_job.CanResume) ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = _job?.CanCancel == true ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.IsEnabled = _connected && _job?.CanPause == true;
        ResumeButton.IsEnabled = _connected && _job?.CanResume == true;
        CancelButton.IsEnabled = _connected && _job?.CanCancel == true;
        ShowCompletionForJob.IsEnabled = _connected && _job is not null;
        DeleteButton.Visibility = _job is null ? Visibility.Collapsed : Visibility.Visible;
        DeleteButton.IsEnabled = _connected && _job is not null;
        // A download on another server: its file is on that machine, so this PC can't open or show it.
        OpenButton.Visibility = OpenWithButton.Visibility = FolderButton.Visibility =
            complete && _job?.FileRemovedAfterExtraction != true && RemoteDataRules.MayUseFiles(_connection.IsRemote) ? Visibility.Visible : Visibility.Collapsed;
        // The completion window's own default is this PC's choice, kept on this PC's Host; this window talks to the server.
        if (_connection.IsRemote) DontShowAgain.Visibility = Visibility.Collapsed;
    }

    /// <summary>Grows the window so every segment row is visible; restores the height when collapsed.</summary>
    private void FitSegmentsSoon() =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FitSegments);

    private void FitSegments()
    {
        if (_closed || Root.XamlRoot is null) return;
        var size = AppWindow.Size;
        if (!Details.SegmentsOpen || Details.SegmentRowCount == 0)
        {
            if (_heightBeforeSegments is { } previous)
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Width, previous));
                _heightBeforeSegments = null;
                return;
            }
            // Collapsed: drop blank space below the content so the window ends at the expander.
            StatusScroll.UpdateLayout();
            var slack = StatusScroll.ViewportHeight - StatusScroll.ExtentHeight;
            if (slack > 1)
            {
                var shrunk = Math.Max(240, size.Height - (int)Math.Floor(slack * Root.XamlRoot.RasterizationScale));
                if (shrunk < size.Height) AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Width, shrunk));
            }
            return;
        }

        StatusScroll.UpdateLayout();
        var overflow = StatusScroll.ExtentHeight - StatusScroll.ViewportHeight;
        if (overflow <= 1) return;
        var scale = Root.XamlRoot.RasterizationScale;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        var target = Math.Min(area.Height, size.Height + (int)Math.Ceiling(overflow * scale));
        if (target <= size.Height) return;
        _heightBeforeSegments ??= size.Height;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Width, target));
        var position = AppWindow.Position;
        if (position.Y + target > area.Y + area.Height)
            AppWindow.Move(new Windows.Graphics.PointInt32(position.X, Math.Max(area.Y, area.Y + area.Height - target)));
    }

    private static string Bytes(long bytes) => JobDetailsPanel.Bytes(bytes);

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
            CloseButtonText = "Keep downloading",
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
