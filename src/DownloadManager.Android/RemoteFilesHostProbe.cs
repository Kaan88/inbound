#if INBOUND_RCLONE_PROBE
using System.Security.Cryptography;
using Android.Util;
using DownloadManager.Contracts;
using Inbound.App.Host;

namespace Inbound.App;

// This runs only in the separate probe package with an adb-reversed disposable WebDAV fixture.
internal static class RemoteFilesHostProbe
{
    private const string Tag = "InboundRemoteFilesProbe";

    public static void Run(int port) => _ = Task.Run(async () =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        try
        {
            var host = await InboundApplication.GetHostAsync(Android.App.Application.Context).ConfigureAwait(false);
            await using var connection = new InProcessHostConnection(host.Dispatcher);
            await connection.OpenAsync(_ => { }, ct).ConfigureAwait(false);
            var status = await connection.RequestAsync(IpcMessage.Create(SitesMessageTypes.Get), ct).ConfigureAwait(false);
            if (status.GetPayload<SitesStatusDto>() is not { Enabled: false, RcloneSource: SitesRcloneSources.Shipped })
                throw new InvalidOperationException("Expected disabled Remote Files with bundled rclone on a fresh install.");
            var fixtureFolder = Path.Combine(AndroidHost.SharedDownloadFolder, "inbound-remote-files-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                var enabled = await connection.RequestAsync(IpcMessage.Create(SitesMessageTypes.SetEnabled,
                    new SitesSetEnabledRequest { Enabled = true }), ct).ConfigureAwait(false);
                if (enabled.GetPayload<SitesStatusDto>() is not { Enabled: true, Error: null })
                    throw new InvalidOperationException("Could not enable Remote Files.");
                var request = new SiteSaveRequest { Name = "Disposable WebDAV fixture", Kind = "webdav",
                    Url = $"http://127.0.0.1:{port}/", Vendor = "other", LocalFolder = "/data/local/tmp" };
                var refused = await connection.RequestAsync(IpcMessage.Create(SitesMessageTypes.Save, request), ct).ConfigureAwait(false);
                if (refused.Type != MessageTypes.HostActionResult
                    || refused.GetPayload<HostActionResult>() is not { Ok: false, Message: { } message }
                    || !message.Contains("outside this phone's Download folder", StringComparison.Ordinal))
                    throw new InvalidOperationException("An outside folder was not refused.");
                request.LocalFolder = AndroidHost.SharedDownloadFolder;
                var saved = await connection.RequestAsync(IpcMessage.Create(SitesMessageTypes.Save, request), ct).ConfigureAwait(false);
                if (saved.GetPayload<SiteResultDto>() is not { Ok: true, Site: { } site })
                    throw new InvalidOperationException("Could not save the WebDAV fixture.");
                var browse = await connection.RequestAsync(IpcMessage.Create(SitesMessageTypes.Browse,
                    new SiteBrowseRequest { Id = site.Id }), ct).ConfigureAwait(false);
                var listing = browse.GetPayload<SiteBrowseResultDto>();
                if (listing is not { Ok: true } || !listing.Items.Any(item => item.Name == "inbound-remote-files.txt" && !item.IsFolder))
                    throw new InvalidOperationException("Remote fixture was not listed: " + listing?.Message);
                var transfer = await connection.RequestAsync(IpcMessage.Create(SitesMessageTypes.SafeDownloadStart,
                    new SiteTransferStartRequest { SiteId = site.Id, RemoteFolder = "/", LocalFolder = fixtureFolder,
                        Names = [new SiteTransferNameDto { Name = "inbound-remote-files.txt" }] }), ct).ConfigureAwait(false);
                if (transfer.GetPayload<SiteTransferResultDto>() is not { Ok: true, Transfer: { } batch })
                    throw new InvalidOperationException("Could not start fixture transfer: " + transfer.GetPayload<SiteTransferResultDto>().Message);
                while (batch.State == "running")
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    var progress = await connection.RequestAsync(IpcMessage.Create(SitesMessageTypes.TransferGet,
                        new SiteTransferIdRequest { Id = batch.Id }), ct).ConfigureAwait(false);
                    batch = progress.GetPayload<SiteTransferResultDto>().Transfer ?? throw new InvalidOperationException("Transfer disappeared.");
                }
                if (batch.State != "done" || batch is not { FilesDone: 1, FilesSkipped: 0, FilesFailed: 0, BytesDone: 3 })
                    throw new InvalidOperationException($"Unexpected transfer: {batch.State}, {batch.FilesDone} files, {batch.BytesDone} bytes: {batch.Message}");
                var bytes = await File.ReadAllBytesAsync(Path.Combine(fixtureFolder, "inbound-remote-files.txt"), ct).ConfigureAwait(false);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (bytes.Length != 3 || hash != "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
                    throw new InvalidOperationException("Fixture payload mismatch.");
                Log.Info(Tag, $"Transfer {batch.Id} finished: {bytes.Length} bytes sha256={hash}");
            }
            finally
            {
                try
                {
                    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var off = await connection.RequestAsync(IpcMessage.Create(SitesMessageTypes.SetEnabled,
                        new SitesSetEnabledRequest { Enabled = false }), stopTimeout.Token).ConfigureAwait(false);
                    if (off.GetPayload<SitesStatusDto>() is not { Enabled: false, Rclone: SitesRcloneStates.Off })
                        throw new InvalidOperationException("Could not stop Remote Files.");
                }
                finally
                {
                    if (Directory.Exists(fixtureFolder)) Directory.Delete(fixtureFolder, recursive: true);
                }
            }
            Log.Info(Tag, "PASS: bundled rclone via local IPC; outside folder refused; WebDAV fixture listed; Remote Files stopped");
        }
        catch (Exception ex) { Log.Error(Tag, ex.ToString()); }
    });
}
#endif
