using System.Collections.Specialized;
using System.ComponentModel;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using DownloadManager.Mobile.TorBox;
using DownloadManager.Mobile.Torrents;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Tabs;
using Inbound.App.Host;

namespace Inbound.App.Screens;

/// <summary>
/// A torrent's details (t4): the card with percent, rates, bar and Pause/Resume, Stop and Delete, and the tabs Overview, Files, Peers and
/// Trackers. The row the screen was opened from keeps it current (the Host sends progress as it changes, every second while this screen has
/// told it a torrent is on show with torrent.watch); the lists are asked for every two seconds, only while this page is shown, the torrent runs
/// and the Host can be asked. Nothing runs while the app is in the background or the page is closed.
/// </summary>
internal sealed class TorrentDetailsScreen
{
    private const int RefreshMs = 2000;
    private const int WatchRenewMs = 45_000;
    private const int FilesApplyMs = 800;
    private enum Tab { Overview, Files, Peers, Trackers }

    private readonly MainActivity _activity;
    private readonly DownloadsModel _model;
    private readonly JobRow _row;
    private readonly TorrentClient _client;
    private readonly Func<bool> _canAsk;
    private readonly Handler _ui = new(Looper.MainLooper!);
    private readonly TextView _percent, _rate, _progress, _note;
    private readonly LinearProgressIndicator _bar;
    private readonly MaterialButton _main, _applyFiles;
    private readonly ImageButton _stop;
    private readonly LinearLayout _content;
    private readonly TabLayout _tabs;
    private readonly CancellationTokenSource _life = new();
    private Tab _tab = Tab.Overview;
    private bool _closed, _visible = true, _renderQueued, _ticking, _watching, _asking;
    private long _watchedAt;
    private string _runKey = "";
    private TorrentDetailsResult? _details;
    private TorrentTrackersResult? _trackers;
    private TorrentPiecesResult? _pieces;
    private TorrentPeersResult? _peers;
    private TorBoxFileTree? _tree;
    private TorrentFilesResult? _filesLoaded;
    private Java.Lang.IRunnable? _filesPending;
    private bool _settingFiles;
    private readonly List<(string Label, TextView Value, View Row)> _overview = new();

    /// <param name="canAsk">False while the Host can't be asked (a paired Host that isn't connected): nothing is requested then.</param>
    public TorrentDetailsScreen(MainActivity activity, DownloadsModel model, JobRow row, TorrentClient client, Func<bool> canAsk)
    {
        _activity = activity;
        _model = model;
        _row = row;
        _client = client;
        _canAsk = canAsk;
        Root = LayoutInflater.From(activity)!.Inflate(Resource.Layout.torrent_details, null, false)!;
        InsetsListener.Pad(Root, top: true, bottom: true);
        _percent = Root.FindViewById<TextView>(Resource.Id.td_percent)!;
        _rate = Root.FindViewById<TextView>(Resource.Id.td_rate)!;
        _progress = Root.FindViewById<TextView>(Resource.Id.td_progress)!;
        _bar = Root.FindViewById<LinearProgressIndicator>(Resource.Id.td_bar)!;
        _main = Root.FindViewById<MaterialButton>(Resource.Id.td_main)!;
        _stop = Root.FindViewById<ImageButton>(Resource.Id.td_stop)!;
        _content = Root.FindViewById<LinearLayout>(Resource.Id.td_content)!;
        _applyFiles = Root.FindViewById<MaterialButton>(Resource.Id.td_apply_files)!;
        _note = Root.FindViewById<TextView>(Resource.Id.td_note)!;
        _tabs = new TabLayout(activity);
        foreach (var name in new[] { "Overview", "Files", "Peers", "Trackers" }) _tabs.AddTab(_tabs.NewTab().SetText(name)!);
        _tabs.TabSelected += (_, e) => { if (e.Tab is { } tab) SelectTab((Tab)tab.Position); };
        Root.FindViewById<FrameLayout>(Resource.Id.td_tabs_slot)!.AddView(_tabs, new FrameLayout.LayoutParams(-1, -2));
        Root.FindViewById<TextView>(Resource.Id.td_title)!.Text = row.Name;

        Root.FindViewById<View>(Resource.Id.td_back)!.Click += (_, _) => _activity.CloseRowPage(Root);
        Root.FindViewById<View>(Resource.Id.td_more)!.Click += (_, _) => ShowMenu(Root.FindViewById<View>(Resource.Id.td_more)!);
        Root.FindViewById<View>(Resource.Id.td_delete)!.Click += (_, _) => _activity.ConfirmDeleteFromDetails(_row);
        _main.Click += (_, _) => OnMain();
        _stop.Click += (_, _) => OnStop();
        _applyFiles.Click += (_, _) => ApplyFiles(chooseNow: true);

        _row.PropertyChanged += OnRowChanged;
        _model.Rows.CollectionChanged += OnRows;
        _model.SettingsChanged += QueueRender;
        Render();
        Refresh();
    }

