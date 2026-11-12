using System.Collections.ObjectModel;
using DownloadManager.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T2: a torrent's Peers, Trackers and Pieces &amp; Speed tabs, and Start/Stop seeding for a finished torrent. The shown
/// tab's list is read from the Host every two seconds only while that tab shows in this open, shown, unminimized window
/// and the torrent may be on the torrent engine (<see cref="TorrentDetailsRules.Polls"/>): no timer runs otherwise. A Host
/// without torrents (or a server that doesn't say <c>torrents</c>) gets none of these tabs.
/// </summary>
public sealed partial class DownloadWindow
{
    private const string PeersNoteText = "Peers see the public IP address of the computer that downloads, and it sees theirs; that is how BitTorrent works.";
    private const string TrackersNoteText = "A tracker's address is shown without what follows its name, where a private tracker keeps your passkey.";

    /// <summary>Ticks while a list tab is read, as often as <see cref="TorrentDetailsRules.TickInterval"/> says for that tab.</summary>
    private readonly DispatcherTimer _torrentTimer = new() { Interval = TorrentDetailsRules.Refresh };
    private readonly ObservableCollection<TorrentPeerRow> _peerRows = [];
    private readonly ObservableCollection<TorrentTrackerRow> _trackerRows = [];

    /// <summary>A peer keeps the number it got when first seen in this window, so "peer 3" stays the same peer.</summary>
    private readonly Dictionary<string, int> _peerNumbers = new(StringComparer.Ordinal);
    private bool _torrentQuerying;

    /// <summary>When the shown tab's list was last asked for (<see cref="Environment.TickCount64"/>); null reads it at once.</summary>
    private long? _torrentQueriedAt;
    private bool _torrentUnsupported;
    private TorrentDetailsTab _polledTab;

    /// <summary>The torrent's state when the Host last said it isn't on the engine; nothing is read again until it changes.</summary>
    private (string State, bool Seeding)? _notRunningFor;
    private bool _seedBusy;

    // T12: the tabs' headers, built the first time a torrent shows (an ordinary download never needs them).
    private DetailsTable? _filesTable;
    private DetailsTable? _peersTable;
    private DetailsTable? _trackersTable;

    /// <summary>The last list each tab read, to sort again when the pointer leaves a held list or a header is clicked.</summary>
    private IReadOnlyList<TorrentPeerDto> _lastPeers = [];
    private IReadOnlyList<TorrentTrackerDto> _lastTrackers = [];

    private DetailsTable FilesTable => _filesTable ??= MakeTable("progress.files", Hidden(ListColumnSets.TorrentFiles(), "priority"), FilesHeader, FilesList, FilesGrid, FilesSideways, FilesCard,
        () => SortFiles(FilesSortReason.Clicked), () => SortFiles(FilesSortReason.Released));

    private DetailsTable PeersTable => _peersTable ??= MakeTable("progress.peers", ListColumnSets.TorrentPeers(), PeersHeader, PeersList, PeersGrid, PeersSideways, PeersCard,
        () => { if (_lastPeers.Count > 0) ShowPeers(_lastPeers, sorting: true); });

    private DetailsTable TrackersTable => _trackersTable ??= MakeTable("progress.trackers", Hidden(ListColumnSets.TorrentTrackers(), "next"), TrackersHeader, TrackersList,
        TrackersGrid, TrackersSideways, TrackersCard, () => { if (_lastTrackers.Count > 0) ShowTrackers(_lastTrackers, sorting: true); });

    /// <summary>A column that shows only where the Host has T3's controls (Priority, Next announce) starts hidden.</summary>
    private static ListColumnLayout Hidden(ListColumnLayout layout, string key)
    {
        layout.SetVisible(key, false);
        return layout;
    }

    /// <param name="sorted">After a header click.</param>
    /// <param name="released">When a held list lets go with a re-sort waiting; <paramref name="sorted"/> when not given.</param>
    private DetailsTable MakeTable(string name, ListColumnLayout layout, Grid header, ListView list, Grid listGrid, ScrollViewer sideways,
        FrameworkElement card, Action sorted, Action? released = null)
    {
        var table = new DetailsTable(name, layout, header, list, listGrid, sideways, card, Root, sorted);
        table.Released += released ?? sorted;
        return table;
    }

