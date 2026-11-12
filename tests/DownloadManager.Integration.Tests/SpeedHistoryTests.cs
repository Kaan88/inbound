using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Runtime;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>UI.R24: the Host keeps the last hour of total download speed, with no timer while idle.</summary>
public sealed class SpeedHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADownloadsOwnFiguresStartWhenItStartedAndHoldThenDropToZero()
    {
        var history = new SpeedHistory(T0);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Null(history.Read(a, T0.AddSeconds(1)));
        for (var s = 10; s <= 12; s++)
        {
            history.Report(a, 1000, T0.AddSeconds(s));
            history.Report(b, 5000, T0.AddSeconds(s));
        }

        // Nothing after second 12: second 13 and 14 hold the last figure, then 0.
        var read = history.Read(a, T0.AddSeconds(16))!;
        Assert.Equal(a, read.JobId);
        Assert.Equal(T0.AddSeconds(10), read.Start);
        Assert.Equal([1000L, 1000, 1000, 1000, 1000, 0, 0], read.BytesPerSecond);
        // The total is both downloads.
        Assert.Equal(6000, history.Read(T0.AddSeconds(16)).BytesPerSecond[11]);
    }

    [Fact]
    public void AStoppedDownloadsFiguresAreLetGo()
    {
        var history = new SpeedHistory(T0);
        var a = Guid.NewGuid();
        history.Report(a, 1000, T0.AddSeconds(1));
        history.Stopped(a, T0.AddSeconds(2));
        Assert.Null(history.Read(a, T0.AddSeconds(3)));
        // Running again starts a new track.
        history.Report(a, 2000, T0.AddSeconds(5));
        Assert.Equal(T0.AddSeconds(5), history.Read(a, T0.AddSeconds(5))!.Start);
    }

    [Fact]
    public void AnIdleHourReadsAsZerosFromTheTimesAlone()
    {
        var history = new SpeedHistory(T0);
        var read = history.Read(T0.AddMinutes(90));
        Assert.Equal(SpeedHistory.Seconds, read.BytesPerSecond.Count);
        Assert.Equal(T0.AddMinutes(90).AddSeconds(-(SpeedHistory.Seconds - 1)), read.Start);
        Assert.All(read.BytesPerSecond, value => Assert.Equal(0, value));
    }

    [Fact]
    public void ItStartsAtTheHostsStartNotAnHourBack()
    {
        var history = new SpeedHistory(T0);
        var read = history.Read(T0.AddSeconds(9.5));
        Assert.Equal(T0, read.Start);
        Assert.Equal(10, read.BytesPerSecond.Count);
    }

    [Fact]
    public void RunningDownloadsAddUpAndStoppingEndsTheirShare()
    {
        var history = new SpeedHistory(T0);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        for (var s = 1; s <= 5; s++)
        {
            history.Report(a, 1000, T0.AddSeconds(s).AddMilliseconds(100));
            if (s >= 3) history.Report(b, 500, T0.AddSeconds(s).AddMilliseconds(400));
        }

        history.Stopped(a, T0.AddSeconds(6).AddMilliseconds(200));
        history.Report(b, 500, T0.AddSeconds(6).AddMilliseconds(500));
        history.Stopped(b, T0.AddSeconds(7));

        var values = history.Read(T0.AddSeconds(20)).BytesPerSecond;
        Assert.Equal(new long[] { 0, 1000, 1000, 1500, 1500, 1500, 500 }, values.Take(7));
        Assert.All(values.Skip(7), value => Assert.Equal(0, value));
    }

    [Fact]
    public void AShortSilenceHoldsTheFigureAndALongOneIsZero()
    {
        var history = new SpeedHistory(T0);
        var job = Guid.NewGuid();
        history.Report(job, 2000, T0.AddSeconds(1));
        // Two seconds without a report keep the figure; after that nothing arrived.
        history.Report(job, 2000, T0.AddSeconds(8));
        var values = history.Read(T0.AddSeconds(8)).BytesPerSecond;
        Assert.Equal(new long[] { 0, 2000, 2000, 2000, 0, 0, 0, 0, 2000 }, values);
    }

    [Fact]
    public void ADownloadThatGoesQuietStopsCountingWhileAnotherKeepsReporting()
    {
        // A runner that hangs after a pause never reports or stops; its last figure must not stay in the total.
        var history = new SpeedHistory(T0);
        var hung = Guid.NewGuid();
        var busy = Guid.NewGuid();
        history.Report(hung, 1000, T0.AddSeconds(1));
        for (var s = 1; s <= 8; s++) history.Report(busy, 500, T0.AddSeconds(s).AddMilliseconds(500));
        var values = history.Read(T0.AddSeconds(8)).BytesPerSecond;
        Assert.Equal(new long[] { 0, 1500, 1500, 1500, 500, 500, 500, 500, 500 }, values);
    }

    [Fact]
    public void AReportAfterAQuietSpellCountsAgain()
    {
        var history = new SpeedHistory(T0);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        history.Report(a, 1000, T0.AddSeconds(1));
        history.Report(b, 500, T0.AddSeconds(1));
        history.Report(b, 500, T0.AddSeconds(5));
        history.Report(a, 1000, T0.AddSeconds(6));
        var values = history.Read(T0.AddSeconds(6)).BytesPerSecond;
        Assert.Equal(new long[] { 0, 1500, 1500, 1500, 0, 500, 1500 }, values);
    }

    [Fact]
    public void OnlyTheLastHourIsKeptAcrossTheRing()
    {
        var history = new SpeedHistory(T0);
        var job = Guid.NewGuid();
        for (var s = 0; s < SpeedHistory.Seconds + 600; s++) history.Report(job, s, T0.AddSeconds(s));
        var now = T0.AddSeconds(SpeedHistory.Seconds + 599);
        var read = history.Read(now);
        Assert.Equal(SpeedHistory.Seconds, read.BytesPerSecond.Count);
        Assert.Equal(600, read.BytesPerSecond[0]);
        Assert.Equal(SpeedHistory.Seconds + 599, read.BytesPerSecond[^1]);
    }

    [Fact]
    public void AClockThatGoesBackWritesNothing()
    {
        var history = new SpeedHistory(T0);
        var job = Guid.NewGuid();
        history.Report(job, 100, T0.AddSeconds(5));
        history.Report(job, 900, T0.AddSeconds(2));
        var values = history.Read(T0.AddSeconds(5)).BytesPerSecond;
        Assert.Equal(new long[] { 0, 0, 0, 0, 0, 100 }, values);
    }

    [Fact]
    public void AClockSetBackCarriesOnInsteadOfStandingStill()
    {
        var history = new SpeedHistory(T0);
        var job = Guid.NewGuid();
        history.Report(job, 500, T0.AddSeconds(99));
        history.Report(job, 500, T0.AddSeconds(100));

        // A minute back, as a time sync might: the next reports still count, from where the history was.
        history.Report(job, 700, T0.AddSeconds(40));
        history.Report(job, 700, T0.AddSeconds(41));
        var read = history.Read(T0.AddSeconds(41));

        Assert.Equal(102, read.BytesPerSecond.Count);
        Assert.Equal(500, read.BytesPerSecond[99]);
        Assert.Equal(700, read.BytesPerSecond[^1]);
        // The newest second is the clock's now.
        Assert.Equal(T0.AddSeconds(41), read.Start.AddSeconds(read.BytesPerSecond.Count - 1));
    }

    [Fact]
    public void NonsenseRatesCountAsZero()
    {
        var history = new SpeedHistory(T0);
        history.Report(Guid.NewGuid(), double.NaN, T0.AddSeconds(1));
        history.Report(Guid.NewGuid(), -5, T0.AddSeconds(1));
        history.Report(Guid.NewGuid(), double.PositiveInfinity, T0.AddSeconds(1));
        Assert.All(history.Read(T0.AddSeconds(1)).BytesPerSecond, value => Assert.Equal(0, value));
    }

    // ---- T8: the torrents' upload beside the total ----

    [Fact]
    public void UploadIsLeftOutUntilATorrentSendsSomething()
    {
        var history = new SpeedHistory(T0);
        history.Report(Guid.NewGuid(), 1000, T0.AddSeconds(1));
        Assert.Null(history.Read(T0.AddSeconds(2)).UploadBytesPerSecond);
        // A torrent that reports 0 still counts: the list is there, all zeros.
        history.ReportUpload(Guid.NewGuid(), 0, T0.AddSeconds(3));
        var read = history.Read(T0.AddSeconds(3));
        Assert.Equal(read.BytesPerSecond.Count, read.UploadBytesPerSecond!.Count);
        Assert.All(read.UploadBytesPerSecond, value => Assert.Equal(0, value));
    }

    [Fact]
    public void UploadsAddUpBesideTheDownloadsWithTheSameStart()
    {
        var history = new SpeedHistory(T0);
        var seeding = Guid.NewGuid();
        var downloading = Guid.NewGuid();
        for (var s = 1; s <= 3; s++)
        {
            history.ReportUpload(seeding, 2000, T0.AddSeconds(s));
            history.ReportUpload(downloading, 500, T0.AddSeconds(s).AddMilliseconds(300));
            history.Report(downloading, 9000, T0.AddSeconds(s).AddMilliseconds(300));
        }

        var read = history.Read(T0.AddSeconds(3));
        Assert.Equal(T0, read.Start);
        Assert.Equal(new long[] { 0, 9000, 9000, 9000 }, read.BytesPerSecond);
        Assert.Equal(new long[] { 0, 2500, 2500, 2500 }, read.UploadBytesPerSecond!);
        // One download's own figures carry no upload list.
        Assert.Null(history.Read(downloading, T0.AddSeconds(3))!.UploadBytesPerSecond);
    }

    [Fact]
    public void AnUploadFigureHoldsAcrossTheWorkersSlowPaceThenDropsToZero()
    {
        // While no window watches torrents the worker reports every 5 seconds; the figure holds across that.
        var history = new SpeedHistory(T0);
        var torrent = Guid.NewGuid();
        history.ReportUpload(torrent, 1000, T0.AddSeconds(1));
        history.ReportUpload(torrent, 1000, T0.AddSeconds(6));
        var values = history.Read(T0.AddSeconds(16)).UploadBytesPerSecond!;
        Assert.Equal(new long[] { 0, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 0, 0, 0, 0 }, values);
        Assert.Equal(6, SpeedHistory.UploadHoldSeconds);
    }

    [Fact]
    public void StoppingAnUploadEndsItsShareAndLeavesTheDownloadAlone()
    {
        var history = new SpeedHistory(T0);
        var torrent = Guid.NewGuid();
        history.Report(torrent, 4000, T0.AddSeconds(1));
        history.ReportUpload(torrent, 1000, T0.AddSeconds(1));
        history.UploadStopped(torrent, T0.AddSeconds(2));
        var read = history.Read(T0.AddSeconds(3));
        Assert.Equal(new long[] { 0, 1000, 0, 0 }, read.UploadBytesPerSecond!);
        Assert.Equal(new long[] { 0, 4000, 4000, 4000 }, read.BytesPerSecond);
        // Stopping one that never sent changes nothing and adds no list.
        var quiet = new SpeedHistory(T0);
        quiet.UploadStopped(Guid.NewGuid(), T0.AddSeconds(1));
        Assert.Null(quiet.Read(T0.AddSeconds(1)).UploadBytesPerSecond);
    }

    [Fact]
    public void NonsenseUploadRatesCountAsZero()
    {
        var history = new SpeedHistory(T0);
        history.ReportUpload(Guid.NewGuid(), double.NaN, T0.AddSeconds(1));
        history.ReportUpload(Guid.NewGuid(), -5, T0.AddSeconds(1));
        Assert.All(history.Read(T0.AddSeconds(1)).UploadBytesPerSecond!, value => Assert.Equal(0, value));
    }
}

