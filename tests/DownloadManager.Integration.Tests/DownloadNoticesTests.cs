using System.Collections.Concurrent;
using System.Text;
using DownloadManager.Contracts;
using DownloadManager.Core.Archives;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Archives;

namespace DownloadManager.Integration.Tests;

/// <summary>What the user is told, on real downloads and a real coordinator. The extraction tool is a stand-in.</summary>
[Collection("runtime-archive")]
public sealed class DownloadNoticesTests(ArchiveFileServer server)
{
    private static readonly byte[] Bytes = Encoding.ASCII.GetBytes("some bytes to download");

    private static ArchiveCoordinatorOptions Tool(FakeExtractor tool) => new() { ExtractorFactory = _ => tool };

    private sealed class Told : IDisposable
    {
        private readonly DownloadNotices _notices;
        private readonly ConcurrentQueue<DownloadNotice> _said = new();

        public Told(ArchiveHarness run) => _notices = new DownloadNotices(run.Service, run.Archives, _said.Enqueue);

        public List<DownloadNotice> Said => _said.ToList();

        /// <summary>Until this many came, then a moment longer, so one too many shows too.</summary>
        public async Task<List<DownloadNotice>> WaitForAsync(int count)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (_said.Count < count && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
            await Task.Delay(400);
            return Said;
        }

        public void Dispose() => _notices.Dispose();
    }

