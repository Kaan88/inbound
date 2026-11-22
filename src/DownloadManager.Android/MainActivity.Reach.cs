using DownloadManager.Mobile;
using Inbound.App.Screens;

namespace Inbound.App;

/// <summary>Getting to the file (mockups x7 to x10b, x33): "&lt;file&gt; is on this phone" on any tab, and Show in folder.</summary>
public sealed partial class MainActivity
{
    private sealed record TrackedDownload(string Id, string Name, string PhonePath);

    private readonly List<TrackedDownload> _trackedDownloads = [];
    private CancellationTokenSource? _trackingLoop;

    /// <summary>
    /// Watches a transfer from a location to this phone's own Download folder. When it is done a snackbar says so on whichever tab is
    /// open, with Show in folder. Looks every 3 s and only while something is tracked and the app is visible.
    /// </summary>
    internal void TrackPhoneDownload(string id, string name, string phonePath)
    {
        if (IsFinishing || IsDestroyed) return;
        lock (_trackedDownloads) _trackedDownloads.Add(new TrackedDownload(id, name, phonePath));
        StartTrackingLoop();
    }

    private void StartTrackingLoop()
    {
        if (_trackingLoop is not null) return;
        var loop = _trackingLoop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
                while (await timer.WaitForNextTickAsync(loop.Token).ConfigureAwait(false))
                {
                    if (!_appVisibleForTracking) continue;
                    TrackedDownload[] pending;
                    lock (_trackedDownloads) pending = [.. _trackedDownloads];
                    if (pending.Length == 0) break;
                    var connection = _connection;
                    if (connection is null) continue;
                    var client = new RemoteFilesClient(connection);
                    foreach (var tracked in pending)
                    {
                        bool finished = false, gone = false;
                        try
                        {
                            var result = await client.GetTransferAsync(tracked.Id, loop.Token).ConfigureAwait(false);
                            var state = result.Transfer?.State;
                            if (!result.Ok || result.Transfer is null || state is "failed" or "canceled") gone = true;
                            else if (state == "done") finished = gone = true;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { /* Try again on the next tick. */ }
                        if (!gone) continue;
                        lock (_trackedDownloads) _trackedDownloads.Remove(tracked);
                        if (finished)
                            ShowMessage(tracked.Name + " is on this phone", "Show in folder", () => ShowPhoneFolder(tracked.PhonePath, tracked.Name));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* A failed look must not end the app. */ }
            finally { if (ReferenceEquals(_trackingLoop, loop)) _trackingLoop = null; }
        });
    }

    private volatile bool _appVisibleForTracking = true;

    private void StopTrackingLoop()
    {
        var loop = _trackingLoop;
        _trackingLoop = null;
        loop?.Cancel();
    }

    /// <summary>
    /// Show in folder: the Remote Files tab at <paramref name="phonePath"/> ("Download/Inbound/Music") on the This phone pane, with
    /// <paramref name="file"/> outlined for a moment. Nothing happens when the shown Host has no Remote Files.
    /// </summary>
    internal void ShowPhoneFolder(string phonePath, string? file)
    {
        RunOnUiThread(() =>
        {
            if (IsFinishing || IsDestroyed || _model?.Hello?.Capabilities.Contains("sites") != true) return;
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