/// <summary>UI.R24 through the service.</summary>
public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task TheServiceRecordsTheSpeedOfARunningDownload()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var job = await AddSlowAsync(ctx, "history.bin", 2 * Mb, 951, 512 * 1024, 1);
        await ctx.WaitFor(job.Id, j => j.State == "downloading" && j.ReceivedBytes > 512 * 1024, TimeSpan.FromSeconds(30));
        var during = ctx.Service.GetSpeedHistory();
        Assert.Equal(1, during.IntervalSeconds);
        Assert.InRange(during.BytesPerSecond.Count, 1, SpeedHistory.Seconds);
        Assert.Contains(during.BytesPerSecond.TakeLast(5), value => value > 64 * 1024);

        var done = await ctx.WaitFor(job.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(30));
        Assert.Equal("completed", done.State);
        // Seconds after the end are zeros without anything having run to write them.
        await Task.Delay(TimeSpan.FromSeconds(4));
        var after = ctx.Service.GetSpeedHistory();
        Assert.Equal(during.Start, after.Start);
        Assert.All(after.BytesPerSecond.TakeLast(2), value => Assert.Equal(0, value));
        Assert.True(after.BytesPerSecond.Count >= during.BytesPerSecond.Count + 3);
    }
}

/// <summary>UI.R24: the cases a review found (2026-11-08).</summary>
public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task APausedDownloadLeavesTheTotalWhileAnotherRuns()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var paused = await AddSlowAsync(ctx, "history-paused.bin", 8 * Mb, 952, 512 * 1024, 1);
        var running = await AddSlowAsync(ctx, "history-running.bin", 8 * Mb, 953, 64 * 1024, 1);
        await ctx.WaitFor(paused.Id, j => j.State == "downloading" && j.ReceivedBytes > 1 * Mb, TimeSpan.FromSeconds(30));
        await ctx.WaitFor(running.Id, j => j.State == "downloading" && j.ReceivedBytes > 64 * 1024, TimeSpan.FromSeconds(30));
        Assert.Contains(ctx.Service.GetSpeedHistory().BytesPerSecond.TakeLast(3), value => value > 256 * 1024);

        Assert.True((await ctx.Service.PauseAsync(paused.Id)).Ok);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var history = ctx.Service.GetSpeedHistory().BytesPerSecond;
        // The second just closed holds only the 64 KiB/s download.
        Assert.InRange(history[^2], 1, 160 * 1024);
        Assert.Equal("downloading", ctx.Service.GetJob(running.Id)!.State);
    }
}

