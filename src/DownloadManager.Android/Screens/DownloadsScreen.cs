using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Android.Animation;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using AndroidX.RecyclerView.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using DownloadManager.Mobile.Archives;
using DownloadManager.Mobile.TorBox;
using DownloadManager.Mobile.Torrents;
using Google.Android.Material.Chip;
using Google.Android.Material.ProgressIndicator;

namespace Inbound.App.Screens;

/// <summary>The Downloads screen (A1 to A3, and the selection of B5): search, summary card, state chips, the list and the selection bar.</summary>
internal sealed partial class DownloadsScreen
{
    private readonly MainActivity _activity;
    private readonly DownloadsModel _model;
    private readonly View _page;
    private readonly DownloadList _list;
    private readonly RecyclerView _recycler;
    private readonly RowAdapter _adapter;
    private readonly SpeedGraphView _graph;
    private readonly TextView _rate;
    private readonly TextView _details;
    private readonly ImageButton _toggle;
    private readonly EditText _search;
    private readonly View _searchBar, _selectionBar, _searchClear;
    private readonly TextView _selTitle;
    private readonly ImageButton _selResume, _selPause;
    private readonly Dictionary<DownloadFilter, Chip> _chipFor = new();
    private readonly Handler _queueTimingUi = new(Looper.MainLooper!);
    private readonly Java.Lang.Runnable _queueTimingExpiry;
    private readonly Dictionary<int, (string Name, DateTimeOffset Start)> _queueStarts = new();
    private readonly ArchiveStore _archives;
    private readonly TorBoxList _torBox = new();
    private int _torCount;
    private Google.Android.Material.BottomSheet.BottomSheetDialog? _passwordSheet;
    private bool _disposed;
    private double _shownRate;
    private bool _toggleIsPause = true;
    private bool _remoteFiltersExpanded;
    private long _rateShownAt;

    public DownloadsScreen(MainActivity activity, View page, DownloadsModel model, Bundle? selectionDraft)
    {
        _activity = activity;
        _model = model;
        _selectionRestore = selectionDraft;
        _page = page;
        _list = new DownloadList(model, a => activity.RunOnUiThread(a));
        _recycler = page.FindViewById<RecyclerView>(Resource.Id.list)!;
        _rate = page.FindViewById<TextView>(Resource.Id.summary_rate)!;
        _details = page.FindViewById<TextView>(Resource.Id.summary_details)!;
        _toggle = page.FindViewById<ImageButton>(Resource.Id.summary_toggle)!;
        _search = page.FindViewById<EditText>(Resource.Id.search_input)!;
        _searchBar = page.FindViewById<View>(Resource.Id.search_bar)!;
        _selectionBar = page.FindViewById<View>(Resource.Id.selection_bar)!;
        _searchClear = page.FindViewById<View>(Resource.Id.search_clear)!;
        _selTitle = page.FindViewById<TextView>(Resource.Id.sel_title)!;
        _selResume = page.FindViewById<ImageButton>(Resource.Id.sel_resume)!;
        _selPause = page.FindViewById<ImageButton>(Resource.Id.sel_pause)!;
        _graph = new SpeedGraphView(activity);
        var graphSlot = page.FindViewById<FrameLayout>(Resource.Id.summary_graph)!;
        var graphLayout = (ViewGroup.MarginLayoutParams)graphSlot.LayoutParameters!;
        var graphMargin = Motion.Dp(activity, 20);
        graphLayout.LeftMargin = graphMargin;
        graphLayout.RightMargin = graphMargin;
        graphSlot.LayoutParameters = graphLayout;
        graphSlot.AddView(_graph, new FrameLayout.LayoutParams(-1, -1));

        _adapter = new RowAdapter(this);
        _recycler.SetLayoutManager(new LinearLayoutManager(activity));
        _recycler.SetAdapter(_adapter);
        _queueTimingExpiry = new Java.Lang.Runnable(RefreshScheduledRows);
        _model.SettingsChanged += RefreshScheduledRows;
        RefreshScheduledRows();
        // Archive sets (t3): drawn on the row of their first volume, from archive.list and archive.changed.
        _archives = new ArchiveStore(_model.JobOf);
        _archives.Changed += _adapter.RefreshAll;
        _model.ArchiveChanged += _archives.Apply;
        _model.HostHelloChanged += LoadArchives;
        // The Torrents chip shows while the Host has torrents (or one is in the list): the hello can come after this screen is built.
        _model.HostHelloChanged += OnSummary;
        LoadArchives();
        // TorBox jobs still being fetched (t3): rows above the downloads, from remote.list and remote.changed.
        _model.RemoteChanged += OnRemoteChanged;
        _model.HostHelloChanged += LoadTorBox;
        LoadTorBox();
        // Lists: 200 ms fade and move. Rows never reorder: a row only comes in at the top or leaves.
        _recycler.SetItemAnimator(new DefaultItemAnimator { AddDuration = 200, MoveDuration = 200, RemoveDuration = 200, ChangeDuration = 0, SupportsChangeAnimations = false });

        BuildChips(page.FindViewById<ViewGroup>(Resource.Id.chips)!);
        _list.SummaryChanged += OnSummary;
        _list.SelectionChanged += OnSelection;
        _list.SelectedRowsChanged += OnSelectedRowsChanged;
        _model.FirstSnapshotShown += () =>
        {
            _adapter.Live();
            OnSummary();
            _page.Post(RestoreSelection);
        };
        OnSummary();
        OnSelection();

        _toggle.Click += (_, _) => ToggleAll();
        _search.TextChanged += (_, _) =>
        {
            _list.Search = _search.Text ?? "";
            SyncTorBox();
            _searchClear.Visibility = (_search.Text?.Length ?? 0) > 0 ? ViewStates.Visible : ViewStates.Gone;
            UpdateFilterVisibility();
            _activity.UpdateBack();
        };
        _search.FocusChange += (_, _) =>
        {
            if (_activity.Capabilities.IsRemote && _search.HasFocus)
            {
                // Keep filters available while focus moves from search to the chips.
                _remoteFiltersExpanded = true;
                UpdateFilterVisibility();
            }
            _activity.UpdateBack();
        };
        _search.EditorAction += (_, e) =>
        {
            if (e.ActionId != Android.Views.InputMethods.ImeAction.Search) return;
            HideKeyboard();
            _search.ClearFocus();
            e.Handled = true;
        };
        _searchClear.Click += (_, _) => _search.Text = "";
        page.FindViewById<View>(Resource.Id.sel_close)!.Click += (_, _) => _list.ClearSelection();
        _selResume.Click += (_, _) => RunOnSelection(r => _model.ResumeAsync(r.Id), r => r.Group is RowGroup.Paused or RowGroup.Queued or RowGroup.Failed);
        _selPause.Click += (_, _) => RunOnSelection(r => _model.PauseAsync(r.Id), r => r.Action == RowAction.Pause);
        page.FindViewById<View>(Resource.Id.sel_delete)!.Click += (_, _) => ConfirmDelete(_list.SelectedRows);
        page.FindViewById<View>(Resource.Id.sel_more)!.Click += (v, _) => ShowSelectionMenu((View)v!);
    }

