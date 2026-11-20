using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class RemoteFilesClientTests
{
    [Fact]
    public async Task DeviceWriteSendsOrderedPiecesAndFinishesOnlyAtTheDeclaredSize()
    {
        var bytes = new byte[SiteDeviceLimits.ChunkBytes + 3];
        bytes[SiteDeviceLimits.ChunkBytes] = 42;
        var host = new FakeHostConnection
        {
            Reply = request => request.Type == SitesMessageTypes.DeviceCreateOpen
                ? IpcMessage.Create(SitesMessageTypes.DeviceStream, new SiteDeviceStreamDto { Ok = true, StreamId = "upload" }, request.Id)
                : IpcMessage.Create(SitesMessageTypes.DeviceChunk, new SiteDeviceChunkDto
                {
                    Ok = true, Done = request.GetPayload<SiteDeviceChunkRequest>().Last
                }, request.Id)
        };
        using var source = new MemoryStream(bytes);
        var result = await new RemoteFilesClient(host).WriteDeviceFileAsync(
            new SiteDeviceOpenRequest { Id = "site", Folder = "/target", Name = "file.bin", Size = bytes.Length, Listing = "confirmed" }, source);

        Assert.True(result.Ok);
        Assert.Equal(new[] { SitesMessageTypes.DeviceCreateOpen, SitesMessageTypes.DeviceWrite, SitesMessageTypes.DeviceWrite }, host.Sent.Select(r => r.Type));
        Assert.False(host.Sent[0].GetPayload<SiteDeviceOpenRequest>().Replace);
        var first = host.Sent[1].GetPayload<SiteDeviceChunkRequest>();
        var last = host.Sent[2].GetPayload<SiteDeviceChunkRequest>();
        Assert.False(first.Last);
        Assert.Equal(SiteDeviceLimits.ChunkBytes, first.Data!.Length);
        Assert.True(last.Last);
        Assert.Equal("upload", last.StreamId);
        Assert.Equal(new byte[] { 42, 0, 0 }, last.Data);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task DeviceWriteClosesWithoutSendingAFinalChunkIfTheSizeChanged(int size)
    {
        var host = new FakeHostConnection
        {
            Reply = request => request.Type == SitesMessageTypes.DeviceCreateOpen
                ? IpcMessage.Create(SitesMessageTypes.DeviceStream, new SiteDeviceStreamDto { Ok = true, StreamId = "upload" }, request.Id)
                : IpcMessage.Create(SitesMessageTypes.Result, new SiteResultDto { Ok = true }, request.Id)
        };
        using var source = new MemoryStream(new byte[3]);
        await Assert.ThrowsAsync(size < source.Length ? typeof(InvalidDataException) : typeof(EndOfStreamException),
            () => new RemoteFilesClient(host).WriteDeviceFileAsync(
                new SiteDeviceOpenRequest { Id = "site", Folder = "/", Name = "file", Size = size, Listing = "confirmed" }, source));
        Assert.Equal(new[] { SitesMessageTypes.DeviceCreateOpen, SitesMessageTypes.DeviceClose }, host.Sent.Select(r => r.Type));
        Assert.Equal("upload", host.Sent[1].GetPayload<SiteDeviceChunkRequest>().StreamId);
    }
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
            Id = "site", Path = "/folder", Offset = 10, Limit = 20, Refresh = true, Listing = "page-token",
            ChronologicalFiles = true, IncludeFolderCounts = true
        });

        var sent = Assert.Single(host.Sent);
        Assert.Equal(SitesMessageTypes.Browse, sent.Type);
        var request = sent.GetPayload<SiteBrowseRequest>();
        Assert.Equal(("site", "/folder", 10, 20, true, "page-token"),
            (request.Id, request.Path, request.Offset, request.Limit, request.Refresh, request.Listing));
        Assert.True(request.ChronologicalFiles);
        Assert.True(request.IncludeFolderCounts);
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
                Items = [new SiteItemDto { Name = "folder", IsFolder = true, ChildCount = 12 },
                    new SiteItemDto { Name = "file", Size = 42 }]
            }, request.Id)
        };
        var result = await new RemoteFilesClient(host).BrowseAsync(new SiteBrowseRequest { Id = "site" });
        Assert.True(result.Ok);
        Assert.Equal(("/folder", 5, 7, "listing"), (result.Path, result.Offset, result.Total, result.Listing));
        Assert.Equal(42, Assert.Single(result.Items, item => !item.IsFolder).Size);
        Assert.Equal(12, Assert.Single(result.Items, item => item.IsFolder).ChildCount);
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
    public async Task FilesOnlyDeletionNeverFallsBackToRecursiveDeletion()
    {
        var host = new FakeHostConnection
        {
            Reply = request => IpcMessage.Create(MessageTypes.HostActionResult,
                new HostActionResult { Ok = false, Message = "Unknown command" }, request.Id)
        };
        var request = new SiteChangeRequest { Id = "site", Path = "/pub", Items = [new() { Name = "a.txt" }] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RemoteFilesClient(host).DeleteFilesAsync(request));
        var sent = Assert.Single(host.Sent);
        Assert.Equal(SitesMessageTypes.DeleteFiles, sent.Type);
        Assert.Equal("a.txt", Assert.Single(sent.GetPayload<SiteChangeRequest>().Items).Name);
    }

    [Fact]
    public async Task CloudSignInUsesTheSelectedConnectionAndExplicitTokenAndCancellation()
    {
        var host = new FakeHostConnection
        {
            Reply = request => request.Type == SitesMessageTypes.SignIn
                ? IpcMessage.Create(SitesMessageTypes.SignInResult, new SiteSignInResultDto { Ok = true }, request.Id)
                : IpcMessage.Create(SitesMessageTypes.Result, new SiteResultDto { Ok = true }, request.Id)
        };
        var client = new RemoteFilesClient(host);
        await client.SignInSiteAsync("cloud", "synthetic-test-value");
        await client.CancelSiteSignInAsync("cloud");
        Assert.Equal(new[] { SitesMessageTypes.SignIn, SitesMessageTypes.SignInCancel }, host.Sent.Select(message => message.Type));
        Assert.Equal("synthetic-test-value", host.Sent[0].GetPayload<SiteSignInRequest>().Token);
        Assert.Equal("cloud", host.Sent[1].GetPayload<SiteIdRequest>().Id);
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
