using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Integration.Tests;

public sealed class RemoteJobRulesTests
{
    [Theory]
    [InlineData(RemoteJobState.Intended, true, false)]
    [InlineData(RemoteJobState.Submitting, false, false)]
    [InlineData(RemoteJobState.SubmissionUnknown, true, false)]
    [InlineData(RemoteJobState.Waiting, true, false)]
    [InlineData(RemoteJobState.AwaitingSelection, false, false)]
    [InlineData(RemoteJobState.LocalStarted, false, true)]
    [InlineData(RemoteJobState.Failed, false, true)]
    [InlineData(RemoteJobState.Blocked, true, false)]
    [InlineData(RemoteJobState.Removed, false, true)]
    public void EachStatesPlace(RemoteJobState state, bool polled, bool terminal)
    {
        Assert.Equal(polled, RemoteJobRules.IsPolled(state));
        Assert.Equal(terminal, RemoteJobRules.IsTerminal(state));
    }

    [Fact]
    public void OnlyTheFirstOfEachIdCountsAsAFileToChoose()
    {
        static RemoteFile File(string id, string path, long size = 10, bool infected = false)
            => new() { FileId = id, Path = path, Size = size, Infected = infected };

        Assert.Equal(0, RemoteChildPlanner.UsableCount([]));
        Assert.Equal(1, RemoteChildPlanner.UsableCount([File("1", "a/x.bin"), File("1", "a/y.bin")]));
        Assert.Equal(2, RemoteChildPlanner.UsableCount([File("1", "a/x.bin"), File("2", "a/y.bin")]));
        // The plan skips a repeated id whatever the repeat holds, so a usable repeat can't make the count two.
        Assert.Equal(1, RemoteChildPlanner.UsableCount([File("1", "a/x.bin", 0), File("1", "a/y.bin"), File("2", "a/z.bin")]));
        Assert.Equal(1, RemoteChildPlanner.UsableCount([File("1", "../x.bin"), File("2", "a/x.bin", infected: true), File("3", "a/y.bin"), File("4", "a/e.txt", 0)]));
    }

    [Fact]
    public void ARestartOnlyChangesSubmitting()
    {
        foreach (var state in Enum.GetValues<RemoteJobState>())
        {
            var expected = state == RemoteJobState.Submitting ? RemoteJobState.SubmissionUnknown : state;
            Assert.Equal(expected, RemoteJobRules.AfterRestart(state));
        }
    }

    [Theory]
    // Nothing clear came back, so it may have been accepted.
    [InlineData(RemoteFailureKind.Network, RemoteSubmitOutcome.SubmissionUnknown)]
    [InlineData(RemoteFailureKind.BadResponse, RemoteSubmitOutcome.SubmissionUnknown)]
    [InlineData(RemoteFailureKind.ServiceError, RemoteSubmitOutcome.SubmissionUnknown)]
    [InlineData(RemoteFailureKind.AlreadyExists, RemoteSubmitOutcome.SubmissionUnknown)]
    [InlineData(RemoteFailureKind.RateLimited, RemoteSubmitOutcome.TryAgainLater)]
    [InlineData(RemoteFailureKind.AccountBusy, RemoteSubmitOutcome.Blocked)]
    [InlineData(RemoteFailureKind.NotCached, RemoteSubmitOutcome.NeedsApproval)]
    [InlineData(RemoteFailureKind.AccountLimit, RemoteSubmitOutcome.FailedAndStopCreating)]
    [InlineData(RemoteFailureKind.Rejected, RemoteSubmitOutcome.Failed)]
    [InlineData(RemoteFailureKind.NotFound, RemoteSubmitOutcome.Failed)]
    [InlineData(RemoteFailureKind.LinkPasswordRequired, RemoteSubmitOutcome.Failed)]
    [InlineData(RemoteFailureKind.Authentication, RemoteSubmitOutcome.Failed)]
    public void WhatARefusedSubmissionLeadsTo(RemoteFailureKind kind, RemoteSubmitOutcome expected)
        => Assert.Equal(expected, RemoteJobRules.AfterSubmitFailure(kind));

