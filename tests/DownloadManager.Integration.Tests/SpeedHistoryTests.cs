using DownloadManager.Contracts;
using DownloadManager.Runtime;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>UI.R24: the Host keeps the last hour of total download speed, with no timer while idle.</summary>
public sealed class SpeedHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

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
    public void NonsenseRatesCountAsZero()
    {
        var history = new SpeedHistory(T0);
        history.Report(Guid.NewGuid(), double.NaN, T0.AddSeconds(1));
        history.Report(Guid.NewGuid(), -5, T0.AddSeconds(1));
        history.Report(Guid.NewGuid(), double.PositiveInfinity, T0.AddSeconds(1));
        Assert.All(history.Read(T0.AddSeconds(1)).BytesPerSecond, value => Assert.Equal(0, value));
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
    }
}