    private const int PauseIcon = Resource.Drawable.ic_pause_thin;

    public bool Selecting => _list.Selecting;

    public bool Searching => _remoteFiltersExpanded || (_search.Text?.Length ?? 0) > 0 || _search.HasFocus;

    public void EndSelection()
    {
        _selectionRestore = null;
        _list.ClearSelection();
    }

    public void CloseSearch()
    {
        _search.Text = "";
        _search.ClearFocus();
        _remoteFiltersExpanded = false;
        UpdateFilterVisibility();
        HideKeyboard();
        _activity.UpdateBack();
    }

    /// <summary>The card of a row, if it is on screen, for the container transform back from Details.</summary>
    public View? CardOf(JobRow row)
    {
        var i = _list.Visible.IndexOf(row);
        return i < 0 ? null : (_recycler.FindViewHolderForAdapterPosition(i + _adapter.TorBoxRows) as Holder)?.Card;
    }

    private void LoadArchives()
    {
        if (_disposed) return;
        if (!ArchiveClient.Offered(_model.Hello) || _activity.NewArchiveClient() is not { } client) { _archives.Clear(); return; }
        // A failure leaves the rows as the downloads alone make them; the next hello or archive.changed brings the sets.
        ArchiveUi.Run(_activity, () => client.ListAsync(), list => { if (!_disposed) _archives.Load(list); }, _ => { });
    }

    private void LoadTorBox()
    {
        if (_disposed) return;
        if (!TorBoxClient.Offered(_model.Hello) || _activity.NewTorBoxClient() is not { } client) { _torBox.Load([]); SyncTorBox(); return; }
        // A failure shows no rows rather than another host's; the next hello or remote.changed brings them.
        TorBoxUi.Run(_activity, () => client.ListAsync(), result =>
        {
            if (_disposed) return;
            _torBox.Load(result.Ok ? result.Jobs : []);
            SyncTorBox();
        }, _ => { if (_disposed) return; _torBox.Load([]); SyncTorBox(); });
    }

    private void OnRemoteChanged(RemoteChangedDto change)
    {
        if (!_disposed && _torBox.Apply(change)) SyncTorBox();
    }

    /// <summary>The TorBox rows follow the chip (All and Active only), the search and the selection (hidden while selecting); the chip counts follow the jobs.</summary>
    internal void SyncTorBox()
    {
        if (_disposed) return;
        var host = _activity.Capabilities.IsRemote ? _activity.HostName : null;
        _torCount = TorBoxDownloads.Rows(_torBox.Jobs, host).Count;
        var show = TorBoxDownloads.ShownIn(_list.Filter) && !_list.Selecting;
        _adapter.SetTorBox(show ? TorBoxDownloads.Rows(_torBox.Jobs, host, _list.Search) : []);
        UpdateChipLabels();
    }

