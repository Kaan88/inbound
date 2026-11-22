using Android.Media;
using DownloadManager.Contracts;
using DownloadManager.Runtime.Sites;
using DownloadManager.Sites;

namespace Inbound.App.Host;

/// <summary>
/// Adds the files a location transfer wrote to this phone to Android's media index. Remote Files lists the phone's
/// Download folder from the Downloads MediaStore collection, and files rclone writes there (a Keep both copy, a
/// replaced file) were not in it, so they did not show in the This phone pane (S24 check, 2026-11-22).
/// </summary>
internal sealed class PhoneMediaIndex(SitesCommands sites, Action<string> log)
{
    private readonly object _sync = new();
    // Per batch, how many files were done when it was last looked at, and which targets are already scanned.
    private readonly Dictionary<string, int> _doneSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _scanned = new(StringComparer.Ordinal);

    public void OnTransfersChanged(SiteTransfersDto change)
    {
        var due = new List<string>();
        lock (_sync)
        {
            // The change lists every batch; forget the ones that were cleared.
            var listed = change.Transfers.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _doneSeen.Keys.Where(id => !listed.Contains(id)).ToList()) { _doneSeen.Remove(gone); _scanned.Remove(gone); }
            foreach (var transfer in change.Transfers)
            {
                if (transfer.Direction != SiteTransferDirections.Download) continue;
                if (_doneSeen.TryGetValue(transfer.Id, out var seen) && seen >= transfer.FilesDone) continue;
                _doneSeen[transfer.Id] = transfer.FilesDone;
                due.Add(transfer.Id);
            }
        }
        if (due.Count > 0) _ = Task.Run(() => Scan(due));
    }

    private void Scan(List<string> ids)
    {
        try
        {
            var paths = new List<string>();
            foreach (var id in ids)
            {
                var result = sites.Transfers.Get(id);
                if (result is not { Ok: true, Transfer: { } transfer, Items: { } items } || string.IsNullOrEmpty(transfer.LocalFolder)) continue;
                lock (_sync)
                {
                    if (!_scanned.TryGetValue(id, out var scanned)) _scanned[id] = scanned = new(StringComparer.Ordinal);
                    foreach (var item in items)
                    {
                        if (item.IsFolder || item.State != SiteTransferItemStates.Done) continue;
                        var relative = item.Target ?? item.Path;
                        if (!scanned.Add(relative)) continue;
                        paths.Add(Path.Combine(transfer.LocalFolder, relative.Replace('/', Path.DirectorySeparatorChar)));
                    }
                }
            }
            if (paths.Count > 0) MediaScannerConnection.ScanFile(Android.App.Application.Context, [.. paths], null, null);
        }
        catch (Exception error)
        {
            log("Could not add transferred files to the phone's media index: " + error.Message);
        }
    }
}
