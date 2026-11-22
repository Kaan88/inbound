using DownloadManager.Mobile;
using Inbound.App.Host;
using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using Inbound.App.Screens;

namespace Inbound.App;

/// <summary>Getting to the file (mockups x7 to x10b, x33): "&lt;file&gt; is on this phone" on any tab, and Show in folder.</summary>
public sealed partial class MainActivity
{
    private sealed class TrackedDownload(string id, string name, string phonePath)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string PhonePath { get; } = phonePath;
        public int Misses;
    }

    // Static: the list survives a recreated activity (rotation, theme change) and a switch of the shown Host.
    private static readonly List<TrackedDownload> TrackedDownloads = [];
    private CancellationTokenSource? _trackingLoop;

    /// <summary>
    /// Watches a transfer from a location to this phone's own Download folder. When it is done a snackbar says so on whichever tab is
    /// open, with Show in folder. Looks every 3 s and only while something is tracked and the app is visible.
    /// </summary>
    internal void TrackPhoneDownload(string id, string name, string phonePath)
    {
        if (IsFinishing || IsDestroyed) return;
        lock (TrackedDownloads) TrackedDownloads.Add(new TrackedDownload(id, name, phonePath));
        StartTrackingLoop();
    }

    /// <summary>Starts the watch again when the app comes back to the front (or the activity is recreated) with downloads still tracked.</summary>
    private void ResumeTracking()
    {
        bool any;
        lock (TrackedDownloads) any = TrackedDownloads.Count > 0;
        if (any) StartTrackingLoop();
    }

    private void StartTrackingLoop()
    {
        CancellationTokenSource loop;
        lock (TrackedDownloads)
        {
            if (_trackingLoop is not null) return;
            loop = _trackingLoop = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            // Always the phone's own Host, never the shown one: the shown Host may be a paired server that knows nothing of these ids.
            var phone = new LazyInProcessConnection(DispatcherAsync(InboundApplication.GetHostAsync(this)));
            try
            {
                await phone.OpenAsync(_ => { }, loop.Token).ConfigureAwait(false);
                var client = new RemoteFilesClient(phone);
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
                while (await timer.WaitForNextTickAsync(loop.Token).ConfigureAwait(false))
                {
                    TrackedDownload[] pending;
                    lock (TrackedDownloads)
                    {
                        if (TrackedDownloads.Count == 0)
                        {
                            // Decided and cleared under the lock, so a download tracked a moment later starts a new loop.
                            if (ReferenceEquals(_trackingLoop, loop)) _trackingLoop = null;
                            break;
                        }
                        pending = [.. TrackedDownloads];
                    }
                    foreach (var tracked in pending)
                    {
                        bool finished = false, gone = false;
                        try
                        {
                            var result = await client.GetTransferAsync(tracked.Id, loop.Token).ConfigureAwait(false);
                            var state = result.Transfer?.State;
                            if (!result.Ok || result.Transfer is null) gone = ++tracked.Misses >= 5; // try again a few times before giving up
                            else if (state is "failed" or "canceled") gone = true;
                            else if (state == "done") finished = gone = true;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { gone = ++tracked.Misses >= 5; } // try again on the next tick, a few times (Fable L4)
                        if (!gone) continue;
                        lock (TrackedDownloads) TrackedDownloads.Remove(tracked);
                        if (finished)
                            ShowMessage(tracked.Name + " is on this phone", "Show in folder", () => ShowPhoneFolder(tracked.PhonePath, tracked.Name));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* A failed look must not end the app. */ }
            finally
            {
                lock (TrackedDownloads) { if (ReferenceEquals(_trackingLoop, loop)) _trackingLoop = null; }
                try { await phone.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
                loop.Dispose();
            }
        });
    }

    private void StopTrackingLoop()
    {
        CancellationTokenSource? loop;
        lock (TrackedDownloads) { loop = _trackingLoop; _trackingLoop = null; }
        try { loop?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Show in folder: the Remote Files tab at <paramref name="phonePath"/> ("Download/Inbound/Music") on the This phone pane, with
    /// <paramref name="file"/> outlined for a moment. Without Remote Files, or on a paired Host, the Files app shows the Download folder instead.
    /// </summary>
    internal void ShowPhoneFolder(string phonePath, string? file)
    {
        RunOnUiThread(() =>
        {
            if (IsFinishing || IsDestroyed) return;
            // Remote Files opens only on this phone's own Host; on a paired Host (or without Remote Files) the Files app shows the folder.
            if (Capabilities.IsRemote || _model?.Hello?.Capabilities.Contains("sites") != true)
            {
                if (!Outside.ShowDownloadFolder(this)) ShowMessage("No app on this phone can show the folder.");
                return;
            }
            // An open page would cover the folder (Fable L8). Details and Torrent details close and give way, as for x10b; any
            // other page (a half-filled New download) keeps its state and the tap does nothing.
            if (_nav?.Page is { } open)
            {
                if (!ReferenceEquals(open, _details?.Root) && !ReferenceEquals(open, _torrentDetails?.Root)) return;
                _afterPage = () => ShowPhoneFolder(phonePath, file);
                _nav.Close(open);
                return;
            }
            CloseFolderPage();
            var bottom = FindViewById<Google.Android.Material.BottomNavigation.BottomNavigationView>(Resource.Id.nav)!;
            if (!_onRemoteFiles) bottom.SelectedItemId = Resource.Id.nav_remote_files;
            _remoteFiles?.ShowPhoneFolder(phonePath, file);
        });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // x10b: Show in folder from Downloads. The app's own folder page over the Downloads tab, which stays selected.

    private PhoneFolderPage? _folderPage;
    private AndroidX.Activity.OnBackPressedCallback? _folderBack;
    private Action? _afterPage;
    private long _expectNewRowUntil;
    private JobRow? _lastAddedRow;

    /// <summary>The Show in folder of an "added" snackbar: the folder page, with the download just added outlined when it is the one saved there.</summary>
    internal void ShowAddedFolder(string phoneFolder, bool useRow = true)
    {
        var row = useRow ? _lastAddedRow : null;
        // The page reads the file name live from the row: right after Add the Host often does not know it yet.
        if (row is not null && PhoneFolderOf(row.Job.DestinationDirectory) == phoneFolder) ShowDownloadsFolder(phoneFolder, null, row);
        else ShowDownloadsFolder(phoneFolder, null);
    }

    /// <summary>
    /// A snackbar for after a page closes: the page is still animating away, so the message waits and sits above the bottom bar
    /// (the FAB lifts with it), and the new card's outline starts then, when the list is in view.
    /// </summary>
    internal void ShowMessageAfterClose(string text, string? action = null, Action? onAction = null)
    {
        new Android.OS.Handler(Android.OS.Looper.MainLooper!).PostDelayed(() =>
        {
            if (IsFinishing || IsDestroyed) return;
            ShowMessage(text, action, onAction, downloadsFeedback: true);
            if (_pendingMark is { } row) { _pendingMark = null; _downloads?.MarkNew(row); }
        }, Motion.ContainerMs + 100);
    }

    private JobRow? _pendingMark;

    /// <summary>The snackbar view that has the Remote Files Add location button lifted (x33); see ShowMessage.</summary>
    private View? _fabLiftOwner;

    private sealed class FolderBack(Action back) : AndroidX.Activity.OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed() => back();
    }

    /// <summary>Back with a Navigator page over the folder page (a notification's Details, a share): the visible page goes first.</summary>
    private void FolderBackPressed()
    {
        if (_nav?.PageOpen == true && _folderBack is { } callback)
        {
            callback.Enabled = false;
            OnBackPressedDispatcher.OnBackPressed();
            callback.Enabled = _folderPage is not null;
            return;
        }
        CloseFolderPage();
    }

    /// <summary>
    /// Show in folder from the Downloads side (a snackbar, the finished card, Details): the folder page with <paramref name="file"/>
    /// outlined; <paramref name="row"/> is the download while it still runs, so its row shows the progress.
    /// </summary>
    internal void ShowDownloadsFolder(string phonePath, string? file, JobRow? row = null)
    {
        RunOnUiThread(() =>
        {
            if (IsFinishing || IsDestroyed || Capabilities.IsRemote) return;
            if (_nav?.Page is { } open)
            {
                // Only Details and Torrent details (the sources of this action) close and give way; any other page (the queue editor,
                // a half-filled New download) keeps its state and the tap does nothing.
                if (!ReferenceEquals(open, _details?.Root) && !ReferenceEquals(open, _torrentDetails?.Root)) return;
                _afterPage = () => OpenFolderPage(phonePath, file, row);
                _nav.Close(open);
                return;
            }
            OpenFolderPage(phonePath, file, row);
        });
    }

    /// <summary>Show in folder for a finished row (card button, Details menu): its folder when it is on this phone's Download folder, else the Files app.</summary>
    internal void ShowRowFolder(JobRow row)
    {
        var folder = PhoneFolderOf(row.Job.DestinationDirectory);
        if (folder is not null) ShowDownloadsFolder(folder, row.Name, row);
        else if (!Outside.ShowDownloadFolder(this)) ShowMessage("No app on this phone can show the folder.");
    }

    private void OpenFolderPage(string phonePath, string? file, JobRow? row, List<DownloadManager.Contracts.SiteItemDto>? canned = null)
    {
        if (IsFinishing || IsDestroyed) return;
        var bottom = FindViewById<Google.Android.Material.BottomNavigation.BottomNavigationView>(Resource.Id.nav)!;
        if (_onSettings || _onQueues || _onRemoteFiles) bottom.SelectedItemId = Resource.Id.nav_downloads;
        CloseFolderPage();
        var pages = FindViewById<FrameLayout>(Resource.Id.pages)!;
        var page = new PhoneFolderPage(this, phonePath, file, row, () => CloseFolderPage(), canned);
        _folderPage = page;
        if (!Motion.Reduced(this)) AndroidX.Transitions.TransitionManager.BeginDelayedTransition(pages, Motion.Fade());
        pages.AddView(page.Root, new FrameLayout.LayoutParams(-1, -1));
        FindViewById<View>(Resource.Id.downloads_root)!.ImportantForAccessibility = ImportantForAccessibility.NoHideDescendants;
        ViewCompat.SetAccessibilityPaneTitle(page.Root, "Folder " + phonePath);
        ViewCompat.RequestApplyInsets(page.Root);
        if (_folderBack is null) { _folderBack = new FolderBack(FolderBackPressed); OnBackPressedDispatcher.AddCallback(this, _folderBack); }
        _folderBack.Enabled = true;
    }

    /// <summary>Back from the folder page to the Downloads list (the Downloads tab never stopped being selected).</summary>
    private void CloseFolderPage()
    {
        if (_folderPage is not { } page) return;
        _folderPage = null;
        if (_folderBack is not null) _folderBack.Enabled = false;
        var pages = FindViewById<FrameLayout>(Resource.Id.pages);
        if (pages is not null)
        {
            if (!Motion.Reduced(this) && !IsFinishing && !IsDestroyed) AndroidX.Transitions.TransitionManager.BeginDelayedTransition(pages, Motion.Fade());
            pages.RemoveView(page.Root);
        }
        page.Dispose();
        FindViewById<View>(Resource.Id.downloads_root)!.ImportantForAccessibility = ImportantForAccessibility.Auto;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // x10: a newly added download shows its folder line and an outline on its card for about 3 s.

    /// <summary>Called as a download is submitted: the next new row (within a short while) is the one the person just added.</summary>
    internal void ExpectNewRow() { _expectNewRowUntil = Environment.TickCount64 + 20_000; _lastAddedRow = null; _pendingMark = null; }

    internal void ForgetNewRow() { _expectNewRowUntil = 0; _lastAddedRow = null; _pendingMark = null; }

    private void MarkIfExpected(JobRow row)
    {
        if (_expectNewRowUntil < Environment.TickCount64) return;
        _expectNewRowUntil = 0;
        _lastAddedRow = row;
        // The New download page still covers the list: the outline starts when its snackbar appears.
        if (_nav?.PageOpen == true) _pendingMark = row;
        else _downloads?.MarkNew(row);
    }

    /// <summary>A removed row: a folder page waiting on its download stops showing progress.</summary>
    private void RowGone(JobRow row)
    {
        _folderPage?.OnRowRemoved(row);
        if (ReferenceEquals(_lastAddedRow, row)) _lastAddedRow = null;
    }

    /// <summary>
    /// The "Added to Downloads" snackbar of a torrent or TorBox file: Show in folder on this phone (x10), no action on a paired Host,
    /// whose files are not on this phone.
    /// </summary>
    internal void ShowAdded(string text, string? destination, bool afterPage = false, bool newRow = true)
    {
        var folder = Capabilities.IsRemote || destination is null ? null : PhoneFolderOf(destination);
        string? action = folder is null ? null : "Show in folder";
        Action? click = folder is null ? null : () => ShowAddedFolder(folder, newRow);
        if (afterPage) ShowMessageAfterClose(text, action, click);
        else ShowMessage(text, action, click, downloadsFeedback: true);
    }

#if INBOUND_TEST_BUILD
    /// <summary>
    /// Test build only, for captures: <c>--es x10b_fixture finished|running</c> opens the x10b folder page with canned rows (manual-v2.pdf
    /// outlined with Open, or its progress), and <c>--es x10_fixture added</c> shows the x10 snackbar and outlines the newest card.
    /// </summary>
    internal void RunFolderFixture(Intent? intent)
    {
        if (intent?.GetStringExtra("x10_fixture") == "added")
        {
            ShowMessage("Download added · saving to Download/Inbound", "Show in folder", () => ShowDownloadsFolder("Download/Inbound", null), downloadsFeedback: true);
            // The outline fades after about 3 s; mark the newest card again every 0.4 s so a capture finds it.
            for (int i = 0; i < 100; i++)
                FindViewById<Android.Views.View>(Resource.Id.root)?.PostDelayed(() => { if (_model?.Rows.FirstOrDefault() is { } newest) _downloads?.MarkNew(newest); }, i * 400);
        }

        var mode = intent?.GetStringExtra("x10b_fixture");
        if (mode is not "finished" and not "running") return;
        var now = DateTimeOffset.Now;
        var rows = new List<DownloadManager.Contracts.SiteItemDto>
        {
            new() { Name = "Music", IsFolder = true, ChildCount = 4 },
            new() { Name = "Video", IsFolder = true, ChildCount = 2 },
            new() { Name = "manual-v2.pdf", Size = 8_400_000, Modified = now },
            new() { Name = "field-recordings-vol3.zip", Size = 640_000_000, Modified = now.AddHours(-3) },
            new() { Name = "poster-final.png", Size = 3_200_000, Modified = now.AddDays(-1) },
            new() { Name = "setlist.pdf", Size = 220_000, Modified = now.AddDays(-1) },
        };
        JobRow? running = null;
        if (mode == "running")
        {
            running = new JobRow { Id = Guid.NewGuid() };
            running.ApplyFrom(new DownloadManager.Contracts.JobDto
            {
                State = "downloading", FileName = "manual-v2.pdf", TotalBytes = 8_400_000, ReceivedBytes = 1_200_000, BytesPerSecond = 2_100_000,
                DestinationDirectory = AndroidHost.SharedDownloadFolder + "/Inbound",
            });
        }
        OpenFolderPage("Download/Inbound", "manual-v2.pdf", running, rows);
        if (mode == "finished")
            for (int i = 0; i < 100; i++)
                FindViewById<Android.Views.View>(Resource.Id.root)?.PostDelayed(() => _folderPage?.FixtureRemark(), 4000 + i * 400);
    }
#endif

    /// <summary>"Download/Inbound" for a folder in this phone's shared Download folder; null for any other place.</summary>
    internal static string? PhoneFolderOf(string folder)
    {
        var path = RemoteTransferText.PhonePath(folder.Replace('\\', '/'));
        return path == "Download" || path.StartsWith("Download/", StringComparison.Ordinal) ? path : null;
    }
}