    private async Task<JobDto> AddFailingAsync(ArchiveHarness run, string name, string packageKey)
    {
        var response = await run.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{server.BaseAddress}/{Uri.EscapeDataString("not-there-" + name)}",
            DestinationDirectory = run.Downloads,
            FileName = name,
            StartMode = "startNow",
            Source = "cli",
            PackageKey = packageKey,
        });
        Assert.True(response.Accepted, response.Error);
        return response.Job!;
    }

    [Fact]
    public async Task ADownloadOnItsOwnIsToldAboutAsBefore()
    {
        await using var run = await ArchiveHarness.StartAsync(server);
        using var told = new Told(run);

        await run.DownloadAsync("alone.bin", Bytes);

        var notice = Assert.Single(await told.WaitForAsync(1));
        Assert.Equal(new DownloadNotice(DownloadNoticeKind.Completed, "Download complete", "alone.bin"), notice);
    }

    [Fact]
    public async Task AFailureOnItsOwnIsToldAboutAsBefore()
    {
        await using var run = await ArchiveHarness.StartAsync(server);
        using var told = new Told(run);

        var response = await run.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{server.BaseAddress}/not-there-alone",
            DestinationDirectory = run.Downloads,
            FileName = "alone.bin",
            StartMode = "startNow",
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);

        var notice = Assert.Single(await told.WaitForAsync(1));
        Assert.Equal(DownloadNoticeKind.Failed, notice.Kind);
        Assert.StartsWith("alone.bin: ", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APackageOfOneFileReadsLikeADownloadOnItsOwn()
    {
        await using var run = await ArchiveHarness.StartAsync(server);
        using var told = new Told(run);

        await run.DownloadAsync("only.bin", Bytes, packageKey: "of-one");

        var notice = Assert.Single(await told.WaitForAsync(1));
        Assert.Equal(new DownloadNotice(DownloadNoticeKind.Completed, "Download complete", "only.bin"), notice);
    }

    [Fact]
    public async Task APackageIsToldAboutOnceWhenItsLastFileIsDone()
    {
        await using var run = await ArchiveHarness.StartAsync(server);
        using var told = new Told(run);

        var jobs = new List<JobDto>();
        foreach (var index in Enumerable.Range(1, 4)) jobs.Add(await run.DownloadAsync($"file{index}.bin", Bytes, packageKey: "four"));
        foreach (var job in jobs) await run.WaitForJobAsync(job.Id, "completed");

        var notice = Assert.Single(await told.WaitForAsync(1));
        Assert.Equal(DownloadNoticeKind.Completed, notice.Kind);
        Assert.Equal("Download complete", notice.Title);
        Assert.EndsWith(": 4 files", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailingPackageSaysTwoThingsHoweverManyFilesFail()
    {
        await using var run = await ArchiveHarness.StartAsync(server);
        using var told = new Told(run);

        // Held back until all are listed, so the batch can't settle between adds.
        var held = await run.DownloadAsync("good.bin", Bytes, "later", packageKey: "bad-batch");
        var failing = new List<JobDto>();
        foreach (var index in Enumerable.Range(1, 8)) failing.Add(await AddFailingAsync(run, $"bad{index}.bin", "bad-batch"));
        foreach (var job in failing) await run.WaitForJobAsync(job.Id, "failed");
        await Task.Delay(400);
        Assert.Single(told.Said);

        await run.Service.ResumeAsync(held.Id);
        await run.WaitForJobAsync(held.Id, "completed");

        var said = await told.WaitForAsync(2);
        Assert.Equal(2, said.Count);
        Assert.All(said, notice => Assert.Equal(DownloadNoticeKind.Failed, notice.Kind));
        Assert.Contains(": ", said[0].Message, StringComparison.Ordinal);
        Assert.StartsWith("bad", said[0].Message, StringComparison.Ordinal);
        Assert.EndsWith("8 of 9 downloads failed", said[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALastFileFailingSaysItOnceWithItsReason()
    {
        await using var run = await ArchiveHarness.StartAsync(server);
        using var told = new Told(run);

        var good = await run.DownloadAsync("good.bin", Bytes, packageKey: "last-bad");
        await run.WaitForJobAsync(good.Id, "completed");
        await AddFailingAsync(run, "bad.bin", "last-bad");

        var notice = Assert.Single(await told.WaitForAsync(1), candidate => candidate.Kind == DownloadNoticeKind.Failed);
        Assert.Contains("1 of 2 downloads failed. bad.bin: ", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APackageWithSeveralArchivesSaysOneThingWhenTheLastIsExtracted()
    {
        var tool = new FakeExtractor { Hold = TimeSpan.FromMilliseconds(150) };
        await using var run = await ArchiveHarness.StartAsync(server, Tool(tool));
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });
        using var told = new Told(run);

        foreach (var name in new[] { "one.rar", "two.rar", "three.rar" }) await run.DownloadAsync(name, Bytes, packageKey: "archives");

        var notice = Assert.Single(await told.WaitForAsync(1));
        Assert.Equal(3, run.Archives.List().Count(set => set.State == "completed"));
        Assert.Equal(DownloadNoticeKind.Completed, notice.Kind);
        Assert.EndsWith(": 3 files, 3 archives extracted", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APackageWhoseArchivesFailSaysOneThingAndThatTheFilesArrived()
    {
        var tool = new FakeExtractor { FailWith = ExtractionFailureKind.Corrupted };
        await using var run = await ArchiveHarness.StartAsync(server, Tool(tool));
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });
        using var told = new Told(run);

        foreach (var name in new[] { "one.rar", "two.rar" }) await run.DownloadAsync(name, Bytes, packageKey: "broken");

        var notice = Assert.Single(await told.WaitForAsync(1));
        Assert.Equal(DownloadNoticeKind.Extraction, notice.Kind);
        Assert.Equal("Extraction stopped", notice.Title);
        Assert.EndsWith(": 2 files downloaded, but 2 of 2 archives weren't extracted", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APackagesArchivesExtractedLaterAreOneNoticeToo()
    {
        var tool = new FakeExtractor();
        await using var run = await ArchiveHarness.StartAsync(server, Tool(tool));
        using var told = new Told(run);

        var jobs = new List<JobDto>();
        foreach (var name in new[] { "one.rar", "two.rar" }) jobs.Add(await run.DownloadAsync(name, Bytes, packageKey: "by-hand"));
        foreach (var job in jobs) await run.WaitForJobAsync(job.Id, "completed");
        var downloaded = Assert.Single(await told.WaitForAsync(1));
        Assert.EndsWith(": 2 files", downloaded.Message, StringComparison.Ordinal);

        var asked = await run.Archives.ExtractAsync(new ArchiveExtractRequest { JobIds = [jobs[0].Id, jobs[1].Id] });
        Assert.True(asked.Ok, asked.Error);

        var said = await told.WaitForAsync(2);
        Assert.Equal(2, said.Count);
        Assert.Equal(DownloadNoticeKind.PackageExtractionCompleted, said[1].Kind);
        Assert.EndsWith(": 2 archives extracted", said[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArchiveOnItsOwnSaysBothThingsAsBefore()
    {
        var tool = new FakeExtractor();
        await using var run = await ArchiveHarness.StartAsync(server, Tool(tool));
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });
        using var told = new Told(run);

        await run.DownloadAsync("single.rar", Bytes);

        var said = await told.WaitForAsync(2);
        Assert.Equal(2, said.Count);
        Assert.Contains(said, notice => notice == new DownloadNotice(DownloadNoticeKind.Completed, "Download complete", "single.rar"));
        Assert.Contains(said, notice => notice.Kind == DownloadNoticeKind.Extraction && notice.Title == "Extraction complete");
    }

    /// <summary>A download that stays running for as long as a test needs.</summary>
    private async Task<JobDto> AddSlowAsync(ArchiveHarness run, string name, string packageKey)
    {
        var response = await run.Service.AddJobAsync(new AddJobRequest
        {
            Url = server.Serve(name, new byte[8 * 1024 * 1024]),
            DestinationDirectory = run.Downloads,
            FileName = name,
            StartMode = "startNow",
            Source = "cli",
            PackageKey = packageKey,
            SpeedLimitBytesPerSecond = 16 * 1024,
        });
        Assert.True(response.Accepted, response.Error);
        return response.Job!;
    }

    /// <summary>A file held back keeps the package open while the others finish.</summary>
    private static async Task<(JobDto Held, List<JobDto> Done)> TwoDoneOneHeldAsync(ArchiveHarness run, string key)
    {
        var held = await run.DownloadAsync("held.bin", Bytes, "later", packageKey: key);
        var done = new List<JobDto>();
        foreach (var name in new[] { "one.bin", "two.bin" }) done.Add(await run.DownloadAsync(name, Bytes, packageKey: key));
        foreach (var job in done) await run.WaitForJobAsync(job.Id, "completed");
        return (held, done);
    }

    [Fact]
    public async Task APackageWithACancelledFileSaysHowManyArrived()
    {
        await using var run = await ArchiveHarness.StartAsync(server);
        using var told = new Told(run);

        var (held, _) = await TwoDoneOneHeldAsync(run, "one-cancelled");
        await Task.Delay(300);
        Assert.Empty(told.Said);

        await run.Service.CancelAsync(held.Id);

        var notice = Assert.Single(await told.WaitForAsync(1));
        Assert.Equal(DownloadNoticeKind.Completed, notice.Kind);
        Assert.EndsWith(": 2 of 3 files", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APackageWithAStoppedFileSaysHowManyArrivedAndThenThatItFinished()
    {
        await using var run = await ArchiveHarness.StartAsync(server);
        using var told = new Told(run);

        var (held, _) = await TwoDoneOneHeldAsync(run, "one-stopped");
        Assert.True((await run.Service.PauseAsync(held.Id)).Ok);

        var first = Assert.Single(await told.WaitForAsync(1));
        Assert.EndsWith(": 2 of 3 files", first.Message, StringComparison.Ordinal);

        await run.Service.ResumeAsync(held.Id);
        await run.WaitForJobAsync(held.Id, "completed");

        var said = await told.WaitForAsync(2);
        Assert.Equal(2, said.Count);
        Assert.EndsWith(": 3 files", said[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoppingAndResumingAFileSaysNothingNewUntilAnotherFileChanges()
    {
        await using var run = await ArchiveHarness.StartAsync(server);
        using var told = new Told(run);

        var slow = await AddSlowAsync(run, "slow.bin", "again");
        var done = await run.DownloadAsync("done.bin", Bytes, packageKey: "again");
        await run.WaitForJobAsync(done.Id, "completed");
        Assert.True((await run.Service.PauseAsync(slow.Id)).Ok);
        Assert.Single(await told.WaitForAsync(1));

        // Going again and stopped again: nothing about the package has changed.
        await run.WaitForJobAsync(slow.Id, "paused");
        await run.Service.ResumeAsync(slow.Id);
        await run.WaitForJobAsync(slow.Id, "downloading");
        Assert.True((await run.Service.PauseAsync(slow.Id)).Ok);
        await Task.Delay(400);
        Assert.Single(told.Said);
    }

    [Fact]
    public async Task CancellingAFileAlreadyToldAsStoppedSaysTheSameThingOnce()
    {
        await using var run = await ArchiveHarness.StartAsync(server);
        using var told = new Told(run);

        var (held, _) = await TwoDoneOneHeldAsync(run, "stopped-then-cancelled");
        Assert.True((await run.Service.PauseAsync(held.Id)).Ok);
        Assert.Single(await told.WaitForAsync(1));

        await run.Service.CancelAsync(held.Id);

        await Task.Delay(400);
        Assert.Single(told.Said);
    }

    [Fact]
    public async Task CancellingAWholePackageSaysNothing()
    {
        await using var run = await ArchiveHarness.StartAsync(server);
        using var told = new Told(run);

        var (held, done) = await TwoDoneOneHeldAsync(run, "all-cancelled");
        var package = run.Service.FindPackageOf(done[0].Id);

        await run.Service.CancelAsync(held.Id, cancelledByPackage: package);

        await Task.Delay(400);
        Assert.Empty(told.Said);
    }

    [Fact]
    public async Task RemovingARunningFileFromAPackageSaysNothing()
    {
        await using var run = await ArchiveHarness.StartAsync(server);
        using var told = new Told(run);

        var running = await AddSlowAsync(run, "slow.bin", "removing");
        var done = await run.DownloadAsync("small.bin", Bytes, packageKey: "removing");
        await run.WaitForJobAsync(done.Id, "completed");
        await run.WaitForJobAsync(running.Id, "downloading");

        Assert.True((await run.Service.RemoveAsync(running.Id, deleteFile: false)).Ok);

        await Task.Delay(400);
        Assert.Empty(told.Said);
    }

    [Fact]
    public async Task ADeletedPackageSaysNothingMore()
    {
        await using var run = await ArchiveHarness.StartAsync(server);
        using var told = new Told(run);

        var held = await run.DownloadAsync("good.bin", Bytes, "later", packageKey: "gone");
        var bad = await AddFailingAsync(run, "bad.bin", "gone");
        var worse = await AddFailingAsync(run, "worse.bin", "gone");
        var worst = await AddFailingAsync(run, "worst.bin", "gone");
        await run.WaitForJobAsync(bad.Id, "failed");
        await run.WaitForJobAsync(worse.Id, "failed");
        await run.WaitForJobAsync(worst.Id, "failed");
        Assert.Single(await told.WaitForAsync(1));

        // One at a time, so that partway through what is left looks like a package that has stopped.
        await run.Service.RemoveAsync(bad.Id, deleteFile: false);
        await run.Service.RemoveAsync(held.Id, deleteFile: false);
        await run.Service.RemoveAsync(worse.Id, deleteFile: false);
        await run.Service.RemoveAsync(worst.Id, deleteFile: false);

        await Task.Delay(400);
        Assert.Single(told.Said);
    }
}
