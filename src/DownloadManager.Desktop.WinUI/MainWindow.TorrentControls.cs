using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T3 in the main window: whether the shown Host has the torrent controls (asked once per connection), Force Recheck
/// and Announce Now in the row menu, the status chip's DHT nodes, and the watch that makes the Host report torrent
/// progress every second while this window shows running torrents. Nothing runs while no torrent runs.
/// </summary>
public sealed partial class MainWindow
{
    private MenuFlyoutItem? _menuRecheck;
    private MenuFlyoutItem? _menuAnnounce;
    private TorrentWatch? _torrentWatch;

    /// <summary>The DHT words of the chip, read while the watch runs; null while not known.</summary>
    private string? _dhtWords;
    private bool _torrentControlBusy;

    /// <summary>The shown Host has the T3 torrent controls.</summary>
    private bool TorrentControls => TorrentControlRules.Supported(_remote.State.TorrentsOffered, _connection.TorrentControls);

    private void InitializeTorrentControls()
    {
        _torrentWatch = new TorrentWatch(DispatcherQueue, on => _connection.WatchTorrentsAsync(on), () => _ = ReadDhtAsync());
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidVisibilityChange || args.DidPresenterChange) SyncTorrentWatch();
        };
        Closed += (_, _) => _torrentWatch?.Stop();
    }

    /// <summary>
    /// The window is about to show another Host: the one shown until now is told the watch ended (the local Host stays
    /// connected, and would otherwise report every second until the lease runs out), and the watch starts over.
    /// </summary>
    private void EndTorrentWatchFor(HostConnection old)
    {
        if (_torrentWatch is null) return;
        if (_torrentWatch.IsWatching && old.IsConnected) _ = old.WatchTorrentsAsync(false);
        _torrentWatch.Reset();
        _dhtWords = null;
    }

    /// <summary>A connection came or went: the Host forgot any watch, and another Host may or may not have the controls.</summary>
    private void OnTorrentControlsConnection(bool connected)
    {
        _torrentWatch?.Reset();
        _dhtWords = null;
        ForgetTorrentInfoReads();
        if (!connected) return;
        var connection = _connection;
        _ = ProbeTorrentControlsAsync(connection);
    }

    private async Task ProbeTorrentControlsAsync(HostConnection connection)
    {
        await connection.ProbeTorrentControlsAsync();
        if (!ReferenceEquals(connection, _connection)) return;
        SyncTorrentWatch();
        UpdateButtons();
    }

    /// <summary>
    /// Watches while this window shows, isn't minimized, and lists a torrent that downloads or seeds, or the Torrent Info
    /// pane is seen with one (T7).
    /// </summary>
    private void SyncTorrentWatch()
    {
        if (_torrentWatch is null) return;
        var watches = TorrentControlRules.Watches(AppWindow.IsVisible,
            AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized },
            _connection.IsConnected, TorrentControls, _visibleJobs.Select(row => row.Dto).Append(TorrentInfoWatches ? InfoJob : null));
        var was = _torrentWatch.IsWatching;
        _torrentWatch.Sync(watches);
        if (watches && !was) _ = ReadDhtAsync();
        if (!watches && _dhtWords is not null)
        {
            _dhtWords = null;
            UpdateTorrentChip();
        }
    }

    /// <summary>The DHT nodes for the chip, read when the watch starts and on each renewal (never while no torrent runs).</summary>
    private async Task ReadDhtAsync()
    {
        try
        {
            var connection = _connection;
            var network = await connection.GetTorrentNetworkAsync();
            if (!ReferenceEquals(connection, _connection)) return;
            _dhtWords = network is { Ok: true, Running: true } ? TorrentControlRules.DhtText(network.DhtNodes) : null;
            UpdateTorrentChip();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UiTrace.Write($"DHT nodes not read: {ex.GetType().Name}");
        }
    }

    private void AddTorrentControlItems(MenuFlyout flyout)
    {
        flyout.Items.Add(_menuRecheck = CreateContextItem("Force Recheck", MenuRecheck_Click));
        flyout.Items.Add(_menuAnnounce = CreateContextItem("Announce Now", MenuAnnounce_Click));
        ToolTipService.SetToolTip(_menuRecheck, "Check the torrent's files on disk again");
        ToolTipService.SetToolTip(_menuAnnounce, "Ask the trackers for peers now");
    }

    /// <summary>Force Recheck and Announce Now show for the selected torrents they apply to, on a Host with the controls.</summary>
    private void SyncTorrentControlItems()
    {
        var selection = SelectedJobDtos;
        var controls = TorrentControls;
        if (_menuRecheck is not null)
        {
            _menuRecheck.Visibility = controls && selection.Any(TorrentControlRules.CanRecheck) ? Visibility.Visible : Visibility.Collapsed;
            _menuRecheck.IsEnabled = !_torrentControlBusy;
        }

        if (_menuAnnounce is not null)
        {
            _menuAnnounce.Visibility = controls && selection.Any(TorrentControlRules.CanAnnounce) ? Visibility.Visible : Visibility.Collapsed;
            _menuAnnounce.IsEnabled = !_torrentControlBusy;
        }
    }

    private async void MenuRecheck_Click(object sender, RoutedEventArgs e) => await RunTorrentControlAsync(recheck: true);

    private async void MenuAnnounce_Click(object sender, RoutedEventArgs e) => await RunTorrentControlAsync(recheck: false);

    /// <summary>Force Recheck or Announce Now for each selected torrent it applies to, one after another.</summary>
    private async Task RunTorrentControlAsync(bool recheck)
    {
        if (_torrentControlBusy || !TorrentControls) return;
        var selection = SelectedJobDtos;
        var targets = TorrentControlRules.Targets(selection, recheck ? TorrentControlRules.CanRecheck : TorrentControlRules.CanAnnounce);
        if (targets.Count == 0) return;
        _torrentControlBusy = true;
        var done = 0;
        var failed = 0;
        string? firstError = null;
        try
        {
            foreach (var jobId in targets)
            {
                var result = recheck ? await _remote.RecheckTorrentAsync(jobId) : await _remote.AnnounceTorrentAsync(jobId);
                if (result.Ok) done++;
                else
                {
                    failed++;
                    firstError ??= string.IsNullOrWhiteSpace(result.Error) ? null : RemoteText.Failure(result.Error);
                }
            }
        }
        catch (Exception ex)
        {
            UiTrace.Write($"Torrent {(recheck ? "recheck" : "announce")} failed: {ex.GetType().Name}");
            failed += targets.Count - done - failed;
        }
        finally
        {
            _torrentControlBusy = false;
        }

        var name = targets.Count == 1 && _jobsById.TryGetValue(targets[0], out var row) ? row.FileName : null;
        ShowPassingStatus(TorrentControlRules.Said(recheck, name, done, failed, firstError));
    }
}
