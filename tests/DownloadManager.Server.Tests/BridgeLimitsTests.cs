using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Server.Tests;

/// <summary>Audit 3 M3: the popup's job list stays under the browsers' 1 MB native-message limit.</summary>
public sealed class BridgeLimitsTests
{
    private static SnapshotDto Snapshot(int count)
    {
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        return new SnapshotDto
        {
            // Oldest first, as the Host lists them, with two sharing a time.
            Jobs = Enumerable.Range(0, count)
                .Select(i => new JobDto { Id = Guid.NewGuid(), FileName = $"f{i}.zip", CreatedAt = start.AddMinutes(i == count - 1 ? i - 1 : i) })
                .ToList(),
        };
    }

    [Fact]
    public void JobsQueryWithALimitListsTheNewestOnly()
    {
        var snapshot = Snapshot(50);
        var limited = HostCommandDispatcher.LimitJobs(snapshot,
            IpcMessage.Create(MessageTypes.JobsQuery, new JobsQueryRequest { Limit = 20 }));
        Assert.Equal(20, limited.Jobs.Count);
        // Newest first; the two with the same time keep the list's order.
        Assert.Equal(["f48.zip", "f49.zip", "f47.zip"], limited.Jobs.Take(3).Select(job => job.FileName));
        Assert.DoesNotContain(limited.Jobs, job => job.FileName == "f29.zip");
    }

    [Fact]
    public void JobsQueryWithoutALimitAndTheSnapshotListAll()
    {
        Assert.Equal(50, HostCommandDispatcher.LimitJobs(Snapshot(50), IpcMessage.Create(MessageTypes.JobsQuery)).Jobs.Count);
        Assert.Equal(50, HostCommandDispatcher.LimitJobs(Snapshot(50), IpcMessage.Create(MessageTypes.JobsQuery, new { })).Jobs.Count);
        Assert.Equal(50, HostCommandDispatcher.LimitJobs(Snapshot(50),
            IpcMessage.Create(MessageTypes.JobsQuery, new JobsQueryRequest { Limit = 0 })).Jobs.Count);
        Assert.Equal(50, HostCommandDispatcher.LimitJobs(Snapshot(50),
            IpcMessage.Create(MessageTypes.JobsSnapshot, new JobsQueryRequest { Limit = 5 })).Jobs.Count);
        Assert.Equal(50, HostCommandDispatcher.LimitJobs(Snapshot(50),
            IpcMessage.Create(MessageTypes.JobsQuery, new { limit = "many" })).Jobs.Count);
    }

    [Fact]
    public void Audit3L7_TheServerLogDeletesOldFilesAndStopsADaysFileAtItsLimit()
    {
        var work = ServerHarness.NewWork();
        try
        {
            var old = Path.Combine(work, "server-20200101.log");
            var otherName = Path.Combine(work, "web-bridge-20200101.log");
            File.WriteAllText(old, "x");
            File.WriteAllText(otherName, "x");
            var log = new ServerLog(work, console: false) { MaxDailyBytes = 500 };
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(otherName));

            for (var i = 0; i < 40; i++) log.Info($"line {i} " + new string('b', 40));
            var today = Directory.GetFiles(work, "server-*.log").Single();
            var lines = File.ReadAllLines(today);
            Assert.Contains(DownloadManager.Persistence.LogFileRetention.CapReachedLine, lines[^1]);
            Assert.True(lines.Length < 15);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void AFrameOverOneMegabyteBecomesAnErrorForItsRequestAndAnEventIsDropped()
    {
        var small = IpcMessage.Create(MessageTypes.JobsSnapshot, Snapshot(3), replyTo: "r1");
        Assert.Same(small, NativeBridgePolicy.FitForBrowser(small, out _));

        var big = Snapshot(1);
        big.Jobs[0].FileName = new string('a', NativeBridgePolicy.BrowserMaxMessageBytes);
        var reply = IpcMessage.Create(MessageTypes.JobsSnapshot, big, replyTo: "r2");
        var fitted = NativeBridgePolicy.FitForBrowser(reply, out var size);
        Assert.True(size > NativeBridgePolicy.BrowserMaxMessageBytes);
        Assert.NotNull(fitted);
        Assert.Equal(MessageTypes.HandoffError, fitted!.Type);
        Assert.Equal("r2", fitted.ReplyTo);
        Assert.Equal("message_too_large", fitted.Payload!.Value.GetProperty("errorCode").GetString());
        Assert.Contains("1 MB", fitted.Payload!.Value.GetProperty("error").GetString());
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(fitted, IpcJson.Options).Length < 1024);

        var evt = IpcMessage.Create(MessageTypes.JobsSnapshot, big);
        Assert.Null(NativeBridgePolicy.FitForBrowser(evt, out _));
    }
}