    private void UpdateChipLabels()
    {
        foreach (var (f, chip) in _chipFor)
            chip.Text = TorBoxDownloads.ShownIn(f) ? $"{f} {_list.Count(f) + _torCount}" : _list.ChipLabel(f);
    }

    private void OnArchiveAction(JobRow row, ArchiveAction action)
    {
        if (_archives.SetFor(row.Id) is not { } set || _activity.NewArchiveClient() is not { } client) return;
        if (action == ArchiveAction.Enter)
        {
            if (_passwordSheet is { IsShowing: true }) return;
            _passwordSheet = ArchivePasswordSheet.Show(_activity, client, set, updated => { if (!_disposed && updated is not null) _archives.Apply(new ArchiveChangedDto { Set = updated }); });
        }
        else if (action == ArchiveAction.Retry)
            ArchiveUi.Run(_activity, () => client.RetryAsync(set.Id), result =>
            {
                if (_disposed) return;
                if (!result.Ok) _activity.ShowMessage(string.IsNullOrWhiteSpace(result.Error) ? "The extraction was not restarted." : result.Error.Trim());
                else if (result.Set is not null) _archives.Apply(new ArchiveChangedDto { Set = result.Set });
            }, words => { if (!_disposed) _activity.ShowMessage(words); });
    }

    public void Dispose()
    {
        _disposed = true;
        _passwordSheet?.Dismiss();
        _passwordSheet = null;
        _model.ArchiveChanged -= _archives.Apply;
        _model.HostHelloChanged -= LoadArchives;
        _model.HostHelloChanged -= LoadTorBox;
        _model.RemoteChanged -= OnRemoteChanged;
        _model.HostHelloChanged -= OnSummary;
        _archives.Changed -= _adapter.RefreshAll;
        _model.SettingsChanged -= RefreshScheduledRows;
        _queueTimingUi.RemoveCallbacks(_queueTimingExpiry);
        _queueTimingExpiry.Dispose();
        _queueTimingUi.Dispose();
        _list.Dispose();
    }

