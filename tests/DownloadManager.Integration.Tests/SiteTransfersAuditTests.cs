using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Runtime.Sites;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.10 follow-ups (4) and (7) with a stand-in rclone: a hung copy is retried and its rclone job stopped, and the
/// connection wait schedule totals about ten minutes. No rclone process, no real ten-minute waits.
/// </summary>
public sealed class SiteTransfersAuditTests : IAsyncLifetime
{
    private const string SiteId = "0123456789abcdef0123456789abcdef";
    private static int _nextPid = 1000;
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "site-transfers-audit-" + Guid.NewGuid().ToString("N"));

    private string State => Path.Combine(_root, "state");
    private string Local => Path.Combine(_root, "local");

    // The production schedule (about ten minutes) is too slow to run; a 7-step 20 ms schedule stands in for it.
    private static readonly TimeSpan[] ShortDelays = Enumerable.Repeat(TimeSpan.FromMilliseconds(20), 7).ToArray();

    private static readonly SiteRecord Site = new()
    {
        Id = SiteId,
        Name = "Fake",
        Kind = SiteKinds.Ftp,
        Host = "127.0.0.1",
        Port = 21,
        User = "inbound",
        AuthMethod = SiteAuthMethods.Password,
    };

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(State);
        Directory.CreateDirectory(Local);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public void TheDefaultConnectionScheduleTotalsAboutTenMinutes()
    {
        Assert.Equal(
            new[] { 5d, 15d, 30d, 60d, 120d, 180d, 180d },
            SiteTransfers.DefaultConnectionDelays.Select(d => d.TotalSeconds));
        Assert.Equal(590, (int)SiteTransfers.DefaultConnectionDelays.Sum(d => d.TotalSeconds));
    }

    [Fact]
    public async Task AHungCopyIsRetriedAndItsJobIsStopped()
    {
        // rclone accepts the copy but never answers: the answer-token timeout fires and the file must be retried, not fail.
        var handler = new FakeHandler(async (command, _, ct) =>
        {
            if (command == "operations/copyfile") await Task.Delay(Timeout.Infinite, ct);
            return new JsonObject();
        });

        await using var harness = OpenHarness(handler, copyAnswerTimeout: TimeSpan.FromMilliseconds(50));
        var started = await StartAsync(harness.Transfers);
        var id = started.Transfer!.Id;

        var finished = await WaitForAsync(harness.Transfers, id, t => SiteTransferStates.IsFinished(t.State), TimeSpan.FromSeconds(10));
        Assert.Equal(SiteTransferStates.Failed, finished.State);
        Assert.Contains("failed connections in a row", finished.Message);
        Assert.Equal(0, finished.FilesFailed);

        // The hung copy was a connection failure, not a permanent one: no try was spent.
        var item = Assert.Single(harness.Transfers.Get(id).Items!);
        Assert.Equal(SiteTransferItemStates.Waiting, item.State);
        Assert.Equal(0, item.Tries);

        // The job rclone may have started (no id is known) was stopped by its group before the file was retried.
        Assert.True(handler.StopGroupCalls > 0, "job/stopgroup was never called for a hung copy.");
    }

    [Fact]
    public async Task AServerBackWithinTheWaitContinuesTheBatch()
    {
        var statuses = 0;
        var handler = new FakeHandler((command, _, _) =>
        {
            if (command == "operations/copyfile") return Task.FromResult(new JsonObject { ["jobid"] = 1 });
            if (command == "job/status")
            {
                statuses++;
                return Task.FromResult(statuses <= 2
                    ? new JsonObject { ["finished"] = true, ["success"] = false, ["error"] = "dial tcp 127.0.0.1:22: connect: connection refused" }
                    : new JsonObject { ["finished"] = true, ["success"] = true });
            }

            return Task.FromResult(new JsonObject());
        });

        await using var harness = OpenHarness(handler);
        var started = await StartAsync(harness.Transfers);

        var done = await WaitForAsync(harness.Transfers, started.Transfer!.Id, t => SiteTransferStates.IsFinished(t.State), TimeSpan.FromSeconds(10));
        Assert.Equal(SiteTransferStates.Done, done.State);
        Assert.Equal((1, 0), (done.FilesDone, done.FilesFailed));
        Assert.True(statuses >= 3, "the batch should have waited and tried again until the server answered");
    }

    private async Task<SiteTransferResultDto> StartAsync(SiteTransfers transfers) =>
        await transfers.StartSyncAsync(Site, SiteTransferDirections.Download, "/pub", Local,
            [new SiteTransferItem { Path = "a.txt", Size = 3 }]);

    private Harness OpenHarness(FakeHandler handler, TimeSpan? copyAnswerTimeout = null)
    {
        var supervisor = new RcloneSupervisor(
            _ => Task.FromResult<IRcloneInstance>(new FakeInstance(handler, Interlocked.Increment(ref _nextPid))),
            null, null, TimeProvider.System);
        var transfers = new SiteTransfers(State, (_, _) => Task.FromResult<SiteRecord?>(Site), ct => supervisor.AcquireAsync(ct),
            () => true, () => 1, _ => { }, connectionDelays: ShortDelays, copyAnswerTimeout: copyAnswerTimeout);
        return new Harness(transfers, supervisor);
    }

    private static async Task<SiteTransferDto> WaitForAsync(SiteTransfers transfers, string id, Func<SiteTransferDto, bool> condition, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            var dto = transfers.List().Transfers.SingleOrDefault(t => t.Id == id);
            if (dto is not null && condition(dto)) return dto;
            await Task.Delay(10);
        }

        throw new TimeoutException("The transfer did not reach the condition.");
    }

    private sealed class Harness(SiteTransfers transfers, RcloneSupervisor supervisor) : IAsyncDisposable
    {
        public SiteTransfers Transfers { get; } = transfers;

        public async ValueTask DisposeAsync()
        {
            await Transfers.DisposeAsync();
            await supervisor.DisposeAsync();
        }
    }

    private sealed class FakeInstance(FakeHandler handler, int pid) : IRcloneInstance
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RcloneClient Client { get; } = new(new Uri("http://127.0.0.1:9/"), "user", "rc-pass", handler: handler);
        public int ProcessId { get; } = pid;
        public Task Exited => _exited.Task;
        public Task<bool> StopAsync(TimeSpan grace) { _exited.TrySetResult(); return Task.FromResult(true); }
        public ValueTask DisposeAsync() { Client.Dispose(); return ValueTask.CompletedTask; }
    }

    /// <summary>Answers each rc command; records <c>job/stopgroup</c>. Throws (on the token) where a script hangs.</summary>
    private sealed class FakeHandler(Func<string, JsonObject, CancellationToken, Task<JsonObject>> respond) : HttpMessageHandler
    {
        public int StopGroupCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var command = request.RequestUri!.AbsolutePath.TrimStart('/');
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            JsonObject input;
            try { input = JsonNode.Parse(body) as JsonObject ?? new JsonObject(); }
            catch (System.Text.Json.JsonException) { input = new JsonObject(); }

            if (command == "job/stopgroup") StopGroupCalls++;
            var answer = await respond(command, input, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
