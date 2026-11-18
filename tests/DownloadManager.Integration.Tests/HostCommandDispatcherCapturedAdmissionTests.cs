using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Mobile.Links;

namespace DownloadManager.Integration.Tests;

public sealed partial class HostCommandDispatcherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedCapturedAdmissionAfterLostAckCreatesOneJob(bool scopedRemote)
    {
        var web = await WebAsync();
        var capture = new CapturedDownload("https://203.0.113.10/capture.bin", "capture.bin");
        var request = capture.Add(scopedRemote, false, Inside, 1, later: true);
        // Exercise the actual wire shape, not an in-memory synthetic identity.
        var json = JsonSerializer.Serialize(request, IpcJson.Options);
        var wire = JsonSerializer.Deserialize<AddJobRequest>(json, IpcJson.Options)!;
        Assert.Equal("inbound-android-capture", wire.OwnerExtensionId);
        Assert.Equal(capture.RequestId, wire.ClientRequestId);
        var session = scopedRemote ? web.Session : web.Pipe;
        var first = (await AskAsync(web.Dispatcher, session, MessageTypes.JobAdd, wire)).GetPayload<AddJobResponse>();
        Assert.True(first.Accepted, first.Error); // caller loses this ACK and retries the same serialized payload
        var retry = JsonSerializer.Deserialize<AddJobRequest>(json, IpcJson.Options)!;
        var second = (await AskAsync(web.Dispatcher, session, MessageTypes.JobAdd, retry)).GetPayload<AddJobResponse>();
        Assert.True(second.Accepted, second.Error);
        Assert.Equal(first.Job!.Id, second.Job!.Id);
        Assert.Single(web.Service.GetSnapshot().Jobs);
    }
}