    internal void RefreshScheduledRows()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = _model.Settings;
        var enabled = settings.Schedules.Where(schedule => schedule.Enabled).Select(schedule => schedule.QueueId).ToHashSet();
        var hadScheduledRows = _queueStarts.Count != 0;
        _queueStarts.Clear();
        DateTimeOffset? firstExpiry = null;
        foreach (var queue in settings.Queues)
        {
            if (QueueSettingsModel.ReportedUpcomingStart(queue, enabled.Contains(queue.Id), now) is not { } start)
                continue;
            _queueStarts[queue.Id] = (queue.Name, start);
            if (firstExpiry is null || start < firstExpiry) firstExpiry = start;
        }
        if (hadScheduledRows || firstExpiry is not null) _adapter.RefreshAll();
        _queueTimingUi.RemoveCallbacks(_queueTimingExpiry);
        if (firstExpiry is { } expiry)
            _queueTimingUi.PostDelayed(_queueTimingExpiry, Math.Max(1L, (long)Math.Ceiling((expiry - now).TotalMilliseconds)));
    }

    private string SubtitleFor(JobRow row)
    {
        if (row.Action == RowAction.RefreshLink)
        {
            bool expired = row.Job.Failure?.HttpStatus == 410 ||
                string.Equals(row.Job.Failure?.Kind, "expiredUrl", StringComparison.OrdinalIgnoreCase);
            return (expired ? "Link expired" : row.Subtitle) + " · tap to refresh";
        }
        if (!row.Job.IsQueued ||
            !string.Equals(row.Job.State, "queued", StringComparison.OrdinalIgnoreCase) ||
            !_queueStarts.TryGetValue(row.Job.QueueId, out var queue))
            return row.Subtitle;
        var now = DateTimeOffset.UtcNow;
        if (queue.Start <= now) return row.Subtitle;
        var pattern = queue.Start.Date == now.ToOffset(queue.Start.Offset).Date ? "HH:mm" : "d MMM, HH:mm";
        var size = row.Job.TotalBytes is > 0 and var bytes ? " · " + Fmt.Bytes(bytes) : "";
        return $"Queued for {queue.Name} · scheduled {queue.Start.ToString(pattern, CultureInfo.CurrentCulture)}" + size;
    }

    private void HideKeyboard()
    {
        var imm = (Android.Views.InputMethods.InputMethodManager)_activity.GetSystemService(Context.InputMethodService)!;
        imm.HideSoftInputFromWindow(_search.WindowToken, 0);
    }

    private void BuildChips(ViewGroup group)
    {
        foreach (var f in Enum.GetValues<DownloadFilter>())
        {
            var chip = (Chip)LayoutInflater.From(_activity)!.Inflate(Resource.Layout.chip_filter, group, false)!;
            var filter = f;
            chip.Checked = f == DownloadFilter.All;
            chip.Click += (_, _) => { _list.Filter = filter; SyncTorBox(); UpdateFilterVisibility(); };
            group.AddView(chip);
            _chipFor[f] = chip;
        }
    }

    private void OnSummary()
    {
        UpdateChipLabels();
        if (_chipFor.TryGetValue(DownloadFilter.Torrents, out var torrents))
            torrents.Visibility = TorrentClient.Offered(_model.Hello) || _list.Count(DownloadFilter.Torrents) > 0 || _list.Filter == DownloadFilter.Torrents ? ViewStates.Visible : ViewStates.Gone;
        var s = _list.Summary;
        _details.Text = s.Details;
        _graph.SetData(s.History);
        // The figure is shown as it is, at most once a second while downloads run: a glide between updates that arrive several
        // times a second made it count up and down (owner, S24, 2026-11-17: "very quickly counting up and down, it's weird").
        var now = System.Environment.TickCount64;
        var wasActive = _shownRate > 0;
        if (s.Active == 0) { _shownRate = 0; _rate.Text = s.Headline; }
        else if (!wasActive || now - _rateShownAt >= 1000) { _shownRate = s.Rate; _rateShownAt = now; _rate.Text = s.Headline; }

        var canResume = s.Active == 0 && _model.Rows.Any(r => r.Group is RowGroup.Paused or RowGroup.Queued);
        _toggle.Visibility = s.Active > 0 || canResume ? ViewStates.Visible : ViewStates.Invisible;
        _toggleIsPause = s.Active > 0;
        _toggle.SetImageResource(_toggleIsPause ? PauseIcon : Resource.Drawable.ic_play);
        _toggle.ContentDescription = _toggleIsPause ? "Pause all downloads" : "Resume all downloads";
    }

    private void OnSelection()
    {
        var sel = _list.Selecting;
        _searchBar.Visibility = sel ? ViewStates.Gone : ViewStates.Visible;
        _selectionBar.Visibility = sel ? ViewStates.Visible : ViewStates.Gone;
        _activity.SetDownloadsSelectionChrome(sel);
        if (!sel) CloseSelectionWindows();
        _page.FindViewById<View>(Resource.Id.summary)!.Visibility = sel ? ViewStates.Gone : ViewStates.Visible;
        UpdateFilterVisibility();
        _refreshMenuItems?.Invoke();
        _selTitle.Text = _list.SelectionTitle;
        _selResume.Visibility = _list.CanResumeSelection ? ViewStates.Visible : ViewStates.Gone;
        _selPause.Visibility = _list.CanPauseSelection ? ViewStates.Visible : ViewStates.Gone;
        SyncTorBox();
        _adapter.RefreshAll();
        _activity.UpdateBack();
    }

    private void UpdateFilterVisibility()
    {
        var show = !_list.Selecting && (!_activity.Capabilities.IsRemote || Searching || _list.Filter != DownloadFilter.All);
        _page.FindViewById<View>(Resource.Id.chips_scroll)!.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
    }

    // A selected row changed state: the bar's Pause and Resume follow (finding 14). The rows redraw themselves.
    private void OnSelectedRowsChanged()
    {
        _refreshMenuItems?.Invoke();
        _selResume.Visibility = _list.CanResumeSelection ? ViewStates.Visible : ViewStates.Gone;
        _selPause.Visibility = _list.CanPauseSelection ? ViewStates.Visible : ViewStates.Gone;
    }

    private void ToggleAll()
    {
        var pause = _toggleIsPause;
        _ = Task.Run(async () => { var r = await _model.PauseAllAsync(pause).ConfigureAwait(false); if (!r.Ok) _activity.ShowMessage(r.Error ?? "That did not work."); });
    }

    private void RunOnSelection(Func<JobRow, Task<CommandResult>> command, Func<JobRow, bool> applies)
    {
        var rows = _list.SelectedRows.Where(applies).ToList();
        if (rows.Count == 0) return;
        _list.ClearSelection();
        _ = Task.Run(async () =>
        {
            foreach (var r in rows)
            {
                var result = await command(r).ConfigureAwait(false);
                if (!result.Ok) { _activity.ShowMessage(result.Error ?? "That did not work."); return; }
            }
        });
    }

    internal void ConfirmDelete(IReadOnlyList<JobRow> rows)
    {
        if (rows.Count == 0) return;
        DeleteFlow.Confirm(_activity, rows, _activity.Capabilities.CanDeleteWithFile, deleteFile =>
        {
            var ids = rows.Select(r => r.Id).ToList();
            _list.ClearSelection();
            _ = Task.Run(async () =>
            {
                var r = await _model.RemoveAsync(ids, deleteFile).ConfigureAwait(false);
                if (!r.Ok) _activity.ShowMessage(r.Error ?? "The download was not removed.");
            });
        });
    }

    private void OnRowAction(JobRow row)
    {
        switch (row.Action)
        {
            case RowAction.Pause: Fire(_model.PauseAsync(row.Id)); break;
            case RowAction.Resume: Fire(_model.ResumeAsync(row.Id)); break;
            case RowAction.Retry: Fire(_model.RetryAsync(row.Id)); break;
            case RowAction.RefreshLink: _activity.ShowRefreshLink(row); break;
            case RowAction.StopSeeding: StopSeeding(row); break;
            case RowAction.ShowInFolder when _activity.Capabilities.CanShowInFolder:
                if (!Outside.ShowDownloadFolder(_activity)) _activity.ShowMessage("No app on this phone can show the folder.");
                break;
        }
    }

    /// <summary>The Stop button of a seeding torrent: it stays finished in the list and stops uploading (torrent.seed Off).</summary>
    private void StopSeeding(JobRow row)
    {
        if (_activity.NewTorrentClient() is not { } client) return;
        TorBoxUi.Run(_activity, () => client.SeedAsync(row.Id, false), result =>
        {
            if (_disposed) return;
            if (!result.Ok) _activity.ShowMessage(string.IsNullOrWhiteSpace(result.Error) ? "Seeding was not stopped." : result.Error.Trim());
        }, words => { if (!_disposed) _activity.ShowMessage(words); });
    }

    private void Fire(Task<CommandResult> command) =>
        _ = Task.Run(async () => { var r = await command.ConfigureAwait(false); if (!r.Ok) _activity.ShowMessage(r.Error ?? "That did not work."); });

    private sealed class RowAdapter : RecyclerView.Adapter
    {
        private static readonly Java.Lang.String Payload = new("p");
        private readonly DownloadsScreen _s;
        private readonly HashSet<JobRow> _dirty = new();
        private readonly Handler _ui = new(Looper.MainLooper!);
        private bool _flushQueued;
        private bool _live;

        public RowAdapter(DownloadsScreen screen)
        {
            _s = screen;
            screen._list.Visible.CollectionChanged += OnRows;
        }

        public bool IsLive => _live;

        private IReadOnlyList<TorBoxDownloadRow> _torRows = [];

        /// <summary>The TorBox rows sit above the downloads; every download position is offset by their count.</summary>
        public int TorBoxRows => _live ? _torRows.Count : 0;

        public void SetTorBox(IReadOnlyList<TorBoxDownloadRow> next)
        {
            var old = _torRows;
            _torRows = next;
            if (!_live) return;
            if (old.Select(r => r.Id).SequenceEqual(next.Select(r => r.Id)))
            {
                if (next.Count > 0) NotifyItemRangeChanged(0, next.Count, Payload);
                return;
            }

            // Rows leave and come in with the usual animation; a changed order falls back to a redraw.
            var keep = next.Select(r => r.Id).ToHashSet();
            var working = old.Select(r => r.Id).ToList();
            for (var i = working.Count - 1; i >= 0; i--)
            {
                if (keep.Contains(working[i])) continue;
                working.RemoveAt(i);
                NotifyItemRemoved(i);
            }

            for (var i = 0; i < next.Count; i++)
            {
                if (i < working.Count && working[i] == next[i].Id) continue;
                if (working.Contains(next[i].Id)) { NotifyDataSetChanged(); return; }
                working.Insert(i, next[i].Id);
                NotifyItemInserted(i);
            }

            NotifyItemRangeChanged(0, next.Count, Payload);
        }

        public void Live() { _live = true; NotifyDataSetChanged(); }

        public void RefreshAll()
        {
            if (_live && ItemCount > 0) NotifyItemRangeChanged(0, ItemCount, Payload);
        }

        public override int ItemCount => TorBoxRows + _s._list.Visible.Count;

        public override int GetItemViewType(int position) => position < TorBoxRows ? 1 : 0;

        private void OnRows(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_live) return; // the snapshot is shown with one NotifyDataSetChanged
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                // A list that is at the top stays at the top when a row comes in above.
                var atTop = _s._recycler.GetLayoutManager() is LinearLayoutManager lm && lm.FindFirstCompletelyVisibleItemPosition() <= 0;
                NotifyItemInserted(e.NewStartingIndex + TorBoxRows);
                if (atTop && e.NewStartingIndex == 0) _s._recycler.ScrollToPosition(0);
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove) NotifyItemRemoved(e.OldStartingIndex + TorBoxRows);
            else NotifyDataSetChanged();
        }

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var view = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.row, parent, false)!;
            return viewType == 1 ? new TorBoxHolder(view, _s) : new Holder(view, this, _s);
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) => Bind(holder, position, false);

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position, IList<Java.Lang.Object> payloads) =>
            Bind(holder, position, payloads.Count > 0);

        private void Bind(RecyclerView.ViewHolder holder, int position, bool animate)
        {
            var torBox = TorBoxRows;
            if (holder is TorBoxHolder t) { if (position < torBox) t.Bind(_torRows[position], animate); }
            else if (position >= torBox) ((Holder)holder).Bind(_s._list.Visible[position - torBox], animate);
        }

        public override void OnViewRecycled(Java.Lang.Object holder)
        {
            (holder as Holder)?.Unbind();
            base.OnViewRecycled(holder);
        }

        public void Watch(JobRow row, bool on)
        {
            if (on) row.PropertyChanged += OnRowChanged; else row.PropertyChanged -= OnRowChanged;
        }

        private void OnRowChanged(object? s, PropertyChangedEventArgs e)
        {
            _dirty.Add((JobRow)s!);
            if (_flushQueued) return;
            _flushQueued = true;
            _ui.Post(() =>
            {
                _flushQueued = false;
                foreach (var r in _dirty) { var i = _s._list.Visible.IndexOf(r); if (i >= 0) NotifyItemChanged(i + TorBoxRows, Payload); }
                _dirty.Clear();
            });
        }
    }

    /// <summary>A TorBox job being fetched (t3): violet bolt, "On TorBox · 37% · then to this phone", a violet bar. Tap opens My TorBox; no round button, the Host cannot pause a remote job.</summary>
    private sealed class TorBoxHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _name, _subtitle;
        private readonly LinearProgressIndicator _bar;
        private readonly ImageView _icon;

        public TorBoxHolder(View v, DownloadsScreen screen) : base(v)
        {
            _name = v.FindViewById<TextView>(Resource.Id.name)!;
            _subtitle = v.FindViewById<TextView>(Resource.Id.subtitle)!;
            _bar = v.FindViewById<LinearProgressIndicator>(Resource.Id.bar)!;
            _icon = v.FindViewById<ImageView>(Resource.Id.icon)!;
            v.FindViewById<View>(Resource.Id.action)!.Visibility = ViewStates.Invisible;
            v.FindViewById<View>(Resource.Id.archive_action)!.Visibility = ViewStates.Gone;
            v.Click += (_, _) => screen._activity.OpenTorBox();
        }

        public void Bind(TorBoxDownloadRow row, bool animate)
        {
            var ctx = ItemView.Context!;
            _name.Text = row.Name;
            var styled = new Android.Text.SpannableString(row.Subtitle);
            styled.SetSpan(new Android.Text.Style.ForegroundColorSpan(new Android.Graphics.Color(Motion.Attr(ctx, Resource.Attribute.colorPrimary))), 0, row.Lead.Length, Android.Text.SpanTypes.ExclusiveExclusive);
            _subtitle.SetText(styled, Android.Widget.TextView.BufferType.Spannable);
            _subtitle.SetTextColor(new Android.Graphics.Color(Motion.Attr(ctx, Resource.Attribute.colorOnSurfaceVariant)));
            ((Google.Android.Material.Card.MaterialCardView)ItemView).SetCardBackgroundColor(Motion.Attr(ctx, Resource.Attribute.colorSurfaceContainer));
            _icon.SetImageResource(Resource.Drawable.ic_flow_bolt);
            _icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(ctx.GetColor(Resource.Color.state_media)));
            ItemView.ContentDescription = row.Name + ". " + row.Subtitle;
            _bar.Visibility = ViewStates.Visible;
            _bar.SetIndicatorColor(ctx.GetColor(Resource.Color.state_media));
            if (_bar.Indeterminate) _bar.Indeterminate = false;
            _bar.SetProgressCompat((int)(row.Progress * 1000), animate);
        }
    }

    private sealed class Holder : RecyclerView.ViewHolder
    {
        private readonly RowAdapter _owner;
        private readonly DownloadsScreen _s;
        private readonly TextView _name, _subtitle;
        private readonly LinearProgressIndicator _bar;
        private readonly ImageView _icon;
        private readonly View _tile;
        private readonly ImageButton _action;
        private readonly Google.Android.Material.Button.MaterialButton _archiveAction;
        private ArchiveAction _archiveActionKind;
        private JobRow? _row;

        public Holder(View v, RowAdapter owner, DownloadsScreen screen) : base(v)
        {
            _owner = owner;
            _s = screen;
            Card = v;
            _name = v.FindViewById<TextView>(Resource.Id.name)!;
            _subtitle = v.FindViewById<TextView>(Resource.Id.subtitle)!;
            _bar = v.FindViewById<LinearProgressIndicator>(Resource.Id.bar)!;
            _icon = v.FindViewById<ImageView>(Resource.Id.icon)!;
            _tile = v.FindViewById<View>(Resource.Id.tile)!;
            _action = v.FindViewById<ImageButton>(Resource.Id.action)!;
            v.Click += (_, _) =>
            {
                if (_row is not { } r) return;
                if (_s._list.Selecting) _s._list.Toggle(r);
                else if (r.Job.Torrent is not null) _s._activity.OpenTorrentDetails(r, Card);
                else _s._activity.OpenDetails(r, Card);
            };
            v.LongClick += (_, e) =>
            {
                if (_row is { } r && !_s._list.Selecting) { _s._list.StartSelecting(r); e.Handled = true; }
            };
            _action.Click += (_, _) => { if (_row is { } r) _s.OnRowAction(r); };
            _archiveAction = v.FindViewById<Google.Android.Material.Button.MaterialButton>(Resource.Id.archive_action)!;
            _archiveAction.Click += (_, _) => { if (_row is { } r && _archiveActionKind != ArchiveAction.None) _s.OnArchiveAction(r, _archiveActionKind); };
        }

        public View Card { get; }

        /// <summary>A recycled holder stops watching its row, so a row gone from the list keeps no listener.</summary>
        public void Unbind()
        {
            if (_row is not null) _owner.Watch(_row, false);
            _row = null;
        }

        public void Bind(JobRow row, bool animate)
        {
            if (!ReferenceEquals(_row, row))
            {
                if (_row is not null) _owner.Watch(_row, false);
                _row = row;
                _owner.Watch(row, true);
                animate = false;
                // The card that a page is open from stays hidden until the page has closed.
                Card.Visibility = ReferenceEquals(Card, _s._activity.HiddenCard) ? ViewStates.Invisible : ViewStates.Visible;
            }

            var ctx = ItemView.Context!;
            _name.Text = row.Name;
            // An archive set on this row (its first volume) says what extraction is doing, with its lead word coloured (t3).
            var info = _s._archives.InfoFor(row.Id);
            var torrent = info is null ? TorrentRowInfo.For(row.Job, _s._model.Settings) : null;
            var subtitle = info?.Subtitle ?? torrent?.Subtitle ?? _s.SubtitleFor(row);
            var variant = Motion.Attr(ctx, Resource.Attribute.colorOnSurfaceVariant);
            if (info is not null)
            {
                var tone = info.Tone switch
                {
                    ArchiveTone.Amber => ctx.GetColor(Resource.Color.state_waiting),
                    ArchiveTone.Failed => ctx.GetColor(Resource.Color.state_failed),
                    ArchiveTone.Done => ctx.GetColor(Resource.Color.state_done),
                    _ => variant,
                };
                var styled = new Android.Text.SpannableString(subtitle);
                styled.SetSpan(new Android.Text.Style.ForegroundColorSpan(new Android.Graphics.Color(tone)), 0, info.Lead.Length, Android.Text.SpanTypes.ExclusiveExclusive);
                _subtitle.SetText(styled, Android.Widget.TextView.BufferType.Spannable);
                _subtitle.SetTextColor(new Android.Graphics.Color(variant));
            }
            else if (torrent is { Tone: not TorrentTone.Neutral })
            {
                // "Seeding" in green, "Choose files to download" in amber; the rest of the line stays plain (t3).
                var tone = ctx.GetColor(torrent.Tone == TorrentTone.Done ? Resource.Color.state_done : Resource.Color.state_waiting);
                var styled = new Android.Text.SpannableString(subtitle);
                styled.SetSpan(new Android.Text.Style.ForegroundColorSpan(new Android.Graphics.Color(tone)), 0, torrent.Lead.Length, Android.Text.SpanTypes.ExclusiveExclusive);
                _subtitle.SetText(styled, Android.Widget.TextView.BufferType.Spannable);
                _subtitle.SetTextColor(new Android.Graphics.Color(variant));
            }
            else
            {
                _subtitle.Text = subtitle;
                var color = row.Kind switch
                {
                    StatusKind.Done => ctx.GetColor(Resource.Color.state_done),
                    StatusKind.Failed => ctx.GetColor(Resource.Color.state_failed),
                    StatusKind.Waiting => ctx.GetColor(Resource.Color.state_waiting),
                    _ => variant,
                };
                _subtitle.SetTextColor(new Android.Graphics.Color(color));
            }

            var selected = _s._list.IsSelected(row);
            var card = (Google.Android.Material.Card.MaterialCardView)Card;
            card.SetCardBackgroundColor(Motion.Attr(ctx, selected ? Resource.Attribute.colorSecondaryContainer : Resource.Attribute.colorSurfaceContainer));
            _tile.SetBackgroundResource(selected ? Resource.Drawable.bg_circle_primary : Resource.Drawable.bg_tile);
            if (selected)
            {
                _icon.SetImageResource(Resource.Drawable.ic_check);
                _icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(Motion.Attr(ctx, Resource.Attribute.colorOnPrimary)));
            }
            else if (info is not null)
            {
                _icon.SetImageResource(info.Icon == ArchiveIcon.Lock ? Resource.Drawable.ic_browser_lock : Resource.Drawable.ic_archive);
                _icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(ctx.GetColor(Resource.Color.state_waiting)));
            }
            else if (row.Job.Torrent is not null)
            {
                _icon.SetImageResource(Resource.Drawable.ic_torrent);
                var tint = torrent?.Tone switch
                {
                    TorrentTone.Done => ctx.GetColor(Resource.Color.state_done),
                    TorrentTone.Amber => ctx.GetColor(Resource.Color.state_waiting),
                    _ => Motion.Attr(ctx, row.Group == RowGroup.Active ? Resource.Attribute.colorPrimary : Resource.Attribute.colorOnSurfaceVariant),
                };
                _icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(tint));
            }
            else
            {
                var fileIcon = FileIcons.For(FileKinds.Of(row.Name));
                _icon.SetImageResource(fileIcon.Drawable);
                _icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(FileIcons.Color(ctx, fileIcon)));
            }

            Card.Selected = selected;
            Card.ContentDescription = row.Name + ". " + subtitle;
            BindAction(row, ctx, info);

            if (info is not null)
            {
                _bar.Visibility = info.ShowBar ? ViewStates.Visible : ViewStates.Gone;
                if (!info.ShowBar) return;
                _bar.SetIndicatorColor(ctx.GetColor(Resource.Color.state_waiting));
                if (info.Progress is not { } done) { if (!_bar.Indeterminate) _bar.Indeterminate = true; return; }
                if (_bar.Indeterminate) _bar.Indeterminate = false;
                _bar.SetProgressCompat((int)(done * 1000), animate);
                return;
            }

            _bar.Visibility = row.ShowBar ? ViewStates.Visible : ViewStates.Gone;
            if (!row.ShowBar) return;
            _bar.SetIndicatorColor(row.Kind == StatusKind.Failed ? ctx.GetColor(Resource.Color.state_failed)
                : row.Kind == StatusKind.Done ? ctx.GetColor(Resource.Color.state_done)
                : Motion.Attr(ctx, row.Group == RowGroup.Paused ? Resource.Attribute.colorOnSurfaceVariant : Resource.Attribute.colorPrimary));
            if (row.Indeterminate) { if (!_bar.Indeterminate) _bar.Indeterminate = true; return; }
            if (_bar.Indeterminate) _bar.Indeterminate = false;
            _bar.SetProgressCompat((int)(row.Progress * 1000), animate); // glides to the new value; no jump between updates
        }

        private void BindAction(JobRow row, Context ctx, ArchiveRowInfo? info)
        {
            // Enter (password) and Retry (extraction) are small tonal buttons with words, as in the mockup; the download's own button waits.
            _archiveActionKind = info?.Action ?? ArchiveAction.None;
            if (_archiveActionKind != ArchiveAction.None)
            {
                _action.Visibility = ViewStates.Gone;
                _archiveAction.Visibility = _s.Selecting ? ViewStates.Gone : ViewStates.Visible;
                _archiveAction.Text = _archiveActionKind == ArchiveAction.Enter ? "Enter" : "Retry";
                _archiveAction.ContentDescription = (_archiveActionKind == ArchiveAction.Enter ? "Enter the password for " : "Retry extracting ") + row.Name;
                return;
            }

            _archiveAction.Visibility = ViewStates.Gone;
            var (icon, text) = row.Action switch
            {
                RowAction.Pause => (PauseIcon, "Pause"),
                RowAction.Resume => (Resource.Drawable.ic_play_outline, "Resume"),
                RowAction.Retry => (Resource.Drawable.ic_refresh, "Retry"),
                RowAction.RefreshLink => (Resource.Drawable.ic_refresh, "Refresh link for"),
                RowAction.ShowInFolder when _s._activity.Capabilities.CanShowInFolder => (Resource.Drawable.ic_flow_folder, "Show in folder"),
                RowAction.StopSeeding => (Resource.Drawable.ic_stop_outline, "Stop seeding"),
                _ => (0, ""),
            };
            _action.Visibility = _s.Selecting ? ViewStates.Gone : icon == 0 ? ViewStates.Invisible : ViewStates.Visible;
            if (icon == 0) return;
            _action.SetImageResource(icon);
            _action.ContentDescription = text + " " + row.Name;
            var neutral = row.Action is RowAction.Resume or RowAction.ShowInFolder;
            var background = neutral ? Resource.Attribute.colorSurfaceContainerHighest : Resource.Attribute.colorSecondaryContainer;
            var foreground = neutral ? Resource.Attribute.colorOnSurfaceVariant : Resource.Attribute.colorOnSecondaryContainer;
            _action.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(Motion.Attr(ctx, background)));
            _action.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(Motion.Attr(ctx, foreground)));
        }
    }
}
