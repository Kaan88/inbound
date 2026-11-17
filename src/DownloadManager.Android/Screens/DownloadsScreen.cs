using System.Collections.Specialized;
using System.ComponentModel;
using Android.Animation;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using AndroidX.RecyclerView.Widget;
using DownloadManager.Mobile;
using Google.Android.Material.Chip;
using Google.Android.Material.ProgressIndicator;

namespace Inbound.App.Screens;

/// <summary>The Downloads screen (A1 to A3, and the selection of B5): search, summary card, state chips, the list and the selection bar.</summary>
internal sealed class DownloadsScreen
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
    private double _shownRate;
    private bool _toggleIsPause = true;
    private ValueAnimator? _rateAnimation;

    public DownloadsScreen(MainActivity activity, View page, DownloadsModel model)
    {
        _activity = activity;
        _model = model;
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
        page.FindViewById<FrameLayout>(Resource.Id.summary_graph)!.AddView(_graph, new FrameLayout.LayoutParams(-1, -1));

        _adapter = new RowAdapter(this);
        _recycler.SetLayoutManager(new LinearLayoutManager(activity));
        _recycler.SetAdapter(_adapter);
        // Lists: 200 ms fade and move. Rows never reorder: a row only comes in at the top or leaves.
        _recycler.SetItemAnimator(new DefaultItemAnimator { AddDuration = 200, MoveDuration = 200, RemoveDuration = 200, ChangeDuration = 0, SupportsChangeAnimations = false });

        BuildChips(page.FindViewById<ViewGroup>(Resource.Id.chips)!);
        _list.SummaryChanged += OnSummary;
        _list.SelectionChanged += OnSelection;
        _model.FirstSnapshotShown += () => { _adapter.Live(); OnSummary(); };
        OnSummary();
        OnSelection();

        _toggle.Click += (_, _) => ToggleAll();
        _search.TextChanged += (_, _) =>
        {
            _list.Search = _search.Text ?? "";
            _searchClear.Visibility = (_search.Text?.Length ?? 0) > 0 ? ViewStates.Visible : ViewStates.Gone;
            _activity.UpdateBack();
        };
        _search.FocusChange += (_, _) => _activity.UpdateBack();
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

    public bool Selecting => _list.Selecting;

    public bool Searching => (_search.Text?.Length ?? 0) > 0 || _search.HasFocus;

    public void EndSelection() => _list.ClearSelection();

    public void CloseSearch()
    {
        _search.Text = "";
        _search.ClearFocus();
        HideKeyboard();
        _activity.UpdateBack();
    }

    /// <summary>The card of a row, if it is on screen, for the container transform back from Details.</summary>
    public View? CardOf(JobRow row)
    {
        var i = _list.Visible.IndexOf(row);
        return i < 0 ? null : (_recycler.FindViewHolderForAdapterPosition(i) as Holder)?.Card;
    }

    public void Dispose() => _list.Dispose();

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
            chip.Click += (_, _) => _list.Filter = filter;
            group.AddView(chip);
            _chipFor[f] = chip;
        }
    }

    private void OnSummary()
    {
        foreach (var (f, chip) in _chipFor) chip.Text = _list.ChipLabel(f);
        var s = _list.Summary;
        _details.Text = s.Details;
        _graph.SetData(s.History);
        // The figure glides to the new rate instead of jumping (progress keeps moving with animations removed: the animator then ends at once).
        _rateAnimation?.Cancel();
        if (s.Active == 0) { _shownRate = 0; _rate.Text = s.Headline; }
        else if (_shownRate <= 0) { _shownRate = s.Rate; _rate.Text = s.Headline; }
        else
        {
            var a = ValueAnimator.OfFloat((float)_shownRate, (float)s.Rate)!;
            a.SetDuration(250);
            a.Update += (_, e) => { _shownRate = (float)e.Animation.AnimatedValue!; _rate.Text = Fmt.Rate(_shownRate); };
            _rateAnimation = a;
            a.Start();
        }

        var canResume = s.Active == 0 && _model.Rows.Any(r => r.Group is RowGroup.Paused or RowGroup.Queued);
        _toggle.Visibility = s.Active > 0 || canResume ? ViewStates.Visible : ViewStates.Invisible;
        _toggleIsPause = s.Active > 0;
        _toggle.SetImageResource(_toggleIsPause ? Resource.Drawable.ic_pause : Resource.Drawable.ic_play);
        _toggle.ContentDescription = _toggleIsPause ? "Pause all downloads" : "Resume all downloads";
    }

    private void OnSelection()
    {
        var sel = _list.Selecting;
        _searchBar.Visibility = sel ? ViewStates.Gone : ViewStates.Visible;
        _selectionBar.Visibility = sel ? ViewStates.Visible : ViewStates.Gone;
        _selTitle.Text = _list.SelectionTitle;
        _selResume.Visibility = _list.CanResumeSelection ? ViewStates.Visible : ViewStates.Gone;
        _selPause.Visibility = _list.CanPauseSelection ? ViewStates.Visible : ViewStates.Gone;
        _adapter.RefreshAll();
        _activity.UpdateBack();
    }

    private void ToggleAll()
    {
        var pause = _toggleIsPause;
        _ = Task.Run(async () => { var r = await _model.PauseAllAsync(pause).ConfigureAwait(false); if (!r.Ok) _activity.ShowMessage(r.Error ?? "That did not work."); });
    }

    private void RunOnSelection(Func<JobRow, Task<CommandResult>> command, Func<JobRow, bool> applies)
    {
        var rows = _list.SelectedRows.Where(applies).ToList();
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
        DeleteFlow.Confirm(_activity, rows, deleteFile =>
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

    private void ShowSelectionMenu(View anchor)
    {
        var menu = new PopupMenu(_activity, anchor);
        var items = menu.Menu!;
        items.Add(0, 1, 0, "Select all");
        items.Add(0, 2, 1, "Download again");
        items.Add(0, 3, 2, "Copy address");
        menu.MenuItemClick += (_, e) =>
        {
            switch (e.Item!.ItemId)
            {
                case 1: _list.SelectAll(); break;
                case 2: RunOnSelection(r => _model.RedownloadAsync(r.Id), r => r.Group is RowGroup.Finished or RowGroup.Failed); break;
                case 3:
                    var text = string.Join("\n", _list.SelectedRows.Where(r => !r.Job.AddressHidden).Select(r => r.Job.Url));
                    if (text.Length > 0) Outside.CopyText(_activity, "Address", text); else _activity.ShowMessage("These downloads keep their address private.");
                    break;
            }
        };
        menu.Show();
    }

    private void OnRowAction(JobRow row)
    {
        switch (row.Action)
        {
            case RowAction.Pause: Fire(_model.PauseAsync(row.Id)); break;
            case RowAction.Resume: Fire(_model.ResumeAsync(row.Id)); break;
            case RowAction.Retry: Fire(_model.RetryAsync(row.Id)); break;
            case RowAction.ShowInFolder:
                if (!Outside.ShowDownloadFolder(_activity)) _activity.ShowMessage("No app on this phone can show the folder.");
                break;
        }
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

        public void Live() { _live = true; NotifyDataSetChanged(); }

        public void RefreshAll()
        {
            if (_live && ItemCount > 0) NotifyItemRangeChanged(0, ItemCount, Payload);
        }

        public override int ItemCount => _s._list.Visible.Count;

        private void OnRows(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_live) return; // the snapshot is shown with one NotifyDataSetChanged
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                // A list that is at the top stays at the top when a row comes in above.
                var atTop = _s._recycler.GetLayoutManager() is LinearLayoutManager lm && lm.FindFirstCompletelyVisibleItemPosition() <= 0;
                NotifyItemInserted(e.NewStartingIndex);
                if (atTop && e.NewStartingIndex == 0) _s._recycler.ScrollToPosition(0);
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove) NotifyItemRemoved(e.OldStartingIndex);
            else NotifyDataSetChanged();
        }

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType) =>
            new Holder(LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.row, parent, false)!, this, _s);

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) => ((Holder)holder).Bind(_s._list.Visible[position], false);

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position, IList<Java.Lang.Object> payloads) =>
            ((Holder)holder).Bind(_s._list.Visible[position], payloads.Count > 0);

        public override void OnViewRecycled(Java.Lang.Object holder)
        {
            ((Holder)holder).Unbind();
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
                foreach (var r in _dirty) { var i = _s._list.Visible.IndexOf(r); if (i >= 0) NotifyItemChanged(i, Payload); }
                _dirty.Clear();
            });
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
                else _s._activity.OpenDetails(r, Card);
            };
            v.LongClick += (_, e) =>
            {
                if (_row is { } r && !_s._list.Selecting) { _s._list.StartSelecting(r); e.Handled = true; }
            };
            _action.Click += (_, _) => { if (_row is { } r) _s.OnRowAction(r); };
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
                Card.Visibility = ReferenceEquals(Card, Navigator.HiddenCard) ? ViewStates.Invisible : ViewStates.Visible;
            }

            var ctx = ItemView.Context!;
            _name.Text = row.Name;
            _subtitle.Text = row.Subtitle;
            var color = row.Kind switch
            {
                StatusKind.Done => ctx.GetColor(Resource.Color.state_done),
                StatusKind.Failed => ctx.GetColor(Resource.Color.state_failed),
                StatusKind.Waiting => ctx.GetColor(Resource.Color.state_waiting),
                _ => Motion.Attr(ctx, Resource.Attribute.colorOnSurfaceVariant),
            };
            _subtitle.SetTextColor(new Android.Graphics.Color(color));

            var selected = _s._list.IsSelected(row);
            var card = (Google.Android.Material.Card.MaterialCardView)Card;
            card.SetCardBackgroundColor(Motion.Attr(ctx, selected ? Resource.Attribute.colorSecondaryContainer : Resource.Attribute.colorSurfaceContainer));
            if (selected)
            {
                _icon.SetImageResource(Resource.Drawable.ic_check);
                _icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(Motion.Attr(ctx, Resource.Attribute.colorPrimary)));
            }
            else
            {
                var info = FileIcons.For(FileKinds.Of(row.Name));
                _icon.SetImageResource(info.Drawable);
                _icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(FileIcons.Color(ctx, info)));
            }

            ViewCompat.SetStateDescription(Card, selected ? "Selected" : null);
            Card.ContentDescription = row.Name + ". " + row.Subtitle;
            BindAction(row, ctx);

            _bar.Visibility = row.ShowBar ? ViewStates.Visible : ViewStates.Gone;
            if (!row.ShowBar) return;
            // The bar follows the state: blue while it transfers, grey while it waits.
            _bar.SetIndicatorColor(Motion.Attr(ctx, row.Group == RowGroup.Paused ? Resource.Attribute.colorOnSurfaceVariant : Resource.Attribute.colorPrimary));
            if (row.Indeterminate) { if (!_bar.Indeterminate) _bar.Indeterminate = true; return; }
            if (_bar.Indeterminate) _bar.Indeterminate = false;
            _bar.SetProgressCompat((int)(row.Progress * 1000), animate); // glides to the new value; no jump between updates
        }

        private void BindAction(JobRow row, Context ctx)
        {
            var (icon, text) = row.Action switch
            {
                RowAction.Pause => (Resource.Drawable.ic_pause, "Pause"),
                RowAction.Resume => (Resource.Drawable.ic_play, "Resume"),
                RowAction.Retry => (Resource.Drawable.ic_refresh, "Retry"),
                RowAction.ShowInFolder => (Resource.Drawable.ic_folder, "Show in folder"),
                _ => (0, ""),
            };
            _action.Visibility = icon == 0 ? ViewStates.Invisible : ViewStates.Visible;
            if (icon == 0) return;
            _action.SetImageResource(icon);
            _action.ContentDescription = text + " " + row.Name;
            _action.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(Motion.Attr(ctx, _s._list.IsSelected(row) ? Resource.Attribute.colorSurfaceContainerHighest : Resource.Attribute.colorSecondaryContainer)));
            _action.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(Motion.Attr(ctx, Resource.Attribute.colorOnSecondaryContainer)));
        }
    }
}
