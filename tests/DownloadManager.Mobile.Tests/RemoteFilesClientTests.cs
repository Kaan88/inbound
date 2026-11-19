using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class RemoteFilesClientTests
{
    [Fact]
    public async Task StatusAndListUseTheProvidedConnectionAndTypedPayloads()
    {
        var host = new FakeHostConnection();
        host.Reply = request => request.Type switch
        {
            SitesMessageTypes.Get => IpcMessage.Create(SitesMessageTypes.Status, new SitesStatusDto { Enabled = true, Rclone = "ready" }, request.Id),
            SitesMessageTypes.ListRequest => IpcMessage.Create(SitesMessageTypes.List, new SitesListDto { Sites = [new SiteDto { Id = "one", Name = "Server" }] }, request.Id),
            _ => throw new InvalidOperationException(),
        };
        var client = new RemoteFilesClient(host);

        var status = await client.GetStatusAsync();
        var list = await client.ListAsync();

        Assert.True(status.Enabled);
        Assert.Equal("ready", status.Rclone);
        Assert.Equal("one", Assert.Single(list.Sites).Id);
        Assert.Equal(new[] { SitesMessageTypes.Get, SitesMessageTypes.ListRequest }, host.Sent.Select(x => x.Type));
        Assert.All(host.Sent, message => Assert.Equal(JsonValueKind.Object, message.Payload!.Value.ValueKind));
        Assert.Equal(0, host.Opened);
        Assert.False(host.Disposed);
    }

    [Fact]
    public async Task BrowsePreservesPagingAndHostProblems()
    {
        var host = new FakeHostConnection
        {
            Reply = request => IpcMessage.Create(SitesMessageTypes.BrowseResult,
                new SiteBrowseResultDto { Ok = false, Problem = SiteProblems.ListingChanged, Message = "Start again" }, request.Id)
        };
        var client = new RemoteFilesClient(host);
        var result = await client.BrowseAsync(new SiteBrowseRequest
        {
            Id = "site", Path = "/folder", Offset = 10, Limit = 20, Refresh = true, Listing = "page-token"
        });

        var sent = Assert.Single(host.Sent);
        Assert.Equal(SitesMessageTypes.Browse, sent.Type);
        var request = sent.GetPayload<SiteBrowseRequest>();
        Assert.Equal(("site", "/folder", 10, 20, true, "page-token"),
            (request.Id, request.Path, request.Offset, request.Limit, request.Refresh, request.Listing));
        Assert.False(result.Ok);
        Assert.Equal(SiteProblems.ListingChanged, result.Problem);
        Assert.Equal("Start again", result.Message);
    }

    [Fact]
    public async Task BrowseReturnsPageAndListingToken()
    {
        var host = new FakeHostConnection
        {
            Reply = request => IpcMessage.Create(SitesMessageTypes.BrowseResult, new SiteBrowseResultDto
            {
                Ok = true, Path = "/folder", Offset = 5, Total = 7, Listing = "listing",
                Items = [new SiteItemDto { Name = "file", Size = 42 }]
            }, request.Id)
        };
        var result = await new RemoteFilesClient(host).BrowseAsync(new SiteBrowseRequest { Id = "site" });
        Assert.True(result.Ok);
        Assert.Equal(("/folder", 5, 7, "listing"), (result.Path, result.Offset, result.Total, result.Listing));
        Assert.Equal(42, Assert.Single(result.Items).Size);
    }

    [Fact]
    public async Task RefusalAndUnexpectedOrMissingReplyAreNotEmptyResults()
    {
        var host = new FakeHostConnection();
        var client = new RemoteFilesClient(host);
        host.Reply = request => IpcMessage.Create(MessageTypes.HostActionResult,
            new HostActionResult { Ok = false, Message = "Not allowed" }, request.Id);
        Assert.Equal("Not allowed", (await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetStatusAsync())).Message);

        host.Reply = request => IpcMessage.Create("unexpected", new EmptyPayload(), request.Id);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync());
        host.Reply = request => IpcMessage.Create(SitesMessageTypes.Status, replyTo: request.Id);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetStatusAsync());
    }

    [Fact]
    public async Task SafeDownloadDoesNotFallBackWhenTheHostDoesNotRecognizeTheCommand()
    {
        var host = new FakeHostConnection
        {
            Reply = request => IpcMessage.Create(MessageTypes.HostActionResult,
                new HostActionResult { Ok = false, Message = "Unknown command" }, request.Id)
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RemoteFilesClient(host).StartTransferAsync(new SiteTransferStartRequest { SafeSingleDownload = true }));
        Assert.Equal("Unknown command", error.Message);
        Assert.Equal(SitesMessageTypes.SafeDownloadStart, Assert.Single(host.Sent).Type);
    }

    [Fact]
    public async Task CancellationAndTransportFailurePropagateWithoutFallback()
    {
        var host = new FakeHostConnection { Reply = _ => throw new IOException("Disconnected") };
        var client = new RemoteFilesClient(host);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListAsync(cancelled.Token));
        Assert.Empty(host.Sent);
        await Assert.ThrowsAsync<IOException>(() => client.ListAsync());
        Assert.Single(host.Sent);
    }

    [Fact]
    public async Task CancellationIsForwardedToAnInFlightRequest()
    {
        var host = new WaitingHost();
        using var cancellation = new CancellationTokenSource();
        var pending = new RemoteFilesClient(host).BrowseAsync(new SiteBrowseRequest(), cancellation.Token);
        Assert.Equal(cancellation.Token, host.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class WaitingHost : IHostConnection
    {
        public CancellationToken Token { get; private set; }
        public Task OpenAsync(Action<IpcMessage> onFrame, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<IpcMessage> RequestAsync(IpcMessage request, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
