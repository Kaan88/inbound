using System.Collections.Specialized;
using System.ComponentModel;
using Android.App;
using AndroidX.AppCompat.App;
using AndroidX.Transitions;
using Google.Android.Material.Transition;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Card;
using Google.Android.Material.Color;
using Google.Android.Material.ProgressIndicator;
using Java.Lang;

namespace AndroidTrial.Native;

[Activity(Name = "app.inbound.tk.nativeui.MainActivity", Label = "Inbound (Native)", MainLauncher = true, Exported = true,
    LaunchMode = Android.Content.PM.LaunchMode.SingleTop, WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class MainActivity : AppCompatActivity
{
    private DownloadsModel? _model;
    private RowAdapter? _adapter;
    private ViewGroup? _root;
    private View? _details, _openCard;
    private JobRow? _detailsRow;
    private SpeedGraphView? _graph;
    private bool _detailsOpen;
    private AndroidX.Activity.OnBackPressedCallback? _back;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        var options = ScreenOptions.From(Intent);
        // Dynamic colour (Android 12+): the wallpaper's roles replace the app's own scheme. Off by default.
        if (options.Dynamic) DynamicColors.ApplyToActivityIfAvailable(this);
        base.OnCreate(savedInstanceState);
        WindowCompat.SetDecorFitsSystemWindows(Window!, false); // edge to edge
        SetContentView(Resource.Layout.main);

        var appbar = FindViewById<View>(Resource.Id.appbar)!;
        var nav = FindViewById<BottomNavigationView>(Resource.Id.nav)!;
        var fab = FindViewById<View>(Resource.Id.fab)!;
        var list = FindViewById<RecyclerView>(Resource.Id.list)!;
        _root = FindViewById<ViewGroup>(Resource.Id.root)!;
        _details = FindViewById<View>(Resource.Id.details)!;
        _graph = FindViewById<SpeedGraphView>(Resource.Id.d_graph)!;
        var baseFabMargin = ((ViewGroup.MarginLayoutParams)fab.LayoutParameters!).BottomMargin;
        ViewCompat.SetOnApplyWindowInsetsListener(FindViewById<View>(Resource.Id.root)!, new InsetsListener((v, insets) =>
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            appbar.SetPadding(bars.Left, bars.Top, bars.Right, 0);
            nav.SetPadding(bars.Left, 0, bars.Right, bars.Bottom);
            _details!.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            ((ViewGroup.MarginLayoutParams)fab.LayoutParameters!).BottomMargin = baseFabMargin + bars.Bottom;
            return insets;
        }));
        nav.SelectedItemId = Resource.Id.nav_downloads;

        _model = new DownloadsModel(a => RunOnUiThread(a));
        _adapter = new RowAdapter(_model, list, OpenDetails);
        list.SetLayoutManager(new LinearLayoutManager(this));
        list.SetAdapter(_adapter);
        var animator = new DefaultItemAnimator { AddDuration = 200, MoveDuration = 200, RemoveDuration = 200, ChangeDuration = 0, SupportsChangeAnimations = false };
        list.SetItemAnimator(animator);
        FindViewById<Google.Android.Material.AppBar.MaterialToolbar>(Resource.Id.details_toolbar)!.NavigationClick += (_, _) => CloseDetails();
        _back = new BackCallback(CloseDetails);
        OnBackPressedDispatcher.AddCallback(this, _back);
        fab.Click += (_, _) => ShowAddSheet();
        TrialSupport.Begin(this, _model, options);
    }

    private void ShowAddSheet()
    {
        Android.Util.Log.Info(TrialSupport.Tag, "fab: show add sheet");
        if (SupportFragmentManager.FindFragmentByTag("add") is not null) return;
        AddSheet.OnAdd = url => _ = Task.Run(async () =>
        {
            try { await _model!.AddUrlAsync(url, TrialSupport.Destination(this), CancellationToken.None).ConfigureAwait(false); }
            catch (System.Exception ex) { Android.Util.Log.Error(TrialSupport.Tag, "add: " + ex); }
        });
        new AddSheet().Show(SupportFragmentManager, "add");
    }

    private MaterialContainerTransform NewTransform(View start, View end)
    {
        var surface = new Android.Graphics.Color(MaterialColors.GetColor(_root!, Resource.Attribute.colorSurface));
        var t = new MaterialContainerTransform();
        t.SetDuration(450);
        t.StartView = start;
        t.EndView = end;
        t.AddTarget(end);
        t.SetAllContainerColors(surface);
        t.ScrimColor = Android.Graphics.Color.Transparent;
        return t;
    }

    private void OpenDetails(JobRow row, View card)
    {
        if (_detailsOpen) return;
        _detailsOpen = true;
        _back!.Enabled = true;
        _detailsRow = row;
        _openCard = card;
        row.PropertyChanged += OnDetailsRowChanged;
        BindDetails();
        TransitionManager.BeginDelayedTransition(_root!, NewTransform(card, _details!));
        card.Visibility = ViewStates.Invisible;
        _details!.Visibility = ViewStates.Visible;
    }

    private void CloseDetails()
    {
        if (!_detailsOpen) return;
        _detailsOpen = false;
        _back!.Enabled = false;
        if (_detailsRow is not null) _detailsRow.PropertyChanged -= OnDetailsRowChanged;
        var card = _openCard!;
        if (card.IsAttachedToWindow) // the row may have been recycled or removed meanwhile: then the screen only fades out
        {
            TransitionManager.BeginDelayedTransition(_root!, NewTransform(_details!, card));
            card.Visibility = ViewStates.Visible;
        }
        else TransitionManager.BeginDelayedTransition(_root!, new Fade());
        _details!.Visibility = ViewStates.Gone;
        _detailsRow = null;
    }

    private sealed class BackCallback(Action close) : AndroidX.Activity.OnBackPressedCallback(false)
    {
        public override void HandleOnBackPressed() => close();
    }

    private void OnDetailsRowChanged(object? s, PropertyChangedEventArgs e) => BindDetails();

    private void BindDetails()
    {
        var row = _detailsRow;
        if (row is null) return;
        _details!.FindViewById<TextView>(Resource.Id.d_name)!.Text = row.Name;
        _details.FindViewById<TextView>(Resource.Id.d_state)!.Text = row.Subtitle;
        _details.FindViewById<TextView>(Resource.Id.d_percent)!.Text = $"{(int)(row.Progress * 100)}%";
        var rate = row.RateHistory.Count > 0 && row.Kind == StatusKind.Active ? row.RateHistory[^1] : 0;
        _details.FindViewById<TextView>(Resource.Id.d_rate)!.Text = rate > 0 ? Fmt.Rate(rate) : "";
        var bar = _details.FindViewById<LinearProgressIndicator>(Resource.Id.d_bar)!;
        bar.Visibility = row.ShowBar ? ViewStates.Visible : ViewStates.Invisible;
        if (row.Indeterminate) { if (!bar.Indeterminate) bar.Indeterminate = true; }
        else { if (bar.Indeterminate) bar.Indeterminate = false; bar.SetProgressCompat((int)(row.Progress * 1000), true); }
        _graph!.SetSamples(row.RateHistory);
    }

    private sealed class InsetsListener(Func<View, WindowInsetsCompat, WindowInsetsCompat> f) : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(View v, WindowInsetsCompat insets) => f(v, insets);
    }

    private sealed class RowAdapter : RecyclerView.Adapter
    {
        private static readonly Java.Lang.String Payload = new("p");
        private readonly DownloadsModel _model;
        private readonly HashSet<JobRow> _dirty = new();
        private readonly Handler _ui = new(Looper.MainLooper!);
        private bool _flushQueued;
        private bool _live;

        private readonly RecyclerView _list;

        private readonly Action<JobRow, View> _open;

        public RowAdapter(DownloadsModel model, RecyclerView list, Action<JobRow, View> open)
        {
            _open = open;
            _model = model;
            _list = list;
            model.Rows.CollectionChanged += OnRows;
            model.FirstSnapshotShown += () => { _live = true; NotifyDataSetChanged(); };
        }

        public override int ItemCount => _model.Rows.Count;

        private void OnRows(object? s, NotifyCollectionChangedEventArgs e)
        {
            if (!_live) return; // the snapshot is shown with one NotifyDataSetChanged
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                // RecyclerView keeps the first visible row in place when rows are inserted above it; a list that is at the top stays at the top.
                var atTop = _list.GetLayoutManager() is LinearLayoutManager lm && lm.FindFirstCompletelyVisibleItemPosition() <= 0;
                NotifyItemInserted(e.NewStartingIndex);
                if (atTop && e.NewStartingIndex == 0) _list.ScrollToPosition(0);
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove) NotifyItemRemoved(e.OldStartingIndex);
            else NotifyDataSetChanged();
        }

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var v = LayoutInflater.From(parent.Context)!.Inflate(Resource.Layout.row, parent, false)!;
            var h = new Holder(v, this);
            v.Click += (_, _) => { var i = h.BindingAdapterPosition; if (i >= 0) _open(_model.Rows[i], v); };
            return h;
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) => ((Holder)holder).Bind(_model.Rows[position], false);

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position, IList<Java.Lang.Object> payloads) =>
            ((Holder)holder).Bind(_model.Rows[position], payloads.Count > 0);

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
                foreach (var r in _dirty) { var i = _model.Rows.IndexOf(r); if (i >= 0) NotifyItemChanged(i, Payload); }
                _dirty.Clear();
            });
        }
    }

    private sealed class Holder : RecyclerView.ViewHolder
    {
        private readonly RowAdapter _owner;
        private readonly TextView _name, _subtitle;
        private readonly LinearProgressIndicator _bar;
        private JobRow? _row;

        public Holder(View v, RowAdapter owner) : base(v)
        {
            _owner = owner;
            _name = v.FindViewById<TextView>(Resource.Id.name)!;
            _subtitle = v.FindViewById<TextView>(Resource.Id.subtitle)!;
            _bar = v.FindViewById<LinearProgressIndicator>(Resource.Id.bar)!;
        }

        public void Bind(JobRow row, bool animate)
        {
            if (!ReferenceEquals(_row, row))
            {
                if (_row is not null) _owner.Watch(_row, false);
                _row = row;
                _owner.Watch(row, true);
                animate = false;
            }

            var ctx = ItemView.Context!;
            _name.Text = row.Name;
            _subtitle.Text = row.Subtitle;
            var color = row.Kind switch
            {
                StatusKind.Done => ctx.GetColor(Resource.Color.state_done),
                StatusKind.Failed => ctx.GetColor(Resource.Color.state_failed),
                StatusKind.Waiting => ctx.GetColor(Resource.Color.state_waiting),
                _ => ResolveAttr(ctx, Resource.Attribute.colorOnSurfaceVariant),
            };
            _subtitle.SetTextColor(new Android.Graphics.Color(color));
            _bar.Visibility = row.ShowBar ? ViewStates.Visible : ViewStates.Gone;
            if (!row.ShowBar) return;
            if (row.Indeterminate) { if (!_bar.Indeterminate) _bar.Indeterminate = true; return; }
            if (_bar.Indeterminate) _bar.Indeterminate = false;
            _bar.SetProgressCompat((int)(row.Progress * 1000), animate); // animates to the new value; no jump between updates
        }

        private static int ResolveAttr(Context ctx, int attr)
        {
            var tv = new Android.Util.TypedValue();
            ctx.Theme!.ResolveAttribute(attr, tv, true);
            return tv.Data;
        }
    }
}