    [Fact]
    public void EveryFailureKindHasAnAnswer()
    {
        foreach (var kind in Enum.GetValues<RemoteFailureKind>())
        {
            Assert.True(Enum.IsDefined(RemoteJobRules.AfterSubmitFailure(kind)));
        }
    }

    [Theory]
    [InlineData(RemoteStage.Ready, RemoteItemOutcome.Ready)]
    [InlineData(RemoteStage.Failed, RemoteItemOutcome.Failed)]
    [InlineData(RemoteStage.Queued, RemoteItemOutcome.KeepWaiting)]
    [InlineData(RemoteStage.MetadataPending, RemoteItemOutcome.KeepWaiting)]
    [InlineData(RemoteStage.Processing, RemoteItemOutcome.KeepWaiting)]
    [InlineData(RemoteStage.Stalled, RemoteItemOutcome.KeepWaiting)]
    // Unknown never counts as ready.
    [InlineData(RemoteStage.Unknown, RemoteItemOutcome.KeepWaiting)]
    public void WhatAnItemsStageLeadsTo(RemoteStage stage, RemoteItemOutcome expected)
        => Assert.Equal(expected, RemoteJobRules.AfterItemRead(stage));

    [Theory]
    [InlineData(null, 30)]
    [InlineData(0L, 15)]
    [InlineData(5L, 15)]
    [InlineData(15L, 15)]
    [InlineData(40L, 40)]
    [InlineData(60L, 60)]
    [InlineData(3600L, 60)]
    public void PollingFollowsTheEtaWithinItsLimits(long? eta, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), RemoteJobRules.NextPoll(eta));

    [Fact]
    public void PollingSlowsWhileNothingChanges()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), RemoteJobRules.NextPoll(null, 9));
        Assert.Equal(TimeSpan.FromSeconds(60), RemoteJobRules.NextPoll(null, 10));
        Assert.Equal(TimeSpan.FromSeconds(120), RemoteJobRules.NextPoll(null, 20));
        Assert.Equal(RemoteJobRules.LongestIdlePoll, RemoteJobRules.NextPoll(null, 500));
        Assert.Equal(RemoteJobRules.LongestIdlePoll, RemoteJobRules.NextPoll(null, 100_000));

        // The bounds are the provider's. Another provider's are its own, and only it knows why.
        var other = new RemoteTransferPolicy { ShortestPoll = TimeSpan.FromSeconds(2), LongestPoll = TimeSpan.FromSeconds(4), DefaultPoll = TimeSpan.FromSeconds(3) };
        Assert.Equal(TimeSpan.FromSeconds(2), RemoteJobRules.NextPoll(1, 0, other));
        Assert.Equal(TimeSpan.FromSeconds(4), RemoteJobRules.NextPoll(100, 0, other));
        Assert.Equal(TimeSpan.FromSeconds(3), RemoteJobRules.NextPoll(null, 0, other));
        Assert.Equal(TimeSpan.Zero, new RemoteTransferPolicy().CreationSpacing);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(3600, 3600)]
    [InlineData(99_999_999, 3600)]
    [InlineData(0, 60)]
    [InlineData(-5, 60)]
    public void ARateLimitHoldHasALimitToo(int asked, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), RemoteJobRules.RateLimitHold(TimeSpan.FromSeconds(asked)));

    [Fact]
    public void ARateLimitThatAsksForNothingStillHolds()
        => Assert.Equal(TimeSpan.FromSeconds(60), RemoteJobRules.RateLimitHold(null));

    [Theory]
    [InlineData(0, 0, 0, 0, 0, "Its downloads are no longer on the list")]
    [InlineData(3, 3, 0, 0, 0, "Completed")]
    [InlineData(3, 1, 1, 0, 1, "1 of 3 local downloads failed")]
    [InlineData(3, 1, 0, 1, 1, "Downloading locally")]
    [InlineData(3, 1, 0, 2, 0, "Stopped locally")]
    [InlineData(3, 0, 0, 1, 0, "Waiting to download locally")]
    public void ALocalStartedJobGoesByWhatItsDownloadsAreDoing(int total, int completed, int failed, int paused, int active, string expected)
    {
        var record = new RemoteJobRecord { Id = Guid.NewGuid(), Provider = "x", Kind = RemoteItemKind.Torrent, State = RemoteJobState.LocalStarted };
        Assert.Equal(expected, RemoteJobRules.StatusText(record, "X", new RemoteLocalSummary(total, completed, failed, paused, active)));
    }

    [Theory]
    [InlineData(100, 100, null, null, true)]
    [InlineData(100, 99, null, null, false)]
    [InlineData(0, 500, null, null, false)]
    [InlineData(null, null, 1000L, 1000L, true)]
    [InlineData(null, null, 0L, 500L, false)]
    public void ASitesDailyLimitIsUsedUpOnlyWhenItHasOne(int? links, int? linksUsed, long? bytes, long? bytesUsed, bool expected)
    {
        var hoster = new RemoteHoster { Name = "x", DailyLinkLimit = links, DailyLinksUsed = linksUsed, DailyBandwidthLimit = bytes, DailyBandwidthUsed = bytesUsed };
        Assert.Equal(expected, RemoteJobRules.DailyLimitUsedUp(hoster));
    }

    [Fact]
    public void BackoffDoublesAndStopsAtFifteenMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), RemoteJobRules.Backoff(1, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(60), RemoteJobRules.Backoff(2, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(120), RemoteJobRules.Backoff(3, 0.5));
        Assert.Equal(TimeSpan.FromMinutes(15), RemoteJobRules.Backoff(6, 0.5));
        Assert.Equal(TimeSpan.FromMinutes(15), RemoteJobRules.Backoff(1000, 1));
    }

    [Fact]
    public void BackoffIsJittered()
    {
        Assert.Equal(TimeSpan.FromSeconds(24), RemoteJobRules.Backoff(1, 0));
        Assert.Equal(TimeSpan.FromSeconds(36), RemoteJobRules.Backoff(1, 1));
        Assert.Equal(TimeSpan.FromMinutes(12), RemoteJobRules.Backoff(100, 0));
    }

    [Fact]
    public void AHashMatchesItsAlternativesInAnyCase()
    {
        var item = new RemoteItem
        {
            Ref = new RemoteItemRef("p", RemoteItemKind.WebDownload, "1"),
            Hash = "AAA",
            AlternativeHashes = ["bbb"],
        };
        Assert.True(RemoteJobRules.SameHash(item, "aaa"));
        Assert.True(RemoteJobRules.SameHash(item, "BBB"));
        Assert.False(RemoteJobRules.SameHash(item, "ccc"));
    }

    [Theory]
    [InlineData(RemoteJobState.Intended, null, "Checking P...")]
    [InlineData(RemoteJobState.Waiting, RemoteStage.Queued, "Added to P queue")]
    [InlineData(RemoteJobState.Waiting, RemoteStage.MetadataPending, "Waiting for P to list the files")]
    [InlineData(RemoteJobState.Waiting, RemoteStage.Processing, "Processing on P")]
    [InlineData(RemoteJobState.Waiting, RemoteStage.Stalled, "Stalled on P")]
    [InlineData(RemoteJobState.LocalStarted, null, "Its downloads are no longer on the list")]
    [InlineData(RemoteJobState.AwaitingSelection, null, "Ready to download. Choose the files.")]
    public void TheWordsForEachState(RemoteJobState state, RemoteStage? stage, string expected)
    {
        var record = new RemoteJobRecord { Id = Guid.NewGuid(), Provider = "p", Kind = RemoteItemKind.Torrent, State = state, Stage = stage };
        Assert.Equal(expected, RemoteJobRules.StatusText(record, "P"));
    }

    [Fact]
    public void ARowNothingWasSentForSaysSoAndAsksForNoApproval()
    {
        var held = new RemoteJobRecord { Id = Guid.NewGuid(), Provider = "p", Kind = RemoteItemKind.WebDownload, State = RemoteJobState.Intended, ApprovalNeeded = true, HeldForApproval = true };
        var notCached = held with { HeldForApproval = false };

        Assert.Equal("Copied from the clipboard. Nothing has been sent to P.", RemoteJobRules.StatusText(held, "P"));
        Assert.Equal("Not cached on P, so it wasn't started.", RemoteJobRules.StatusText(notCached, "P"));
        Assert.DoesNotContain("approv", RemoteJobRules.StatusText(held, "P"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("approv", RemoteJobRules.StatusText(notCached, "P"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnknownStateShowsAsItself()
    {
        var record = new RemoteJobRecord { Id = Guid.NewGuid(), Provider = "p", Kind = RemoteItemKind.Torrent, State = RemoteJobState.Waiting, Stage = RemoteStage.Unknown, RawState = "brand new word" };
        Assert.Equal("P reports: brand new word", RemoteJobRules.StatusText(record, "P"));
    }

    // ---- Planning local jobs ------------------------------------------------------------------

    private static RemoteItem Item(params RemoteFile[] files) => new()
    {
        Ref = new RemoteItemRef("p", RemoteItemKind.Torrent, "9"),
        Name = "Pack",
        Stage = RemoteStage.Ready,
        Files = files,
    };

    private static RemoteFile File(string id, string path, long size = 10, bool infected = false)
        => new() { FileId = id, Path = path, Size = size, Infected = infected };

    private static RemoteJobRecord Record(RemoteSelectionMode mode = RemoteSelectionMode.All, params string[] chosen) => new()
    {
        Id = Guid.NewGuid(),
        Provider = "p",
        Kind = RemoteItemKind.Torrent,
        SelectionMode = mode,
        SelectedFileIds = chosen,
        QueueId = 3,
        StartMode = "later",
    };

    private static RemoteChildPlanner.Plan Build(RemoteJobRecord record, RemoteItem item)
        => RemoteChildPlanner.Build(record, item, "P", 4, @"C:\dest");

    [Fact]
    public void IdenticalNamesInDifferentFoldersBothSurvive()
    {
        var plan = Build(Record(), Item(File("0", "Pack/a/same.bin"), File("1", "Pack/b/same.bin")));
        Assert.Equal(2, plan.Requests.Count);
        Assert.Equal(["same.bin", "same.bin"], plan.Requests.Select(request => request.FileName));
        Assert.Equal(Path.Combine("Pack", "a", "same.bin"), plan.Requests[0].Source.RelativePath);
        Assert.Equal(Path.Combine("Pack", "b", "same.bin"), plan.Requests[1].Source.RelativePath);
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void AFlatItemGoesToTheRoot()
    {
        var plan = Build(Record(), Item(File("0", "flat.bin")));
        Assert.Equal("flat.bin", plan.Requests.Single().Source.RelativePath);
        Assert.Equal("flat.bin", plan.Requests.Single().FileName);
    }

    [Theory]
    [InlineData("name.bin/name.bin", "name.bin")]
    [InlineData("NAME.BIN/name.bin", "name.bin")]
    [InlineData("Game.v1.02.Demo-GROUP/Game.v1.02.Demo-GROUP", "Game.v1.02.Demo-GROUP")]
    [InlineData("Game.v1.02.Demo-GROUP.rar/Game.v1.02.Demo-GROUP.rar", @"Game.v1.02.Demo-GROUP\Game.v1.02.Demo-GROUP.rar")]
    [InlineData("Game.v1.02.part01.rar/Game.v1.02.part01.rar", @"Game.v1.02\Game.v1.02.part01.rar")]
    public void ALoneFileGetsNoFolderWithAnExtension(string path, string expected)
    {
        var request = Build(Record(), Item(File("0", path))).Requests.Single();
        Assert.Equal(expected.Replace('\\', Path.DirectorySeparatorChar), request.Source.RelativePath);
        Assert.Equal(Path.GetFileName(path), request.FileName);
    }

    [Theory]
    [InlineData("Game.v1.02.Demo-GROUP/other.bin")]
    [InlineData("name.rar/sub/name.rar")]
    public void ALoneFilesRealFolderIsKeptWithItsWholeName(string path)
        => Assert.Equal(path.Replace('/', Path.DirectorySeparatorChar), Build(Record(), Item(File("0", path))).Requests.Single().Source.RelativePath);

    [Fact]
    public void AnItemWithSeveralFilesKeepsItsFolderWhateverItsName()
    {
        var plan = Build(Record(), Item(File("0", "a.rar/a.rar"), File("1", "a.rar/b.bin")));
        Assert.Equal(Path.Combine("a.rar", "a.rar"), plan.Requests[0].Source.RelativePath);
    }

    [Fact]
    public void TheRequestCarriesTheChosenQueueStartModeAndLimit()
    {
        var record = Record();
        var request = Build(record, Item(File("7", "Pack/a.bin"))).Requests.Single();
        Assert.Equal(3, request.QueueId);
        Assert.Equal("later", request.StartMode);
        Assert.Equal(4, request.MaxConnections);
        Assert.Equal(10, request.ExpectedTotalBytes);
        Assert.Equal(record.Id.ToString("N"), request.Source.RemoteJobId);
        Assert.Equal("9", request.Source.ItemId);
        Assert.Equal("7", request.Source.FileId);
        Assert.Equal("torrents", request.Source.Kind);
    }

    [Fact]
    public void InfectedEmptyAndUnsafeFilesAreLeftOutWithANote()
    {
        var plan = Build(Record(), Item(
            File("0", "Pack/good.bin"),
            File("1", "Pack/bad.bin", infected: true),
            File("2", "Pack/empty.bin", size: 0),
            File("3", "../../evil.bin"),
            File("4", @"C:\Windows\evil.bin"),
            File("5", "/etc/evil.bin")));
        Assert.Equal("0", plan.Requests.Single().Source.FileId);
        Assert.Equal(5, plan.Notes.Count);
        Assert.Contains(plan.Notes, note => note.Contains("infected") && note.Contains("bad.bin"));
        Assert.Contains(plan.Notes, note => note.Contains("empty") && note.Contains("empty.bin"));
        Assert.Equal(3, plan.Notes.Count(note => note.Contains("isn't safe")));
    }

    [Fact]
    public void OnlyTheChosenFilesAreCreated()
    {
        var plan = Build(Record(RemoteSelectionMode.Chosen, "1", "missing"), Item(File("0", "Pack/a.bin"), File("1", "Pack/b.bin")));
        Assert.Equal("1", plan.Requests.Single().Source.FileId);
        Assert.Contains(plan.Notes, note => note.Contains("no longer"));
    }

    [Fact]
    public void AChosenInfectedFileIsStillLeftOut()
    {
        var plan = Build(Record(RemoteSelectionMode.Chosen, "1"), Item(File("1", "Pack/b.bin", infected: true)));
        Assert.Empty(plan.Requests);
        Assert.Contains(plan.Notes, note => note.Contains("infected"));
    }

    [Fact]
    public void AnItemFullOfBadFilesDoesntGrowItsNotesForEver()
    {
        var files = Enumerable.Range(0, 500).Select(i => File(i.ToString(), $"Pack/{i}.bin", size: 0)).ToArray();
        var plan = Build(Record(), Item(files));
        Assert.Empty(plan.Requests);
        Assert.Equal(51, plan.Notes.Count);
        Assert.Contains("450 more", plan.Notes[^1]);
    }

    // ---- The store ----------------------------------------------------------------------------

    private static StoragePaths NewPaths()
    {
        var paths = new StoragePaths(Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"), "state"));
        paths.EnsureCreated();
        return paths;
    }

    [Fact]
    public async Task ARecordComesBackAsItWasSaved()
    {
        var paths = NewPaths();
        var store = new JsonRemoteJobStore(paths);
        var record = Record(RemoteSelectionMode.Chosen, "1", "2") with
        {
            State = RemoteJobState.SubmissionUnknown,
            Stage = RemoteStage.MetadataPending,
            FailureKind = RemoteFailureKind.Network,
            FailureMessage = "words",
            ProtectedInput = SecretProtector.ProtectRemoteInput("https://hoster.invalid/secret-path#secret-key"),
            ProtectedLinkPassword = SecretProtector.ProtectRemoteLinkPassword("invented-password"),
            ProtectedTorrent = SecretProtector.ProtectRemoteTorrent([1, 2, 3]),
            PredictedHash = "abc",
            NextCheckAt = DateTimeOffset.UnixEpoch,
            Notes = ["a note"],
        };
        await store.SaveAsync(record);

        var path = Path.Combine(paths.RemoteJobsDirectory, $"{record.Id:N}.json");
        var json = await System.IO.File.ReadAllTextAsync(path);
        Assert.DoesNotContain("secret-path", json);
        Assert.DoesNotContain("secret-key", json);
        Assert.DoesNotContain("invented-password", json);

        var loaded = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(record.State, loaded.State);
        Assert.Equal(record.Stage, loaded.Stage);
        Assert.Equal(record.FailureKind, loaded.FailureKind);
        Assert.Equal(record.SelectedFileIds, loaded.SelectedFileIds);
        Assert.Equal(record.Notes, loaded.Notes);
        Assert.Equal(record.NextCheckAt, loaded.NextCheckAt);
        Assert.Equal("https://hoster.invalid/secret-path#secret-key", SecretProtector.UnprotectRemoteInput(loaded.ProtectedInput));
        Assert.Equal("invented-password", SecretProtector.UnprotectRemoteLinkPassword(loaded.ProtectedLinkPassword));
        Assert.Equal([1, 2, 3], SecretProtector.UnprotectRemoteTorrent(loaded.ProtectedTorrent)!);

        await store.DeleteAsync(record.Id);
        Assert.Empty(await store.LoadAllAsync());
    }

    [Fact]
    public void EachProtectedFieldHasItsOwnPurpose()
    {
        var input = SecretProtector.ProtectRemoteInput("x");
        Assert.Null(SecretProtector.UnprotectRemoteLinkPassword(input));
        Assert.Null(SecretProtector.UnprotectRemoteTorrent(input));
        Assert.Null(SecretProtector.UnprotectProviderKey(input));
        Assert.Null(SecretProtector.UnprotectUrl(input));
        var password = SecretProtector.ProtectRemoteLinkPassword("x");
        Assert.Null(SecretProtector.UnprotectRemoteInput(password));
        Assert.Null(SecretProtector.UnprotectRemoteInput(SecretProtector.ProtectRemoteTorrent([1])));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":99,\"id\":\"11111111-1111-1111-1111-111111111111\",\"provider\":\"p\",\"kind\":\"torrent\"}")]
    [InlineData("{\"id\":\"22222222-2222-2222-2222-222222222222\",\"provider\":\"p\",\"kind\":\"torrent\"}")]
    public async Task ADamagedFileIsSetAsideAndTheRestLoad(string damaged)
    {
        var paths = NewPaths();
        var store = new JsonRemoteJobStore(paths);
        var good = Record();
        await store.SaveAsync(good);
        var bad = Path.Combine(paths.RemoteJobsDirectory, "11111111111111111111111111111111.json");
        await System.IO.File.WriteAllTextAsync(bad, damaged);

        Assert.Equal(good.Id, Assert.Single(await store.LoadAllAsync()).Id);
        Assert.False(System.IO.File.Exists(bad));
        Assert.NotEmpty(Directory.GetFiles(paths.CorruptDirectory));
    }

    [Fact]
    public async Task PacingOutlastsAReloadAndADamagedFileIsNoHarm()
    {
        var paths = NewPaths();
        var store = new JsonRemoteJobStore(paths);
        Assert.Equal(new RemoteAccountPacing(), await store.LoadPacingAsync("p"));
        var pacing = new RemoteAccountPacing { NextWebCreateAt = DateTimeOffset.UnixEpoch, CreationStoppedReason = "words" };
        await store.SavePacingAsync("p", pacing);
        Assert.Equal(pacing, await new JsonRemoteJobStore(paths).LoadPacingAsync("p"));

        await System.IO.File.WriteAllTextAsync(Path.Combine(paths.RemoteDirectory, "pacing-p.json"), "{ damaged");
        Assert.Equal(new RemoteAccountPacing(), await new JsonRemoteJobStore(paths).LoadPacingAsync("p"));
    }

    [Fact]
    public async Task AProviderIdCantBecomeAPath()
        => await Assert.ThrowsAsync<ArgumentException>(() => new JsonRemoteJobStore(NewPaths()).SavePacingAsync("../evil", new RemoteAccountPacing()));
}
