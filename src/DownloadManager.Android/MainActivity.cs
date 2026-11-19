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
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "text/*")]
public sealed partial class MainActivity : AppCompatActivity
{
    private const string FirstRunKey = "first_run_done";

    private DownloadsModel? _model;
    private IHostConnection? _connection; // step 6: the shown host's, not always the in-process one
    private DownloadsScreen? _downloads;
    private Navigator? _nav;
    private AppUpdatesScreen? _updateScreen;
    private FirstRunScreen? _firstRun;
    private DetailsScreen? _details;
    private NewDownloadScreen? _newDownload;
    private PrefsStore? _prefs;
    private bool _onSettings;
    private bool _onQueues;
    private QueuesScreen? _queues;
    private QueueEditorScreen? _queueEditor;
    private HostSettingsScreen? _hostSettings;
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
            e => Log.Error(InboundApplication.Tag, "screen: a Host frame failed: " + e), Background.InboundBackground.UserStartedDownloads));
        var hosts = Hosts.HostChoice.Hosts(this);
        Capabilities = hosts.Capabilities;
        HostName = hosts.Current.Name;
        _linkHostStamp = hosts.Current.ConnectionStamp;
        _linkHosts = hosts; hosts.Changed += OnLinkHostsChanged;
        // The phone's own Host runs whatever host is shown: its downloads, their reconcile and resume, and the keep-alive of
        // step 2 must not wait until the user looks at This phone again (review of steps 3 and 6, finding 6).
        if (Capabilities.IsRemote) StartPhoneHost();
        Hosts.HostBar.Place(this, FindViewById<ViewGroup>(Resource.Id.host_chip_slot)!, FindViewById<ViewGroup>(Resource.Id.host_strip_slot)!, _connection);
        // --- end of the step 6 hook ---
        _model = new DownloadsModel(_connection, a => RunOnUiThread(a));

        var root = FindViewById<ViewGroup>(Resource.Id.root)!;
        var downloadsRoot = FindViewById<View>(Resource.Id.downloads_root)!;
        InsetsListener.Pad(downloadsRoot, top: true, bottom: false);
        _downloads = new DownloadsScreen(this, downloadsRoot, _model);
        _ = new SettingsScreen(this, FindViewById<View>(Resource.Id.settings_root)!, appearance);
        _queues = new QueuesScreen(this, FindViewById<View>(Resource.Id.queues_root)!, _model,
            new QueueSettingsModel(_model, _connection, requireCapabilities: Capabilities.IsRemote), OpenQueueEditor);
        _hostSettings = new HostSettingsScreen(this, FindViewById<View>(Resource.Id.settings_content)!, _model,
            new QueueSettingsModel(_model, _connection, requireCapabilities: Capabilities.IsRemote));
        _updateScreen = new AppUpdatesScreen(this, FindViewById<View>(Resource.Id.settings_content)!, savedInstanceState?.GetBundle("app_update_prompt"));
        _nav = new Navigator(this, root, FindViewById<FrameLayout>(Resource.Id.overlay)!,
            () => BackPlan.Next(_nav!.PageOpen, _downloads.Selecting, _downloads.Searching, _onSettings || _onQueues), PerformBack);
        FindViewById<View>(Resource.Id.fab)!.Click += (_, _) => ShowAddSheet();
        var bottom = FindViewById<Google.Android.Material.BottomNavigation.BottomNavigationView>(Resource.Id.nav)!;
        bottom.ItemSelected += (_, e) => { ShowTab(e.Item.ItemId); e.Handled = true; };
        // A recreated activity (theme or dynamic colour changed) comes back on the tab it was on.
        if (savedInstanceState?.GetBoolean("on_settings") == true) bottom.SelectedItemId = Resource.Id.nav_settings;
        else if (savedInstanceState?.GetBoolean("on_queues") == true) bottom.SelectedItemId = Resource.Id.nav_queues;
        _model.Rows.CollectionChanged += OnRowsForFinished;
        ShowFirstRunIfNeeded();
        RestorePage(savedInstanceState);
        ShowUpdatesIfRequested(Intent);
        ReceiveShare(Intent, savedInstanceState is not null || (Intent?.Flags & ActivityFlags.LaunchedFromHistory) != 0);
        _nav.UpdateBack();
#if INBOUND_TEST_BUILD
        // A recreated activity (process death, rotation) or a task brought back from history carries the old intent again: it must not resume twice.
        HandleResumeAll(Intent, redelivered: savedInstanceState is not null || (Intent?.Flags & ActivityFlags.LaunchedFromHistory) != 0);
        HandleUpdatePreview(Intent, savedInstanceState);
