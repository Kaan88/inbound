using System.Collections.Specialized;
using System.ComponentModel;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using AndroidX.RecyclerView.Widget;
using DownloadManager.Mobile;
using Google.Android.Material.ProgressIndicator;
using Inbound.App.Background;
using Inbound.App.Host;

namespace Inbound.App;

/// <summary>The Downloads screen: the list of downloads of the in-process Host, and the Add sheet.</summary>
[Activity(Name = "app.inbound.android.MainActivity", Label = "Inbound", MainLauncher = true, Exported = true,
    LaunchMode = Android.Content.PM.LaunchMode.SingleTop, WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class MainActivity : AppCompatActivity
{
    private DownloadsModel? _model;
    private LazyInProcessConnection? _connection;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        WindowCompat.SetDecorFitsSystemWindows(Window!, false); // edge to edge
        SetContentView(Resource.Layout.main);

        var appbar = FindViewById<View>(Resource.Id.appbar)!;
        var fab = FindViewById<View>(Resource.Id.fab)!;
        var list = FindViewById<RecyclerView>(Resource.Id.list)!;
        var baseFabMargin = ((ViewGroup.MarginLayoutParams)fab.LayoutParameters!).BottomMargin;
        ViewCompat.SetOnApplyWindowInsetsListener(FindViewById<View>(Resource.Id.root)!, new InsetsListener((v, insets) =>
        {
            if (insets?.GetInsets(WindowInsetsCompat.Type.SystemBars()) is not { } bars) return insets;
            appbar.SetPadding(bars.Left, bars.Top, bars.Right, 0);
            ((ViewGroup.MarginLayoutParams)fab.LayoutParameters!).BottomMargin = baseFabMargin + bars.Bottom;
            fab.RequestLayout();
            return insets;
        }));

        _connection = new LazyInProcessConnection(DispatcherAsync(InboundApplication.GetHostAsync(this)),
            e => Log.Error(InboundApplication.Tag, "screen: a Host frame failed: " + e));
        _model = new DownloadsModel(_connection, a => RunOnUiThread(a));
        var adapter = new RowAdapter(_model, list);
        list.SetLayoutManager(new LinearLayoutManager(this));
        list.SetAdapter(adapter);
        list.SetItemAnimator(new DefaultItemAnimator { AddDuration = 200, MoveDuration = 200, RemoveDuration = 200, ChangeDuration = 0, SupportsChangeAnimations = false });
        fab.Click += (_, _) => ShowAddSheet();
        AskForNotifications();
#if INBOUND_TEST_BUILD
        // A recreated activity (process death, rotation) or a task brought back from history carries the old intent again: it must not resume twice.
        HandleResumeAll(Intent, redelivered: savedInstanceState is not null || (Intent?.Flags & ActivityFlags.LaunchedFromHistory) != 0);
#endif

        var reported = false;
        _model.FirstSnapshotShown += () =>
        {
            if (reported) return;
            reported = true;
            ReportWhenDrawn(_model.Rows.Count);
        };
        var model = _model;
        _ = Task.Run(async () =>
        {
            try
            {
                await model.StartAsync(CancellationToken.None).ConfigureAwait(false);
#if INBOUND_TEST_BUILD
                TestHooks.Run(model, Intent);
#endif
            }
            catch (System.Exception ex)
            {
                Log.Error(InboundApplication.Tag, "host: " + ex);
                if (!model.Disposed) RunOnUiThread(() => ShowStartFailed(ex));
            }
        });
    }

    private static async Task<DownloadManager.Runtime.Ipc.HostCommandDispatcher> DispatcherAsync(Task<AndroidHost> host) =>
        (await host.ConfigureAwait(false)).Dispatcher;

    /// <summary>
    /// The Host did not start: Add is turned off (it could never work) and the message stays until the user tries again, which
    /// builds this screen anew and so starts the Host again (InboundApplication drops a failed start).
    /// </summary>
    private void ShowStartFailed(System.Exception error)
    {
        if (IsFinishing || IsDestroyed) return;
        FindViewById<View>(Resource.Id.fab)!.Enabled = false;
        // A Host that could not be stopped is not started again in this process, so there is nothing to try again.
        var stuck = error is HostLeftRunningException;
        var bar = Google.Android.Material.Snackbar.Snackbar.Make(FindViewById<View>(Resource.Id.root)!,
            stuck ? error.Message : "The downloader could not start: " + error.Message,
            Google.Android.Material.Snackbar.Snackbar.LengthIndefinite);
        if (!stuck) bar.SetAction("Try again", _ => Recreate());
        bar.Show();
    }

    /// <summary>A message the user must see; on the application context, so it outlives this activity.</summary>
    private void ShowError(string text)
    {
        var app = ApplicationContext!;
        new Handler(Looper.MainLooper!).Post(() => Toast.MakeText(app, text, ToastLength.Long)!.Show());
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
#if INBOUND_TEST_BUILD
        HandleResumeAll(intent, redelivered: (intent?.Flags & ActivityFlags.LaunchedFromHistory) != 0);
        if (_model is not null) TestHooks.Run(_model, intent);
#endif
    }

    // The app is visible between OnStart and OnStop: only then may a keep-alive session be started (a background start is refused by the system).
    protected override void OnStart()
    {
        base.OnStart();
        InboundBackground.SetVisible(true);
    }

    protected override void OnStop()
    {
        InboundBackground.SetVisible(false);
        base.OnStop();
    }

#if INBOUND_TEST_BUILD
    /// <summary>
    /// Test build only: <c>--ez resume_all true</c> does what the notifications' Resume does (ResumeActivity, which is not exported). The
    /// release build has no way for another app to ask MainActivity to resume anything (review of step 2).
    /// </summary>
    private void HandleResumeAll(Intent? intent, bool redelivered)
    {
        if (intent is null || !intent.GetBooleanExtra(Notifications.ExtraResumeAll, false)) return;
        intent.RemoveExtra(Notifications.ExtraResumeAll);
        if (redelivered) return;
        ResumeSet.ResumeFromNotification(ApplicationContext!, "test hook");
    }
#endif

    /// <summary>API 33 and up: asks once, at the first start. Denied is fine: everything still runs, there is just no notification.</summary>
    private void AskForNotifications()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        const string permission = "android.permission.POST_NOTIFICATIONS";
        if (CheckSelfPermission(permission) == Android.Content.PM.Permission.Granted) return;
        var prefs = GetSharedPreferences("inbound", FileCreationMode.Private)!;
        if (prefs.GetBoolean("asked_notifications", false)) return;
        prefs.Edit()!.PutBoolean("asked_notifications", true)!.Apply();
        RequestPermissions([permission], 1);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        _model?.Dispose();
        if (_connection is not null) _ = _connection.DisposeAsync().AsTask();
    }

    private void ShowAddSheet()
    {
        if (SupportFragmentManager.FindFragmentByTag("add") is not null) return;
        new AddSheet().Show(SupportFragmentManager, "add");
    }

    /// <summary>
    /// From the Add sheet. Sent even when the Host is still starting (the command waits for the session); a refusal or a
    /// failure is shown, never dropped.
    /// </summary>
    internal void Add(string url)
    {
        var model = _model;
        if (model is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await model.AddAsync(url).ConfigureAwait(false);
                if (!result.Accepted)
                {
                    Log.Warn(InboundApplication.Tag, "add refused: " + result.Error);
                    ShowError(result.Error ?? "The download was not added.");
                }
            }
            catch (System.Exception ex)
            {
                Log.Error(InboundApplication.Tag, "add: " + ex);
                ShowError("The download was not added: " + ex.Message);
            }
        });
    }

    /// <summary>"Usable list": called when the snapshot's rows are in the list. Waits for two frames, then ReportFullyDrawn and a log line with the time since process start.</summary>
    private void ReportWhenDrawn(int rows)
    {
        var shownAt = SystemClock.ElapsedRealtime();
        Choreographer.Instance!.PostFrameCallback(new FrameCb(() => Choreographer.Instance!.PostFrameCallback(new FrameCb(() =>
        {
            ReportFullyDrawn();
            var since = SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime;
            Log.Info(InboundApplication.Tag, $"fullyDrawn={since}ms rows={rows} rowsShownToDrawn={SystemClock.ElapsedRealtime() - shownAt}ms");
        }))));
    }

    private sealed class FrameCb(Action run) : Java.Lang.Object, Choreographer.IFrameCallback
    {
        public void DoFrame(long frameTimeNanos) => run();
    }

    private sealed class InsetsListener(Func<View?, WindowInsetsCompat?, WindowInsetsCompat?> f) : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat? OnApplyWindowInsets(View? v, WindowInsetsCompat? insets) => f(v, insets);
    }

    private sealed class RowAdapter : RecyclerView.Adapter
    {
        private static readonly Java.Lang.String Payload = new("p");
        private readonly DownloadsModel _model;
        private readonly HashSet<JobRow> _dirty = new();
        private readonly Handler _ui = new(Looper.MainLooper!);
        private readonly RecyclerView _list;
        private bool _flushQueued;
        private bool _live;

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
            var tv = new TypedValue();
            ctx.Theme!.ResolveAttribute(attr, tv, true);
            return tv.Data;
        }
    }
}
