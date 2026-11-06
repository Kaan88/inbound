using System.Text;
using DownloadManager.Archives;
using DownloadManager.Archives.Tests;
using DownloadManager.Contracts;
using DownloadManager.Core.Archives;
using DownloadManager.Persistence;
using DownloadManager.Runtime.Archives;

namespace DownloadManager.Integration.Tests;

/// <summary>Regressions from the review, through the real coordinator and mostly the real tool.</summary>
[Collection("runtime-archive")]
public sealed class RuntimeArchiveReviewTests(ArchiveFileServer fixture)
{
    private const string Secret = "Test-Pw_123 sp";
    private static readonly byte[] Text = Encoding.ASCII.GetBytes("from the first archive");
    private static readonly byte[] Other = Encoding.ASCII.GetBytes("from the other archive");

    private readonly ArchiveFileServer _server = fixture;

    private static Task AskAsync(ArchiveHarness run, string path) => run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = path });

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.Fail($"This never happened: {what}.");
    }

    /// <summary>Real RAR5 volumes, exactly two of them.</summary>
    private static async Task<Dictionary<string, byte[]>> TwoVolumesAsync(ArchiveHarness run, string archiveName, string entry = "big.bin", int seed = 21)
    {
        var built = await ArchiveTools.BuildRar5Async(run.Root, archiveName, [(entry, ArchiveTools.RandomBytes(150_000, seed))], "-v100k");
        Assert.Equal(2, built.Count);
        return built;
    }

    // ----- Finding 1: a name only suggests a set -----

    [UnrarTheory]
    [InlineData("report.001.rar", "report.002.rar")]
    [InlineData("lecture.part1.rar", "lecture.part2.rar")]
    public async Task SeparateArchivesWithNumberedNamesAreEachExtractedAndNeitherIsLost(string one, string two)
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true });
        var first = run.WriteFile(one, RarBuilder.Rar5(("one.txt", Text)));
        var second = run.WriteFile(two, RarBuilder.Rar5(("two.txt", Other)));

        await AskAsync(run, first);
        await AskAsync(run, second);

        await WaitUntilAsync(() => run.Archives.List().Count(set => set is { State: "completed", ArchivesDeleted: true }) == 2, "both sets finished");
        Assert.Equal(["one.txt", "two.txt"], run.AllFiles());
        Assert.Equal(Text, File.ReadAllBytes(run.PathOf("one.txt")));
        Assert.Equal(Other, File.ReadAllBytes(run.PathOf("two.txt")));
    }

    [UnrarFact]
    public async Task WithoutDeletingTheSecondArchiveIsStillNotCalledExtracted()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var first = run.WriteFile("report.001.rar", RarBuilder.Rar5(("one.txt", Text)));
        run.WriteFile("report.002.rar", RarBuilder.Rar5(("two.txt", Other)));

        await AskAsync(run, first);
        var done = await run.WaitForStateAsync("completed");

        Assert.Equal("report.001", done.Name);
        Assert.Equal(["report.001.rar"], done.Volumes);
        Assert.False(File.Exists(run.PathOf("two.txt")));

        // It is its own set, and asking for it works.
        Assert.True((await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = run.PathOf("report.002.rar") })).Ok);
        await WaitUntilAsync(() => File.Exists(run.PathOf("two.txt")), "the second archive extracted");
    }

    [RarFact]
    public async Task AnUnrelatedWholeArchiveWithTheNextNumberIsNotTouchedByARealSet()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true });
        var built = await TwoVolumesAsync(run, "a.rar");
        foreach (var (name, bytes) in built) run.WriteFile(name, bytes);
        run.WriteFile("a.part3.rar", RarBuilder.Rar5(("stray.txt", Other)));

        await AskAsync(run, run.PathOf("a.part1.rar"));
        await run.WaitForSetAsync(set => set is { State: "completed", ArchivesDeleted: true });

        Assert.Equal(["a.part3.rar", "big.bin"], run.AllFiles());
        Assert.False(File.Exists(run.PathOf("stray.txt")));
    }

    [RarFact]
    public async Task AVolumeOfSomeOtherSetUnderANextNumberIsKeptAndTheNoteSaysSo()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true });
        var built = await TwoVolumesAsync(run, "a.rar");
        foreach (var (name, bytes) in built) run.WriteFile(name, bytes);

        // Its header says volume, so names can't tell it apart and only what the tool read can.
        var otherSet = await TwoVolumesAsync(run, "other.rar", "stray.bin", seed: 22);
        run.WriteFile("a.part3.rar", otherSet["other.part2.rar"]);

        await AskAsync(run, run.PathOf("a.part1.rar"));
        var done = await run.WaitForStateAsync("completed");
        await WaitUntilAsync(() => !File.Exists(run.PathOf("a.part1.rar")) && !File.Exists(run.PathOf("a.part2.rar")), "the read volumes deleted");

        Assert.True(File.Exists(run.PathOf("a.part3.rar")));
        Assert.Contains("a.part3.rar", (await run.WaitForSetAsync(set => set.Id == done.Id && set.Note is not null)).Note);
        Assert.Contains("did not read", run.Archives.Find(done.Id)!.Note);
    }

    [Fact]
    public async Task AnExtractorThatCantSayWhatItReadDeletesNothing()
    {
        var tool = new SilentExtractor();
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => tool });
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true });
        var archive = run.WriteFile("quiet.rar", RarBuilder.Rar5(("a.txt", Text)));

        await AskAsync(run, archive);
        var done = await run.WaitForStateAsync("completed");

        Assert.True(File.Exists(archive));
        Assert.False(done.ArchivesDeleted);
        Assert.Contains("didn't report", done.Note);
    }

    /// <summary>Succeeds without saying which volumes it read or that anything was flushed.</summary>
    private sealed class SilentExtractor : IArchiveExtractor
    {
        public Task<ArchiveProbe> ProbeAsync(string firstVolume, string? password, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new ExtractionResult(request.Destination, []));
    }

    // ----- Finding 2: a set's identity is its real first volume -----

    [Fact]
    public async Task TwinsThatTurnUpDuringARunAreNotDeletedAndGetTheirOwnRecord()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new FakeExtractor { Release = release };
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => tool });
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true });

        var padded = run.WriteFile("name.part01.rar", Text);
        await AskAsync(run, padded);
        await tool.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var one = await run.DownloadAsync("name.part1.rar", Other);
        var two = await run.DownloadAsync("name.part2.rar", Other);
        await run.WaitForJobAsync(one.Id, "completed");
        await run.WaitForJobAsync(two.Id, "completed");
        release.SetResult();

        await WaitUntilAsync(() => run.Archives.List().Any(set => set is { State: "completed", ArchivesDeleted: true }), "the padded set finished");

        Assert.False(File.Exists(padded));
        Assert.True(File.Exists(run.PathOf("name.part1.rar")));
        Assert.True(File.Exists(run.PathOf("name.part2.rar")));
        var unpadded = run.Archives.List().Single(set => Path.GetFileName(set.FirstVolume) == "name.part1.rar");
        Assert.NotEqual("completed", unpadded.State);
    }

    [Fact]
    public async Task APasswordStaysWithItsOwnSetWhenATwinAppears()
    {
        var tool = new FakeExtractor();
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => tool });
        var padded = run.WriteFile("name.part01.rar", Text);
        var withPassword = await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest { FilePath = padded, Password = Secret });
        Assert.True(withPassword.Ok, withPassword.Error);

        var job = await run.DownloadAsync("name.part1.rar", Other);
        await run.WaitForJobAsync(job.Id, "completed");
        await WaitUntilAsync(() => run.Archives.List().Count == 2, "a second set listed");

        var list = run.Archives.List();
        Assert.True(list.Single(set => Path.GetFileName(set.FirstVolume) == "name.part01.rar").HasPassword);
        Assert.False(list.Single(set => Path.GetFileName(set.FirstVolume) == "name.part1.rar").HasPassword);
        Assert.Equal(withPassword.Set!.Id, list.Single(set => Path.GetFileName(set.FirstVolume) == "name.part01.rar").Id);
    }

    [Fact]
    public async Task ALaterDownloadOfATwinIsExtractedThoughItsPaddedTwinHasFinished()
    {
        var tool = new FakeExtractor();
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => tool });
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true, AutoExtract = true });

        await AskAsync(run, run.WriteFile("name.part01.rar", Text));
        await run.WaitForSetAsync(set => set is { State: "completed", ArchivesDeleted: true });

        var job = await run.DownloadAsync("name.part1.rar", Other);
        await run.WaitForJobAsync(job.Id, "completed");
        await WaitUntilAsync(() => tool.Runs == 2, "the twin extracted");
    }

    [Fact]
    public async Task ASetFirstSeenByALaterVolumeBecomesTheFirstVolumeThatTurnsUp()
    {
        var tool = new FakeExtractor();
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => tool });
        var late = run.WriteFile("v.part10.rar", Text);
        var recorded = await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest { FilePath = late, Password = Secret });
        Assert.Equal("v.part1.rar", Path.GetFileName(recorded.Set!.FirstVolume));

        var first = await run.DownloadAsync("v.part01.rar", Other);
        await run.WaitForJobAsync(first.Id, "completed");

        await WaitUntilAsync(() => Path.GetFileName(run.Archives.Find(recorded.Set.Id)?.FirstVolume) == "v.part01.rar", "the record taking the real first volume");
        var set = Assert.Single(run.Archives.List());
        Assert.Equal(recorded.Set.Id, set.Id);
        Assert.True(set.HasPassword);
    }

    [Fact]
    public async Task AKeyFromBeforeThisRuleIsCorrectedAtTheNextStart()
    {
        var tool = new FakeExtractor();
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => tool });
        var padded = run.WriteFile("old.part01.rar", Text);
        var recorded = await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest { FilePath = padded, Password = Secret });
        var file = Path.Combine(run.Paths.Root, "archive", "sets", $"{recorded.Set!.Id:N}.json");
        var text = await File.ReadAllTextAsync(file);
        await File.WriteAllTextAsync(file, text.Replace("OLD.PART01.RAR", "OLD.PART1.RAR", StringComparison.Ordinal));

        await run.Archives.ShutdownAsync();
        await run.StartCoordinatorAsync(new ArchiveCoordinatorOptions { ExtractorFactory = _ => tool });

        Assert.Contains("OLD.PART01.RAR", await File.ReadAllTextAsync(file));

        // The twin that this old key would have swallowed has a set of its own.
        var job = await run.DownloadAsync("old.part1.rar", Other);
        await run.WaitForJobAsync(job.Id, "completed");
        await WaitUntilAsync(() => run.Archives.List().Count == 2, "a second set listed");
        Assert.Equal(2, run.Archives.List().Select(set => set.Id).Distinct().Count());
    }

    // ----- Finding 4: a subfolder is for one set -----

    [RarFact]
    public async Task SetsSharingABaseNameGetTheirOwnSubfoldersAndARetryKeepsItsOwn()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { Destination = "subfolder", ConflictPolicy = "overwrite", DeleteArchives = false });
        run.WriteFile("name.rar", RarBuilder.Rar5(("data.txt", Text)));
        var built = await ArchiveTools.BuildRar5Async(run.Root, "name.rar", [("data.txt", ArchiveTools.RandomBytes(150_000, 8))], "-v100k");
        foreach (var (file, bytes) in built) run.WriteFile(file, bytes);

        await AskAsync(run, run.PathOf("name.rar"));
        await run.WaitForStateAsync("completed");
        await AskAsync(run, run.PathOf("name.part1.rar"));
        await WaitUntilAsync(() => run.Archives.List().Count(set => set.State == "completed") == 2, "both finished");

        var sets = run.Archives.List();
        var destinations = sets.Select(set => set.Destination).ToList();
        Assert.Equal(2, destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(Text, File.ReadAllBytes(Path.Combine(sets.Single(set => Path.GetFileName(set.FirstVolume) == "name.rar").Destination!, "data.txt")));
        var volumes = sets.Single(set => Path.GetFileName(set.FirstVolume) == "name.part1.rar");
        Assert.Equal(150_000, new FileInfo(Path.Combine(volumes.Destination!, "data.txt")).Length);

        var attempts = volumes.Attempts;
        Assert.True((await run.Archives.RetryAsync(volumes.Id)).Ok);
        await run.WaitForSetAsync(set => set.Id == volumes.Id && set.State == "completed" && set.Attempts == attempts + 1);
        Assert.Equal(volumes.Destination, run.Archives.Find(volumes.Id)!.Destination);
    }

    [Fact]
    public async Task ANameThatCantBeAFolderFallsBackWithoutSharing()
    {
        var tool = new FakeExtractor();
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => tool });
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { Destination = "subfolder" });

        foreach (var name in new[] { "CON.rar", "NUL.rar" }) await AskAsync(run, run.WriteFile(name, Text));
        await WaitUntilAsync(() => run.Archives.List().Count(set => set.State == "completed") == 2, "both finished");

        Assert.Equal(2, run.Archives.List().Select(set => set.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task AnArchiveNamedLikeAWorkingFolderGetsAFolderThatIsNotOne()
    {
        var tool = new FakeExtractor();
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => tool });
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { Destination = "subfolder" });

        await AskAsync(run, run.WriteFile(".inbound-extract-x.rar", Text));
        var done = await run.WaitForStateAsync("completed");

        Assert.Equal("_.inbound-extract-x", Path.GetFileName(done.Destination));
    }

    // ----- Finding 11: a volume that finishes while a run is failing -----

    [Fact]
    public async Task AVolumeThatArrivesWhileARunIsFailingPutsTheSetBackInLine()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new FakeExtractor
        {
            Release = release,
            FailOnRun = number => number == 1 ? new ExtractionException(ExtractionFailureKind.MissingVolume, "Missing volume: w.part2.rar", ["w.part2.rar"]) : null,
        };
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => tool });
        await AskAsync(run, run.WriteFile("w.part1.rar", Text));
        await tool.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        run.WriteFile("w.part2.rar", Other);
        release.SetResult();

        await run.WaitForStateAsync("completed");
        Assert.Equal(2, tool.Runs);
    }

    [Fact]
    public async Task AVolumeThatWasAlreadyHereWhenTheToolCalledItMissingDoesNotLoop()
    {
        var tool = new FakeExtractor
        {
            FailOnRun = _ => new ExtractionException(ExtractionFailureKind.MissingVolume, "Missing volume: w.part2.rar", ["w.part2.rar"]),
        };
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => tool });
        run.WriteFile("w.part2.rar", Other);

        await AskAsync(run, run.WriteFile("w.part1.rar", Text));
        await run.WaitForFailureAsync("missingVolume");
        await Task.Delay(300);

        Assert.Equal(1, tool.Runs);
    }

    // ----- Finding 9: a file held open is not a corrupt file -----

    [UnrarFact]
    public async Task ARecordHeldOpenAtTheNextStartIsNotLostAndTheSetDoesNotRunAgain()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });
        var job = await run.DownloadAsync("q2.rar", RarBuilder.Rar5(("inner.txt", Text)));
        var done = await run.WaitForStateAsync("completed");
        var record = Path.Combine(run.Paths.Root, "archive", "sets", $"{done.Id:N}.json");

        using (new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await run.StartAgainAsync();
            await Task.Delay(500);

            Assert.Equal(1, run.ToolRuns);
            Assert.False(File.Exists(run.PathOf("inner (1).txt")));
            Assert.Empty(run.Archives.List());
            Assert.Empty(Directory.EnumerateFiles(run.Paths.CorruptDirectory));

            // Asked for by name, it says why not, instead of starting it over.
            var asked = await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = run.PathOf("q2.rar") });
            Assert.False(asked.Ok);
            Assert.Contains("couldn't be read", asked.Ignored.Single().Reason);
        }

        await run.StartAgainAsync();
        Assert.Equal(done.Id, Assert.Single(run.Archives.List()).Id);
        Assert.Equal("completed", run.Archives.Find(done.Id)!.State);
        await Task.Delay(300);
        Assert.Equal(1, run.ToolRuns);
        Assert.Equal(job.Id, Assert.Single(run.Archives.Find(done.Id)!.JobIds));
    }

    [Fact]
    public async Task AReallyCorruptRecordIsStillSetAside()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var sets = Directory.CreateDirectory(Path.Combine(run.Paths.Root, "archive", "sets")).FullName;
        await File.WriteAllTextAsync(Path.Combine(sets, $"{Guid.NewGuid():N}.json"), "{ not json");

        await run.StartAgainAsync();

        Assert.Single(Directory.EnumerateFiles(run.Paths.CorruptDirectory));
    }

    [Fact]
    public async Task SettingsHeldOpenAtStartAreNotOverwritten()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true });
        var file = Path.Combine(run.Paths.Root, "archive", "settings.json");
        var before = await File.ReadAllTextAsync(file);

        var store = new JsonArchiveStore(run.Paths, null, new ArchiveStoreOptions { ReadAttempts = 2, ReadRetryDelay = TimeSpan.FromMilliseconds(1) });
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => store.LoadSettingsAsync());
        }

        Assert.Equal(before, await File.ReadAllTextAsync(file));
        Assert.Empty(Directory.EnumerateFiles(run.Paths.CorruptDirectory));
    }

    // ----- Findings 12 and 13: what a leftover folder can do at a Host start -----

    private static async Task<ArchiveSetDto> FinishedSetAsync(ArchiveHarness run)
    {
        await AskAsync(run, run.WriteFile("first.rar", RarBuilder.Rar5(("first.txt", Text))));
        return await run.WaitForStateAsync("completed");
    }

    [UnrarFact]
    public async Task AFolderNoSetAccountsForIsLeftAloneHoweverItLooksInside()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await FinishedSetAsync(run);
        var victim = Directory.CreateDirectory(Path.Combine(run.Root, "victim")).FullName;
        var precious = Path.Combine(victim, "precious.txt");
        await File.WriteAllTextAsync(precious, "precious");
        var info = new FileInfo(precious);

        // Everything a hostile archive could have written, naming a file outside this folder, with its true size and time.
        var evil = Directory.CreateDirectory(Path.Combine(run.Downloads, ".inbound-extract-evil")).FullName;
        await File.WriteAllTextAsync(Path.Combine(evil, "attempt.json"), System.Text.Json.JsonSerializer.Serialize(new { FirstVolume = run.PathOf("first.rar"), Destination = victim }));
        await File.WriteAllTextAsync(Path.Combine(evil, "placed.log"), System.Text.Json.JsonSerializer.Serialize(new { Source = "gone.txt", Target = precious, Length = info.Length, LastWriteTicks = info.LastWriteTimeUtc.Ticks }) + "\n");
        Directory.CreateDirectory(Path.Combine(evil, "displaced", "Startup"));
        await File.WriteAllTextAsync(Path.Combine(evil, "displaced", "Startup", "planted.cmd"), "x");

        await run.StartAgainAsync();

        Assert.True(File.Exists(precious));
        Assert.False(File.Exists(Path.Combine(victim, "Startup", "planted.cmd")));
        Assert.True(Directory.Exists(evil));
        Assert.Contains(run.Log, line => line.Contains("Left a folder alone", StringComparison.Ordinal));
    }

    [UnrarFact]
    public async Task EvenAtTheExactNameABadLogCanNotStopAStartOrReachOutside()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var done = await FinishedSetAsync(run);

        // A finished set's run is on record, so the second set is the one that gets recovered.
        var failing = run.WriteFile("second.rar", Encoding.ASCII.GetBytes("not an archive"));
        await AskAsync(run, failing);
        var second = await run.WaitForFailureAsync("corrupted");
        Assert.NotEqual(done.Id, second.Id);

        var victim = Directory.CreateDirectory(Path.Combine(run.Root, "victim")).FullName;
        var precious = Path.Combine(victim, "precious.txt");
        await File.WriteAllTextAsync(precious, "precious");
        var folder = StagingArea.FolderFor(run.Downloads, failing, run.Downloads);
        Directory.CreateDirectory(folder);
        await File.WriteAllLinesAsync(Path.Combine(folder, "placed.log"),
        [
            """{"Source":"a.txt","Target":"a\u0000b"}""",
            """{"Target":"x"}""",
            System.Text.Json.JsonSerializer.Serialize(new { Source = "a.txt", Target = precious, Length = new FileInfo(precious).Length, LastWriteTicks = new FileInfo(precious).LastWriteTimeUtc.Ticks }),
        ]);

        await run.StartAgainAsync();

        Assert.True(File.Exists(precious));
        Assert.False(Directory.Exists(folder));
        Assert.Equal(2, run.Archives.List().Count);
    }

    // ----- Finding 8: durable before deleted -----

    [UnrarFact]
    public async Task IfTheOutputCantBeFlushedTheArchivesStay()
    {
        var extractor = new UnrarExtractor(new UnrarExtractorOptions
        {
            ToolPath = ArchiveTools.UnrarPath,
            FlushOverride = _ => throw new IOException("the disk said no"),
        });
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => extractor });
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true });
        var archive = run.WriteFile("flush.rar", RarBuilder.Rar5(("a.txt", Text)));

        await AskAsync(run, archive);
        var done = await run.WaitForStateAsync("completed");

        Assert.True(File.Exists(archive));
        Assert.False(done.ArchivesDeleted);
        Assert.Contains("written to the disk", done.Note);
        Assert.Equal(Text, File.ReadAllBytes(run.PathOf("a.txt")));
    }

    // ----- Job C: a download whose archive went after a verified extraction -----

    private static async Task<(JobDto Job, ArchiveSetDto Set)> DownloadExtractAndDeleteAsync(ArchiveHarness run)
    {
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true, DeleteArchives = true });
        var job = await run.DownloadAsync("gone.rar", RarBuilder.Rar5(("inner.txt", Text)));
        var set = await run.WaitForSetAsync(candidate => candidate is { State: "completed", ArchivesDeleted: true });
        await WaitUntilAsync(() => run.Service.GetJob(job.Id)?.FileRemovedAfterExtraction == true, "the download marked");
        return (job, set);
    }

    [UnrarFact]
    public async Task ADownloadWhoseArchiveWasRemovedAfterExtractionStaysCompletedAcrossARestart()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var (job, set) = await DownloadExtractAndDeleteAsync(run);
        Assert.False(File.Exists(job.FinalFilePath));
        Assert.Equal("Archive removed after extraction", run.Service.GetJob(job.Id)!.StatusNote);

        await run.RestartAsync();

        var after = run.Service.GetJob(job.Id)!;
        Assert.Equal("completed", after.State);
        Assert.Null(after.Failure);
        Assert.True(after.FileRemovedAfterExtraction);
        Assert.Equal("Archive removed after extraction", after.StatusNote);
        Assert.False(after.CanResume);
        Assert.Equal("completed", run.Archives.Find(set.Id)!.State);
        Assert.True(run.Archives.Find(set.Id)!.ArchivesDeleted);
        Assert.Contains("archive removed", run.Archives.Find(set.Id)!.StateText);
        Assert.Equal(Text, File.ReadAllBytes(run.PathOf("inner.txt")));

        // And once more, since a first restart must not use the mark up.
        await run.RestartAsync();
        Assert.Equal("completed", run.Service.GetJob(job.Id)!.State);
        Assert.DoesNotContain("missing from its destination", string.Join("|", run.Service.GetSnapshot().Jobs.Select(item => item.Failure?.Message)));
    }

    [UnrarFact]
    public async Task TheMarkIsSavedWithTheJob()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var (job, _) = await DownloadExtractAndDeleteAsync(run);

        var saved = await File.ReadAllTextAsync(Path.Combine(run.Paths.JobsDirectory, $"{job.Id:N}.json"));
        Assert.Contains("fileRemovedAfterExtraction\": true", saved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AKillBetweenTheMarkAndTheDeleteLeavesACompletedJobAndAFileThatStillGoes()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var job = await run.DownloadAsync("between.bin", Text);
        await run.WaitForJobAsync(job.Id, "completed");

        await run.Service.MarkFilesRemovedAfterExtractionAsync([job.FinalFilePath], removed: true);
        await run.RestartAsync();

        var after = run.Service.GetJob(job.Id)!;
        Assert.Equal("completed", after.State);
        Assert.True(after.FileRemovedAfterExtraction);

        File.Delete(job.FinalFilePath);
        await run.RestartAsync();
        Assert.Equal("completed", run.Service.GetJob(job.Id)!.State);
    }

    [UnrarFact]
    public async Task AnArchiveThatCouldNotBeDeletedIsNotMarkedAndFailsNothingLater()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var job = await run.DownloadAsync("held.rar", RarBuilder.Rar5(("inner.txt", Text)));
        await run.WaitForJobAsync(job.Id, "completed");
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true });

        using (new FileStream(job.FinalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await AskAsync(run, job.FinalFilePath);
            var done = await run.WaitForSetAsync(set => set.State == "completed" && set.Note is not null);
            Assert.Contains("Could not delete", done.Note);
        }

        Assert.False(run.Service.GetJob(job.Id)!.FileRemovedAfterExtraction);
        await run.RestartAsync();
        var after = run.Service.GetJob(job.Id)!;
        Assert.Equal("completed", after.State);
        Assert.False(after.FileRemovedAfterExtraction);
    }

    [Fact]
    public async Task AnyOtherMissingFileIsStillReportedAsMissing()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var job = await run.DownloadAsync("plain.bin", Text);
        await run.WaitForJobAsync(job.Id, "completed");

        File.Delete(job.FinalFilePath);
        await run.RestartAsync();

        var after = run.Service.GetJob(job.Id)!;
        Assert.Equal("failed", after.State);
        Assert.Contains("missing from its destination", after.Failure!.Message);
        Assert.False(after.FileRemovedAfterExtraction);
    }
}
