using System.Collections.Specialized;
using System.ComponentModel;
using Android.App;
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
public sealed class MainActivity : Activity
{
    private DownloadsModel? _model;
    private RowAdapter? _adapter;

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
        var baseFabMargin = ((ViewGroup.MarginLayoutParams)fab.LayoutParameters!).BottomMargin;
        ViewCompat.SetOnApplyWindowInsetsListener(FindViewById<View>(Resource.Id.root)!, new InsetsListener((v, insets) =>
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            appbar.SetPadding(bars.Left, bars.Top, bars.Right, 0);
            nav.SetPadding(bars.Left, 0, bars.Right, bars.Bottom);
            ((ViewGroup.MarginLayoutParams)fab.LayoutParameters!).BottomMargin = baseFabMargin + bars.Bottom;
            return insets;
        }));
        nav.SelectedItemId = Resource.Id.nav_downloads;

        _model = new DownloadsModel(a => RunOnUiThread(a));
        _adapter = new RowAdapter(_model, list);
        list.SetLayoutManager(new LinearLayoutManager(this));
        list.SetAdapter(_adapter);
        var animator = new DefaultItemAnimator { AddDuration = 200, MoveDuration = 200, RemoveDuration = 200, ChangeDuration = 0, SupportsChangeAnimations = false };
        list.SetItemAnimator(animator);
        TrialSupport.Begin(this, _model, options);
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

        public RowAdapter(DownloadsModel model, RecyclerView list)
        {
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
            return new Holder(v, this);
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
