using System.Text.Json;
using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// T1: what the browser extension hands off stays an ordinary download with torrents turned on
/// (docs/plans/native-torrent-research.md, "Extension"): a magnet is refused, and a .torrent file is a plain file.
/// </summary>
public sealed partial class HostCommandDispatcherTests
{
    private static HandoffRequest TorrentHandoff(string url, string folder) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        ExtensionId = BrowserBridge.StoreExtensionId,
        Url = url,
        SuggestedFileName = "ubuntu.torrent",
        MimeType = "application/x-bittorrent",
        DestinationDirectory = folder,
        StartMode = "later",
    };

    [Fact]
    public async Task WithTorrentsOnABrowserHandOffOfAMagnetIsRefusedAndNoWorkerStarts()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var magnet = "magnet:?xt=urn:btih:" + new string('a', 40) + "&dn=ubuntu&tr=udp%3A%2F%2Ftracker.test%3A1337";

        var ack = (await AskAsync(dispatcher, Session(), MessageTypes.HandoffRequest, TorrentHandoff(magnet, Path.Combine(_root, "down"))))
            .GetPayload<HandoffAck>();

        Assert.False(ack.Accepted);
        Assert.Equal("invalid_url", ack.ErrorCode);
        Assert.Empty(service.GetSnapshot().Jobs);
        Assert.Empty(_torrentWorkers);
    }

    [Fact]
    public async Task WithTorrentsOnABrowserHandOffOfATorrentFileIsAnOrdinaryDownload()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);

        var ack = (await AskAsync(dispatcher, Session(), MessageTypes.HandoffRequest,
            TorrentHandoff("http://127.0.0.1:9/files/ubuntu.torrent", Path.Combine(_root, "down")))).GetPayload<HandoffAck>();

        Assert.True(ack.Accepted, ack.Error);
        var job = Assert.Single(service.GetSnapshot().Jobs);
        Assert.Equal(ack.JobId, job.Id);
        Assert.Null(job.Torrent);
        Assert.Equal("http://127.0.0.1:9/files/ubuntu.torrent", job.Url);
        Assert.Empty(_torrentWorkers);
    }

    [Fact]
    public async Task ABrowserSeesATorrentWaitingForItsFileChoiceWithItsShortAddressAndNoResume()
    {
        // Wave B (questions 3 and 4): the Host's own snapshot keeps the magnet as added, and a browser bridge gets the
        // short one and no Resume for a download the engine would only hold again.
        var service = await TorrentServiceAsync(filesPerTorrent: 3);
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn=Album&tr=udp%3A%2F%2Ftracker.test%3A1337";
        var added = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = magnet, Destination = Path.Combine(_root, "down"), Files = "askWhenReady",
        })).GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);
        var waiting = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Torrent?.AwaitingFileChoice == true && j.State == "paused"));
        Assert.Contains("tracker.test", waiting.Url); // the Host's own clients are told as before
        Assert.True(waiting.CanResume);

        var snapshot = await AskAsync(dispatcher, Session(), MessageTypes.JobsQuery, new { });
        var events = new[]
        {
            IpcMessage.Create(MessageTypes.JobEvent, new JobEventDto { Job = waiting, EventType = "paused" }),
            IpcMessage.Create(MessageTypes.JobActionResult, new JobActionResponse { Ok = true, Job = waiting }),
        };
        foreach (var role in new[] { BridgeRole.BrowserExtension, BridgeRole.ServerExtension })
        {
            var shown = NativeBridgePolicy.To(role, snapshot)!;
            Assert.DoesNotContain("tracker.test", shown.Payload!.Value.GetRawText());
            // The details the Host keeps for its own clients don't cross the bridge (rule 5).
            Assert.DoesNotContain("orrent", shown.Payload!.Value.GetRawText());
            var job = Assert.Single(shown.GetPayload<SnapshotDto>().Jobs);
            Assert.Equal(($"magnet:?xt=urn:btih:{hash}", false), (job.Url, job.CanResume));
            foreach (var message in events)
            {
                var one = NativeBridgePolicy.To(role, message)!;
                Assert.DoesNotContain("tracker.test", one.Payload!.Value.GetRawText());
                var dto = one.Payload!.Value.GetProperty("job").Deserialize<JobDto>(IpcJson.Options)!;
                Assert.Equal(($"magnet:?xt=urn:btih:{hash}", false), (dto.Url, dto.CanResume));
            }
        }
    }
}