#endif

        var reported = false;
        _model.FirstSnapshotShown += () =>
        {
            foreach (var r in _model.Rows) WatchForFinish(r);
            _snapshotReady = true;
            PresentShare();
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
        var updatePrompt = new Bundle(); _updateScreen?.Save(updatePrompt); outState.PutBundle("app_update_prompt", updatePrompt);
#if INBOUND_TEST_BUILD
        SaveUpdatePreview(outState);
#endif
        base.OnSaveInstanceState(outState);
        outState.PutBoolean("on_settings", _onSettings);
        outState.PutBoolean("on_queues", _onQueues);
        var queueActions = new Bundle(); _queues?.Save(queueActions);
        queueActions.PutString("host_stamp", _linkHostStamp); outState.PutBundle("queue_actions", queueActions);
        // The open page comes back after a rotation or a process death, with what was typed into it (finding 16).
        if (_nav?.PageOpen != true) return;
        if (_hostSettings?.OpenDetailId is { } detail)
        {
            outState.PutString("host_settings_detail", detail);
            outState.PutString("host_settings_stamp", _linkHostStamp);
            var settingsDraft = new Bundle(); _hostSettings.Save(settingsDraft); outState.PutBundle("host_settings_draft", settingsDraft);
        }
        else if (_queueEditor is not null)
        {
            var queueDraft = new Bundle(); _queueEditor.Save(queueDraft); outState.PutBundle("queue_editor", queueDraft);
        }
        else if (_details is not null) outState.PutString("details_job", _details.Row.Id.ToString());
        else if (_newDownload is { Transient: false })
        {
            var page = new Bundle();
            _newDownload.Save(page);
            page.PutString("host_stamp", _linkHostStamp);
            outState.PutBundle("new_download", page);
        }
    }

    private void RestorePage(Bundle? saved)
    {
        if (saved is null || _model is null) return;
        if (saved.GetBundle("queue_actions") is { } actions && actions.GetString("host_stamp") == _linkHostStamp)
        {
            void RestoreActions() { _model.FirstSnapshotShown -= RestoreActions; _queues?.Restore(actions); }
            _model.FirstSnapshotShown += RestoreActions;
        }
        if (saved.GetString("host_settings_detail") is { } detail && saved.GetString("host_settings_stamp") == _linkHostStamp)
        {
            void RestoreSettings()
            {
                _model.FirstSnapshotShown -= RestoreSettings;
                _hostSettings?.OpenDetail(detail);
                if (saved.GetBundle("host_settings_draft") is { } settingsDraft) _hostSettings?.Restore(settingsDraft);
            }
            _model.FirstSnapshotShown += RestoreSettings;
            return;
        }
        if (saved.GetBundle("queue_editor") is { } draft && draft.GetString("host_stamp") == _linkHostStamp)
        {
            void RestoreQueue()
            {
                _model.FirstSnapshotShown -= RestoreQueue;
                OpenQueueEditorDraft(draft);
            }
            _model.FirstSnapshotShown += RestoreQueue;
            return;
        }
        if (saved.GetBundle("new_download") is { } page && page.GetString("host_stamp") == _linkHostStamp) { OpenNewDownload(null, page); return; }
        if (!Guid.TryParse(saved.GetString("details_job"), out var id)) return;
        // Its row is known once the snapshot is shown; a download that is gone meanwhile has no page to come back to.
        var model = _model;
        void Shown()
        {
            model.FirstSnapshotShown -= Shown;
            if (model.Rows.FirstOrDefault(r => r.Id == id) is { } row) OpenDetails(row, null);
        }

        model.FirstSnapshotShown += Shown;
    }

    private void StartPhoneHost()
    {
        var started = InboundApplication.GetHostAsync(this);
        _ = started.ContinueWith(t => Log.Error(InboundApplication.Tag, "host: the phone's Host did not start: " + t.Exception?.InnerException),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>The shown server's connection gave up (pairing ended, certificate refused): only a rebuilt screen tries again.</summary>
    internal bool ConnectionStopped =>
        _connection is DownloadManager.Mobile.Remote.IRemoteStatus { State.Status: DownloadManager.Mobile.Remote.RemoteConnectionStatus.Stopped };

    private static async Task<DownloadManager.Runtime.Ipc.HostCommandDispatcher> DispatcherAsync(Task<AndroidHost> host) =>
        (await host.ConfigureAwait(false)).Dispatcher;

    private void ShowFirstRunIfNeeded()
    {
        if (!FirstRunState.ShouldShow(_prefs!.GetBool(FirstRunKey))) return;
        // Connected to another Inbound from the first-run screen: that host is shown without it. Set-up is not done, though:
        // the first time This phone is shown again, the screen comes back for its notifications and battery rows (finding 8).
        if (Capabilities.IsRemote) return;
        var host = FindViewById<FrameLayout>(Resource.Id.first_run_host)!;
        var behind = new[] { FindViewById<View>(Resource.Id.column)!, FindViewById<View>(Resource.Id.overlay)! };
        _firstRun = new FirstRunScreen(this, AndroidHost.SharedDownloadFolder, () => Hosts.PairingSheet.Show(this), () =>
        {
            _prefs.SetBool(FirstRunKey, true);
            void Gone()
            {
                host.Visibility = ViewStates.Gone;
                host.RemoveAllViews();
                host.Alpha = 1f;
                _firstRun = null;
                foreach (var v in behind) Reachable(v, true);
                PresentShare();
            }

            // With animations removed the system's animators don't run; the fade is done by hand then (finding 21).
            if (Motion.Reduced(this)) Motion.FadeOut(host, 200, Gone);
            else host.Animate()!.Alpha(0f).SetDuration(200).WithEndAction(new Java.Lang.Runnable(Gone))!.Start();
        });
        host.AddView(_firstRun.Root);
        host.Visibility = ViewStates.Visible;
        // TalkBack and the keyboard stay on the first-run screen, not on the Downloads screen under it (findings 17, 20).
        AndroidX.Core.View.ViewCompat.SetAccessibilityPaneTitle(_firstRun.Root, "Welcome to Inbound");
        foreach (var v in behind) Reachable(v, false);
    }

    private static void Reachable(View view, bool reachable)
    {
        view.ImportantForAccessibility = reachable ? ImportantForAccessibility.Auto : ImportantForAccessibility.NoHideDescendants;
        if (view is ViewGroup g) g.DescendantFocusability = reachable ? DescendantFocusability.BeforeDescendants : DescendantFocusability.BlockDescendants;
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
        Updates.AndroidUpdates.OnResumed(this);
        _browser?.Resume();
    }

    private void ShowTab(int tab)
    {
        var settings = tab == Resource.Id.nav_settings;
        var queues = tab == Resource.Id.nav_queues;
        if (_onSettings == settings && _onQueues == queues) return;
        var downloads = FindViewById<View>(Resource.Id.downloads_root)!;
        var settingsPage = FindViewById<View>(Resource.Id.settings_root)!;
        var queuesPage = FindViewById<View>(Resource.Id.queues_root)!;
        var from = _onSettings ? settingsPage : _onQueues ? queuesPage : downloads;
        var to = settings ? settingsPage : queues ? queuesPage : downloads;
        _onSettings = settings; _onQueues = queues;
        _queues?.SetVisible(queues);
        if (Motion.Reduced(this)) Motion.CrossFade(from, to, 150, () => from.Visibility = ViewStates.Gone);
        else
        {
            AndroidX.Transitions.TransitionManager.BeginDelayedTransition(FindViewById<ViewGroup>(Resource.Id.pages)!, Motion.Fade());
            from.Visibility = ViewStates.Gone; to.Visibility = ViewStates.Visible;
        }
        _nav?.UpdateBack();
    }

    private void OpenQueueEditor(DownloadManager.Contracts.QueueDto? queue)
    {
        if (_nav is null || _model is null || _connection is null || _nav.PageOpen || !_model.SnapshotShown) return;
        _queueEditor = new QueueEditorScreen(this, _model, new QueueSettingsModel(_model, _connection, requireCapabilities: Capabilities.IsRemote), queue);
        _nav.Open(_queueEditor.Root, null, null, OnPageClosed, queue is null ? "New queue" : queue.Name,
            () => { _queueEditor?.RequestClose(); return true; }, () => true);
    }

    private void OpenQueueEditorDraft(Bundle draft)
    {
        if (_nav is null || _model is null || _connection is null || _nav.PageOpen) return;
        _queueEditor = new QueueEditorScreen(this, _model, new QueueSettingsModel(_model, _connection, requireCapabilities: Capabilities.IsRemote), null, draft);
        _nav.Open(_queueEditor.Root, null, null, OnPageClosed, "Queue and schedule",
            () => { _queueEditor?.RequestClose(); return true; }, () => true);
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

    internal void OpenDetails(JobRow row, View? card)
    {
        if (_nav is null || _model is null || _nav.PageOpen) return;
        var connection = _connection;
        // No polling while a remote host isn't connected: the requests could only wait and fail.
        _details = new DetailsScreen(this, _model, row, () => connection is not DownloadManager.Mobile.Remote.IRemoteStatus remote
            || remote.State.Status == DownloadManager.Mobile.Remote.RemoteConnectionStatus.Connected);
        _nav.Open(_details.Root, card, () => _downloads?.CardOf(row), OnPageClosed, "Details");
    }

    internal void OpenNewDownload(string? link, Bundle? saved = null, bool transient = false)
    {
        if (_nav is null || _model is null || _nav.PageOpen) return;
        _newDownload = new NewDownloadScreen(this, _model, link, saved, transient);
        _nav.Open(_newDownload.Root, null, null, OnPageClosed, "New download");
        if (link is null && saved is null) _newDownload.FocusAddress();
    }

    /// <summary>Closes <paramref name="page"/> if it is still the open page (a late call for a page already gone does nothing).</summary>
    internal void CloseRowPage(View page) => _nav?.Close(page);

    internal bool OpenHostSettingsPage(View page, string title, Action closed)
    {
        if (_nav is null || _nav.PageOpen || IsFinishing || IsDestroyed) return false;
        _nav.Open(page, null, null, () => { closed(); OnPageClosed(); }, title);
        return true;
    }

    internal void OpenBrowserFromSettings(View page)
    {
        if (_nav?.Page != page || IsFinishing || IsDestroyed) return;
        _afterLinkPage = () => OpenBrowser();
        _nav.Close(page);
    }


    /// <summary>The row card a page is open from (kept hidden by the list until the page has closed).</summary>
    internal View? HiddenCard => _nav?.HiddenCard;

    internal void ConfirmDeleteFromDetails(JobRow row) => _downloads?.ConfirmDelete([row]);

    private void OnPageClosed()
    {
        _queueEditor?.Dispose();
        _queueEditor = null;
        _details?.Dispose();
        _details = null;
        _newDownload?.Dispose();
        _newDownload = null;
        _linkPageLifetime?.Cancel();
        _linkPageGeneration++;
        _browser?.Dispose();
        _browser = null;
        _acceptedCaptures.Clear();
        if (!IsFinishing && !IsDestroyed && !_linkLifetime.IsCancellationRequested)
        {
            var after = _afterLinkPage; _afterLinkPage = null; after?.Invoke();
            PresentShare();
        }
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

    private void ShowUpdatesIfRequested(Intent? intent)
    {
        if (intent?.GetBooleanExtra(Updates.AndroidUpdates.ShowUpdatesExtra, false) != true) return;
        intent.RemoveExtra(Updates.AndroidUpdates.ShowUpdatesExtra);
        _updateScreen?.OpenFromNotification();
        if (_nav?.PageOpen != true)
            FindViewById<Google.Android.Material.BottomNavigation.BottomNavigationView>(Resource.Id.nav)!.SelectedItemId = Resource.Id.nav_settings;
    }

    protected override void OnPause()
    {
        _browser?.Pause();
        Updates.AndroidUpdates.OnPaused(this);
        base.OnPause();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        ShowUpdatesIfRequested(intent);
        ReceiveShare(intent, (intent?.Flags & ActivityFlags.LaunchedFromHistory) != 0);
#if INBOUND_TEST_BUILD
        HandleResumeAll(intent, redelivered: (intent?.Flags & ActivityFlags.LaunchedFromHistory) != 0);
        HandleUpdatePreview(intent, null);
        if (_model is not null) TestHooks.Run(_model, intent);
        if (intent?.GetBooleanExtra("update_fixture", false) == true)
        {
            intent.RemoveExtra("update_fixture");
            _ = Updates.AndroidUpdates.RunFixtureAsync(this);
        }
#endif
    }

    // The app is visible between OnStart and OnStop: only then may a keep-alive session be started (a background start is refused by the system).
    protected override void OnStart()
    {
        base.OnStart();
        InboundBackground.SetVisible(true);
        _updateScreen?.Start();
#if INBOUND_TEST_BUILD
        ResumeUpdatePreview();
#endif
        Updates.AndroidUpdates.OnAppVisible();
        _details?.SetVisible(true);
    }

    protected override void OnStop()
    {
        _updateScreen?.Stop();
#if INBOUND_TEST_BUILD
        SuspendUpdatePreview();
#endif
        InboundBackground.SetVisible(false);
        _details?.SetVisible(false); // no polling while nothing is shown (findings 11, 15)
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
        _linkLifetime.Cancel();
        _linkPageLifetime?.Cancel();
        if (_linkHosts is not null) _linkHosts.Changed -= OnLinkHostsChanged;
        base.OnDestroy();
        // An open page goes with the activity: Details stops polling, and no view of this activity is held on to (findings 15, 16).
        _updateScreen?.Dispose();
        _nav?.Dispose();
        _details?.Dispose();
        _details = null;
        _queueEditor?.Dispose();
        _queues?.Dispose();
        _hostSettings?.Dispose();
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
