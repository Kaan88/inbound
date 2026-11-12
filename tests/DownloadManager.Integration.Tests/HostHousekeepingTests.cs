using DownloadManager.Host;
using DownloadManager.Persistence;

namespace DownloadManager.Integration.Tests;

/// <summary>Audit 3 M2 (update postponement) and L7 (log retention and daily cap).</summary>
public sealed class HostHousekeepingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-housekeeping-" + Guid.NewGuid().ToString("N"));

    public HostHousekeepingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Postponement_NotifiesOncePerVersion()
    {
        var p = new UpdatePostponement();
        Assert.True(p.ShouldNotify("1.2.0"));
        Assert.False(p.ShouldNotify("1.2.0"));
        Assert.False(p.ShouldNotify("1.2.0"));
        Assert.True(p.ShouldNotify("1.3.0"));
        p.Reset();
        Assert.True(p.ShouldNotify("1.3.0"));
    }

    [Fact]
    public void Postponement_DelayGrowsToAnHourAndRestartsForANewVersion()
    {
        var p = new UpdatePostponement();
        p.ShouldNotify("1.2.0");
        var delays = Enumerable.Range(0, 8).Select(_ => p.NextDelay()).ToList();
        Assert.Equal(UpdatePostponement.FirstDelay, delays[0]);
        for (var i = 1; i < delays.Count; i++) Assert.True(delays[i] >= delays[i - 1]);
        Assert.Equal(UpdatePostponement.MaxDelay, delays[^1]);
        p.ShouldNotify("1.3.0");
        Assert.Equal(UpdatePostponement.FirstDelay, p.NextDelay());
    }

    [Fact]
    public void Postponement_MessageNamesTheReason()
    {
        Assert.Equal("Downloads are active; the update will install when they finish.",
            UpdatePostponement.Message("Downloads are active. Use Install anyway or wait for them to finish.", willRetry: true));
        Assert.Equal("An update is already staged for restart.",
            UpdatePostponement.Message("An update is already staged for restart.", willRetry: true));
        Assert.DoesNotContain("Downloads", UpdatePostponement.Message("An update is already being installed.", willRetry: false));
    }

    [Fact]
    public void Postponement_OnlyADownloadsRefusalWaitsForTheDownloadsToEnd()
    {
        // The service's busy reasons (DownloadManagerService.InstallUpdateAsync / InstallUpdateCoreAsync).
        Assert.True(UpdatePostponement.WaitsForDownloads("Downloads are active. Use Install anyway or wait for them to finish."));
        Assert.False(UpdatePostponement.WaitsForDownloads("An update installation is already in progress."));
        Assert.False(UpdatePostponement.WaitsForDownloads("An update is already staged for restart."));
        Assert.False(UpdatePostponement.WaitsForDownloads("An update is already being installed."));
        Assert.False(UpdatePostponement.WaitsForDownloads(null));
    }

    [Fact]
    public void ClipboardRetry_KeepsTryingLessOftenForAboutHalfAMinute()
    {
        var delays = new List<TimeSpan>();
        var waited = TimeSpan.Zero;
        for (var attempt = 0; ClipboardRetrySchedule.Delay(attempt, waited) is { } delay; attempt++)
        {
            delays.Add(delay);
            waited += delay;
            Assert.True(attempt < 100, "the schedule ends");
        }

        Assert.Equal(TimeSpan.FromMilliseconds(250), delays[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(500), delays[1]);
        Assert.All(delays, d => Assert.True(d <= ClipboardRetrySchedule.Longest));
        Assert.Equal(ClipboardRetrySchedule.Longest, delays[^1]);
        // Far longer than the four 250 ms tries it replaced, and it stops.
        Assert.InRange(waited, ClipboardRetrySchedule.GiveUpAfter, ClipboardRetrySchedule.GiveUpAfter + ClipboardRetrySchedule.Longest);
    }

    [Fact]
    public void Postponement_EarlyRetryAtMostOnceAMinuteAndAgainAfterReset()
    {
        var p = new UpdatePostponement();
        var t0 = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        Assert.True(p.TryEarlyRetry(t0));
        // Job events every few seconds (a job still starting, seeding progress) bring no further tries.
        Assert.False(p.TryEarlyRetry(t0.AddSeconds(5)));
        Assert.False(p.TryEarlyRetry(t0 + UpdatePostponement.EarlyRetryGap - TimeSpan.FromSeconds(1)));
        Assert.True(p.TryEarlyRetry(t0 + UpdatePostponement.EarlyRetryGap));
        p.Reset();
        Assert.True(p.TryEarlyRetry(t0 + UpdatePostponement.EarlyRetryGap + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Retention_DeletesOnlyOldFilesOfThatPrefix()
    {
        var today = new DateTime(2026, 11, 12);
        string Make(string name)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, "x");
            return path;
        }

        var old = Make("host-20261001.log");
        var edge = Make("host-20261013.log");
        var recent = Make("host-20261111.log");
        var otherPrefix = Make("native-host-20260101.log");
        var notADate = Make("host-old.log");

        Assert.Equal(1, LogFileRetention.DeleteOld(_dir, "host", today));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(edge));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(otherPrefix));
        Assert.True(File.Exists(notADate));
    }

    [Fact]
    public void FileLogger_StartDeletesOldFiles()
    {
        var old = Path.Combine(_dir, "host-20200101.log");
        File.WriteAllText(old, "x");
        _ = new FileLogger(_dir);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void FileLogger_StopsAtTheDailyCapWithOneClosingLine()
    {
        var logger = new FileLogger(_dir) { MaxDailyBytes = 600 };
        for (var i = 0; i < 50; i++) logger.Info($"line {i} " + new string('a', 40));

        var path = Directory.GetFiles(_dir, "host-*.log").Single();
        var lines = File.ReadAllLines(path);
        Assert.True(new FileInfo(path).Length <= 600 + 200);
        Assert.Contains(LogFileRetention.CapReachedLine, lines[^1]);
        Assert.Equal(1, lines.Count(l => l.Contains(LogFileRetention.CapReachedLine)));
        Assert.True(lines.Length < 20);
    }
}
