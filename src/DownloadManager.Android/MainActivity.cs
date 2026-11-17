using System.Collections.Specialized;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using DownloadManager.Mobile;
using Inbound.App.Background;
using Inbound.App.Host;
using Inbound.App.Screens;

namespace Inbound.App;

/// <summary>
/// The app's one activity: Downloads and Settings behind a bottom bar, with Details and New download opening over them, the Add sheet,
/// and the first-run screen. Talks to the Host only through <see cref="DownloadsModel"/> and its connection.
/// </summary>
[Activity(Name = "app.inbound.android.MainActivity", Label = "Inbound", MainLauncher = true, Exported = true,
    LaunchMode = Android.Content.PM.LaunchMode.SingleTop, WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class MainActivity : AppCompatActivity
{
    private const string FirstRunKey = "first_run_done";

    private DownloadsModel? _model;
    private IHostConnection? _connection; // step 6: the shown host's, not always the in-process one
    private DownloadsScreen? _downloads;
    private Navigator? _nav;
    private FirstRunScreen? _firstRun;
    private DetailsScreen? _details;
    private NewDownloadScreen? _newDownload;
    private PrefsStore? _prefs;
    private bool _onSettings;
    private readonly Dictionary<Guid, RowGroup> _lastGroup = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        WindowCompat.SetDecorFitsSystemWindows(Window!, false); // edge to edge
        SetContentView(Resource.Layout.main);
        _prefs = new PrefsStore(this);
        var appearance = new Appearance(_prefs);
#if INBOUND_TEST_BUILD
        if (Intent?.GetBooleanExtra("skip_first_run", false) == true) _prefs.SetBool(FirstRunKey, true);
#endif

        // --- Step 6 (ANDROID.7) hook: the connection is the shown host's (this phone, or a paired Inbound), and the host
        // chip and the "Files are saved on ..." strip go in the search bar's slots. Hosts/HostChoice.cs, Hosts/HostBar.cs. ---
        _connection = Hosts.HostChoice.Connect(this, () => new LazyInProcessConnection(DispatcherAsync(InboundApplication.GetHostAsync(this)),
            e => Log.Error(InboundApplication.Tag, "screen: a Host frame failed: " + e)));
        var hosts = Hosts.HostChoice.Hosts(this);
        Capabilities = hosts.Capabilities;
        HostName = hosts.Current.Name;
        Hosts.HostBar.Place(this, FindViewById<ViewGroup>(Resource.Id.host_chip_slot)!, FindViewById<ViewGroup>(Resource.Id.host_strip_slot)!, _connection);
        // --- end of the step 6 hook ---
        _model = new DownloadsModel(_connection, a => RunOnUiThread(a));

        var root = FindViewById<ViewGroup>(Resource.Id.root)!;
        var downloadsRoot = FindViewById<View>(Resource.Id.downloads_root)!;
        InsetsListener.Pad(downloadsRoot, top: true, bottom: false);
        _downloads = new DownloadsScreen(this, downloadsRoot, _model);
        _ = new SettingsScreen(this, FindViewById<View>(Resource.Id.settings_root)!, appearance);
        _nav = new Navigator(this, root, FindViewById<FrameLayout>(Resource.Id.overlay)!,
            () => BackPlan.Next(_nav!.PageOpen, _downloads.Selecting, _downloads.Searching, _onSettings), PerformBack);
        FindViewById<View>(Resource.Id.fab)!.Click += (_, _) => ShowAddSheet();
        var bottom = FindViewById<Google.Android.Material.BottomNavigation.BottomNavigationView>(Resource.Id.nav)!;
        bottom.ItemSelected += (_, e) => { ShowTab(e.Item.ItemId == Resource.Id.nav_settings); e.Handled = true; };
        // A recreated activity (theme or dynamic colour changed) comes back on the tab it was on.
        if (savedInstanceState?.GetBoolean("on_settings") == true) bottom.SelectedItemId = Resource.Id.nav_settings;
        _model.Rows.CollectionChanged += OnRowsForFinished;
        ShowFirstRunIfNeeded();
        _nav.UpdateBack();
#if INBOUND_TEST_BUILD
        // A recreated activity (process death, rotation) or a task brought back from history carries the old intent again: it must not resume twice.
        HandleResumeAll(Intent, redelivered: savedInstanceState is not null || (Intent?.Flags & ActivityFlags.LaunchedFromHistory) != 0);
#endif

        var reported = false;
        _model.FirstSnapshotShown += () =>
        {
            foreach (var r in _model.Rows) WatchForFinish(r);
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

    /// <summary>What the shown host can do here: a remote host has no phone file to open or folder to show (ANDROID.7).</summary>
    internal DownloadManager.Mobile.Remote.HostCapabilities Capabilities { get; private set; } = DownloadManager.Mobile.Remote.HostCapabilities.Phone;

    /// <summary>The shown host's name ("This phone", or a paired Inbound's).</summary>
    internal string HostName { get; private set; } = "This phone";

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutBoolean("on_settings", _onSettings);
    }

    private static async Task<DownloadManager.Runtime.Ipc.HostCommandDispatcher> DispatcherAsync(Task<AndroidHost> host) =>
        (await host.ConfigureAwait(false)).Dispatcher;

    private void ShowFirstRunIfNeeded()
    {
        if (!FirstRunState.ShouldShow(_prefs!.GetBool(FirstRunKey))) return;
        // Connected to another Inbound from the first-run screen: the switch rebuilt this activity on that host, set-up is done.
        if (Capabilities.IsRemote) { _prefs.SetBool(FirstRunKey, true); return; }
        var host = FindViewById<FrameLayout>(Resource.Id.first_run_host)!;
        _firstRun = new FirstRunScreen(this, AndroidHost.SharedDownloadFolder, () => Hosts.PairingSheet.Show(this), () =>
        {
            _prefs.SetBool(FirstRunKey, true);
            host.Animate()!.Alpha(0f).SetDuration(200).WithEndAction(new Java.Lang.Runnable(() =>
            {
                host.Visibility = ViewStates.Gone;
                host.RemoveAllViews();
                _firstRun = null;
            }))!.Start();
        });
        host.AddView(_firstRun.Root);
        host.Visibility = ViewStates.Visible;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        _firstRun?.Refresh();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _firstRun?.Refresh(); // back from the battery dialog
    }

    private void ShowTab(bool settings)
    {
        if (_onSettings == settings) return;
        _onSettings = settings;
        // Navigation: 200 ms fade through.
        AndroidX.Transitions.TransitionManager.BeginDelayedTransition(FindViewById<ViewGroup>(Resource.Id.pages)!,
            Motion.Fade());
        FindViewById<View>(Resource.Id.downloads_root)!.Visibility = settings ? ViewStates.Gone : ViewStates.Visible;
        FindViewById<View>(Resource.Id.settings_root)!.Visibility = settings ? ViewStates.Visible : ViewStates.Gone;
        _nav?.UpdateBack();
    }

    private void PerformBack(BackStep step)
    {
        switch (step)
        {
            case BackStep.EndSelection: _downloads?.EndSelection(); break;
            case BackStep.CloseSearch: _downloads?.CloseSearch(); break;
            case BackStep.BackToDownloads:
                FindViewById<Google.Android.Material.BottomNavigation.BottomNavigationView>(Resource.Id.nav)!.SelectedItemId = Resource.Id.nav_downloads;
                break;
        }
    }

    internal void UpdateBack() => _nav?.UpdateBack();

    /// <summary>A message for the person; from any thread. Sits above the bottom bar when that is showing.</summary>
    internal void ShowMessage(string text, string? action = null, Action? onAction = null)
    {
        RunOnUiThread(() =>
        {
            if (IsFinishing || IsDestroyed) return;
            var bar = Google.Android.Material.Snackbar.Snackbar.Make(FindViewById<View>(Resource.Id.root)!, text, Google.Android.Material.Snackbar.Snackbar.LengthLong)!;
            if (_nav?.PageOpen != true) bar.SetAnchorView(FindViewById<View>(Resource.Id.nav));
            if (action is not null && onAction is not null) bar.SetAction(action, _ => onAction());
            bar.Show();
        });
    }

    private void OnRowsForFinished(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null) return;
        foreach (JobRow r in e.NewItems) WatchForFinish(r);
    }

    // The "finished" snackbar of A3: a download that was running and now has its file. Not for rows that arrive already finished.
    private void WatchForFinish(JobRow row)
    {
        if (_lastGroup.ContainsKey(row.Id)) return;
        _lastGroup[row.Id] = row.Group;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(JobRow.Group)) return;
            var before = _lastGroup.GetValueOrDefault(row.Id, row.Group);
            _lastGroup[row.Id] = row.Group;
            if (row.Group != RowGroup.Finished || before == RowGroup.Finished) return;
            var path = row.Job.FinalFilePath;
            // A server's file is on the server: nothing on this phone to open.
            if (!Capabilities.CanOpenFiles) { ShowMessage(row.Name + " finished"); return; }
            ShowMessage(row.Name + " finished", "Open", () =>
            {
                if (!Outside.OpenFile(this, path)) ShowMessage("The file could not be opened.");
            });
        };
    }

    internal void OpenDetails(JobRow row, View card)
    {
        if (_nav is null || _model is null || _nav.PageOpen) return;
        _details = new DetailsScreen(this, _model, row);
        _nav.Open(_details.Root, card, () => _downloads?.CardOf(row), OnPageClosed);
    }

    internal void OpenNewDownload(string? link)
    {
        if (_nav is null || _model is null || _nav.PageOpen) return;
        _newDownload = new NewDownloadScreen(this, _model, link);
        _nav.Open(_newDownload.Root, null, null, OnPageClosed);
        if (link is null) _newDownload.FocusAddress();
    }

    internal void CloseRowPage() => _nav?.Close();

    internal void ConfirmDeleteFromDetails(JobRow row) => _downloads?.ConfirmDelete([row]);

    private void OnPageClosed()
    {
        _details?.Dispose();
        _details = null;
        _newDownload = null;
        var imm = (Android.Views.InputMethods.InputMethodManager)GetSystemService(InputMethodService)!;
        imm.HideSoftInputFromWindow(FindViewById<View>(Resource.Id.root)!.WindowToken, 0);
    }

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

    protected override void OnDestroy()
    {
        base.OnDestroy();
        _downloads?.Dispose();
        _model?.Dispose();
        if (_connection is not null) _ = _connection.DisposeAsync().AsTask();
    }

    private void ShowAddSheet()
    {
        if (SupportFragmentManager.FindFragmentByTag("add") is not null) return;
        new AddSheet().Show(SupportFragmentManager, "add");
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
}
