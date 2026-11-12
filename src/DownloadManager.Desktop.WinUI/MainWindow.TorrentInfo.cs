using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T7: the Torrent Info pane on the right of the list (View › Torrent Info, remembered in ui-state). It shows the one
/// selected torrent's facts from what the list already has, so most of it costs nothing. While it is seen it reads the
/// trackers' summary on the torrent's own updates (at most every <see cref="TorrentControlRules.DetailsReadMs"/> while it
/// runs, and once when it stops) and the details for the v2 info hash once per state until it is known. No timer of its own: while it
/// shows a running torrent the window's torrent watch makes the Host report it every second; hidden, deselected or
/// minimized, nothing is read (<see cref="TorrentInfoRules"/>).
/// </summary>
public sealed partial class MainWindow
{
    private Guid? _infoShownId;
    private string? _infoV2;
    private (Guid Job, string State, bool Seeding, bool Metadata)? _infoDetailsFor;
    private IReadOnlyList<TorrentTrackerDto>? _infoTrackers;
    private (Guid Job, string State, bool Seeding)? _infoTrackersKey;
    private long _infoTrackersAt = long.MinValue / 2;
    private bool _infoReadingTrackers;
    private bool _infoReadingDetails;
    private bool _infoSeen;

    private void InitializeTorrentInfo()
    {
        TorrentInfoMenuItem.IsChecked = _uiState.ShowTorrentInfo;
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidVisibilityChange || args.DidPresenterChange) SyncTorrentInfo();
        };
    }

    private bool InfoMinimized => AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    /// <summary>The torrent the pane shows now, or null (<see cref="TorrentInfoRules.Shown"/>).</summary>
    private JobDto? InfoJob => TorrentInfoRules.Shown(TorrentInfoMenuItem.IsChecked, !_providerFull, SelectedJobDtos);

    /// <summary>The pane is seen: it shows a torrent in this shown, unminimized window.</summary>
    private bool TorrentInfoWatches => TorrentInfoRules.Watches(_infoSeen, InfoJob);

    /// <summary>Shows, fills or hides the pane after a change of selection, of a job, of the window or of the menu.</summary>
    private void SyncTorrentInfo()
    {
        var job = InfoJob;
        var width = job is null ? null : TorrentInfoRules.Width(ListArea.ActualWidth, DownloadListGrid.MinWidth);
        var shown = job is not null && width is not null;
        var visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (TorrentInfoPane.Visibility != visibility) TorrentInfoPane.Visibility = visibility;
        if (width is { } paneWidth && Math.Abs(TorrentInfoPane.Width - paneWidth) > 0.5) TorrentInfoPane.Width = paneWidth;

        var seen = TorrentInfoRules.Seen(AppWindow.IsVisible, InfoMinimized, shown ? job : null);
        var seenChanged = seen != _infoSeen;
        _infoSeen = seen;
        if (job?.Id != _infoShownId)
        {
            // Another torrent: what was read for the last one doesn't hold.
            _infoShownId = job?.Id;
            _infoTrackers = null;
            _infoTrackersKey = null;
            _infoDetailsFor = null;
            _infoV2 = null;
        }

        if (seenChanged) SyncTorrentWatch();
        // Hidden or minimized, nothing is filled or read; showing the window again brings it up to date.
        if (!seen || job is null) return;
        FillTorrentInfo(job);
        ReadTorrentInfoIfDue(job);
    }

    private void FillTorrentInfo(JobDto job)
    {
        var stateWord = job.State == "completed" ? "Complete" : JobViewModel.ToDisplayState(job.State);
        var view = TorrentInfoRules.Describe(job, stateWord, _settings, DateTimeOffset.UtcNow, _infoV2, _infoTrackers, _remote.State.TorrentsOffered);
        Set(TorrentInfoName, view.Name);
        Set(TorrentInfoStatus, view.Status);
        if (Math.Abs(TorrentInfoBar.Value - view.Percent) > 0.01) TorrentInfoBar.Value = view.Percent;
        Set(TorrentInfoProgress, view.Progress);
        Set(TorrentInfoDown, view.Down);
        Set(TorrentInfoUp, view.Up);
        Set(TorrentInfoSwarm, view.Swarm);
        Set(TorrentInfoTimeLeft, view.TimeLeft);
        Set(TorrentInfoRatio, view.Ratio);
        var goal = view.Goal is null ? Visibility.Collapsed : Visibility.Visible;
        if (TorrentInfoGoal.Visibility != goal) TorrentInfoGoal.Visibility = TorrentInfoGoalLabel.Visibility = goal;
        Set(TorrentInfoGoal, view.Goal ?? string.Empty);
        Set(TorrentInfoTrackers, view.Trackers);
        Set(TorrentInfoFolder, view.Folder);
        Set(TorrentInfoAdded, view.Added);
        Set(TorrentInfoHash, view.InfoHash);
        AutomationProperties.SetName(TorrentInfoBar, $"{view.Percent:0} percent downloaded");

        var connected = _connection.IsConnected;
        TorrentInfoOpenFolder.IsEnabled = RemoteDataRules.MayUseFiles(_connection.IsRemote);
        TorrentInfoCopyMagnet.IsEnabled = TorrentListRules.MagnetOf(job) is not null;
        TorrentInfoCopyHash.IsEnabled = job.Torrent?.InfoHash.Length > 0;
        var recheck = TorrentControls && TorrentControlRules.CanRecheck(job) ? Visibility.Visible : Visibility.Collapsed;
        if (TorrentInfoRecheck.Visibility != recheck) TorrentInfoRecheck.Visibility = recheck;
        TorrentInfoRecheck.IsEnabled = connected && !_torrentControlBusy;

        static void Set(TextBlock block, string text)
        {
            if (block.Text != text) block.Text = text;
        }
    }

    /// <summary>The trackers and the v2 hash as <see cref="TorrentInfoRules"/> says they are due. Never from a timer.</summary>
    private void ReadTorrentInfoIfDue(JobDto job)
    {
        if (!_connection.IsConnected) return;
        var key = TorrentInfoRules.ReadKey(job);
        var offered = _remote.State.TorrentsOffered;
        if (!_infoReadingTrackers && TorrentInfoRules.TrackersDue(true, offered, job, _infoTrackersKey != key, Environment.TickCount64 - _infoTrackersAt))
        {
            _infoTrackersKey = key;
            _ = ReadInfoTrackersAsync(job.Id);
        }
        else if (!TorrentDetailsRules.MayBeRunning(job) && _infoTrackers is not null)
        {
            // It stopped: the summary of when it ran no longer holds.
            _infoTrackers = null;
            FillTorrentInfo(job);
        }

        var detailsKey = TorrentInfoRules.DetailsKey(job);
        if (!_infoReadingDetails && TorrentInfoRules.DetailsDue(true, TorrentControls, _infoDetailsFor == detailsKey, _infoV2 is { Length: 64 }))
        {
            // Keyed by its state too: the Host gives the v2 hash only while it runs, so a start asks again.
            _infoDetailsFor = detailsKey;
            _ = ReadInfoDetailsAsync(job.Id);
        }
    }

    private async Task ReadInfoTrackersAsync(Guid jobId)
    {
        _infoReadingTrackers = true;
        _infoTrackersAt = Environment.TickCount64;
        var connection = _connection;
        try
        {
            var result = await connection.GetTorrentTrackersAsync(jobId);
            if (!ReferenceEquals(connection, _connection) || _infoShownId != jobId) return;
            _infoTrackers = result is { Ok: true, Running: true } ? result.Trackers : null;
            if (InfoJob is { } now && now.Id == jobId) FillTorrentInfo(now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UiTrace.Write($"Torrent info trackers not read: {ex.GetType().Name}");
        }
        finally
        {
            _infoReadingTrackers = false;
            ReadForTorrentShownSince(jobId);
        }
    }

    private async Task ReadInfoDetailsAsync(Guid jobId)
    {
        _infoReadingDetails = true;
        var connection = _connection;
        try
        {
            var details = await connection.GetTorrentDetailsAsync(jobId);
            if (!ReferenceEquals(connection, _connection) || _infoShownId != jobId) return;
            if (details is not { Ok: true })
            {
                // Asked again the next time this torrent is shown.
                _infoDetailsFor = null;
                return;
            }

            _infoV2 = TorrentControlRules.KeptV2(_infoV2, details.InfoHashV2);
            if (InfoJob is { } now && now.Id == jobId) FillTorrentInfo(now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _infoDetailsFor = null;
            UiTrace.Write($"Torrent info details not read: {ex.GetType().Name}");
        }
        finally
        {
            _infoReadingDetails = false;
            ReadForTorrentShownSince(jobId);
        }
    }

    /// <summary>
    /// Another torrent was selected while a read for <paramref name="jobId"/> ran, which kept it from being read: read
    /// for it now rather than wait for its next update, which a paused one may never send.
    /// </summary>
    private void ReadForTorrentShownSince(Guid jobId)
    {
        if (_infoShownId == jobId || !_infoSeen || InfoJob is not { } now || now.Id != _infoShownId) return;
        ReadTorrentInfoIfDue(now);
    }

    /// <summary>A new connection or another Host: what was read is forgotten and read again when due.</summary>
    private void ForgetTorrentInfoReads()
    {
        _infoTrackers = null;
        _infoTrackersKey = null;
        _infoDetailsFor = null;
        _infoV2 = null;
    }

    private void MenuTorrentInfo_Click(object sender, RoutedEventArgs e)
    {
        SyncTorrentInfo();
        SaveUiState();
    }

    private void TorrentInfoHide_Click(object sender, RoutedEventArgs e)
    {
        TorrentInfoMenuItem.IsChecked = false;
        SyncTorrentInfo();
        SaveUiState();
        JobsList.Focus(FocusState.Programmatic);
    }

    private void ListArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5) SyncTorrentInfo();
    }

    private void TorrentInfoOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is { } job) OpenContainingFolder(job);
    }

    private void TorrentInfoCopyHash_Click(object sender, RoutedEventArgs e)
    {
        if (InfoJob?.Torrent is not { InfoHash.Length: > 0 } torrent) return;
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(_infoV2 is { Length: 64 } v2 ? $"{torrent.InfoHash}{Environment.NewLine}{v2}" : torrent.InfoHash);
        try
        {
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            ShowPassingStatus("Info hash copied.");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            ShowPassingStatus("Another program is using the clipboard. Try again.");
        }
    }
}
