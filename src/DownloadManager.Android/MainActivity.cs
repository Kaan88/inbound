using System.Collections.Specialized;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
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
[Activity(Name = "app.inbound.android.MainActivity", Label = MainActivity.ActivityLabel, MainLauncher = true, Exported = true,
    LaunchMode = Android.Content.PM.LaunchMode.SingleTop, WindowSoftInputMode = SoftInput.AdjustResize)]
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "text/*")]
public sealed partial class MainActivity : AppCompatActivity
{
#if INBOUND_RCLONE_PROBE
    internal const string ActivityLabel = "Inbound Probe";
#else
    internal const string ActivityLabel = "Inbound";
#endif
    private const string FirstRunKey = "first_run_done";

    private DownloadsModel? _model;
    private IHostConnection? _connection; // step 6: the shown host's, not always the in-process one
    private DownloadsScreen? _downloads;
    private Navigator? _nav;
    private AppUpdatesScreen? _updateScreen;
    private FirstRunScreen? _firstRun;
    private DetailsScreen? _details;
    private NewDownloadScreen? _newDownload;
    private AddTorrentScreen? _addTorrent;
    private TorBoxScreen? _torBox;
    private TorBoxSettingsPage? _torBoxSettings;
    private TorrentDetailsScreen? _torrentDetails;
    private ActivityResultLauncher? _torrentPicker;
    private Action<Android.Net.Uri?>? _torrentPicked;
    // Which chooser is open ("torrent" or "list"), kept across a recreate: the callback above is not (Fable A9-11).
    private string? _pickKind;
    private PrefsStore? _prefs;
    private bool _onSettings;
    private bool _onQueues;
    private bool _onRemoteFiles;
    private RemoteFilesScreen? _remoteFiles;
    private QueuesScreen? _queues;
    private QueueEditorScreen? _queueEditor;
    private HostSettingsScreen? _hostSettings;
    private readonly Dictionary<Guid, RowGroup> _lastGroup = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _pickKind = savedInstanceState?.GetString("pick_kind");
        // The .torrent chooser of Add torrent: registered now, before the activity starts, as the result API requires.
        _torrentPicker = RegisterForActivityResult(new ActivityResultContracts.StartActivityForResult(), new TorrentPickResult(this));
        WindowCompat.SetDecorFitsSystemWindows(Window!, false); // edge to edge
        SetContentView(Resource.Layout.main);
        _prefs = new PrefsStore(this);
        var appearance = new Appearance(_prefs);
#if INBOUND_TEST_BUILD
        if (Intent?.GetBooleanExtra("skip_first_run", false) == true) _prefs.SetBool(FirstRunKey, true);
        RemoteFilesScreen.FixtureMode = Intent?.GetStringExtra("x_fixture"); // canned Transfers screen, see RemoteFilesScreen.Fixture.cs
#endif

