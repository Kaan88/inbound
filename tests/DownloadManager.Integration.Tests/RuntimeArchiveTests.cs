using System.Text;
using System.Text.Json;
using DownloadManager.Archives;
using DownloadManager.Archives.Tests;
using DownloadManager.Contracts;
using DownloadManager.Core.Archives;
using DownloadManager.Persistence;
using DownloadManager.Runtime.Archives;

namespace DownloadManager.Integration.Tests;

[Collection("runtime-archive")]
public sealed class RuntimeArchiveTests(ArchiveFileServer fixture)
{
    private const string Secret = "Test-Pw_123 sp";
    private static readonly byte[] Text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("from the archive\n", 50)));
    private static readonly byte[] UserText = Encoding.ASCII.GetBytes("the user's own file");

    private readonly ArchiveFileServer _server = fixture;

    private static byte[] OneFileRar(string name = "data.txt") => RarBuilder.Rar5((name, Text));

    private static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    // ----- Scenario G: password-protected archives -----

    [RarTheory]
    [InlineData("-p")]
    [InlineData("-hp")]
    public async Task Rar5PasswordIsAskedForRefusedWhenWrongAndExtractsWhenRight(string encryption)
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var payload = ArchiveTools.RandomBytes(40_000, 3);
        var built = await ArchiveTools.BuildRar5Async(run.Root, "locked.rar", [("inner/secret.bin", payload)], encryption + Secret);
        var archive = run.WriteFile("locked.rar", built["locked.rar"]);

        var asked = await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        Assert.True(asked.Ok, asked.Error);

        var needs = await run.WaitForFailureAsync("passwordRequired");
        Assert.True(needs.NeedsPassword);
        Assert.False(needs.HasPassword);
        Assert.Equal("failed", needs.State);
        Assert.Contains("Password required", needs.StateText);
        Assert.Equal(["locked.rar"], run.AllFiles());

        var wrong = await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest { SetId = needs.Id, Password = "not it" });
        Assert.True(wrong.Ok, wrong.Error);
        var refused = await run.WaitForFailureAsync("wrongPassword");
        Assert.True(refused.NeedsPassword);
        Assert.False(refused.HasPassword);
        Assert.DoesNotContain("protectedPassword", run.RecordText(needs.Id), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(archive));
        Assert.Equal(["locked.rar"], run.AllFiles());

        await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest { SetId = needs.Id, Password = Secret });
        var done = await run.WaitForStateAsync("completed");

        Assert.Equal(Hash(payload), Hash(File.ReadAllBytes(run.PathOf(@"inner\secret.bin"))));
        Assert.Contains("Extraction completed: 1 file", done.StateText);
        Assert.True(File.Exists(archive));
        Assert.DoesNotContain("protectedPassword", run.RecordText(needs.Id), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, run.StateText());
    }

    [SevenZipTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rar4PasswordsWorkAndAWrongOneIsReportedAsSuch(bool encryptedHeaders)
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var bytes = encryptedHeaders ? RarBuilder.Rar4HeaderEncrypted("data.txt", Text, Secret) : RarBuilder.Rar4Encrypted("data.txt", Text, Secret);
        var archive = run.WriteFile("old.rar", bytes);

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive, Password = "not it" });
        var refused = await run.WaitForFailureAsync("wrongPassword");
        Assert.Contains("wrong", refused.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["old.rar"], run.AllFiles());

        await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest { SetId = refused.Id, Password = Secret });
        await run.WaitForStateAsync("completed");

        Assert.Equal(Text, File.ReadAllBytes(run.PathOf("data.txt")));
    }

    [SevenZipFact]
    public async Task APasswordGivenBeforeTheFileExistsIsUsedWhenItArrives()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });

        var given = await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest { FilePath = run.PathOf("later.rar"), Password = Secret });
        Assert.True(given.Ok, given.Error);
        Assert.True(given.Set!.HasPassword);
        Assert.Contains(SecretProtector.Current.Prefix, run.RecordText(given.Set.Id));
        Assert.DoesNotContain(Secret, run.StateText());

        await run.DownloadAsync("later.rar", RarBuilder.Rar4Encrypted("data.txt", Text, Secret));
        await run.WaitForStateAsync("completed");

        Assert.Equal(Text, File.ReadAllBytes(run.PathOf("data.txt")));
    }

    [SevenZipFact]
    public async Task APackagePasswordCoversItsSetsAndOneThatRefusesItStillAsks()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var first = await run.DownloadAsync("one.rar", RarBuilder.Rar4Encrypted("one.txt", Text, Secret), packageKey: "pkg");
        await run.DownloadAsync("two.rar", RarBuilder.Rar4Encrypted("two.txt", Text, "another password"), packageKey: "pkg");
        await run.WaitForJobAsync(first.Id, "completed");
        var packageId = await WaitForPackageAsync(run, first.Id);
        foreach (var job in run.Service.GetSnapshot().Jobs) await run.WaitForJobAsync(job.Id, "completed");

        var asked = await run.Archives.ExtractAsync(new ArchiveExtractRequest { PackageId = packageId, Password = Secret });
        Assert.Equal(2, asked.Sets.Count);

        var completed = await run.WaitForSetAsync(set => set.Name == "one" && set.State == "completed");
        Assert.Equal(packageId, completed.PackageId);
        var refused = await run.WaitForSetAsync(set => set.Name == "two" && set.FailureKind == "wrongPassword");
        Assert.True(refused.NeedsPassword);
        Assert.False(refused.HasPassword);
        Assert.Equal(1, refused.Attempts);

        // Retrying does not repeat the password it refused.
        await run.Archives.RetryAsync(refused.Id);
        await run.WaitForSetAsync(set => set.Name == "two" && set.FailureKind == "passwordRequired");

        await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest { SetId = refused.Id, Password = "another password" });
        await run.WaitForSetAsync(set => set.Name == "two" && set.State == "completed");
        Assert.Equal(Text, File.ReadAllBytes(run.PathOf("two.txt")));
    }

    // ----- Scenario H: multi-part sets -----

    [Fact]
    public async Task AVolumeThatFinishesDuringARunIsPartOfItsResultIfTheToolReadItAndGoesWhenArchivesAreDeleted()
    {
        var tool = new FakeExtractor
        {
            Hold = TimeSpan.FromSeconds(2),
            VolumesRead = request => [request.FirstVolume, Path.Combine(Path.GetDirectoryName(request.FirstVolume)!, "late.part2.rar")],
        };
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => tool });
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true, DeleteArchives = true });

        await run.DownloadAsync("late.part1.rar", Text);
        await tool.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Another download's volume, unknown when the set was claimed.
        var second = await run.DownloadAsync("late.part2.rar", Text);
        await run.WaitForJobAsync(second.Id, "completed");

        var done = await run.WaitForStateAsync("completed");
        Assert.Contains(second.Id, done.JobIds);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (run.AllFiles().Count > 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        Assert.Empty(run.AllFiles());

        // What this run extracted is never mistaken for one of its volumes, whatever it is called.
        await using var other = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => new WritingExtractor("inner.part2.rar", Text) });
        await other.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true, DeleteArchives = true });
        await other.DownloadAsync("inner.part1.rar", Text);
        await other.WaitForStateAsync("completed");
        await Task.Delay(200);
        Assert.Equal(["inner.part2.rar"], other.AllFiles());
    }

    [Fact]
    public async Task AnOfferedPackagePasswordNeverReplacesOneAlreadyThere()
    {
        var tool = new FakeExtractor();
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => tool });
        var job = await run.DownloadAsync("single.rar", Text, packageKey: "one-file-package");
        await run.WaitForJobAsync(job.Id, "completed");
        var package = run.Service.FindGroupOf(job.Id)!.Value;

        // A package of one file is still a package, which a set can be told about.
        Assert.Null(run.Service.FindPackageOf(job.Id));
        Assert.Null(await run.Archives.SetPackagePasswordAsync(package, Secret, keepExisting: true));
        var set = await run.WaitForSetAsync(candidate => candidate.HasPassword);
        Assert.Equal(package, set.PackageId);
        var saved = File.ReadAllText(Path.Combine(run.Paths.Root, "archive", "packages", $"{package:N}.json"));

        Assert.Null(await run.Archives.SetPackagePasswordAsync(package, "Another-Pw", keepExisting: true));
        Assert.Equal(saved, File.ReadAllText(Path.Combine(run.Paths.Root, "archive", "packages", $"{package:N}.json")));
        Assert.Null(await run.Archives.SetPackagePasswordAsync(package, "Another-Pw"));
        Assert.NotEqual(saved, File.ReadAllText(Path.Combine(run.Paths.Root, "archive", "packages", $"{package:N}.json")));

        Assert.Contains("no such package", await run.Archives.SetPackagePasswordAsync(Guid.NewGuid(), Secret));
    }

    [Fact]
    public async Task AnOfferNeverReplacesAPasswordWhoseFileWasHeldOpenAtTheStart()
    {
        var tool = new FakeExtractor();
        var options = new ArchiveCoordinatorOptions { ExtractorFactory = _ => tool };
        await using var run = await ArchiveHarness.StartAsync(_server, options);
        var job = await run.DownloadAsync("held.rar", Text, packageKey: "held-package");
        await run.WaitForJobAsync(job.Id, "completed");
        var package = run.Service.FindGroupOf(job.Id)!.Value;
        Assert.Null(await run.Archives.SetPackagePasswordAsync(package, Secret));
        var file = Path.Combine(run.Paths.Root, "archive", "packages", $"{package:N}.json");
        var saved = File.ReadAllText(file);

        // A scanner has the user's password file at the next start, so it isn't read then.
        await run.Archives.ShutdownAsync();
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await run.StartCoordinatorAsync(options, _ => new JsonArchiveStore(run.Paths, null, new ArchiveStoreOptions { ReadAttempts = 2, ReadRetryDelay = TimeSpan.FromMilliseconds(1) }));
        }

        Assert.Null(await run.Archives.SetPackagePasswordAsync(package, "Another-Pw", keepExisting: true));
        Assert.Equal(saved, File.ReadAllText(file));
    }

    [Fact]
    public async Task AnArchiveSideThatCouldNotStartTakesNoPasswordOffers()
    {
        var tool = new FakeExtractor();
        var options = new ArchiveCoordinatorOptions { ExtractorFactory = _ => tool };
        await using var run = await ArchiveHarness.StartAsync(_server, options);
        var job = await run.DownloadAsync("unstarted.rar", Text, packageKey: "unstarted-package");
        await run.WaitForJobAsync(job.Id, "completed");
        var package = run.Service.FindGroupOf(job.Id)!.Value;
        Assert.Null(await run.Archives.SetPackagePasswordAsync(package, Secret));
        var file = Path.Combine(run.Paths.Root, "archive", "packages", $"{package:N}.json");
        var saved = File.ReadAllText(file);

        // The archive settings are held open, so the start fails, as the Host then logs and carries on.
        await run.Archives.ShutdownAsync();
        var settings = Path.Combine(run.Paths.Root, "archive", "settings.json");
        using (new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => run.StartCoordinatorAsync(options, _ => new JsonArchiveStore(run.Paths, null, new ArchiveStoreOptions { ReadAttempts = 2, ReadRetryDelay = TimeSpan.FromMilliseconds(1) })));
        }

        await run.Archives.ShutdownAsync();

        // A remote job's offer is refused, so it keeps its own copy, and the user's password stays.
        Assert.NotNull(await run.Archives.SetPackagePasswordAsync(package, "Another-Pw", keepExisting: true));
        Assert.Equal(saved, File.ReadAllText(file));
    }

    /// <summary>Puts one file next to the archive, as an extraction would.</summary>
    private sealed class WritingExtractor(string name, byte[] bytes) : IArchiveExtractor
    {
        public Task<ArchiveProbe> ProbeAsync(string firstVolume, string? password, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)
        {
            var path = Path.Combine(request.Destination, name);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            return new ExtractionResult(request.Destination, [new ExtractedFile(name, path, ExtractedFileOutcome.Written)]) { VolumesRead = [request.FirstVolume], FlushedToDisk = true };
        }
    }

    [RarFact]
    public async Task APartNumberedSetWaitsForEveryRealDownloadAndRunsOnce()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });
        var payload = ArchiveTools.RandomBytes(500_000, 5);
        var volumes = await ArchiveTools.BuildRar5Async(run.Root, "movie.rar", [("movie.bin", payload)], "-v150k");
        var names = volumes.Keys.ToList();
        Assert.True(names.Count >= 3, string.Join(", ", names));
        Assert.All(names, name => Assert.Contains(".part", name));

        var first = await run.DownloadAsync(names[0], volumes[names[0]]);
        var held = new List<JobDto>();
        foreach (var name in names.Skip(1)) held.Add(await run.DownloadAsync(name, volumes[name], "later"));
        await run.WaitForJobAsync(first.Id, "completed");

        var waiting = await run.WaitForSetAsync(set => set.State == "waiting" && set.DownloadingVolumes.Count == names.Count - 1);
        // Added with "later", so they wait in the queue: the words say so rather than "still downloading" (UI.R23 (1)).
        Assert.Contains("waiting to download", waiting.StateText);
        Assert.DoesNotContain("still downloading", waiting.StateText);
        Assert.Equal(names[0], Path.GetFileName(waiting.FirstVolume));
        Assert.Equal(0, run.ToolRuns);
        Assert.Single(run.Archives.List());

        // Every volume but the last: still one set, still waiting.
        foreach (var job in held.SkipLast(1))
        {
            await run.Service.ResumeAsync(job.Id);
            await run.WaitForJobAsync(job.Id, "completed");
        }

        await run.WaitForSetAsync(set => set.State == "waiting" && set.DownloadingVolumes.Count == 1);
        Assert.Equal(0, run.ToolRuns);

        await run.Service.ResumeAsync(held[^1].Id);
        var done = await run.WaitForStateAsync("completed");

        Assert.Equal(Hash(payload), Hash(File.ReadAllBytes(run.PathOf("movie.bin"))));
        Assert.Equal(1, done.Attempts);
        Assert.Equal(names.Count, done.JobIds.Count);
        Assert.Single(run.Archives.List());

        // Late triggers and a user's request change nothing: the package's own completion,
        // another start's sweep, and an explicit extract.
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { JobIds = [first.Id] });
        await run.StartAgainAsync();
        await Task.Delay(300);

        Assert.Equal(1, run.ToolRuns);
        Assert.Equal(names.Concat(["movie.bin"]).Order(StringComparer.OrdinalIgnoreCase), run.AllFiles());
        Assert.Equal(1, run.Archives.List().Single().Attempts);
    }

    [SevenZipFact]
    public async Task AnOldStyleSetArrivingBackToFrontWaitsAndExtractsOnce()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });
        var payload = ArchiveTools.RandomBytes(300_000, 6);
        var volumes = RarBuilder.Rar4Volumes("big4.bin", payload, 3, newNumbering: false);

        var last = await run.DownloadAsync("old.r01", volumes[2]);
        await run.WaitForJobAsync(last.Id, "completed");
        var waiting = await run.WaitForSetAsync(set => set.State == "waiting");
        Assert.Contains("old.rar", waiting.MissingVolumes);
        Assert.Contains("old.r00", waiting.MissingVolumes);
        Assert.Contains("missing", waiting.StateText);
        Assert.Equal(0, run.ToolRuns);

        var middle = await run.DownloadAsync("old.r00", volumes[1]);
        await run.WaitForJobAsync(middle.Id, "completed");
        await run.WaitForSetAsync(set => set.State == "waiting" && set.MissingVolumes.SequenceEqual(["old.rar"]));
        Assert.Equal(0, run.ToolRuns);

        await run.DownloadAsync("old.rar", volumes[0]);
        var done = await run.WaitForStateAsync("completed");

        Assert.Equal(Hash(payload), Hash(File.ReadAllBytes(run.PathOf("big4.bin"))));
        Assert.Equal(1, run.ToolRuns);
        Assert.Equal(1, done.Attempts);
        Assert.Single(run.Archives.List());
    }

    [SevenZipFact]
    public async Task ALastVolumeOnlyTheToolKnowsIsMissingIsNamedAndARealDownloadOfItRunsTheSet()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var payload = ArchiveTools.RandomBytes(120_000, 8);
        var volumes = RarBuilder.Rar4Volumes("big.bin", payload, 2, newNumbering: true);
        var first = run.WriteFile("two.part1.rar", volumes[0]);

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = first });
        var failed = await run.WaitForFailureAsync("missingVolume");
        Assert.Contains("two.part2.rar", failed.MissingVolumes);
        Assert.Contains("two.part2.rar", failed.FailureMessage);
        Assert.Equal(["two.part1.rar"], run.AllFiles());
        Assert.Empty(run.StagingFolders());

        await run.DownloadAsync("two.part2.rar", volumes[1]);
        await run.WaitForStateAsync("completed");

        Assert.Equal(Hash(payload), Hash(File.ReadAllBytes(run.PathOf("big.bin"))));
    }

    [SevenZipFact]
    public async Task AGapInTheNamesIsNamedAndNothingRunsUntilItIsFilled()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var payload = ArchiveTools.RandomBytes(150_000, 9);
        var volumes = RarBuilder.Rar4Volumes("big.bin", payload, 3, newNumbering: true);
        var first = run.WriteFile("gap.part01.rar", volumes[0]);
        run.WriteFile("gap.part03.rar", volumes[2]);

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = first });
        var waiting = await run.WaitForSetAsync(set => set.State == "waiting" && set.MissingVolumes.Contains("gap.part02.rar"));
        Assert.Contains("gap.part02.rar", waiting.StateText);
        await Task.Delay(200);
        Assert.Equal(0, run.ToolRuns);

        run.WriteFile("gap.part02.rar", volumes[1]);
        await run.Archives.RetryAsync(waiting.Id);
        await run.WaitForStateAsync("completed");
        Assert.Equal(Hash(payload), Hash(File.ReadAllBytes(run.PathOf("big.bin"))));
    }

    [SevenZipFact]
    public async Task AFailedVolumeLeavesTheSetWaitingAndItsRetryRunsIt()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });
        var payload = ArchiveTools.RandomBytes(120_000, 10);
        var volumes = RarBuilder.Rar4Volumes("big.bin", payload, 2, newNumbering: true);

        var first = await run.DownloadAsync("f.part1.rar", volumes[0]);
        var second = await run.DownloadAsync("f.part2.rar", volumes[1], "later");
        await run.WaitForJobAsync(first.Id, "completed");
        await run.Service.CancelAsync(second.Id, false);
        var waiting = await run.WaitForSetAsync(set => set.State == "waiting" && set.FailedVolumes.Contains("f.part2.rar"));
        Assert.Contains("download failed", waiting.StateText);
        Assert.Equal(0, run.ToolRuns);

        await run.Service.ResumeAsync(second.Id);
        await run.WaitForStateAsync("completed");
        Assert.Equal(Hash(payload), Hash(File.ReadAllBytes(run.PathOf("big.bin"))));
    }

    [SevenZipFact]
    public async Task APackageThatFinishedWhileNothingListenedIsPickedUpOnceAtTheNextStart()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });
        await run.Archives.ShutdownAsync();
        var payload = ArchiveTools.RandomBytes(120_000, 14);
        var volumes = RarBuilder.Rar4Volumes("big.bin", payload, 2, newNumbering: true);

        var first = await run.DownloadAsync("away.part1.rar", volumes[0]);
        var second = await run.DownloadAsync("away.part2.rar", volumes[1]);
        await run.WaitForJobAsync(first.Id, "completed");
        await run.WaitForJobAsync(second.Id, "completed");
        await WaitUntilAsync(() => run.Service.GetCompletedPackages().Count == 1);

        await run.StartCoordinatorAsync();
        var done = await run.WaitForStateAsync("completed");
        await Task.Delay(200);

        Assert.Equal(1, done.Attempts);
        Assert.Equal(1, run.ToolRuns);
        Assert.Equal(Hash(payload), Hash(File.ReadAllBytes(run.PathOf("big.bin"))));
    }

    [SevenZipFact]
    public async Task TwoRequestsAtOnceClaimOneSetOnce()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var archive = run.WriteFile("once.rar", OneFileRar());

        await Task.WhenAll(
            run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive }),
            run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive }),
            run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive }));
        await run.WaitForStateAsync("completed");
        await Task.Delay(200);

        Assert.Equal(1, run.ToolRuns);
        Assert.Equal(["data.txt", "once.rar"], run.AllFiles());
    }

    // ----- Crash safety -----

    /// <summary>
    /// A kill at every save the coordinator makes, from the first record to the last, with archive
    /// deletion on so every step exists. Whatever the moment, the next start ends in one good copy.
    /// </summary>
    [SevenZipFact]
    public async Task AKillAtEveryDurableStepRecoversToExactlyOneCopy()
    {
        var reached = 0;
        for (var save = 1; save < 20; save++)
        {
            FreezableStore? store = null;
            await using var run = await ArchiveHarness.StartAsync(_server, wrapStore: inner => store = new FreezableStore(inner, save));
            await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true, DeleteArchives = true });
            var name = $"step{save}.rar";

            await run.DownloadAsync(name, OneFileRar());
            await WaitUntilAsync(() => store!.ReachedFreeze || run.Archives.List().Any(set => set.ArchivesDeleted));
            if (!store!.ReachedFreeze)
            {
                // Every save has been covered: this run went all the way without reaching this one.
                Assert.True(reached >= 4, $"Only {reached} durable steps were found.");
                return;
            }

            reached = save;
            await run.Archives.ShutdownAsync();
            await run.StartCoordinatorAsync();

            var done = await run.WaitForSetAsync(set => set is { State: "completed", ArchivesDeleted: true });
            Assert.Equal(["data.txt"], run.AllFiles());
            Assert.Equal(Text, File.ReadAllBytes(run.PathOf("data.txt")));
            Assert.Empty(run.StagingFolders());
            Assert.Single(run.Archives.List());
            Assert.NotNull(done.CompletedAt);
        }

        Assert.Fail("The run never finished within the steps tried.");
    }

    [SevenZipTheory]
    [InlineData("rename")]
    [InlineData("overwrite")]
    [InlineData("skip")]
    public async Task AKillBetweenTwoMovesIsUndoneSoNoPolicyDuplicatesOutput(string policy)
    {
        var inner = new SevenZipExtractor(new SevenZipExtractorOptions { ToolPath = ArchiveTools.SevenZipPath });
        FreezableStore? store = null;
        CrashingExtractor? crash = null;
        var options = new ArchiveCoordinatorOptions { ExtractorFactory = _ => crash! };
        await using var run = await ArchiveHarness.StartAsync(_server, options, wrap => store = new FreezableStore(wrap));
        crash = new CrashingExtractor(inner, store!, run.Downloads, report => report is { Phase: ExtractionPhase.Finalizing, FilesFinalized: 2 });
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { ConflictPolicy = policy });

        run.WriteFile("mine.txt", UserText);
        var archive = run.WriteFile("three.rar", RarBuilder.Rar5(("a.txt", Text), ("mine.txt", Text), ("z.txt", Text)));
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        await crash.Crashed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The disk now reads as a kill left it: part of the output is in place.
        Assert.True(File.Exists(run.PathOf("a.txt")));
        Assert.NotEmpty(run.StagingFolders());
        await run.Archives.ShutdownAsync();

        await run.StartCoordinatorAsync();
        var done = await run.WaitForStateAsync("completed");

        Assert.Empty(run.StagingFolders());
        Assert.Equal(1, run.ToolRuns);
        Assert.Equal(2, done.Attempts);
        var expected = new List<string> { "a.txt", "mine.txt", "three.rar", "z.txt" };
        if (policy == "rename") expected.Add("mine (1).txt");
        Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase), run.AllFiles());
        Assert.Equal(policy == "overwrite" ? Text : UserText, File.ReadAllBytes(run.PathOf("mine.txt")));
    }

    [SevenZipFact]
    public async Task AKillAfterSuccessIsRecordedKeepsTheOutputAndFinishesTheCleanup()
    {
        var inner = new SevenZipExtractor(new SevenZipExtractorOptions { ToolPath = ArchiveTools.SevenZipPath });
        FreezableStore? store = null;
        CrashingExtractor? crash = null;
        var options = new ArchiveCoordinatorOptions { ExtractorFactory = _ => crash! };
        await using var run = await ArchiveHarness.StartAsync(_server, options, wrap => store = new FreezableStore(wrap));
        crash = new CrashingExtractor(inner, store!, run.Downloads, crashAfterCommit: true);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true });

        var archive = run.WriteFile("once.rar", OneFileRar());
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        await crash.Crashed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(File.Exists(archive));
        Assert.NotEmpty(run.StagingFolders());
        await run.Archives.ShutdownAsync();

        await run.StartCoordinatorAsync();
        var done = await run.WaitForSetAsync(set => set is { State: "completed", ArchivesDeleted: true });

        Assert.Equal(0, run.ToolRuns);
        Assert.Equal(1, done.Attempts);
        Assert.Equal(["data.txt"], run.AllFiles());
        Assert.Empty(run.StagingFolders());
    }

    [SevenZipFact]
    public async Task AKillDuringExtractionWipesStagingGoesBackToWaitingAndRunsAgain()
    {
        var inner = new SevenZipExtractor(new SevenZipExtractorOptions { ToolPath = ArchiveTools.SevenZipPath });
        FreezableStore? store = null;
        CrashingExtractor? crash = null;
        var options = new ArchiveCoordinatorOptions { ExtractorFactory = _ => crash! };
        await using var run = await ArchiveHarness.StartAsync(_server, options, wrap => store = new FreezableStore(wrap));
        var seenExtracting = false;
        crash = new CrashingExtractor(
            inner,
            store!,
            run.Downloads,
            report => report is { Phase: ExtractionPhase.Extracting, Percent: 0 },
            atCrash: () =>
            {
                // What a killed extractor leaves: the file at its full size, with a tail of zeros.
                var staging = Assert.Single(run.StagingFolders());
                File.WriteAllBytes(Path.Combine(staging, "payload", "data.txt"), new byte[Text.Length]);
                seenExtracting = run.Archives.List().Single().State == "extracting";
            });

        var archive = run.WriteFile("once.rar", OneFileRar());
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        await crash.Crashed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(seenExtracting);
        Assert.Contains("\"extracting\"", run.RecordText(ArchiveId(run)), StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(run.StagingFolders());
        await run.Archives.ShutdownAsync();

        await run.StartCoordinatorAsync();
        var done = await run.WaitForStateAsync("completed");

        Assert.Equal(2, done.Attempts);
        Assert.Equal(Text, File.ReadAllBytes(run.PathOf("data.txt")));
        Assert.Empty(run.StagingFolders());
        Assert.Equal(["data.txt", "once.rar"], run.AllFiles());
    }

    private static Guid ArchiveId(ArchiveHarness run) => run.Archives.List().Single().Id;

    [RarFact]
    public async Task AToolKilledMidRunIsNeverASuccess()
    {
        System.Diagnostics.Process? tool = null;
        var killed = 0;
        var options = new ArchiveCoordinatorOptions
        {
            ExtractorFactory = _ => new SevenZipExtractor(new SevenZipExtractorOptions
            {
                ToolPath = ArchiveTools.SevenZipPath,
                ToolStarted = process =>
                {
                    if (process.StartInfo.ArgumentList[0] == "x" && Interlocked.Exchange(ref killed, 1) == 0)
                    {
                        tool = process;
                        process.Kill();
                    }
                },
            }),
        };
        await using var run = await ArchiveHarness.StartAsync(_server, options);
        var payload = ArchiveTools.RandomBytes(4_000_000, 11);
        var built = await ArchiveTools.BuildRar5Async(run.Root, "big.rar", [("big.bin", payload)]);
        var archive = run.WriteFile("big.rar", built["big.rar"]);

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        var failed = await run.WaitForSetAsync(set => set.State == "failed");

        Assert.NotNull(tool);
        Assert.Equal("unknown", failed.FailureKind);
        Assert.Equal(["big.rar"], run.AllFiles());
        Assert.Empty(run.StagingFolders());

        await run.Archives.RetryAsync(failed.Id);
        await run.WaitForStateAsync("completed");
        Assert.Equal(Hash(payload), Hash(File.ReadAllBytes(run.PathOf("big.bin"))));
    }

    [SevenZipFact]
    public async Task ACleanHostStopDuringExtractionLeavesItWaitingToRunAgain()
    {
        var fake = new FakeExtractor { HoldUntilCancelled = true };
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => fake });
        var archive = run.WriteFile("once.rar", OneFileRar());

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var id = ArchiveId(run);

        await run.Archives.ShutdownAsync();
        var record = JsonDocument.Parse(run.RecordText(id)).RootElement;
        Assert.Equal("waiting", record.GetProperty("state").GetString());
        Assert.False(record.TryGetProperty("failureKind", out _));

        await run.StartCoordinatorAsync();
        var done = await run.WaitForStateAsync("completed");
        Assert.Equal(2, done.Attempts);
        Assert.Equal(Text, File.ReadAllBytes(run.PathOf("data.txt")));
    }

    [SevenZipFact]
    public async Task ARestartWhileVolumesAreStillComingKeepsWaitingAndRunsWhenTheyArrive()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });
        var payload = ArchiveTools.RandomBytes(120_000, 12);
        var volumes = RarBuilder.Rar4Volumes("big.bin", payload, 2, newNumbering: true);
        var first = await run.DownloadAsync("r.part1.rar", volumes[0]);
        var second = await run.DownloadAsync("r.part2.rar", volumes[1], "later");
        await run.WaitForJobAsync(first.Id, "completed");
        await run.WaitForSetAsync(set => set.State == "waiting");

        await run.RestartAsync();

        await run.WaitForSetAsync(set => set.State == "waiting" && set.DownloadingVolumes.Contains("r.part2.rar"));
        await run.Service.ResumeAsync(second.Id);
        await run.WaitForStateAsync("completed");
        Assert.Equal(Hash(payload), Hash(File.ReadAllBytes(run.PathOf("big.bin"))));
    }

    // ----- Cancel, retry, remove -----

    [SevenZipFact]
    public async Task CancelStopsTheRunKeepsTheArchiveAndOnlyARetryStartsItAgain()
    {
        var fake = new FakeExtractor { HoldUntilCancelled = true };
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => fake });
        var archive = run.WriteFile("once.rar", OneFileRar());
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var cancelled = await run.Archives.CancelAsync(ArchiveId(run));
        Assert.True(cancelled.Ok, cancelled.Error);
        var stopped = await run.WaitForFailureAsync("cancelled");
        Assert.Contains("cancelled", stopped.StateText);
        Assert.True(File.Exists(archive));
        await Task.Delay(100);
        Assert.Equal(1, fake.Runs);

        await run.Archives.RetryAsync(stopped.Id);
        await run.WaitForSetAsync(set => set.State == "extracting" && set.Attempts == 2);
        await run.Archives.CancelAsync(stopped.Id);
        await run.WaitForFailureAsync("cancelled");
    }

    [RarFact]
    public async Task CancelKillsTheRealToolAndLeavesNothingBehind()
    {
        int? tool = null;
        ArchiveHarness? started = null;
        var options = new ArchiveCoordinatorOptions
        {
            ExtractorFactory = _ => new SevenZipExtractor(new SevenZipExtractorOptions
            {
                ToolPath = ArchiveTools.SevenZipPath,
                ToolStarted = process =>
                {
                    if (process.StartInfo.ArgumentList[0] != "x") return;
                    tool = process.Id;
                    started!.Archives.CancelAsync(started.Archives.List().Single().Id).GetAwaiter().GetResult();
                },
            }),
        };
        await using var run = await ArchiveHarness.StartAsync(_server, options);
        started = run;
        var payload = ArchiveTools.RandomBytes(32 * 1024 * 1024, 15);
        var built = await ArchiveTools.BuildRar5Async(run.Root, "big.rar", [("big.bin", payload)]);
        var archive = run.WriteFile("big.rar", built["big.rar"]);

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        var stopped = await run.WaitForSetAsync(set => set.State is "failed" or "completed", TimeSpan.FromSeconds(60));

        Assert.Equal("cancelled", stopped.FailureKind);
        Assert.NotNull(tool);
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(tool.Value));
        Assert.Equal(["big.rar"], run.AllFiles());
        Assert.Empty(run.StagingFolders());
    }

    [SevenZipFact]
    public async Task RemoveForgetsTheRecordAndItsPasswordAndNoFile()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var bytes = RarBuilder.Rar4Encrypted("data.txt", Text, Secret);
        var job = await run.DownloadAsync("kept.rar", bytes);
        await run.WaitForJobAsync(job.Id, "completed");
        var listed = await run.WaitForStateAsync("downloaded");
        await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest { SetId = listed.Id, Password = Secret });
        Assert.Contains(SecretProtector.Current.Prefix, run.RecordText(listed.Id));

        var removed = await run.Archives.RemoveAsync(listed.Id);

        Assert.True(removed.Ok, removed.Error);
        Assert.Empty(run.Archives.List());
        Assert.True(File.Exists(run.PathOf("kept.rar")));
        Assert.DoesNotContain(SecretProtector.Current.Prefix, run.RecordText(listed.Id));

        // The next start's sweep sees the same finished download and leaves it off the list.
        await run.RestartAsync();
        await Task.Delay(300);
        Assert.Empty(run.Archives.List());

        // Asking for it by name brings it back, and it asks for the password again.
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = run.PathOf("kept.rar") });
        await run.WaitForFailureAsync("passwordRequired");
    }

    [SevenZipFact]
    public async Task ARunningSetCannotBeRemoved()
    {
        var fake = new FakeExtractor { HoldUntilCancelled = true };
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => fake });
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = run.WriteFile("once.rar", OneFileRar()) });
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var removed = await run.Archives.RemoveAsync(ArchiveId(run));

        Assert.False(removed.Ok);
        Assert.Single(run.Archives.List());
    }

    [SevenZipFact]
    public async Task ARemovedDownloadTakesAnUnaskedSetWithIt()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var job = await run.DownloadAsync("gone.rar", OneFileRar(), "later");
        await run.WaitForStateAsync("downloading");

        await run.Service.RemoveAsync(job.Id, true);

        await WaitUntilAsync(() => run.Archives.List().Count == 0);
    }

    private static async Task<(JobDto First, JobDto Second, ArchiveSetDto Set)> FailOnAMissingVolumeAsync(ArchiveHarness run)
    {
        var first = await run.DownloadAsync("w.part1.rar", Text);
        var second = await run.DownloadAsync("w.part2.rar", Text);
        await run.WaitForJobAsync(first.Id, "completed");
        await run.WaitForJobAsync(second.Id, "completed");
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { JobIds = [first.Id] });
        var failed = await run.WaitForFailureAsync("missingVolume");
        Assert.Equal(2, failed.JobIds.Count);
        return (first, second, failed);
    }

    private static ArchiveCoordinatorOptions FailsOnMissingVolume() => new()
    {
        ExtractorFactory = _ => new FakeExtractor
        {
            FailOnRun = _ => new ExtractionException(ExtractionFailureKind.MissingVolume, "Missing volume: w.part3.rar", ["w.part3.rar"]),
        },
    };

    [Fact]
    public async Task AMissingVolumeIsSaidOnceInTheSetsWords()
    {
        await using var run = await ArchiveHarness.StartAsync(_server, FailsOnMissingVolume());
        var (_, _, failed) = await FailOnAMissingVolumeAsync(run);

        Assert.Equal("Missing volume: w.part3.rar", failed.StateText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASetThatFailedOnAMissingVolumeGoesWithItsLastDownload(bool deleteFiles)
    {
        await using var run = await ArchiveHarness.StartAsync(_server, FailsOnMissingVolume());
        var (first, second, failed) = await FailOnAMissingVolumeAsync(run);
        await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest { SetId = failed.Id, Password = Secret });

        // One download left is still a set to show.
        await run.Service.RemoveAsync(first.Id, deleteFiles);
        await WaitUntilAsync(() => run.Archives.Find(failed.Id)?.JobIds.SequenceEqual([second.Id]) == true);

        await run.Service.RemoveAsync(second.Id, deleteFiles);
        await WaitUntilAsync(() => run.Archives.List().Count == 0);

        Assert.False(File.Exists(Path.Combine(run.Paths.Root, "archive", "sets", $"{failed.Id:N}.json")));
        Assert.Equal(!deleteFiles, File.Exists(run.PathOf("w.part1.rar")));
    }

    [SevenZipFact]
    public async Task AFinishedSetGoesWithItsLastDownloadAndWhatItExtractedStays()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var job = await run.DownloadAsync("done.rar", OneFileRar("out.txt"));
        await run.WaitForJobAsync(job.Id, "completed");
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { JobIds = [job.Id] });
        await run.WaitForStateAsync("completed");

        await run.Service.RemoveAsync(job.Id, true);

        await WaitUntilAsync(() => run.Archives.List().Count == 0);
        Assert.Equal(["out.txt"], run.AllFiles());
    }

    [Fact]
    public async Task ASetPickedFromTheDiskHadNoDownloadToLoseAndStays()
    {
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => new FakeExtractor() });
        var other = await run.DownloadAsync("other.mp4", Text);
        await run.WaitForJobAsync(other.Id, "completed");
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = run.WriteFile("picked.rar", OneFileRar()) });
        var done = await run.WaitForStateAsync("completed");

        await run.Service.RemoveAsync(other.Id, false);
        await run.StartAgainAsync();
        await Task.Delay(300);

        Assert.Equal(done.Id, Assert.Single(run.Archives.List()).Id);
    }

    [Fact]
    public async Task RemovingADownloadLeavesItsRunningExtractionToFinish()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new FakeExtractor { Release = release };
        var quick = new FakeExtractor();
        var tools = new Queue<FakeExtractor>([tool, quick]);
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { MaxConcurrentExtractions = 2, ExtractorFactory = _ => tools.Dequeue() });
        var job = await run.DownloadAsync("slow.rar", OneFileRar());
        await run.WaitForJobAsync(job.Id, "completed");
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { JobIds = [job.Id] });
        await tool.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await run.Service.RemoveAsync(job.Id, false);

        // Another run finishing is when the Host next looks for sets without downloads.
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = run.WriteFile("picked.rar", OneFileRar()) });
        await quick.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => run.Archives.List().Any(set => set.Name == "picked" && set.State == "completed"));
        await Task.Delay(300);

        Assert.Equal("extracting", run.Archives.List().Single(set => set.Name == "slow").State);

        release.SetResult();
        await WaitUntilAsync(() => run.Archives.List().All(set => set.Name != "slow"));
        Assert.Equal(1, tool.Runs);
        Assert.Equal("picked", Assert.Single(run.Archives.List()).Name);
    }

    [Fact]
    public async Task SetsWhoseDownloadsWentBeforeThisStartAreTidiedAway()
    {
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => new FakeExtractor() });
        var job = await run.DownloadAsync("old.rar", OneFileRar());
        await run.WaitForJobAsync(job.Id, "completed");
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { JobIds = [job.Id] });
        var done = await run.WaitForStateAsync("completed");

        await run.Archives.ShutdownAsync();
        await run.Service.RemoveAsync(job.Id, false);
        await run.StartCoordinatorAsync(new ArchiveCoordinatorOptions { ExtractorFactory = _ => new FakeExtractor() });

        await WaitUntilAsync(() => run.Archives.List().Count == 0);
        Assert.False(File.Exists(Path.Combine(run.Paths.Root, "archive", "sets", $"{done.Id:N}.json")));
    }

    [Fact]
    public async Task ARunStoppedByAShutdownDoesntStartAgainIfItsDownloadWentMeanwhile()
    {
        var first = new FakeExtractor { HoldUntilCancelled = true };
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => first });
        var job = await run.DownloadAsync("stopped.rar", OneFileRar());
        await run.WaitForJobAsync(job.Id, "completed");
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { JobIds = [job.Id] });
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await run.Archives.ShutdownAsync();
        await run.Service.RemoveAsync(job.Id, false);
        var second = new FakeExtractor();
        await run.StartCoordinatorAsync(new ArchiveCoordinatorOptions { ExtractorFactory = _ => second });

        await WaitUntilAsync(() => run.Archives.List().Count == 0);
        await Task.Delay(200);
        Assert.Equal(0, second.Runs);
    }

    // ----- Safety -----

    public static TheoryData<string, byte[]> HostileArchives() => new()
    {
        { "parent-traversal", RarBuilder.Rar5Raw(RarBuilder.Rar5File("../../up.txt", Text)) },
        { "symlink", RarBuilder.Rar5Raw(RarBuilder.Rar5Link("link", 2, "..\\..\\target")) },
        { "junction", RarBuilder.Rar5Raw(RarBuilder.Rar5Link("jn", 3, "C:\\Windows")) },
        { "hardlink", RarBuilder.Rar5Raw(RarBuilder.Rar5Link("hard", 4, "other.txt")) },
    };

    [SevenZipTheory]
    [MemberData(nameof(HostileArchives))]
    public async Task UnsafeEntriesAreRefusedAndNothingIsWritten(string name, byte[] bytes)
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var archive = run.WriteFile($"{name}.rar", bytes);

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        var failed = await run.WaitForFailureAsync("unsafePath");

        Assert.Contains("Unsafe archive", failed.StateText);
        Assert.Equal([$"{name}.rar"], run.AllFiles());
        Assert.Empty(run.StagingFolders());
        Assert.False(File.Exists(Path.Combine(run.Root, "up.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(run.Root)!, "up.txt")));
    }

    [SevenZipFact]
    public async Task ACorruptedArchiveIsReportedAndKept()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true });
        var archive = run.WriteFile("bad.rar", RarBuilder.Rar4WithBadCrc("data.txt", Text));

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        var failed = await run.WaitForFailureAsync("corrupted");

        Assert.Contains("Corrupted archive", failed.StateText);
        Assert.Equal(["bad.rar"], run.AllFiles());
    }

    [Theory]
    [InlineData(ExtractionFailureKind.PasswordRequired, "passwordRequired", "Password required", true)]
    [InlineData(ExtractionFailureKind.WrongPassword, "wrongPassword", "Wrong password", true)]
    [InlineData(ExtractionFailureKind.MissingVolume, "missingVolume", "Missing volume", false)]
    [InlineData(ExtractionFailureKind.Corrupted, "corrupted", "Corrupted archive", false)]
    [InlineData(ExtractionFailureKind.UnsupportedVersion, "unsupportedVersion", "Unsupported archive version", false)]
    [InlineData(ExtractionFailureKind.ToolUnavailable, "toolUnavailable", "7-Zip missing", false)]
    [InlineData(ExtractionFailureKind.DiskSpace, "diskSpace", "Not enough disk space", false)]
    [InlineData(ExtractionFailureKind.Permission, "permission", "Permission refused", false)]
    [InlineData(ExtractionFailureKind.UnsafePath, "unsafePath", "Unsafe archive", false)]
    [InlineData(ExtractionFailureKind.Unknown, "unknown", "Extraction failed", false)]
    public async Task EveryFailureKindReachesClientsInItsOwnWordsAndKeepsTheArchive(ExtractionFailureKind kind, string name, string words, bool asks)
    {
        var fake = new FakeExtractor { FailWith = kind };
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => fake });
        var archive = run.WriteFile("any.rar", OneFileRar());

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        var failed = await run.WaitForFailureAsync(name);

        Assert.StartsWith(words, failed.StateText);
        Assert.Equal(asks, failed.NeedsPassword);
        Assert.True(File.Exists(archive));
    }

    // ----- Settings: each one has to change what happens -----

    [SevenZipTheory]
    [InlineData("rename", "from the archive")]
    [InlineData("overwrite", "from the archive")]
    [InlineData("skip", "the user's own file")]
    public async Task TheConflictPolicyDecidesAboutAnExistingFile(string policy, string expectedStart)
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { ConflictPolicy = policy });
        run.WriteFile("data.txt", UserText);
        var archive = run.WriteFile("once.rar", OneFileRar());

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        var done = await run.WaitForStateAsync("completed");

        var kept = Encoding.ASCII.GetString(File.ReadAllBytes(run.PathOf(policy == "rename" ? "data (1).txt" : "data.txt")));
        Assert.StartsWith(policy == "rename" ? "from the archive" : expectedStart, kept);
        switch (policy)
        {
            case "rename":
                Assert.Equal(UserText, File.ReadAllBytes(run.PathOf("data.txt")));
                Assert.Equal(1, done.FilesRenamed);
                break;
            case "overwrite":
                Assert.Equal(1, done.FilesOverwritten);
                Assert.Equal(["data.txt", "once.rar"], run.AllFiles());
                break;
            default:
                Assert.Equal(1, done.FilesSkipped);
                Assert.Equal(["data.txt", "once.rar"], run.AllFiles());
                break;
        }
    }

    [SevenZipFact]
    public async Task AnOverwriteNextToTheArchiveCannotReplaceTheArchive()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { ConflictPolicy = "overwrite" });
        var bytes = RarBuilder.Rar5(("self.rar", Text));
        var archive = run.WriteFile("self.rar", bytes);

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        await run.WaitForStateAsync("completed");

        Assert.Equal(bytes, File.ReadAllBytes(archive));
        Assert.Equal(Text, File.ReadAllBytes(run.PathOf("self (1).rar")));
    }

    [SevenZipFact]
    public async Task TheDestinationSettingDecidesWhereFilesGo()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);

        var next = run.WriteFile("beside.rar", OneFileRar("beside.txt"));
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = next });
        await run.WaitForSetAsync(set => set.Name == "beside" && set.State == "completed");
        Assert.True(File.Exists(run.PathOf("beside.txt")));

        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { Destination = "subfolder" });
        var named = run.WriteFile("namedset.rar", OneFileRar("named.txt"));
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = named });
        var inFolder = await run.WaitForSetAsync(set => set.Name == "namedset" && set.State == "completed");
        Assert.True(File.Exists(run.PathOf(@"namedset\named.txt")));
        Assert.Equal(run.PathOf("namedset"), inFolder.Destination);

        var chosen = Path.Combine(run.Root, "chosen é");
        var refused = await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { Folder = "relative\\path" });
        Assert.False(refused.Ok);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { Destination = "folder", Folder = chosen });
        var away = run.WriteFile("away.rar", OneFileRar("away.txt"));
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = away });
        await run.WaitForSetAsync(set => set.Name == "away" && set.State == "completed");
        Assert.True(File.Exists(Path.Combine(chosen, "away.txt")));
        Assert.False(File.Exists(run.PathOf("away.txt")));
        Assert.Empty(Directory.EnumerateDirectories(chosen));
    }

    [SevenZipFact]
    public async Task TurningOffFolderStructureFlattensTheOutput()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var archive = run.WriteFile("tree.rar", RarBuilder.Rar5(("deep/down/a.txt", Text), ("other/b.txt", Text)));
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { PreserveFolders = false });

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        await run.WaitForStateAsync("completed");

        Assert.Equal(["a.txt", "b.txt", "tree.rar"], run.AllFiles());
    }

    [SevenZipFact]
    public async Task KeepingFolderStructureIsTheDefault()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var archive = run.WriteFile("tree.rar", RarBuilder.Rar5(("deep/down/a.txt", Text)));

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        await run.WaitForStateAsync("completed");

        Assert.True(File.Exists(run.PathOf(@"deep\down\a.txt")));
    }

    [SevenZipFact]
    public async Task ArchivesAreDeletedOnlyWhenAskedAndOnlyAfterSuccessAndOnlyTheirOwn()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var payload = ArchiveTools.RandomBytes(120_000, 13);
        var volumes = RarBuilder.Rar4Volumes("big.bin", payload, 2, newNumbering: true);
        run.WriteFile("other.rar.txt", UserText);

        // Off by default: the archives stay.
        var settings = run.Archives.GetSettings();
        Assert.False(settings.Settings.DeleteArchives);
        var kept = run.WriteFile("keep.rar", OneFileRar("keep.txt"));
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = kept });
        await run.WaitForSetAsync(set => set.Name == "keep" && set.State == "completed");
        Assert.True(File.Exists(kept));

        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true });
        var first = run.WriteFile("set.part1.rar", volumes[0]);
        run.WriteFile("set.part2.rar", volumes[1]);
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = first });
        var done = await run.WaitForSetAsync(set => set.Name == "set" && set.ArchivesDeleted);

        Assert.Equal("completed", done.State);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(run.PathOf("set.part2.rar")));
        Assert.True(File.Exists(run.PathOf("other.rar.txt")));
        Assert.True(File.Exists(kept));
        Assert.Equal(Hash(payload), Hash(File.ReadAllBytes(run.PathOf("big.bin"))));
    }

    [SevenZipFact]
    public async Task AnArchiveIsKeptWhenItHoldsAFileThatWasSkipped()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true, ConflictPolicy = "skip" });
        run.WriteFile("data.txt", UserText);
        var archive = run.WriteFile("once.rar", OneFileRar());

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        var done = await run.WaitForStateAsync("completed");

        Assert.True(File.Exists(archive));
        Assert.False(done.ArchivesDeleted);
        Assert.Contains("kept", done.Note);
    }

    [SevenZipFact]
    public async Task AutoExtractIsOffByDefaultAndAFinishedDownloadJustSaysDownloaded()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var job = await run.DownloadAsync("plain.rar", OneFileRar(), "later");
        var downloading = await run.WaitForStateAsync("downloading");
        Assert.Contains("plain.rar", downloading.StateText);

        await run.Service.ResumeAsync(job.Id);
        await run.WaitForJobAsync(job.Id, "completed");
        var downloaded = await run.WaitForStateAsync("downloaded");
        await Task.Delay(200);

        Assert.Equal("Downloaded", downloaded.StateText);
        Assert.Equal(0, run.ToolRuns);
        Assert.Equal(["plain.rar"], run.AllFiles());
    }

    [SevenZipFact]
    public async Task TurningAutoExtractOnRunsWhatFinishesNextAndNotWhatFinishedBefore()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var before = await run.DownloadAsync("before.rar", OneFileRar("before.txt"));
        await run.WaitForJobAsync(before.Id, "completed");
        await run.WaitForSetAsync(set => set.Name == "before" && set.State == "downloaded");

        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });
        await run.DownloadAsync("after.rar", OneFileRar("after.txt"));
        await run.WaitForSetAsync(set => set.Name == "after" && set.State == "completed");
        await run.StartAgainAsync();
        await Task.Delay(300);

        Assert.True(File.Exists(run.PathOf("after.txt")));
        Assert.False(File.Exists(run.PathOf("before.txt")));
        Assert.Equal("downloaded", run.Archives.List().Single(set => set.Name == "before").State);
    }

    [SevenZipFact]
    public async Task FilesFromBeforeThisFeatureRanStayOutOfTheListUntilAskedFor()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var job = await run.DownloadAsync("older.rar", OneFileRar());
        await run.WaitForJobAsync(job.Id, "completed");
        await run.WaitForStateAsync("downloaded");

        // As if the feature had first run after that download: no record, a later start time.
        var id = ArchiveId(run);
        await run.Archives.ShutdownAsync();
        File.Delete(Path.Combine(run.Paths.Root, "archive", "sets", $"{id:N}.json"));
        File.Delete(Path.Combine(run.Paths.Root, "archive", "settings.json"));
        await run.StartCoordinatorAsync();
        await Task.Delay(300);
        Assert.Empty(run.Archives.List());

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { JobIds = [job.Id] });
        await run.WaitForStateAsync("completed");
    }

    [SevenZipFact]
    public async Task SettingsSurviveARestartAndAnOptionsSave()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var folder = Path.Combine(run.Root, "chosen");
        var updated = await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate
        {
            AutoExtract = true,
            Destination = "folder",
            Folder = folder,
            PreserveFolders = false,
            ConflictPolicy = "skip",
            DeleteArchives = true,
        });
        Assert.True(updated.Ok, updated.Error);

        var options = await run.Service.UpdateSettingsAsync(run.Service.GetSnapshot().Settings);
        Assert.True(options.Ok, options.Error);
        Assert.DoesNotContain("autoExtract", await File.ReadAllTextAsync(run.Paths.SettingsFile), StringComparison.OrdinalIgnoreCase);
        await run.RestartAsync();

        var settings = run.Archives.GetSettings().Settings;
        Assert.True(settings.AutoExtract);
        Assert.Equal("folder", settings.Destination);
        Assert.Equal(folder, settings.Folder);
        Assert.False(settings.PreserveFolders);
        Assert.Equal("skip", settings.ConflictPolicy);
        Assert.True(settings.DeleteArchives);
    }

    [Theory]
    [InlineData("somewhere", null, null)]
    [InlineData(null, "sideways", null)]
    [InlineData("folder", null, "relative\\folder")]
    public async Task BadSettingsAreRefusedAndChangeNothing(string? destination, string? conflict, string? folder)
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var before = JsonSerializer.Serialize(run.Archives.GetSettings().Settings);

        var result = await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { Destination = destination, ConflictPolicy = conflict, Folder = folder });

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Equal(before, JsonSerializer.Serialize(run.Archives.GetSettings().Settings));
    }

    [SevenZipFact]
    public async Task WithoutTheBundled7ZipExtractionFailsPlainlyAndTheStatusSaysSo()
    {
        var options = new ArchiveCoordinatorOptions { SevenZipPath = Path.Combine(Path.GetTempPath(), "inbound-no-7zip", "7z.exe") };
        await using var run = await ArchiveHarness.StartAsync(_server, options, useMachineTool: true);

        var none = run.Archives.GetSettings().Tool;
        Assert.False(none.Found);
        Assert.Equal(SevenZipTool.MissingMessage, none.Message);

        var archive = run.WriteFile("once.rar", OneFileRar());
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        var failed = await run.WaitForFailureAsync("toolUnavailable");
        Assert.Contains("7-Zip", failed.FailureMessage);
        Assert.Equal(["once.rar"], run.AllFiles());
    }

    [SevenZipFact]
    public async Task TheBundled7ZipIsFoundAndExtracts()
    {
        await using var run = await ArchiveHarness.StartAsync(_server, useMachineTool: true);
        var tool = run.Archives.GetSettings().Tool;
        Assert.True(tool.Found);
        Assert.Equal(ArchiveTools.SevenZipPath, tool.Path);
        Assert.Contains("7-Zip", tool.Message);

        var archive = run.WriteFile("once.rar", OneFileRar());
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        await run.WaitForStateAsync("completed");
        Assert.Equal(Text, File.ReadAllBytes(run.PathOf("data.txt")));
    }

    [SevenZipFact]
    public async Task TooLittleDiskSpaceStopsTheRunBeforeAnythingIsWritten()
    {
        var options = new ArchiveCoordinatorOptions { AvailableBytes = _ => 16 };
        await using var run = await ArchiveHarness.StartAsync(_server, options);
        var archive = run.WriteFile("once.rar", OneFileRar());

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        await run.WaitForFailureAsync("diskSpace");

        Assert.Equal(["once.rar"], run.AllFiles());
        Assert.Equal(0, run.ToolRuns);
    }

    [SevenZipFact]
    public async Task TheLimitsOnAnArchiveAreApplied()
    {
        var options = new ArchiveCoordinatorOptions { Limits = new ExtractionLimits { MaxEntries = 1 } };
        await using var run = await ArchiveHarness.StartAsync(_server, options);
        var archive = run.WriteFile("many.rar", RarBuilder.Rar5(("a.txt", Text), ("b.txt", Text)));

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        var failed = await run.WaitForFailureAsync("unsafePath");

        Assert.Contains("entries", failed.FailureMessage);
        Assert.Equal(0, run.ToolRuns);
    }

    // ----- What it claims -----

    [SevenZipFact]
    public async Task AnOrdinarySingleRarExtractsThroughAnExplicitRequestByJob()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var job = await run.DownloadAsync("plain.rar", OneFileRar());
        await run.WaitForJobAsync(job.Id, "completed");

        var asked = await run.Archives.ExtractAsync(new ArchiveExtractRequest { JobIds = [job.Id] });

        Assert.True(asked.Ok, asked.Error);
        var done = await run.WaitForStateAsync("completed");
        Assert.Contains("Extraction completed", done.StateText);
        Assert.Equal(run.Downloads, done.Destination);
        Assert.Equal([job.Id], done.JobIds);
        Assert.Equal(Text, File.ReadAllBytes(run.PathOf("data.txt")));
    }

    [SevenZipFact]
    public async Task ADownloadedZipExtractsByItselfAndOtherFilesAreLeftAlone()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });

        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("data.txt").Open();
            entry.Write(Text);
        }

        var archive = await run.DownloadAsync("package.zip", buffer.ToArray());
        var plain = await run.DownloadAsync("notes.txt", Text);
        foreach (var job in new[] { archive, plain }) await run.WaitForJobAsync(job.Id, "completed");

        var done = await run.WaitForStateAsync("completed");
        Assert.Equal([archive.Id], done.JobIds);
        Assert.Equal(Text, File.ReadAllBytes(run.PathOf("data.txt")));
        Assert.Single(run.Archives.List());

        var byJob = await run.Archives.ExtractAsync(new ArchiveExtractRequest { JobIds = [plain.Id] });
        Assert.False(byJob.Ok);
        Assert.Contains("not an archive", Assert.Single(byJob.Ignored).Reason);
    }

    [SevenZipFact]
    public async Task APathThatIsNotAbsoluteOrNotThereIsRefused()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);

        Assert.False((await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = "relative.rar" })).Ok);
        var absent = await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = run.PathOf("absent.rar") });
        Assert.False(absent.Ok);
        Assert.Contains("not there", absent.Error);
        Assert.Empty(run.Archives.List());
        Assert.False((await run.Archives.ExtractAsync(new ArchiveExtractRequest())).Ok);
    }

    [SevenZipTheory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task ExtractionsAreBoundedOneAtATimeByDefault(int limit, int expectedPeak)
    {
        var fake = new FakeExtractor { Hold = TimeSpan.FromMilliseconds(200) };
        var options = new ArchiveCoordinatorOptions { ExtractorFactory = _ => fake, MaxConcurrentExtractions = limit };
        await using var run = await ArchiveHarness.StartAsync(_server, options);

        foreach (var name in new[] { "a.rar", "b.rar", "c.rar", "d.rar" })
        {
            await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = run.WriteFile(name, OneFileRar()) });
        }

        await WaitUntilAsync(() => run.Archives.List().Count(set => set.State == "completed") == 4);
        Assert.Equal(expectedPeak, fake.Peak);
        Assert.Equal(4, fake.Runs);
    }

    [Fact]
    public void TheDefaultBoundIsOne() => Assert.Equal(1, new ArchiveCoordinatorOptions().MaxConcurrentExtractions);

    [Fact]
    public async Task ACorruptSetDocumentIsSetAsideAndTheRestLoad()
    {
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => new FakeExtractor() });
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = run.WriteFile("ok.rar", OneFileRar()) });
        await run.WaitForStateAsync("completed");
        await run.Archives.ShutdownAsync();
        var bad = Path.Combine(run.Paths.Root, "archive", "sets", $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(bad, "{ not json");

        await run.StartCoordinatorAsync(new ArchiveCoordinatorOptions { ExtractorFactory = _ => new FakeExtractor() });

        Assert.False(File.Exists(bad));
        Assert.Contains(Directory.EnumerateFiles(run.Paths.CorruptDirectory), file => file.Contains("archive-", StringComparison.Ordinal));
        Assert.Single(run.Archives.List());
    }

    // ----- Identity -----

    [Fact]
    public async Task OneSetKeepsOneIdWhateverOrderItsVolumesAreSeenIn()
    {
        var fake = new FakeExtractor { HoldUntilCancelled = true };
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => fake });

        // part10 alone suggests no padding, and part01 then corrects that.
        var late = run.WriteFile("v.part10.rar", Text);
        var first = await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest { FilePath = late, Password = Secret });
        var other = await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest { FilePath = run.PathOf("v.part01.rar"), Password = Secret });
        var upper = await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest { FilePath = run.PathOf("V.PART10.RAR"), Password = Secret });

        Assert.Equal(first.Set!.Id, other.Set!.Id);
        if (OperatingSystem.IsWindows())
        {
            // One file to NTFS, so one set.
            Assert.Equal(first.Set.Id, upper.Set!.Id);
            Assert.Single(run.Archives.List());
            return;
        }

        // On Linux V.PART10.RAR is another file than v.part10.rar, for the same volume number, so it is another set:
        // one set never holds two files for one volume (ArchiveVolumes.Group).
        Assert.NotEqual(first.Set.Id, upper.Set!.Id);
        Assert.Equal(2, run.Archives.List().Count);
    }

    // ----- IPC, in process. RuntimeArchiveHostTests goes through a real pipe. -----

    [Fact]
    public async Task EveryRequestAndReplyGoesThroughTheDispatcherWithoutAPassword()
    {
        var fake = new FakeExtractor { FailWith = ExtractionFailureKind.PasswordRequired };
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => fake });
        var dispatcher = new ArchiveIpcDispatcher(run.Archives);
        var pushed = new List<string>();
        run.Archives.Changed += (_, change) => { lock (pushed) pushed.Add(JsonSerializer.Serialize(change, IpcJson.Options)); };
        var archive = run.WriteFile("once.rar", OneFileRar());

        async Task<IpcMessage> Send<T>(string type, T payload)
        {
            var request = IpcMessage.Create(type, payload);
            var reply = await dispatcher.DispatchAsync(request, CancellationToken.None);
            Assert.Equal(request.Id, reply.ReplyTo);
            Assert.DoesNotContain(Secret, JsonSerializer.Serialize(reply, IpcJson.Options));
            return reply;
        }

        Assert.True(ArchiveIpcDispatcher.Handles(ArchiveMessageTypes.Extract));
        Assert.False(ArchiveIpcDispatcher.Handles(MessageTypes.JobAdd));

        var settings = (await Send(ArchiveMessageTypes.SettingsGet, new { })).GetPayload<ArchiveSettingsResult>();
        Assert.False(settings.Settings.AutoExtract);
        Assert.Equal("rename", settings.Settings.ConflictPolicy);
        Assert.Equal("nextToArchive", settings.Settings.Destination);

        var set = (await Send(ArchiveMessageTypes.SettingsSet, new ArchiveSettingsUpdate { ConflictPolicy = "skip" })).GetPayload<ArchiveSettingsResult>();
        Assert.Equal("skip", set.Settings.ConflictPolicy);

        var extract = (await Send(ArchiveMessageTypes.Extract, new ArchiveExtractRequest { FilePath = archive, Password = Secret })).GetPayload<ArchiveExtractResult>();
        var id = Assert.Single(extract.Sets).Id;
        var asking = await run.WaitForFailureAsync("passwordRequired");
        Assert.Equal(id, asking.Id);

        var listed = (await Send(ArchiveMessageTypes.List, new { })).GetPayload<ArchiveListResult>();
        Assert.Equal(id, Assert.Single(listed.Sets).Id);

        fake.FailWith = null;
        var password = (await Send(ArchiveMessageTypes.SetPassword, new ArchiveSetPasswordRequest { SetId = id, Password = Secret })).GetPayload<ArchiveActionResult>();
        Assert.True(password.Ok, password.Error);
        await run.WaitForStateAsync("completed");

        var retried = (await Send(ArchiveMessageTypes.Retry, new ArchiveActionRequest { SetId = id })).GetPayload<ArchiveActionResult>();
        Assert.True(retried.Ok, retried.Error);
        await run.WaitForSetAsync(candidate => candidate is { State: "completed", Attempts: 3 });

        var cancelled = (await Send(ArchiveMessageTypes.Cancel, new ArchiveActionRequest { SetId = id })).GetPayload<ArchiveActionResult>();
        Assert.False(cancelled.Ok);

        var removed = (await Send(ArchiveMessageTypes.Remove, new ArchiveActionRequest { SetId = id })).GetPayload<ArchiveActionResult>();
        Assert.True(removed.Ok, removed.Error);
        Assert.Empty(run.Archives.List());

        var unknown = (await Send("archive.nonsense", new { })).GetPayload<ArchiveActionResult>();
        Assert.False(unknown.Ok);

        var unreadable = await dispatcher.DispatchAsync(new IpcMessage(Protocol.Version, ArchiveMessageTypes.SetPassword, "x", null, JsonDocument.Parse($"{{\"setId\":\"{Secret}\"}}").RootElement), CancellationToken.None);
        Assert.Equal(ArchiveMessageTypes.ActionResult, unreadable.Type);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(unreadable, IpcJson.Options));

        lock (pushed)
        {
            Assert.NotEmpty(pushed);
            Assert.All(pushed, frame => Assert.DoesNotContain(Secret, frame));
        }
    }

    [Fact]
    public async Task AChangeThatCannotBeSavedIsAnsweredAsAFailureEveryClientCanRead()
    {
        FreezableStore? store = null;
        var options = new ArchiveCoordinatorOptions { ExtractorFactory = _ => new FakeExtractor() };
        await using var run = await ArchiveHarness.StartAsync(_server, options, wrap => store = new FreezableStore(wrap));
        var archive = run.WriteFile("once.rar", OneFileRar());
        store!.Freeze();
        var dispatcher = new ArchiveIpcDispatcher(run.Archives);

        var reply = await dispatcher.DispatchAsync(IpcMessage.Create(ArchiveMessageTypes.Extract, new ArchiveExtractRequest { FilePath = archive, Password = Secret }), CancellationToken.None);

        var result = reply.GetPayload<ArchiveExtractResult>();
        Assert.False(result.Ok);
        Assert.Contains("could not be saved", result.Error);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(reply, IpcJson.Options));
    }

    [Fact]
    public async Task APasswordWithALineBreakIsRefusedWithoutRepeatingIt()
    {
        await using var run = await ArchiveHarness.StartAsync(_server, new ArchiveCoordinatorOptions { ExtractorFactory = _ => new FakeExtractor() });
        var archive = run.WriteFile("once.rar", OneFileRar());
        const string bad = "secret-part\nother-part";

        var result = await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive, Password = bad });

        Assert.False(result.Ok);
        Assert.Contains("line break", result.Error);
        Assert.DoesNotContain("secret-part", result.Error);
        Assert.Empty(run.Archives.List());
    }

    [Fact]
    public void PasswordsCarryTheirOwnPurpose()
    {
        var blob = SecretProtector.ProtectArchivePassword(Secret);

        Assert.DoesNotContain(Secret, blob);
        Assert.Equal(Secret, SecretProtector.UnprotectArchivePassword(blob));
        Assert.Null(SecretProtector.Unprotect(blob));
        Assert.Null(SecretProtector.UnprotectProxy(blob));
        Assert.Null(SecretProtector.UnprotectUrl(blob));
        Assert.Null(SecretProtector.UnprotectArchivePassword(SecretProtector.Protect(Secret)));
    }

    private static async Task<Guid> WaitForPackageAsync(ArchiveHarness run, Guid jobId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (run.Service.FindPackageOf(jobId) is { } packageId) return packageId;
            await Task.Delay(20);
        }

        throw new TimeoutException("The download never joined a package.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.Fail("The condition never held.");
    }
}