/// <summary>UI.R24 through the real Host's pipe.</summary>
[Trait("Platform", "Windows")]
public sealed class HostSpeedHistoryTests : IAsyncLifetime
{
    private TestServerHost _server = null!;

    public async Task InitializeAsync() => _server = await TestServerHost.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task AWindowThatConnectsGetsTheHostsHistory()
    {
        await using var host = await SandboxHost.StartAsync(_server.TorBox.ApiBase);
        await using var connection = await HostConnection.OpenAsync(host.PipeName, _ => { });
        var reply = await connection.RequestAsync(MessageTypes.SpeedHistoryGet, new { });
        Assert.Equal(MessageTypes.SpeedHistoryResult, reply.Type);
        var history = reply.GetPayload<SpeedHistoryDto>();
        Assert.Equal(1, history.IntervalSeconds);
        Assert.InRange(history.BytesPerSecond.Count, 1, SpeedHistory.Seconds);
        Assert.InRange(history.Start, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow);
        Assert.All(history.BytesPerSecond, value => Assert.Equal(0, value));

        // A download through the Host shows in the history the next window reads.
        var added = await connection.AskAsync<AddJobResponse>(MessageTypes.JobAdd, new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/host-history.bin?size={4 * 1024 * 1024}&seed=954&bps={256 * 1024}",
            DestinationDirectory = host.Destination,
            FileName = "host-history.bin",
            Source = "cli",
        });
        Assert.True(added.Accepted, added.Error);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        SpeedHistoryDto later;
        do
        {
            await Task.Delay(250);
            later = (await connection.RequestAsync(MessageTypes.SpeedHistoryGet, new { })).GetPayload<SpeedHistoryDto>();
        }
        while (!later.BytesPerSecond.TakeLast(3).Any(value => value > 64 * 1024) && DateTimeOffset.UtcNow < deadline);

        Assert.Equal(history.Start, later.Start);
        Assert.Contains(later.BytesPerSecond.TakeLast(3), value => value > 64 * 1024);

        // F-12: the same download's own figures, for Progress Details opened mid-download.
        var own = (await connection.RequestAsync(MessageTypes.SpeedHistoryGet, new SpeedHistoryRequest { JobId = added.Job!.Id })).GetPayload<SpeedHistoryDto>();
        Assert.Equal(added.Job.Id, own.JobId);
        Assert.Contains(own.BytesPerSecond, value => value > 64 * 1024);
        // A download that isn't running has none; the answer still names it, so the window knows it was asked.
        var none = (await connection.RequestAsync(MessageTypes.SpeedHistoryGet, new SpeedHistoryRequest { JobId = Guid.NewGuid() })).GetPayload<SpeedHistoryDto>();
        Assert.NotNull(none.JobId);
        Assert.Empty(none.BytesPerSecond);
    }
}
