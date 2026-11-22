using DownloadManager.Mobile;
using Inbound.App.Host;
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
                        catch (Exception) { /* Try again on the next tick. */ }
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
            var bottom = FindViewById<Google.Android.Material.BottomNavigation.BottomNavigationView>(Resource.Id.nav)!;
            if (!_onRemoteFiles) bottom.SelectedItemId = Resource.Id.nav_remote_files;
            _remoteFiles?.ShowPhoneFolder(phonePath, file);
        });
    }

    /// <summary>"Download/Inbound" for a folder in this phone's shared Download folder; null for any other place.</summary>
    internal static string? PhoneFolderOf(string folder)
    {
        var path = RemoteTransferText.PhonePath(folder.Replace('\\', '/'));
        return path == "Download" || path.StartsWith("Download/", StringComparison.Ordinal) ? path : null;
    }
}