    public View Root { get; }

    /// <summary>Stops listening and asking; called when the page has gone, and when the activity is destroyed with it open.</summary>
    public void Dispose()
    {
        _closed = true;
        _life.Cancel();
        _ui.RemoveCallbacksAndMessages(null);
        _row.PropertyChanged -= OnRowChanged;
        _model.Rows.CollectionChanged -= OnRows;
        _model.SettingsChanged -= QueueRender;
        StopWatching();
    }

    /// <summary>The app went to the background (false) or came back (true): nothing is asked while nothing is shown.</summary>
    public void SetVisible(bool visible)
    {
        if (_closed || _visible == visible) return;
        _visible = visible;
        if (visible) { QueueRender(); Refresh(); }
        else StopWatching();
    }

    private bool Running => TorrentText.MayBeRunning(_row.Job);

    private bool MayAsk => !_closed && _visible && _canAsk();

    private void OnRowChanged(object? s, PropertyChangedEventArgs e)
    {
        QueueRender();
        // A change of state (it started, stopped, finished) asks again at once and starts or ends the watch.
        var key = _row.Job.State + "/" + (_row.Job.Torrent?.Seeding == true);
        if (key == _runKey) return;
        _runKey = key;
        Refresh();
    }

    private void OnRows(object? s, NotifyCollectionChangedEventArgs e)
    {
        // The torrent was removed (here or elsewhere): there is nothing left to show.
        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems?.Contains(_row) == true && !_closed) _activity.CloseRowPage(Root);
    }

    private void QueueRender()
    {
        if (_renderQueued || _closed) return;
        _renderQueued = true;
        _ui.Post(() => { _renderQueued = false; Render(); });
    }

    // ---- the card ----

    private void Render()
    {
        if (_closed) return;
        var job = _row.Job;
        Root.FindViewById<TextView>(Resource.Id.td_title)!.Text = _row.Name;
        var seeding = TorrentText.IsSeeding(job);
        var finished = _row.Group == RowGroup.Finished;
        _percent.Text = (finished ? 100 : (int)Math.Floor(_row.Progress * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
        _rate.Text = TorrentText.RateLine(job);
        _progress.Text = TorrentText.ProgressLine(job);
        _bar.Visibility = ViewStates.Visible;
        if (_row.Indeterminate && !finished) { if (!_bar.Indeterminate) _bar.Indeterminate = true; }
        else { if (_bar.Indeterminate) _bar.Indeterminate = false; _bar.SetProgressCompat((int)((finished ? 1 : _row.Progress) * 1000), true); }
        _bar.SetIndicatorColor(seeding ? _activity.GetColor(Resource.Color.state_done) : Motion.Attr(_activity, Resource.Attribute.colorPrimary));

        // The main button is what you most likely want now.
        var awaiting = job.Torrent is { AwaitingFileChoice: true };
        string? text = null; var icon = 0;
        if (awaiting) { text = "Choose files"; icon = Resource.Drawable.ic_file; }
        // While it seeds, ending that is the one thing to do: it takes the main button's place (no gap where Pause was).
        else if (seeding) { text = "Stop seeding"; icon = Resource.Drawable.ic_stop_outline; }
        else if (TorrentText.CanStartSeeding(job)) { text = "Start seeding"; icon = Resource.Drawable.ic_play; }
        else switch (_row.Group)
        {
            case RowGroup.Active: text = "Pause"; icon = Resource.Drawable.ic_pause_thin; break;
            case RowGroup.Failed: text = "Retry"; icon = Resource.Drawable.ic_refresh; break;
            case RowGroup.Finished: break;
            default: text = "Resume"; icon = Resource.Drawable.ic_play; break;
        }

        _main.Visibility = text is null ? ViewStates.Gone : ViewStates.Visible;
        if (text is not null) { _main.Text = text; _main.SetIconResource(icon); }
        // Stop: ends seeding while it seeds; otherwise stops an unfinished torrent (kept in the list), where the Host offers that.
        var canStop = !seeding && _row.Group == RowGroup.Active && _model.Hello?.Capabilities.Contains(JobActionRequest.StopUnfinishedCapability) == true;
        _stop.Visibility = canStop ? ViewStates.Visible : ViewStates.Gone;
        _applyFiles.Visibility = awaiting && _tab == Tab.Files && _tree is not null ? ViewStates.Visible : ViewStates.Gone;
        _applyFiles.Text = _tree is { SelectedCount: > 0 } t ? "Download " + Fmt.Bytes(t.SelectedBytes) : "Download";
        _applyFiles.Enabled = _tree is { SelectedCount: > 0 };
        // Only the Overview follows every progress update; the other tabs are drawn when their data comes or the tab is chosen.
        if (_tab == Tab.Overview) FillOverview();
    }

    private void OnMain()
    {
        var job = _row.Job;
        if (job.Torrent is { AwaitingFileChoice: true }) { _tabs.GetTabAt((int)Tab.Files)?.Select(); return; }
        if (TorrentText.IsSeeding(job)) { Run(() => _client.SeedAsync(_row.Id, false), r => r.Ok, r => r.Error); return; }
        if (TorrentText.CanStartSeeding(job)) { Run(() => _client.SeedAsync(_row.Id, true), r => r.Ok, r => r.Error); return; }
        switch (_row.Group)
        {
            case RowGroup.Active: Fire(_model.PauseAsync(_row.Id)); break;
            case RowGroup.Failed: Fire(_model.RetryAsync(_row.Id)); break;
            case RowGroup.Finished: break;
            default: Fire(_model.ResumeAsync(_row.Id)); break;
        }
    }

    private void OnStop()
    {
        Fire(_model.StopAsync(_row.Id));
    }

    private void Run<T>(Func<Task<T>> work, Func<T, bool> ok, Func<T, string?> error) =>
        TorBoxUi.Run(_activity, work, result =>
        {
            if (_closed) return;
            if (!ok(result)) _activity.ShowMessage(string.IsNullOrWhiteSpace(error(result)) ? "That did not work." : error(result)!.Trim());
        }, words => { if (!_closed) _activity.ShowMessage(words); });

    private void Fire(Task<CommandResult> command) =>
        _ = Task.Run(async () =>
        {
            var r = await command.ConfigureAwait(false);
            if (!r.Ok) _activity.ShowMessage(r.Error ?? "That did not work.");
        });

    private void ShowMenu(View anchor)
    {
        var menu = new PopupMenu(_activity, anchor);
        if (!_row.Job.AddressHidden) menu.Menu!.Add(0, 1, 0, "Copy magnet link");
        if (_row.Group == RowGroup.Finished && _activity.Capabilities.CanShowInFolder) menu.Menu!.Add(0, 2, 1, "Show in folder");
        if (menu.Menu!.Size() == 0) return;
        menu.MenuItemClick += (_, e) =>
        {
            if (e.Item!.ItemId == 1) { Outside.CopyText(_activity, "Magnet link", _row.Job.Url); _activity.ShowMessage("Magnet link copied"); }
            else if (!Outside.ShowDownloadFolder(_activity)) _activity.ShowMessage("No app on this phone can show the folder.");
        };
        menu.Show();
    }

    // ---- tabs ----

    private void SelectTab(Tab tab)
    {
        if (_tab == tab || _closed) return;
        _tab = tab;
        _note.Visibility = ViewStates.Gone;
        Render();
        FillContent();
        Refresh();
    }

    // ---- asking the Host ----

    /// <summary>Asks for what the shown tab needs now, then keeps asking every two seconds while the torrent runs. The watch starts with it.</summary>
    private void Refresh()
    {
        if (_closed || !_visible) return;
        if (Running && MayAsk) KeepWatching(); else StopWatching();
        _ = AskAsync();
        StartTicking();
    }

    private void StartTicking()
    {
        if (_ticking || !(Running && MayAsk)) return;
        _ticking = true;
        _ui.PostDelayed(new Java.Lang.Runnable(() =>
        {
            _ticking = false;
            if (_closed || !(Running && MayAsk)) return;
            KeepWatching();
            _ = AskAsync();
            StartTicking();
        }), RefreshMs);
    }

    private void KeepWatching()
    {
        var now = System.Environment.TickCount64;
        if (_watching && now - _watchedAt < WatchRenewMs) return;
        _watching = true;
        _watchedAt = now;
        _ = Task.Run(async () => { try { await _client.WatchAsync(true).ConfigureAwait(false); } catch (Exception) { /* the progress then comes every five seconds */ } });
    }

    private void StopWatching()
    {
        if (!_watching) return;
        _watching = false;
        _ = Task.Run(async () => { try { await _client.WatchAsync(false).ConfigureAwait(false); } catch (Exception) { /* the lease ends by itself */ } });
    }

    private async Task AskAsync()
    {
        if (_asking || !MayAsk) return;
        _asking = true;
        var id = _row.Id;
        var tab = _tab;
        var running = Running;
        try
        {
            switch (tab)
            {
                case Tab.Overview:
                    var details = _client.DetailsAsync(id, _life.Token);
                    var trackers = running ? _client.TrackersAsync(id, _life.Token) : null;
                    var pieces = running ? _client.PiecesAsync(id, _life.Token) : null;
                    _details = await details.ConfigureAwait(false);
                    if (trackers is not null) _trackers = await trackers.ConfigureAwait(false);
                    if (pieces is not null) _pieces = await pieces.ConfigureAwait(false);
                    break;
                case Tab.Peers: _peers = await _client.PeersAsync(id, _life.Token).ConfigureAwait(false); break;
                case Tab.Trackers: _trackers = await _client.TrackersAsync(id, _life.Token).ConfigureAwait(false); break;
                case Tab.Files when _tree is null && _filesPending is null && !_settingFiles:
                    var files = await _client.FilesAsync(id, _life.Token).ConfigureAwait(false);
                    _activity.RunOnUiThread(() => { if (!_closed) ShowFiles(files); });
                    return;
            }
        }
        catch (System.OperationCanceledException) { return; }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or TimeoutException or System.Text.Json.JsonException)
        {
            // An older Host that doesn't know a request answers an error: that tab says so; the others go on.
            _activity.RunOnUiThread(() => { if (!_closed) { _note.Text = e is InvalidOperationException ? e.Message : TorrentText.OlderHost; _note.Visibility = ViewStates.Visible; } });
        }
        finally { _asking = false; }

        _activity.RunOnUiThread(() => { if (!_closed) FillContent(); });
    }

    // ---- the tab's content ----

    private void FillContent()
    {
        if (_closed) return;
        if (_tab != Tab.Overview) _overview.Clear();
        switch (_tab)
        {
            case Tab.Overview: FillOverview(); break;
            case Tab.Files: FillFiles(); break;
            case Tab.Peers: FillPeers(); break;
            default: FillTrackers(); break;
        }
    }

    private int Dp(int v) => Motion.Dp(_activity, v);

    private TextView Label(string text, int colorAttr, float size = 14)
    {
        var view = new TextView(_activity) { Text = text, TextSize = size };
        view.SetTextColor(new Color(Motion.Attr(_activity, colorAttr)));
        return view;
    }

    private void FillOverview()
    {
        // t4: the folder as Add torrent's Save to writes it ("Download/Torrents/Compressed"), not only its last part.
        var savedTo = SavePathWords.ForField(_details?.DestinationDirectory ?? _row.Job.DestinationDirectory ?? "");
        var lines = TorrentText.Overview(_row.Job, _details, _trackers, _pieces, _model.Settings, savedTo, DateTimeOffset.Now, _activity.Capabilities.IsRemote);
        // Updated in place while the same rows are there, so TalkBack keeps its place while the torrent runs.
        if (_overview.Count == lines.Count && _content.ChildCount == lines.Count && lines.Select(l => l.Label).SequenceEqual(_overview.Select(o => o.Label)))
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (_overview[i].Value.Text == lines[i].Value) continue;
                _overview[i].Value.Text = lines[i].Value;
                _overview[i].Row.ContentDescription = lines[i].Label + ", " + lines[i].Value;
            }

            return;
        }

        _content.RemoveAllViews();
        _overview.Clear();
        foreach (var line in lines)
        {
            var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
            row.SetPadding(0, Dp(14), 0, Dp(14));
            row.AddView(Label(line.Label, Resource.Attribute.colorOnSurfaceVariant), new LinearLayout.LayoutParams(Dp(96), -2));
            var value = Label(line.Value, Resource.Attribute.colorOnSurface);
            // t4: a paired PC's long path stays on one line, cut in the middle like Add torrent's Save to.
            if (line.Label == "Saved to") { value.SetSingleLine(true); value.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle; }
            row.AddView(value, new LinearLayout.LayoutParams(0, -2, 1f));
            row.ContentDescription = line.Label + ", " + line.Value;
            _content.AddView(row);
            _overview.Add((line.Label, value, row));
        }
    }

    private void FillPeers()
    {
        _content.RemoveAllViews();
        if (_peers is not { } peers) { Empty(Running ? "Looking for peers…" : TorrentText.NotRunning); return; }
        if (!peers.Ok) { Empty(string.IsNullOrWhiteSpace(peers.Error) ? TorrentText.OlderHost : peers.Error); return; }
        if (!peers.Running) { Empty(TorrentText.NotRunning); return; }
        if (peers.Peers.Count == 0) { Empty(TorrentText.NoPeers); return; }
        // The address of a peer is not put on screen: a peer is named by its place in the list.
        var number = 1;
        foreach (var peer in peers.Peers.OrderByDescending(p => p.DownloadRate + p.UploadRate).Take(60))
            _content.AddView(TwoLines(TorrentText.PeerTitle(number++, peer), TorrentText.PeerLine(peer)));
    }

    private void FillTrackers()
    {
        _content.RemoveAllViews();
        if (_trackers is not { } trackers) { Empty(Running ? "Asking the trackers…" : TorrentText.NotRunning); return; }
        if (!trackers.Ok) { Empty(string.IsNullOrWhiteSpace(trackers.Error) ? TorrentText.OlderHost : trackers.Error); return; }
        if (!trackers.Running) { Empty(TorrentText.NotRunning); return; }
        if (trackers.Trackers.Count == 0) { Empty(TorrentText.NoTrackers); return; }
        foreach (var tracker in trackers.Trackers) _content.AddView(TwoLines(TorrentText.TrackerShown(tracker.Url), TorrentText.TrackerLine(tracker)));
    }

    private void Empty(string words)
    {
        var view = Label(words, Resource.Attribute.colorOnSurfaceVariant);
        view.SetPadding(0, Dp(14), 0, Dp(14));
        _content.AddView(view);
    }

    private View TwoLines(string title, string line)
    {
        var column = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        column.SetPadding(0, Dp(12), 0, Dp(12));
        var top = Label(title, Resource.Attribute.colorOnSurface, 15);
        top.SetSingleLine(true);
        top.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
        column.AddView(top);
        column.AddView(Label(line, Resource.Attribute.colorOnSurfaceVariant, 13));
        column.ContentDescription = title + ". " + line;
        return column;
    }

    // ---- Files ----

    private void ShowFiles(TorrentFilesResult files)
    {
        _filesLoaded = files;
        _tree = files.Ok && files.Files.Count > 0 ? TorrentFileTrees.Build(files.Files, wantedOnly: true) : null;
        if (!files.Ok) { _note.Text = string.IsNullOrWhiteSpace(files.Error) ? TorrentText.OlderHost : files.Error; _note.Visibility = ViewStates.Visible; }
        Render();
        FillContent();
    }

    private void FillFiles()
    {
        _content.RemoveAllViews();
        if (_tree is null)
        {
            Empty(_filesLoaded is null ? "Loading the files…" : _filesLoaded.Ok ? "The file list is not in yet." : TorrentText.OlderHost);
            return;
        }

        var tree = _tree;
        var job = _row.Job;
        // A finished (or cancelled) torrent's files can't change now; one waiting for a choice is changed with Download, not by each tick.
        var readOnly = job.State is "completed" or "cancelled";
        var summary = Label(tree.Summary, Resource.Attribute.colorOnSurfaceVariant);
        summary.SetPadding(0, Dp(12), 0, Dp(4));
        summary.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        _content.AddView(summary);
        var list = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        _content.AddView(list, new LinearLayout.LayoutParams(-1, -2) { LeftMargin = -Dp(10), RightMargin = -Dp(14) });
        TorBoxTreeView.Render(_activity, list, tree, readOnly, () =>
        {
            summary.Text = tree.Summary;
            _applyFiles.Text = tree.SelectedCount > 0 ? "Download " + Fmt.Bytes(tree.SelectedBytes) : "Download";
            _applyFiles.Enabled = tree.SelectedCount > 0;
            if (job.Torrent is { AwaitingFileChoice: false }) ApplyFiles(chooseNow: false);
        });
    }

    /// <summary>Sends the chosen files to the Host: at once for Download on a torrent waiting for its choice, a moment after the last tick otherwise.</summary>
    private void ApplyFiles(bool chooseNow)
    {
        if (_tree is not { SelectedCount: > 0 } tree)
        {
            if (!chooseNow && _tree is not null) { _note.Text = "Keep at least one file."; _note.Visibility = ViewStates.Visible; }
            return;
        }

        if (_filesPending is not null) { _ui.RemoveCallbacks(_filesPending); _filesPending = null; }
        var indexes = TorrentFileTrees.Indexes(tree);
        void Send()
        {
            _filesPending = null;
            if (_closed) return;
            _settingFiles = true;
            _note.Visibility = ViewStates.Gone;
            TorBoxUi.Run(_activity, () => _client.SetFilesAsync(_row.Id, indexes, _life.Token), result =>
            {
                _settingFiles = false;
                if (_closed) return;
                if (!result.Ok)
                {
                    _note.Text = string.IsNullOrWhiteSpace(result.Error) ? "The Host did not accept that choice." : result.Error.Trim();
                    _note.Visibility = ViewStates.Visible;
                    // The Host's own list is what is true: draw it again.
                    _tree = null; _filesLoaded = null;
                    Refresh();
                    return;
                }

                if (chooseNow) _activity.ShowMessage("Download started");
            }, words => { _settingFiles = false; if (!_closed) { _note.Text = words; _note.Visibility = ViewStates.Visible; } });
        }

        if (chooseNow) Send();
        else { _filesPending = new Java.Lang.Runnable(Send); _ui.PostDelayed(_filesPending, FilesApplyMs); }
    }
}
