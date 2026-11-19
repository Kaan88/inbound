using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class RemoteFilesMutationTests
{
    [Fact]
    public async Task EnableSaveAndRemoveUseTypedRequestsAndPreserveResults()
    {
        var host = new FakeHostConnection
        {
            Reply = request => request.Type == SitesMessageTypes.SetEnabled
                ? IpcMessage.Create(SitesMessageTypes.Status, new SitesStatusDto { Enabled = true }, request.Id)
                : IpcMessage.Create(SitesMessageTypes.Result, new SiteResultDto
                {
                    Ok = false, Problem = SiteProblems.Off, Message = "Unavailable"
                }, request.Id)
        };
        var client = new RemoteFilesClient(host);
        Assert.True((await client.SetEnabledAsync(true)).Enabled);
        var saved = await client.SaveAsync(new SiteSaveRequest
        {
            Id = "site", Name = "name", Host = "host", Password = "test-secret",
            PrivateKey = null, KeyPassphrase = "", ClientSecret = null
        });
        var removed = await client.RemoveAsync("site");

        Assert.True(host.Sent[0].GetPayload<SitesSetEnabledRequest>().Enabled);
        var save = host.Sent[1].GetPayload<SiteSaveRequest>();
        Assert.Equal(("site", "name", "host", "test-secret", null, "", null),
            (save.Id, save.Name, save.Host, save.Password, save.PrivateKey, save.KeyPassphrase, save.ClientSecret));
        Assert.Equal("site", host.Sent[2].GetPayload<SiteIdRequest>().Id);
        Assert.False(saved.Ok);
        Assert.Equal(SiteProblems.Off, saved.Problem);
        Assert.Equal("Unavailable", removed.Message);
        Assert.Equal(0, host.Opened);
        Assert.False(host.Disposed);
    }

    [Fact]
    public async Task StartAndTransferControlsUseSelectedHostAndKeepProblems()
    {
        var host = new FakeHostConnection
        {
            Reply = request => request.Type == SitesMessageTypes.TransferList
                ? IpcMessage.Create(SitesMessageTypes.TransferListResult,
                    new SiteTransfersDto { Transfers = [new SiteTransferDto { Id = "batch" }] }, request.Id)
                : IpcMessage.Create(SitesMessageTypes.TransferResult,
                    new SiteTransferResultDto { Ok = false, Problem = SiteProblems.HostKeyChanged, Message = "Changed" }, request.Id)
        };
        var client = new RemoteFilesClient(host);
        var start = await client.StartTransferAsync(new SiteTransferStartRequest
        {
            SiteId = "site", Direction = "download", RemoteFolder = "/remote", LocalFolder = "/host",
            Conflict = "keepBoth", Names = [new SiteTransferNameDto { Name = "folder", IsFolder = true }]
        });
        Assert.Equal("batch", Assert.Single((await client.ListTransfersAsync()).Transfers).Id);
        await client.GetTransferAsync("batch");
        await client.PauseTransferAsync("batch");
        await client.ResumeTransferAsync("batch");
        await client.CancelTransferAsync("batch");
        await client.RetryTransferAsync("batch");
        await client.ClearTransferAsync("batch");

        Assert.Equal(SiteProblems.HostKeyChanged, start.Problem);
        Assert.Equal("Changed", start.Message);
        var sent = host.Sent[0].GetPayload<SiteTransferStartRequest>();
        Assert.Equal(("site", "download", "/remote", "/host", "keepBoth"),
            (sent.SiteId, sent.Direction, sent.RemoteFolder, sent.LocalFolder, sent.Conflict));
        Assert.True(Assert.Single(sent.Names).IsFolder);
        Assert.Equal(new[] { SitesMessageTypes.TransferGet, SitesMessageTypes.TransferPause, SitesMessageTypes.TransferResume,
            SitesMessageTypes.TransferCancel, SitesMessageTypes.TransferRetry, SitesMessageTypes.TransferClear },
            host.Sent.Skip(2).Select(x => x.Type));
        Assert.All(host.Sent.Skip(2), x => Assert.Equal("batch", x.GetPayload<SiteTransferIdRequest>().Id));
        Assert.Equal(0, host.Opened);
        Assert.False(host.Disposed);
    }

    [Fact]
    public void EmptyIdsNeverReachHostAndClearCannotBecomeBulkClear()
    {
        var host = new FakeHostConnection();
        var client = new RemoteFilesClient(host);
        Assert.Throws<ArgumentException>(() => { _ = client.ClearTransferAsync(" "); });
        Assert.Throws<ArgumentException>(() => { _ = client.PauseTransferAsync(""); });
        Assert.Throws<ArgumentException>(() => { _ = client.GetTransferAsync(""); });
        Assert.Throws<ArgumentException>(() => { _ = client.RemoveAsync(""); });
        Assert.Empty(host.Sent);
    }

    [Fact]
    public async Task MutationRefusalMalformedReplyCancellationAndDisconnectDoNotFallback()
    {
        var host = new FakeHostConnection();
        var client = new RemoteFilesClient(host);
        host.Reply = request => IpcMessage.Create(MessageTypes.HostActionResult,
            new HostActionResult { Ok = false, Message = "Forbidden" }, request.Id);
        Assert.Equal("Forbidden", (await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetEnabledAsync(true))).Message);
        host.Reply = request => IpcMessage.Create("wrong", new EmptyPayload(), request.Id);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.RemoveAsync("site"));
        host.Reply = request => IpcMessage.Create(SitesMessageTypes.TransferResult, replyTo: request.Id);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.StartTransferAsync(new SiteTransferStartRequest()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PauseTransferAsync("batch", cancellation.Token));
        host.Reply = _ => throw new IOException("Disconnected");
        await Assert.ThrowsAsync<IOException>(() => client.ResumeTransferAsync("batch"));
        Assert.Equal(4, host.Sent.Count);
        Assert.Equal(0, host.Opened);
        Assert.False(host.Disposed);
    }
}