        // --- Step 6 (ANDROID.7) hook: the connection is the shown host's (this phone, or a paired Inbound), and the host
        // chip and the "Files are saved on ..." strip go in the search bar's slots. Hosts/HostChoice.cs, Hosts/HostBar.cs. ---
        var hosts = Hosts.HostChoice.Hosts(this);
        var selectedHost = hosts.Current;
        _connection = Hosts.HostChoice.Connect(this, selectedHost, () => new LazyInProcessConnection(DispatcherAsync(InboundApplication.GetHostAsync(this)),
            e => Log.Error(InboundApplication.Tag, "screen: a Host frame failed: " + e), Background.InboundBackground.UserStartedDownloads));
        Capabilities = selectedHost.IsPhone ? DownloadManager.Mobile.Remote.HostCapabilities.Phone : DownloadManager.Mobile.Remote.HostCapabilities.Remote;
        HostName = selectedHost.Name;
        _linkHostStamp = selectedHost.ConnectionStamp;
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
        var selectionDraft = savedInstanceState?.GetBundle("downloads_selection");
        _downloads = new DownloadsScreen(this, downloadsRoot, _model,
            selectionDraft?.GetString("host_stamp") == _linkHostStamp ? selectionDraft : null);
        _ = new SettingsScreen(this, FindViewById<View>(Resource.Id.settings_root)!, appearance, query => _hostSettings?.FilterCategories(query));
        _queues = new QueuesScreen(this, FindViewById<View>(Resource.Id.queues_root)!, _model,
            new QueueSettingsModel(_model, _connection, requireCapabilities: Capabilities.IsRemote), OpenQueueEditor,
            () => FindViewById<Google.Android.Material.BottomNavigation.BottomNavigationView>(Resource.Id.nav)!.SelectedItemId = Resource.Id.nav_downloads);
        _hostSettings = new HostSettingsScreen(this, FindViewById<View>(Resource.Id.settings_content)!, _model,
            new QueueSettingsModel(_model, _connection, requireCapabilities: Capabilities.IsRemote));
        _updateScreen = new AppUpdatesScreen(this, FindViewById<View>(Resource.Id.settings_content)!, savedInstanceState?.GetBundle("app_update_prompt"));
        _nav = new Navigator(this, root, FindViewById<FrameLayout>(Resource.Id.overlay)!,
            () =>
            {
                var onOtherTab = _onSettings || _onQueues || _onRemoteFiles;
                return BackPlan.Next(_nav!.PageOpen, !onOtherTab && _downloads.Selecting,
                    !onOtherTab && _downloads.Searching, onOtherTab);
            }, PerformBack);
        FindViewById<View>(Resource.Id.fab)!.Click += (_, _) => ShowAddSheet();
        var bottom = FindViewById<Google.Android.Material.BottomNavigation.BottomNavigationView>(Resource.Id.nav)!;
        bottom.ItemSelected += (_, e) => { ShowTab(e.Item.ItemId); e.Handled = true; };
        _model.HostHelloChanged += () =>
        {
            var offered = _model.Hello?.Capabilities.Contains("sites") == true;
#if INBOUND_TEST_BUILD
            offered |= RemoteFilesScreen.FixtureMode is not null;
#endif
            if (!offered && _onRemoteFiles) bottom.SelectedItemId = Resource.Id.nav_downloads;
            bottom.Menu.FindItem(Resource.Id.nav_remote_files)!.SetVisible(offered);
        };
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
            var remoteFilesOffered = _model.Hello?.Capabilities.Contains("sites") == true;
#if INBOUND_TEST_BUILD
            if (RemoteFilesScreen.FixtureMode is not null)
            {
                bottom.Menu.FindItem(Resource.Id.nav_remote_files)!.SetVisible(true);
                bottom.SelectedItemId = Resource.Id.nav_remote_files;
            }
#endif
            if (remoteFilesOffered && savedInstanceState?.GetBoolean("on_remote_files") == true)
                bottom.SelectedItemId = Resource.Id.nav_remote_files;
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
        if (_pickKind is not null) outState.PutString("pick_kind", _pickKind);
#if INBOUND_TEST_BUILD
        SaveUpdatePreview(outState);
#endif
        base.OnSaveInstanceState(outState);
        outState.PutBoolean("on_settings", _onSettings);
        outState.PutBoolean("on_queues", _onQueues);
        outState.PutBoolean("on_remote_files", _onRemoteFiles);
        var queueActions = new Bundle(); _queues?.Save(queueActions);
        queueActions.PutString("host_stamp", _linkHostStamp); outState.PutBundle("queue_actions", queueActions);
        // The open page comes back after a rotation or a process death, with what was typed into it (finding 16).
        if (_nav?.PageOpen != true)
        {
            if (!_onSettings && !_onQueues && !_onRemoteFiles && _downloads?.SaveSelection() is { } selection)
            {
                selection.PutString("host_stamp", _linkHostStamp);
                outState.PutBundle("downloads_selection", selection);
            }
            return;
        }
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
        _downloads?.RefreshScheduledRows();
        Updates.AndroidUpdates.OnResumed(this);
        _browser?.Resume();
    }

    private void ShowTab(int tab)
    {
        var settings = tab == Resource.Id.nav_settings;
        var queues = tab == Resource.Id.nav_queues;
        var remoteFiles = tab == Resource.Id.nav_remote_files;
        if (_onSettings == settings && _onQueues == queues && _onRemoteFiles == remoteFiles) return;
        var downloads = FindViewById<View>(Resource.Id.downloads_root)!;
        var settingsPage = FindViewById<View>(Resource.Id.settings_root)!;
        var queuesPage = FindViewById<View>(Resource.Id.queues_root)!;
        var remoteFilesPage = FindViewById<FrameLayout>(Resource.Id.remote_files_root)!;
        var from = _onSettings ? settingsPage : _onQueues ? queuesPage : _onRemoteFiles ? remoteFilesPage : downloads;
        var to = settings ? settingsPage : queues ? queuesPage : remoteFiles ? remoteFilesPage : downloads;
        if (from == downloads) _downloads?.EndSelection();
        if (_onRemoteFiles)
        {
            _remoteFiles?.Dispose();
            _remoteFiles = null;
            remoteFilesPage.RemoveAllViews();
        }
        if (remoteFiles && _connection is not null)
        {
            var current = _linkHosts?.Current;
            bool secureHostConnection = current?.ConnectionStamp == _linkHostStamp &&
                (current?.IsPhone == true || current?.AddressUri?.Scheme == Uri.UriSchemeHttps);
            _remoteFiles = new RemoteFilesScreen(this, _connection, _linkHostStamp, secureHostConnection,
                () => _model?.Hello?.Capabilities.Contains(DownloadManager.Contracts.SiteTransferStartRequest.SafeSingleDownloadCapability) == true,
                () => _model?.Hello?.Capabilities.Contains(DownloadManager.Contracts.SiteChangeRequest.DeleteFilesCapability) == true,
                () => _model?.Hello?.Capabilities.Contains(DownloadManager.Contracts.SiteBrowseRequest.CachedFirstPageCapability) == true,
                () => _model?.Hello?.Capabilities.Contains(DownloadManager.Contracts.SiteTransferStartRequest.RemoteCopyCapability) == true,
                visible => FindViewById<View>(Resource.Id.nav)!.Visibility = visible ? ViewStates.Visible : ViewStates.Gone);
            InsetsListener.Pad(_remoteFiles.View, top: true, bottom: false);
            remoteFilesPage.AddView(_remoteFiles.View);
            _remoteFiles.Start();
        }
        _onSettings = settings; _onQueues = queues;
        _onRemoteFiles = remoteFiles;
        _queues?.SetVisible(queues);
        if (to == downloads) _downloads?.RefreshScheduledRows();
        if (Motion.Reduced(this)) Motion.CrossFade(from, to, 150, () => from.Visibility = ViewStates.Gone);
        else
        {
            AndroidX.Transitions.TransitionManager.BeginDelayedTransition(FindViewById<ViewGroup>(Resource.Id.pages)!, Motion.Fade());
            from.Visibility = ViewStates.Gone; to.Visibility = ViewStates.Visible;
        }
        if (remoteFiles && _remoteFiles is not null) ViewCompat.RequestApplyInsets(_remoteFiles.View);
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
                if (_onRemoteFiles && _remoteFiles?.HandleBack() == true) break;
                FindViewById<Google.Android.Material.BottomNavigation.BottomNavigationView>(Resource.Id.nav)!.SelectedItemId = Resource.Id.nav_downloads;
                break;
        }
    }

    internal void UpdateBack() => _nav?.UpdateBack();

    internal void SetDownloadsSelectionChrome(bool selecting)
    {
        var visibility = selecting ? ViewStates.Gone : ViewStates.Visible;
        FindViewById<View>(Resource.Id.fab)!.Visibility = visibility;
        if (!_onSettings && !_onQueues && !_onRemoteFiles)
            FindViewById<View>(Resource.Id.nav)!.Visibility = visibility;
    }

    /// <summary>A message for the person; from any thread. Sits above the bottom bar when that is showing.</summary>
    internal void ShowMessage(string text, string? action = null, Action? onAction = null, bool downloadsFeedback = false)
    {
        RunOnUiThread(() =>
        {
            if (IsFinishing || IsDestroyed) return;
            // Downloads already ends above the visible navigation bar and its system insets.
            var navigation = FindViewById<View>(Resource.Id.nav);
            bool withinDownloads = downloadsFeedback && navigation?.Visibility == ViewStates.Visible &&
                !_onSettings && !_onQueues && !_onRemoteFiles && _nav?.PageOpen != true;
            var parent = FindViewById<View>(withinDownloads ? Resource.Id.downloads_root : Resource.Id.root)!;
            var bar = Google.Android.Material.Snackbar.Snackbar.Make(parent, text, Google.Android.Material.Snackbar.Snackbar.LengthLong)!;
            if (withinDownloads)
            {
                var view = bar.View;
                bar.SetGestureInsetBottomIgnored(true);
                ViewCompat.SetOnApplyWindowInsetsListener(view, new InsetsListener((_, insets) => insets));
                if (view.LayoutParameters is ViewGroup.MarginLayoutParams margins)
                {
                    margins.LeftMargin = margins.RightMargin = Motion.Dp(this, 16);
                    margins.BottomMargin = Motion.Dp(this, 12);
                    view.LayoutParameters = margins;
                }
                // Change only the existing themed drawable's shape; retain its color and tint.
                var background = view.Background?.Mutate();
                if (background is Google.Android.Material.Shape.MaterialShapeDrawable shape)
                    shape.ShapeAppearanceModel = shape.ShapeAppearanceModel.ToBuilder().SetAllCornerSizes(Motion.Dp(this, 16)).Build();
                else if (background is Android.Graphics.Drawables.GradientDrawable gradient)
                    gradient.SetCornerRadius(Motion.Dp(this, 16));
            }
            else if (_nav?.PageOpen != true && navigation?.Visibility == ViewStates.Visible) bar.SetAnchorView(navigation);
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
            if (!Capabilities.CanOpenFiles) { ShowMessage(row.Name + " finished", downloadsFeedback: true); return; }
            ShowMessage(row.Name + " finished", "Open", () =>
            {
                if (!Outside.OpenFile(this, path)) ShowMessage("The file could not be opened.");
            }, downloadsFeedback: true);
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

    /// <summary>The shown Host answers remote.* (the phone's own Host does, and so does a paired PC or server that offers it): TorBox can be used.</summary>
    internal bool HasTorBox => DownloadManager.Mobile.TorBox.TorBoxClient.Offered(_model?.Hello);

    internal DownloadManager.Mobile.TorBox.TorBoxClient? NewTorBoxClient() =>
        _connection is null ? null : new DownloadManager.Mobile.TorBox.TorBoxClient(_connection);

    /// <summary>The shown Host answers archive.* and has an extractor (its hello lists "archives").</summary>
    internal bool HasArchives => DownloadManager.Mobile.Archives.ArchiveClient.Offered(_model?.Hello);

    /// <summary>
    /// The shown Host is this phone or is reached over HTTPS: a key or password may be sent to it. The rule Remote Files
    /// applies to site passwords, for the TorBox key and archive passwords too (Fable A9-3).
    /// </summary>
    internal bool SecureHostConnection
    {
        get
        {
            var current = _linkHosts?.Current;
            return current?.ConnectionStamp == _linkHostStamp && (current?.IsPhone == true || current?.AddressUri?.Scheme == Uri.UriSchemeHttps);
        }
    }

    /// <summary>Words under a secret field the phone won't send over an unencrypted Host connection.</summary>
    internal const string SecretNeedsHttps = "Secrets are only sent to this phone or to a Host reached over HTTPS. Connect to this Host with HTTPS first.";

    /// <summary>Keeps a window that shows a key or password out of screenshots, recordings and the recents thumbnail (Fable A9-19).</summary>
    internal static void KeepSecret(Android.App.Dialog dialog) => dialog.Window?.AddFlags(Android.Views.WindowManagerFlags.Secure);

    /// <summary>The shown Host keeps saved archive passwords and a set's own delete choice (its hello lists "archiveSavedPasswords").</summary>
    internal bool HasArchiveSavedPasswords => DownloadManager.Mobile.Archives.ArchiveClient.SavedPasswordsOffered(_model?.Hello);

    internal DownloadManager.Mobile.Archives.ArchiveClient? NewArchiveClient() =>
        _connection is null ? null : new DownloadManager.Mobile.Archives.ArchiveClient(_connection);

    /// <summary>The shown Host answers torrent.* (its hello lists "torrents"); whether they are turned on is its setting.</summary>
    internal bool HasTorrents => DownloadManager.Mobile.Torrents.TorrentClient.Offered(_model?.Hello);

    internal DownloadManager.Mobile.Torrents.TorrentClient? NewTorrentClient() =>
        _connection is null ? null : new DownloadManager.Mobile.Torrents.TorrentClient(_connection);

    /// <summary>Add torrent (t1, t2): the Add sheet's "Torrent file or magnet", with This phone (the Host's own torrents) and/or TorBox.</summary>
    internal void OpenAddTorrent()
    {
        if (_nav is null || _model is null || _prefs is null || _nav.PageOpen || !(HasTorBox || HasTorrents)) return;
        _addTorrent = new AddTorrentScreen(this, _model, HasTorBox ? NewTorBoxClient() : null, HasTorrents ? NewTorrentClient() : null,
            new DownloadManager.Mobile.TorBox.TorBoxChoice(_prefs));
        _nav.Open(_addTorrent.Root, null, null, OnPageClosed, "Add torrent");
        _addTorrent.FocusMagnet();
    }

    /// <summary>A torrent's details (t4), from its row in Downloads.</summary>
    internal void OpenTorrentDetails(JobRow row, View? card)
    {
        if (_nav is null || _model is null || _nav.PageOpen || NewTorrentClient() is not { } client) return;
        var connection = _connection;
        _torrentDetails = new TorrentDetailsScreen(this, _model, row, client, () => connection is not DownloadManager.Mobile.Remote.IRemoteStatus remote
            || remote.State.Status == DownloadManager.Mobile.Remote.RemoteConnectionStatus.Connected);
        _nav.Open(_torrentDetails.Root, card, () => _downloads?.CardOf(row), OnPageClosed, "Torrent details");
    }

    /// <summary>Opens the system's chooser for a .torrent file; <paramref name="picked"/> gets its address (null when none was chosen). False when there is no chooser.</summary>
    internal bool PickTorrentFile(Action<Android.Net.Uri?> picked)
    {
        if (_torrentPicker is null) return false;
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        // application/x-bittorrent, and the octet-stream that many file providers give a .torrent file.
        intent.PutExtra(Intent.ExtraMimeTypes, new[] { "application/x-bittorrent", "application/octet-stream" });
        _torrentPicked = picked; _pickKind = "torrent";
        try { _torrentPicker.Launch(intent); }
        catch (ActivityNotFoundException) { _torrentPicked = null; _pickKind = null; return false; }
        return true;
    }

    private sealed class TorrentPickResult(MainActivity owner) : Java.Lang.Object, IActivityResultCallback
    {
        public void OnActivityResult(Java.Lang.Object? result)
        {
            var picked = owner._torrentPicked;
            var kind = owner._pickKind;
            owner._torrentPicked = null; owner._pickKind = null;
            if (result is not ActivityResult outcome || outcome.ResultCode != (int)Result.Ok) return;
            if (picked is not null) { picked(outcome.Data?.Data); return; }
            // Android recreated the app while the chooser was open: the page that asked is gone (Fable A9-11).
            if (outcome.Data?.Data is not { } uri) return;
            if (kind == "list") owner.ReadImportList(uri, null);
            else if (kind == "torrent") owner.ShowMessage("Android restarted Inbound while the file was chosen. Choose the torrent file again.");
            else if (kind == "sshkey") owner.ShowMessage("Android restarted Inbound while the file was chosen. Choose the key file again.");
        }
    }

    /// <summary>The TorBox screen (t5), or the key sheet first when no key is saved yet.</summary>
    internal void OpenTorBox()
    {
        if (_nav is null || _model is null || _nav.PageOpen || !HasTorBox || NewTorBoxClient() is not { } client) return;
        var model = _model;
        var hostName = Capabilities.IsRemote ? HostName : "";
        void Open()
        {
            if (_nav is null || _nav.PageOpen || IsFinishing || IsDestroyed || !ReferenceEquals(model, _model)) return;
            _torBox = new TorBoxScreen(this, model, client);
            _nav.Open(_torBox.Root, null, null, OnPageClosed, "TorBox");
        }

        Screens.TorBoxUi.Run(this, () => client.AccountAsync(), result =>
        {
            var account = result.Ok ? result.Accounts.FirstOrDefault() : null;
            if (account is { HasKey: true, KeyUnreadable: false } || account is null) Open();
            else Screens.TorBoxKeySheet.Show(this, client, account.KeyUnreadable, hostName, Open);
        }, words => ShowMessage(words));
    }

    /// <summary>"Torrents, TorBox, archives" (t7), from the Settings list.</summary>
    internal bool OpenTorBoxSettings()
    {
        if (_nav is null || _nav.PageOpen || _prefs is null || _model is null || _connection is null || !(HasTorBox || HasArchives || HasTorrents)) return false;
        _torBoxSettings = new TorBoxSettingsPage(this, HasTorBox ? NewTorBoxClient() : null, HasArchives ? NewArchiveClient() : null,
            new DownloadManager.Mobile.TorBox.TorBoxChoice(_prefs), HasTorrents, _model,
            new QueueSettingsModel(_model, _connection, requireCapabilities: Capabilities.IsRemote));
        return OpenHostSettingsPage(_torBoxSettings.Root, TorBoxSettingsPage.Title, () => { });
    }

    internal void ShowDownloadsTab() =>
        FindViewById<Google.Android.Material.BottomNavigation.BottomNavigationView>(Resource.Id.nav)!.SelectedItemId = Resource.Id.nav_downloads;

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
        _importList?.Dispose();
        _importList = null;
        _addTorrent?.Dispose();
        _addTorrent = null;
        _torBox?.Dispose();
        _torBox = null;
        _torBoxSettings?.Dispose();
        _torBoxSettings = null;
        _torrentDetails?.Dispose();
        _torrentDetails = null;
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
        _torrentDetails?.SetVisible(true);
        _torBox?.SetVisible(true);
        _addTorrent?.SetVisible(true);
        _remoteFiles?.SetVisible(true);
        ResumeTracking();
    }

    protected override void OnStop()
    {
        _updateScreen?.Stop();
#if INBOUND_TEST_BUILD
        SuspendUpdatePreview();
#endif
        InboundBackground.SetVisible(false);
        _details?.SetVisible(false); // no polling while nothing is shown (findings 11, 15)
        _torrentDetails?.SetVisible(false);
        _torBox?.SetVisible(false);
        _addTorrent?.SetVisible(false);
        _remoteFiles?.SetVisible(false);
        StopTrackingLoop();
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
        _downloads?.CloseSelectionWindows();
        _linkLifetime.Cancel();
        _linkPageLifetime?.Cancel();
        StopTrackingLoop();
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
        _remoteFiles?.Dispose();
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