    private void InitializeTorrentTabs()
    {
        PeersList.ItemsSource = _peerRows;
        TrackersList.ItemsSource = _trackerRows;
        TorrentChart.Heading = "Download speed";
        TorrentChart.PeriodSeconds = 60;
        _torrentTimer.Tick += (_, _) => TorrentTick();
        Closed += (_, _) => _torrentTimer.Stop();
        // Another Host (a switch of server, or one updated or restarted meanwhile) may know the lists, and may run the
        // torrent now although the one before said it didn't: forget both and read the shown tab again. This runs after
        // the window's own connection handler, so it starts the reading itself.
        _connection.ConnectionChanged += (connected, _) =>
        {
            if (!connected) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed) return;
                _torrentUnsupported = false;
                _notRunningFor = null;
                SyncTorrentPolling();
            });
        };
    }

    /// <summary>The Host has torrents: a server's hello lists <c>torrents</c>.</summary>
    private bool TorrentsOffered => TorrentDetailsRules.Offered(_connection.Hello?.Capabilities);

    private bool IsTorrentTab(object? item) => item == FilesTab || item == PeersTab || item == TrackersTab || item == PiecesTab;

    private TorrentDetailsTab ShownTorrentTab
        => !_isTorrent ? TorrentDetailsTab.None
            : PeersPage.Visibility == Visibility.Visible ? TorrentDetailsTab.Peers
            : TrackersPage.Visibility == Visibility.Visible ? TorrentDetailsTab.Trackers
            : PiecesPage.Visibility == Visibility.Visible ? TorrentDetailsTab.Pieces
            : TorrentDetailsTab.None;

    private bool Minimized => AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    /// <summary>The tabs show for a torrent on a Host with torrents; a tab that went away gives its place to Overview.</summary>
    private void ShowTorrentTabs(JobDto job)
    {
        var shown = job.Torrent is not null && TorrentsOffered ? Visibility.Visible : Visibility.Collapsed;
        PeersTab.Visibility = TrackersTab.Visibility = PiecesTab.Visibility = shown;
        if (job.Torrent is not null)
        {
            // Built now, so their headers (and the widths kept for them) are there before a tab first shows.
            _ = FilesTable;
            _ = PeersTable;
            _ = TrackersTable;
        }

        if (shown == Visibility.Collapsed && (Tabs.SelectedItem == PeersTab || Tabs.SelectedItem == TrackersTab || Tabs.SelectedItem == PiecesTab))
            Tabs.SelectedItem = OverviewTab;
        if (job.Torrent is null) return;

        // A new state (it started, finished, began or stopped seeding) may put it on the engine again.
        if (_notRunningFor is { } key && key != TorrentDetailsRules.RunKey(job)) _notRunningFor = null;
        TorrentChart.Heading = TorrentDetailsRules.GraphHeading(job);
        TorrentSpeedText.Text = TorrentDetailsRules.SpeedLine(job);
        ShowSeedButton(job);
    }

    /// <summary>Starts or stops the reading as the tab, the window and the torrent allow (<see cref="TorrentDetailsRules.Polls"/>).</summary>
    private void SyncTorrentPolling()
    {
        var tab = ShownTorrentTab;
        var polls = TorrentDetailsRules.Polls(tab, AppWindow.IsVisible, Minimized, _closed, _connected, _torrentUnsupported,
            _notRunningFor is not null, _job);
        TorrentChart.IsSeen = !_closed && tab == TorrentDetailsTab.Pieces && AppWindow.IsVisible && !Minimized;
        if (polls)
        {
            // Another tab shows its list at once, not at the next reading of the one before.
            if (!_torrentTimer.IsEnabled || tab != _polledTab)
            {
                _polledTab = tab;
                _torrentTimer.Interval = TorrentDetailsRules.TickInterval(tab);
                _torrentTimer.Start();
                _torrentQueriedAt = null;
                TorrentTick();
            }
        }
        else if (_torrentTimer.IsEnabled)
        {
            _torrentTimer.Stop();
        }

        if (tab != TorrentDetailsTab.None && !polls) ShowIdleTab(tab);
    }

    /// <summary>A shown tab that isn't read says why: the Host doesn't list them, or the torrent isn't running.</summary>
    private void ShowIdleTab(TorrentDetailsTab tab)
    {
        if (_closed) return;
        string? words = _torrentUnsupported ? TorrentDetailsRules.Unsupported
            : !TorrentDetailsRules.MayBeRunning(_job) || _notRunningFor is not null ? TorrentDetailsRules.NotRunning
            : null;
        if (words is null) return;
        ShowNotRunning(tab, words);
    }

    private void ShowNotRunning(TorrentDetailsTab tab, string words)
    {
        switch (tab)
        {
            case TorrentDetailsTab.Peers:
                _lastPeers = [];
                _peerRows.Clear();
                PeersTab.Text = "Peers";
                PeersNote.Text = words;
                break;
            case TorrentDetailsTab.Trackers:
                _lastTrackers = [];
                _trackerRows.Clear();
                TrackersTab.Text = "Trackers";
                TrackersNote.Text = words;
                break;
            case TorrentDetailsTab.Pieces:
                PiecesBar.SetPieces(null);
                PiecesSummaryText.Text = string.Empty;
                PiecesNote.Text = words;
                PiecesNote.Visibility = Visibility.Visible;
                break;
        }
    }

    private void TorrentTick()
    {
        if (_closed || _job is not { } job) return;
        var tab = ShownTorrentTab;
        if (tab == TorrentDetailsTab.Pieces)
        {
            var now = DateTimeOffset.UtcNow;
            TorrentChart.AddSample(now, _connected ? TorrentDetailsRules.GraphRate(job, now) : null);
        }

        if (_torrentQuerying || !TorrentDetailsRules.ReadDue(_torrentQueriedAt, Environment.TickCount64)) return;
        _ = ReadTorrentTabAsync(tab);
    }

    /// <summary>One read of the shown tab's list. Never throws; what went wrong is said on the tab.</summary>
    private async Task ReadTorrentTabAsync(TorrentDetailsTab tab)
    {
        if (tab == TorrentDetailsTab.None) return;
        _torrentQuerying = true;
        _torrentQueriedAt = Environment.TickCount64;
        var key = _job is { } job ? TorrentDetailsRules.RunKey(job) : default;
        try
        {
            switch (tab)
            {
                case TorrentDetailsTab.Peers:
                    var peers = await _connection.GetTorrentPeersAsync(_jobId);
                    if (Settled(peers, peers?.Ok, peers?.Running, peers?.Error, tab, key)) ShowPeers(peers!.Peers);
                    break;
                case TorrentDetailsTab.Trackers:
                    var trackers = await _connection.GetTorrentTrackersAsync(_jobId);
                    if (Settled(trackers, trackers?.Ok, trackers?.Running, trackers?.Error, tab, key)) ShowTrackers(trackers!.Trackers);
                    break;
                case TorrentDetailsTab.Pieces:
                    var pieces = await _connection.GetTorrentPiecesAsync(_jobId);
                    if (Settled(pieces, pieces?.Ok, pieces?.Running, pieces?.Error, tab, key)) ShowPieces(pieces!);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A dropped connection says so in the notice; the next tick tries again.
            UiTrace.Write($"Torrent {tab} not read: {ex.GetType().Name}");
        }
        finally
        {
            _torrentQuerying = false;
        }
    }

    /// <summary>
    /// What an answer means before its list is shown: an older Host that doesn't know the request (no more reading), a
    /// torrent not on the engine (none until its state changes), or an error said on the tab. True: show the list.
    /// </summary>
    private bool Settled(object? answer, bool? ok, bool? running, string? error, TorrentDetailsTab tab, (string State, bool Seeding) key)
    {
        if (_closed) return false;
        if (answer is null)
        {
            _torrentUnsupported = true;
            SyncTorrentPolling();
            return false;
        }

        // The tab changed while this read was out.
        if (ShownTorrentTab != tab) return false;
        if (ok != true)
        {
            var words = string.IsNullOrWhiteSpace(error) ? "The Host didn't answer." : RemoteText.Failure(error);
            if (tab == TorrentDetailsTab.Peers) PeersNote.Text = words;
            else if (tab == TorrentDetailsTab.Trackers) TrackersNote.Text = words;
            else
            {
                PiecesNote.Text = words;
                PiecesNote.Visibility = Visibility.Visible;
            }

            return false;
        }

        if (running != true)
        {
            _notRunningFor = key;
            ShowNotRunning(tab, TorrentDetailsRules.NotRunning);
            SyncTorrentPolling();
            return false;
        }

        return true;
    }

    /// <param name="sorting">A header was clicked (or a held list let go): the new order shows even under the pointer.</param>
    private void ShowPeers(IReadOnlyList<TorrentPeerDto> peers, bool sorting = false)
    {
        _lastPeers = peers;
        string Key(TorrentPeerDto peer) => TorrentDetailsRules.PeerAddress(peer.Address, peer.Port);
        // A torrent open for days meets many peers: the numbers start again rather than grow without end.
        if (_peerNumbers.Count > 5000) _peerNumbers.Clear();
        foreach (var peer in peers)
        {
            var key = Key(peer);
            if (!_peerNumbers.ContainsKey(key)) _peerNumbers[key] = _peerNumbers.Count + 1;
        }

        // The busiest first (peers that move nothing keep the order they came in), or by the column clicked (T12). Under
        // the pointer the rows keep their places and the new order waits until it leaves.
        var table = PeersTable;
        var sorted = TorrentDetailsSort.Peers(peers, peer => _peerNumbers[Key(peer)], table.SortKey, table.SortAscending).ToList();
        if (!sorting && table.Held)
        {
            var held = DetailsSort.Held(_peerRows.Select(row => row.Key).ToList(), sorted, Key);
            table.ResortPending = !held.Select(Key).SequenceEqual(sorted.Select(Key));
            sorted = held;
        }

        DetailsRowSync.Sync(_peerRows, sorted, Key, row => row.Key, () => new TorrentPeerRow(),
            (row, peer, _) => row.Apply(Key(peer), _peerNumbers[Key(peer)], peer));
        PeersTab.Text = TorrentDetailsRules.TabTitle("Peers", peers.Count);
        PeersNote.Text = peers.Count == 0 ? "No peers are connected right now." : PeersNoteText;
    }

    private void ShowTrackers(IReadOnlyList<TorrentTrackerDto> trackers, bool sorting = false)
    {
        _lastTrackers = trackers;
        // By tier, or by the column clicked (T12); held under the pointer as the peers are.
        var table = TrackersTable;
        // A tracker listed in two tiers is two rows: each is known by its address and the time it comes (TrackerKeys).
        var keys = TorrentDetailsSort.TrackerKeys(trackers);
        string Key(TorrentTrackerDto tracker) => keys[tracker];
        var ordered = TorrentDetailsSort.Trackers(trackers, table.SortKey, table.SortAscending).ToList();
        if (!sorting && table.Held)
        {
            var held = DetailsSort.Held(_trackerRows.Select(row => row.Key).ToList(), ordered, Key);
            table.ResortPending = !held.Select(Key).SequenceEqual(ordered.Select(Key));
            ordered = held;
        }

        DetailsRowSync.Sync(_trackerRows, ordered, Key, row => row.Key, () => new TorrentTrackerRow(),
            (row, tracker, _) => row.Apply(Key(tracker), tracker, withNext: Controls));
        TrackersTab.Text = TorrentDetailsRules.TabTitle("Trackers", trackers.Count);
        TrackersNote.Text = trackers.Count == 0 ? "This torrent has no trackers: it finds peers through the DHT and other peers." : TrackersNoteText;
    }

    private void ShowPieces(TorrentPiecesResult pieces)
    {
        PiecesBar.SetPieces(pieces);
        PiecesSummaryText.Text = pieces.Count > 0 ? TorrentDetailsRules.PiecesSummary(pieces) : string.Empty;
        AutomationProperties.SetName(PiecesBar, pieces.Count > 0 ? TorrentDetailsRules.PiecesSummary(pieces) : "Pieces this torrent has");
        PiecesNote.Visibility = Visibility.Collapsed;
    }

    // ---- Start seeding and Stop seeding ----

    private void ShowSeedButton(JobDto job)
    {
        var start = TorrentDetailsRules.SeedButton(job, TorrentsOffered, _settings);
        SeedButton.Visibility = start is null ? Visibility.Collapsed : Visibility.Visible;
        if (start is not { } on) return;
        var text = on ? "Start seeding" : "Stop seeding";
        SeedLabel.Text = text;
        AutomationProperties.SetName(SeedButton, text);
        ToolTipService.SetToolTip(SeedButton, on ? "Share this torrent's files with other peers again" : "Stop sharing this torrent's files");
        SeedGlyph.Glyph = on ? "" : "";
        SeedButton.IsEnabled = _connected && !_seedBusy;
    }

    private async void Seed_Click(object sender, RoutedEventArgs e)
    {
        if (_seedBusy || _job is not { } job || TorrentDetailsRules.SeedButton(job, TorrentsOffered, _settings) is not { } on) return;
        _seedBusy = true;
        SeedButton.IsEnabled = false;
        try
        {
            var result = await _connection.SetTorrentSeedingAsync(_jobId, on);
            if (_closed) return;
            if (!result.Ok)
                Notice.Text = (on ? "Seeding didn't start: " : "Seeding didn't stop: ")
                    + (string.IsNullOrWhiteSpace(result.Error) ? "the Host didn't say why." : RemoteText.Failure(result.Error));
        }
        catch (Exception ex)
        {
            if (!_closed) Notice.Text = on ? $"Seeding didn't start. {ex.Message}" : $"Seeding didn't stop. {ex.Message}";
        }
        finally
        {
            _seedBusy = false;
            if (!_closed && _job is { } now) ShowSeedButton(now);
        }
    }
}
