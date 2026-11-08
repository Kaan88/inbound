using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Remote;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Regressions from reviewing the remote core. Each is what a real failure looked like against the
/// mock. The mock proves what this app does with an answer, not that TorBox gives that answer.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    private const string WebList = "GET webdl/mylist";

    /// <summary>Whatever comes in place of a real list, once the submission has gone out.</summary>
    public static TheoryData<string> BrokenLists => new()
    {
        "not json 404", "endpoint 404", "deprecated 410", "empty 404", "no reason", "a word for data", "empty object", "no data at all", "null for data",
    };

    private static MockFault BrokenList(string which) => which switch
    {
        "not json 404" => MockFault.NotJson(404, "<html>Not here</html>"),
        "endpoint 404" => MockFault.Error("ENDPOINT_NOT_FOUND", "No such endpoint.", 404),
        "deprecated 410" => MockFault.Error("DEPRECATED", "Use the other one.", 410),
        "empty 404" => MockFault.Empty(404),
        "no reason" => new MockFault(MockFaultKind.Respond) { Status = 200, RawBody = """{"success":false,"error":null,"data":null}""" },
        "a word for data" => new MockFault(MockFaultKind.Respond) { Status = 200, RawBody = """{"success":true,"error":null,"data":"later"}""" },
        "empty object" => new MockFault(MockFaultKind.Respond) { Status = 200, RawBody = """{"success":true,"error":null,"data":{}}""" },
        "no data at all" => new MockFault(MockFaultKind.Respond) { Status = 200, RawBody = """{"success":true,"error":null}""" },
        "null for data" => new MockFault(MockFaultKind.Respond) { Status = 200, RawBody = """{"success":true,"error":null,"detail":"ok","data":null}""" },
        _ => throw new ArgumentOutOfRangeException(nameof(which)),
    };

    /// <summary>Runs this once, as the submission is about to go out: past the checks before it, with nothing yet sent.</summary>
    private static void AsItGoesOut(RemoteRig rig, Action act)
    {
        var done = 0;
        rig.Jobs.Changed += (_, view) =>
        {
            if (view.State == RemoteJobState.Submitting && Interlocked.Exchange(ref done, 1) == 0) act();
        };
    }

    // ---- A failed look is never an empty account ------------------------------------------------

    [Theory]
    [MemberData(nameof(BrokenLists))]
    public async Task ABrokenListAfterALostAnswerNeverLeadsToASecondSubmission(string which)
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, file) = rig.ScriptHoster("lost", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.AcceptThenAbort, times: 1);
        AsItGoesOut(rig, () => rig.Mock.Fail(WebList, BrokenList(which)));

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        await rig.AdvanceAsync(61);
        await rig.AdvanceAsync(70);
        await rig.AdvanceAsync(900);

        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.SubmissionUnknown, rig.View(job.Id).State);

        rig.Mock.ClearFaults();
        await rig.AdvanceAsync(900);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        await rig.AssertCompletedAsync(file, "lost", "lost.bin");
    }

    [Theory]
    [MemberData(nameof(BrokenLists))]
    public async Task ABrokenListBeforeAnySubmissionSendsNothing(string which)
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("first", MockTorBoxState.Ready);
        rig.Mock.Fail(WebList, BrokenList(which));

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        await rig.AdvanceAsync(120);

        Assert.Equal(0, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.Intended, rig.View(job.Id).State);
        Assert.NotNull(rig.View(job.Id).FailureMessage);
    }

    [Theory]
    [MemberData(nameof(BrokenLists))]
    public async Task AGatewayFaultDoesNotMakeAWaitingItemGone(string which)
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("still", MockTorBoxState.Downloading(), MockTorBoxState.Downloading());
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.Waiting, rig.View(job.Id).State);

        rig.Mock.Fail(WebList, BrokenList(which));
        await rig.AdvanceAsync(61);
        await rig.AdvanceAsync(70);
        await rig.AdvanceAsync(200);

        Assert.Equal(RemoteJobState.Waiting, rig.View(job.Id).State);
        Assert.NotEqual(RemoteFailureKind.NotFound, rig.View(job.Id).FailureKind);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
    }

    // ---- Local jobs follow their item -----------------------------------------------------------

    [Fact]
    public async Task AfterARetryResubmitsTheChildrenFollowTheNewItemAndKeepTheirBytes()
    {
        await using var rig = await RemoteRig.CreateAsync(_server, connections: 1);
        rig.Mock.CdnBytesPerSecond = 256 * Kb;
        var done = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/done.bin", 4 * Kb, 11);
        var partial = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/partial.bin", 900 * Kb, 12);
        var link = rig.HosterLink("moved");
        var hash = RemoteRig.Md5Of(link);
        rig.Mock.ScriptSubmission(hash, [done, partial], [MockTorBoxState.Ready]);

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        var children = rig.Children();
        Assert.Equal(2, children.Count);
        var firstItem = rig.Mock.FindItem(RemoteItemKind.WebDownload, hash)!;

        var doneJob = children.Single(child => child.FileName == "done.bin");
        var partialJob = children.Single(child => child.FileName == "partial.bin");
        AssertCompleted(await rig.Ctx.WaitFor(doneJob.Id, candidate => candidate.State is "completed" or "failed", TimeSpan.FromSeconds(30)));
        await rig.Ctx.WaitFor(partialJob.Id, candidate => candidate.ReceivedBytes > 50 * Kb, TimeSpan.FromSeconds(30));
        Assert.True((await rig.Ctx.Service.PauseAsync(partialJob.Id)).Ok);
        var paused = await rig.Ctx.WaitFor(partialJob.Id, candidate => candidate.State == "paused", TimeSpan.FromSeconds(30));
        Assert.True(paused.ReceivedBytes < partial.Size);

        // The item goes away on the provider. Looking again finds that out, and only a retry sends it again.
        rig.Mock.RemoveItem(firstItem);
        Assert.True((await rig.Jobs.ChooseFilesAsync(job.Id, all: true, null)).Ok);
        await rig.RunAsync();
        await rig.AdvanceAsync(61);
        Assert.Equal(RemoteFailureKind.NotFound, rig.View(job.Id).FailureKind);

        rig.Mock.CdnBytesPerSecond = null;
        rig.Mock.ScriptSubmission(hash, [done, partial], [MockTorBoxState.Ready]);
        Assert.True((await rig.Jobs.RetryAsync(job.Id)).Ok);
        await rig.AdvanceAsync(65);
        var secondItem = rig.Mock.FindItem(RemoteItemKind.WebDownload, hash)!;
        Assert.NotEqual(firstItem.Id, secondItem.Id);
        Assert.Equal(2, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);

        // The same two jobs, on the new item, and no "name (1).bin" beside them.
        var moved = rig.Children();
        Assert.Equal(2, moved.Count);
        Assert.All(moved, child => Assert.Contains($"/{secondItem.Id}/", child.Url));
        Assert.Equal("completed", moved.Single(child => child.Id == doneJob.Id).State);

        Assert.True((await rig.Ctx.Service.ResumeAsync(partialJob.Id)).Ok);
        AssertCompleted(await rig.Ctx.WaitFor(partialJob.Id, candidate => candidate.State is "completed" or "failed", TimeSpan.FromSeconds(30)));
        Assert.Equal(Sha256Of(partial.Content), await Hashing.Sha256FileAsync(Path.Combine(rig.Destination, "Pack", "partial.bin")));
        // It went on from where it was: a request for everything would have had no range.
        var resumed = rig.Mock.IssuedLinks.Last(issued => issued.File == partial);
        Assert.Contains(resumed.Ranges, range => !range.StartsWith("bytes=0-", StringComparison.Ordinal));
        Assert.Equal(Sha256Of(done.Content), await Hashing.Sha256FileAsync(Path.Combine(rig.Destination, "Pack", "done.bin")));
    }

    [Fact]
    public async Task AChildThatFailedBecauseItsItemWentGoesOnByItselfWhenTheItemIsBack()
    {
        await using var rig = await RemoteRig.CreateAsync(_server, connections: 1);
        rig.Mock.CdnBytesPerSecond = 128 * Kb;
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 900 * Kb, 13);
        var link = rig.HosterLink("revived");
        var hash = RemoteRig.Md5Of(link);
        rig.Mock.ScriptSubmission(hash, [file], [MockTorBoxState.Ready]);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await rig.RunAsync();
        var child = Assert.Single(rig.Children());
        Assert.True((await rig.Ctx.Service.PauseAsync(child.Id)).Ok);
        await rig.Ctx.WaitFor(child.Id, candidate => candidate.State == "paused", TimeSpan.FromSeconds(30));

        rig.Mock.RemoveItem(rig.Mock.FindItem(RemoteItemKind.WebDownload, hash)!);
        Assert.True((await rig.Ctx.Service.ResumeAsync(child.Id)).Ok);
        var failed = await rig.Ctx.WaitFor(child.Id, candidate => candidate.State == "failed", TimeSpan.FromSeconds(30));
        Assert.Contains("no longer on your TorBox account", failed.Failure!.Message);

        Assert.True((await rig.Jobs.ChooseFilesAsync(job.Id, all: true, null)).Ok);
        await rig.RunAsync();
        await rig.AdvanceAsync(61);
        rig.Mock.CdnBytesPerSecond = null;
        rig.Mock.ScriptSubmission(hash, [file], [MockTorBoxState.Ready]);
        Assert.True((await rig.Jobs.RetryAsync(job.Id)).Ok);
        await rig.AdvanceAsync(65);

        await rig.AssertCompletedAsync(file, "Pack", "a.bin");
        Assert.Single(rig.Children());
    }

    [Fact]
    public async Task AFinishedFileTheItemNoLongerHoldsIsKeptAndSaidSo()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 4 * Kb, 14, withMd5: true);
        var link = rig.HosterLink("changed");
        var hash = RemoteRig.Md5Of(link);
        rig.Mock.ScriptSubmission(hash, [file], [MockTorBoxState.Ready]);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await rig.RunAsync();
        var original = file.Content;
        await rig.AssertCompletedAsync(file, "Pack", "a.bin");
        var child = Assert.Single(rig.Children());
        var firstUrl = child.Url;

        rig.Mock.RemoveItem(rig.Mock.FindItem(RemoteItemKind.WebDownload, hash)!);
        Assert.True((await rig.Jobs.ChooseFilesAsync(job.Id, all: true, null)).Ok);
        await rig.RunAsync();
        await rig.AdvanceAsync(61);
        var other = MockTorBoxFile.Of(file.Id, "Pack/a.bin", 8 * Kb, 15, withMd5: true);
        rig.Mock.ScriptSubmission(hash, [other], [MockTorBoxState.Ready]);
        Assert.True((await rig.Jobs.RetryAsync(job.Id)).Ok);
        await rig.AdvanceAsync(65);

        var view = rig.View(job.Id);
        Assert.Equal(RemoteJobState.LocalStarted, view.State);
        Assert.Contains(view.Notes, note => note.Contains("different file", StringComparison.Ordinal));
        var kept = Assert.Single(rig.Children());
        Assert.Equal(firstUrl, kept.Url);
        Assert.Equal("completed", kept.State);
        Assert.Equal(Sha256Of(original), await Hashing.Sha256FileAsync(Path.Combine(rig.Destination, "Pack", "a.bin")));
    }

    // ---- The grace counts from the lost answer ----------------------------------------------------

    [Fact]
    public async Task TheGraceCountsFromWhenTheAnswerWasLostNotFromBeforeAWait()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("slowpost", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.Status5xx(502), times: 1);
        // Standing in for a wait on the request budget, between the save and the request.
        AsItGoesOut(rig, () => rig.Time.Advance(TimeSpan.FromSeconds(90)));

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await rig.RunAsync();

        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.SubmissionUnknown, rig.View(job.Id).State);
        Assert.Equal(rig.Time.GetUtcNow() + RemoteJobRules.RegistrationGrace, rig.View(job.Id).NextCheckAt);

        await rig.AdvanceAsync(59);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        // Past the grace, and the slot counts from the request too, so it still waits its turn.
        await rig.AdvanceAsync(2);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        await rig.AdvanceAsync(5);
        Assert.Equal(2, rig.Mock.Calls(CreateWeb));
    }

    // ---- A step always moves on -------------------------------------------------------------------

    [Fact]
    public async Task AnItemIdGoesInUnderTheProvidersOwnSpelling()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 4 * Kb, 16);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);

        var padded = await rig.AddAsync(new RemoteAddRequest { ExistingItem = new RemoteItemRef("torbox", RemoteItemKind.Torrent, $"00{item.Id}"), Selection = RemoteSelectionMode.All });
        Assert.Equal(item.Id.ToString(), padded.ItemId);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(padded.Id).State);

        var refused = await rig.Jobs.AddAsync(new RemoteAddRequest { ExistingItem = new RemoteItemRef("torbox", RemoteItemKind.Torrent, "abc"), Destination = rig.Destination });
        Assert.Contains("isn't an item id", refused.Error);
    }

    [Fact]
    public async Task AnAnswerAboutAnotherItemCountsAsAFailedLookAndDoesNotRepeatAtOnce()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 4 * Kb, 17)]);
        rig.Mock.Fail("GET torrents/mylist", new MockFault(MockFaultKind.Respond) { Status = 200, RawBody = """{"success":true,"error":null,"data":{"id":999999,"name":"Other"}}""" });
        var job = await rig.AddAsync(new RemoteAddRequest { ExistingItem = new RemoteItemRef("torbox", RemoteItemKind.Torrent, item.Id.ToString()), Selection = RemoteSelectionMode.All });

        await rig.RunAsync();

        Assert.Equal(1, rig.Mock.Calls("GET torrents/mylist"));
        var view = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Waiting, view.State);
        Assert.Equal(RemoteFailureKind.BadResponse, view.FailureKind);
        Assert.True(view.NextCheckAt > rig.Time.GetUtcNow());
    }

    // ---- The key ----------------------------------------------------------------------------------

    [Fact]
    public async Task ClearingTheKeyFailsNothingThatHadOnlyBeenWaiting()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (forApproval, _, _) = rig.ScriptHoster("approval", MockTorBoxState.Ready);
        var waiting = await rig.AddAsync(new RemoteAddRequest { Input = forApproval });
        await rig.RunAsync();
        Assert.True(rig.View(waiting.Id).ApprovalNeeded);

        await rig.Box.Account.ClearAsync();
        await rig.Jobs.CheckNowAsync();
        await rig.RunAsync();
        var kept = rig.View(waiting.Id);
        Assert.Equal(RemoteJobState.Intended, kept.State);
        Assert.True(kept.ApprovalNeeded);
        Assert.Equal(RemoteFailureKind.Authentication, kept.FailureKind);
        Assert.Null(kept.NextCheckAt);

        await rig.Box.Account.SetKeyAsync(MockKey);
        await rig.Jobs.CheckNowAsync();
        await rig.RunAsync();
        kept = rig.View(waiting.Id);
        Assert.Equal(RemoteJobState.Intended, kept.State);
        Assert.Null(kept.FailureKind);

        // Nothing was ever sent, so agreeing to it isn't refused.
        var approved = await rig.Jobs.ApproveUncachedAsync(waiting.Id);
        Assert.True(approved.Ok, approved.Error);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(waiting.Id).State);
    }

    [Fact]
    public async Task ClearingTheKeyLeavesAnApprovedJobWaitingForItsTurn()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("approved", MockTorBoxState.Ready);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await rig.Box.Account.ClearAsync();
        await rig.RunAsync();

        var view = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Intended, view.State);
        Assert.Equal(RemoteFailureKind.Authentication, view.FailureKind);
        Assert.Equal(rig.Time.GetUtcNow() + RemoteJobRules.LongestIdlePoll, view.NextCheckAt);
        Assert.Equal(0, rig.Mock.Calls(CreateWeb));

        await rig.Box.Account.SetKeyAsync(MockKey);
        await rig.Jobs.CheckNowAsync();
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
    }

    [Fact]
    public async Task ChangingTheKeyLiftsWhatTheOldAccountsAnswersCaused()
    {
        await using var rig = await RemoteRig.CreateAsync(_server, loops: true);
        var (refused, _, _) = rig.ScriptHoster("refused", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.Error("MONTHLY_LIMIT", "Used up."), times: 1);
        var first = await rig.AddAsync(new RemoteAddRequest { Input = refused, ApprovedUncached = true });
        await EventuallyWithTime(rig.Time, () => rig.View(first.Id).State == RemoteJobState.Failed, "the refusal");

        await rig.Box.Account.SetKeyAsync(MockKey);
        await Eventually(() => new JsonRemoteJobStore(rig.Ctx.Paths).LoadPacingAsync("torbox").GetAwaiter().GetResult().CreationStoppedReason is null, "the stop to be lifted");

        var (next, _, _) = rig.ScriptHoster("next", MockTorBoxState.Ready);
        var second = await rig.AddAsync(new RemoteAddRequest { Input = next, ApprovedUncached = true });
        await EventuallyWithTime(rig.Time, () => rig.View(second.Id).State == RemoteJobState.LocalStarted, "the next job to go through");
    }

    // ---- Approval ---------------------------------------------------------------------------------

    [Fact]
    public async Task AnApprovalGivenWhileTheCacheIsBeingCheckedCounts()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("inflight", MockTorBoxState.Ready);
        rig.Mock.Fail("POST webdl/checkcached", MockFault.Delay(TimeSpan.FromMilliseconds(800)), times: 1);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link });

        var pass = rig.RunAsync();
        await Eventually(() => rig.Mock.Calls("POST webdl/checkcached") > 0, "the cache check to start");
        Assert.True((await rig.Jobs.ApproveUncachedAsync(job.Id)).Ok);
        await pass;
        await rig.RunAsync();

        Assert.False(rig.View(job.Id).ApprovalNeeded);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
    }

    [Fact]
    public async Task AnApprovalSentBeforeTheFirstLookStillCounts()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("early", MockTorBoxState.Ready);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link });

        var again = await rig.Jobs.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Destination = rig.Destination });
        Assert.True(again.Existing);
        Assert.Equal(job.Id, again.Job!.Id);
        await rig.RunAsync();

        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
    }

    // ---- Rate limits ------------------------------------------------------------------------------

    [Theory]
    [InlineData(5_000_000)] // Past what a timer takes.
    [InlineData(2_592_000)] // A month, which a timer would take.
    public async Task AnAbsurdRetryAfterHoldsTheAccountForAnHourAtMostAndTheLoopLives(int seconds)
    {
        await using var rig = await RemoteRig.CreateAsync(_server, loops: true);
        var (link, hash, _) = rig.ScriptHoster("absurd", MockTorBoxState.Downloading());
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await EventuallyWithTime(rig.Time, () => rig.View(job.Id).State == RemoteJobState.Waiting, "the job to be waiting");

        rig.Mock.Fail(WebList, MockFault.TooManyRequests(seconds));
        await EventuallyWithTime(rig.Time, () => rig.View(job.Id).FailureKind == RemoteFailureKind.RateLimited, "the rate limit");
        var at = rig.Time.GetUtcNow();
        Assert.True(rig.View(job.Id).NextCheckAt <= at + RemoteJobRules.LongestRateLimitHold);
        var saved = await new JsonRemoteJobStore(rig.Ctx.Paths).LoadPacingAsync("torbox");
        Assert.True(saved.RateLimitedUntil <= at + RemoteJobRules.LongestRateLimitHold);

        // The loop is still alive: when the hold is over, it looks again.
        rig.Mock.ClearFaults();
        rig.Mock.FindItem(RemoteItemKind.WebDownload, hash)!.States.Add(MockTorBoxState.Ready);
        rig.Time.Advance(RemoteJobRules.LongestRateLimitHold);
        await EventuallyWithTime(rig.Time, () => rig.View(job.Id).State == RemoteJobState.LocalStarted, "the loop to go on");
        await rig.Jobs.StopAsync();
    }

    [Fact]
    public async Task AHoldSavedFarInTheFutureIsCutDownAtStart()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("loaded", MockTorBoxState.Ready);
        await new JsonRemoteJobStore(rig.Ctx.Paths).SavePacingAsync("torbox", new RemoteAccountPacing { RateLimitedUntil = rig.Time.GetUtcNow() + TimeSpan.FromDays(4000) });
        await rig.RestartCoordinatorOnlyAsync();

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await rig.RunAsync();
        Assert.Equal(0, rig.Mock.Calls(CreateWeb));

        await rig.AdvanceAsync((int)RemoteJobRules.LongestRateLimitHold.TotalSeconds);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
    }

    [Theory]
    [InlineData(200, "RATE_LIMIT_EXCEEDED", "60 per 1 hour", 3600)]
    [InlineData(403, "SLOW_DOWN", "Too fast", 60)]
    [InlineData(200, "UNKNOWN_ERROR", "60 per 1 hour", 3600)]
    public async Task ARateLimitNotSentAsA429StillHoldsTheAccount(int status, string code, string detail, int seconds)
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (first, _, _) = rig.ScriptHoster("one", MockTorBoxState.Ready);
        var (second, _, _) = rig.ScriptHoster("two", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.Error(code, detail, status), times: 1);
        var one = await rig.AddAsync(new RemoteAddRequest { Input = first, ApprovedUncached = true });
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        var two = await rig.AddAsync(new RemoteAddRequest { Input = second, ApprovedUncached = true });

        await rig.RunAsync();
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(seconds), rig.View(one.Id).NextCheckAt);
        var calls = rig.Mock.TotalApiCalls;

        // Everything else due waits with it, and no call at all is made meanwhile.
        await rig.AdvanceAsync(seconds - 1);
        Assert.Equal(calls, rig.Mock.TotalApiCalls);
        Assert.Equal(RemoteJobState.Intended, rig.View(two.Id).State);

        await rig.AdvanceAsync(1);
        Assert.True(rig.Mock.TotalApiCalls > calls);
    }

    // ---- One refusal about one item ---------------------------------------------------------------

    [Theory]
    [InlineData("DOWNLOAD_TOO_LARGE")]
    [InlineData("PLAN_RESTRICTED_FEATURE")]
    public async Task ARefusalAboutOneItemLeavesTheOthersAlone(string code)
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (big, _, _) = rig.ScriptHoster("big", MockTorBoxState.Ready);
        var (small, _, _) = rig.ScriptHoster("small", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.Error(code, "Not this one."), times: 1);
        var one = await rig.AddAsync(new RemoteAddRequest { Input = big, ApprovedUncached = true });
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        var two = await rig.AddAsync(new RemoteAddRequest { Input = small, ApprovedUncached = true });

        await rig.RunAsync();
        await rig.AdvanceAsync(65);

        Assert.Equal(RemoteJobState.Failed, rig.View(one.Id).State);
        Assert.Equal(RemoteFailureKind.Rejected, rig.View(one.Id).FailureKind);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(two.Id).State);
    }

    [Fact]
    public async Task AQuotaStopSaysItCameFromAnEarlierRefusalAndSparesWhatIsCached()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (first, _, _) = rig.ScriptHoster("first", MockTorBoxState.Ready);
        var (second, _, _) = rig.ScriptHoster("second", MockTorBoxState.Ready);
        var (cachedLink, cachedHash, _) = rig.ScriptHoster("cached", MockTorBoxState.CachedReady);
        rig.Mock.AddCached(cachedHash);
        rig.Mock.Fail(CreateWeb, MockFault.Error("MONTHLY_LIMIT", "Used up."), times: 1);
        var one = await rig.AddAsync(new RemoteAddRequest { Input = first, ApprovedUncached = true });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.Failed, rig.View(one.Id).State);

        var two = await rig.AddAsync(new RemoteAddRequest { Input = second, ApprovedUncached = true });
        var three = await rig.AddAsync(new RemoteAddRequest { Input = cachedLink });
        await rig.AdvanceAsync(65);
        await rig.AdvanceAsync(65);

        Assert.Equal(RemoteJobState.Failed, rig.View(two.Id).State);
        Assert.Contains("earlier item", rig.View(two.Id).FailureMessage);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(three.Id).State);
    }

    // ---- Several items under one hash -------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OfTwoItemsWithOneHashTheUsableOneIsAdopted(bool failedListedFirst)
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var link = rig.HosterLink("twins");
        var hash = RemoteRig.Md5Of(link);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "twins/a.bin", 4 * Kb, 18);

        MockTorBoxItem Failed() => rig.Mock.AddItem(RemoteItemKind.WebDownload, "old", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "twins/a.bin", 4 * Kb, 19)], MockTorBoxState.Failed("It went wrong."));
        MockTorBoxItem Ready() => rig.Mock.AddItem(RemoteItemKind.WebDownload, "new", [file], MockTorBoxState.Ready);
        var first = failedListedFirst ? Failed() : Ready();
        var second = failedListedFirst ? Ready() : Failed();
        first.Hash = hash;
        second.Hash = hash;
        var ready = failedListedFirst ? second : first;

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await rig.RunAsync();

        Assert.Equal(0, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(ready.Id.ToString(), rig.View(job.Id).ItemId);
        await rig.AssertCompletedAsync(file, "twins", "a.bin");
    }

    [Fact]
    public async Task RetryingAFailedItemFindsABetterOneUnderTheSameHash()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, hash, file) = rig.ScriptHoster("later", MockTorBoxState.Failed("It went wrong."));
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.Failed, rig.View(job.Id).State);
        var old = rig.View(job.Id).ItemId;

        var better = rig.Mock.AddItem(RemoteItemKind.WebDownload, "better", [file], MockTorBoxState.Ready);
        better.Hash = hash;
        Assert.True((await rig.Jobs.RetryAsync(job.Id)).Ok);
        await rig.RunAsync();

        Assert.NotEqual(old, rig.View(job.Id).ItemId);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
    }

    [Fact]
    public async Task AReadyItemWithALeftoverErrorIsDownloadedAndTheErrorIsNoted()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 4 * Kb, 20);
        var item = rig.Mock.AddItem(RemoteItemKind.WebDownload, "Pack", [file],
            MockTorBoxState.Ready with { Error = "An earlier attempt failed." });
        var job = await rig.AddAsync(new RemoteAddRequest { ExistingItem = new RemoteItemRef("torbox", RemoteItemKind.WebDownload, item.Id.ToString()), Selection = RemoteSelectionMode.All });

        await rig.RunAsync();

        var view = rig.View(job.Id);
        Assert.Equal(RemoteJobState.LocalStarted, view.State);
        Assert.Contains(view.Notes, note => note.Contains("An earlier attempt failed.", StringComparison.Ordinal));
        await rig.AssertCompletedAsync(file, "Pack", "a.bin");
    }

    // ---- Choosing -----------------------------------------------------------------------------------

    [Fact]
    public async Task AChoiceMadeWhileJobsAreBeingCreatedIsActedOn()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var first = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 4 * Kb, 21);
        var second = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/b.bin", 4 * Kb, 22);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [first, second]);
        var job = await rig.AddAsync(new RemoteAddRequest
        {
            ExistingItem = new RemoteItemRef("torbox", RemoteItemKind.Torrent, item.Id.ToString()),
            Selection = RemoteSelectionMode.Chosen,
            FileIds = [first.Id.ToString()],
        });

        var once = 0;
        rig.Host.BeforeAdd = async () =>
        {
            if (Interlocked.Exchange(ref once, 1) == 0)
            {
                var chosen = await rig.Jobs.ChooseFilesAsync(job.Id, all: false, [first.Id.ToString(), second.Id.ToString()]);
                Assert.True(chosen.Ok, chosen.Error);
            }
        };
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(2, rig.Children().Count);
    }

    [Fact]
    public async Task AChoiceOfFilesTheListDoesntHaveAsksAgainInsteadOfFailing()
    {
        // Before, this failed the job. Nothing about the item is wrong, so the choice is asked for again, saying why.
        await using var rig = await RemoteRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 4 * Kb, 23);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        var job = await rig.AddAsync(new RemoteAddRequest
        {
            ExistingItem = new RemoteItemRef("torbox", RemoteItemKind.Torrent, item.Id.ToString()),
            Selection = RemoteSelectionMode.Chosen,
            FileIds = ["99"],
        });
        await rig.RunAsync();
        var asking = rig.View(job.Id);
        Assert.Equal(RemoteJobState.AwaitingSelection, asking.State);
        Assert.Equal("1 of the chosen files isn't in TorBox's list. Choose again.", asking.Status);
        Assert.Empty(rig.Children());

        var again = await rig.Jobs.ChooseFilesAsync(job.Id, all: true, null);
        Assert.True(again.Ok, again.Error);
        Assert.Null(rig.View(job.Id).FailureMessage);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        await rig.AssertCompletedAsync(file, "Pack", "a.bin");
    }

    [Fact]
    public async Task AChoiceOfNothingUsableCanBeChosenAgain()
    {
        // The chosen file is in the list, but it can't be downloaded.
        await using var rig = await RemoteRig.CreateAsync(_server);
        var infected = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/bad.bin", 4 * Kb, 26, infected: true);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 4 * Kb, 23);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [infected, file]);
        var job = await rig.AddAsync(new RemoteAddRequest
        {
            ExistingItem = new RemoteItemRef("torbox", RemoteItemKind.Torrent, item.Id.ToString()),
            Selection = RemoteSelectionMode.Chosen,
            FileIds = [infected.Id.ToString()],
        });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.Failed, rig.View(job.Id).State);
        Assert.Equal("None of the chosen files can be downloaded.", rig.View(job.Id).FailureMessage);

        var again = await rig.Jobs.ChooseFilesAsync(job.Id, all: true, null);
        Assert.True(again.Ok, again.Error);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        await rig.AssertCompletedAsync(file, "Pack", "a.bin");
    }

    [Fact]
    public async Task ABadChoiceOnAJobWithDownloadsIsRefusedAndNeverFailsIt()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 4 * Kb, 24);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        var job = await rig.AddAsync(new RemoteAddRequest { ExistingItem = new RemoteItemRef("torbox", RemoteItemKind.Torrent, item.Id.ToString()), Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);

        var refused = await rig.Jobs.ChooseFilesAsync(job.Id, all: false, ["99"]);
        Assert.False(refused.Ok);
        Assert.Contains("None of those files", refused.Error);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);

        // The list couldn't say, so the choice is taken, and it still doesn't fail what is downloading.
        rig.Mock.Fail("GET torrents/mylist", MockFault.Abort, times: 1);
        Assert.True((await rig.Jobs.ChooseFilesAsync(job.Id, all: false, ["99"])).Ok);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Contains(rig.View(job.Id).Notes, note => note.Contains("None of the chosen files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddingWhatIsAlreadyThereSaysWhichOptionsWerentUsed()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 4 * Kb, 25);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        var reference = new RemoteItemRef("torbox", RemoteItemKind.Torrent, item.Id.ToString());
        var job = await rig.AddAsync(new RemoteAddRequest { ExistingItem = reference, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();

        var same = await rig.Jobs.AddAsync(new RemoteAddRequest { ExistingItem = reference, Destination = rig.Destination, Selection = RemoteSelectionMode.All });
        Assert.True(same.Existing);
        Assert.Null(same.Note);

        var other = await rig.Jobs.AddAsync(new RemoteAddRequest
        {
            ExistingItem = reference,
            Destination = Path.Combine(rig.Destination, "elsewhere"),
            Selection = RemoteSelectionMode.Chosen,
            FileIds = ["7"],
        });
        Assert.True(other.Existing);
        Assert.Equal(job.Id, other.Job!.Id);
        Assert.Contains("the folder and file choice given here weren't used", other.Note);
        Assert.DoesNotContain("remote select", other.Note);
    }

    // ---- Defaults by input ----------------------------------------------------------------------------

    [Fact]
    public async Task ScenarioB_ACachedHosterLinkStartsWithNoChoiceAskedFor()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, hash, file) = rig.ScriptHoster("nochoice", MockTorBoxState.CachedReady);
        rig.Mock.AddCached(hash);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link });
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        await rig.AssertCompletedAsync(file, "nochoice", "nochoice.bin");
        Assert.Equal(0, rig.Mock.HosterRequests);
    }

    [Fact]
    public async Task ScenarioC_AnUncachedHosterLinkStartsWhenReadyWithNoChoiceAskedFor()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, file) = rig.ScriptHoster("nochoice2", MockTorBoxState.Downloading(), MockTorBoxState.Ready);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.Waiting, rig.View(job.Id).State);

        await rig.AdvanceAsync(20);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        await rig.AssertCompletedAsync(file, "nochoice2", "nochoice2.bin");
    }

    [Fact]
    public async Task AMagnetStillAsksWhenNothingWasChosen()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var hash = new string('c', 40);
        rig.Mock.AddCached(hash);
        rig.Mock.ScriptSubmission(hash,
            [MockTorBoxFile.Of(rig.Mock.NextFileId(), "m/a.bin", 4 * Kb, 26), MockTorBoxFile.Of(rig.Mock.NextFileId(), "m/b.bin", 4 * Kb, 27)],
            [MockTorBoxState.CachedReady]);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = "magnet:?xt=urn:btih:" + hash });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.AwaitingSelection, rig.View(job.Id).State);
    }

    // ---- With no list of sites -------------------------------------------------------------------------

    [Fact]
    public async Task ScenarioD_WhenTheProviderCantSayWhatALinkIsItSaysSoAndSendsNothing()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        rig.Mock.Fail("GET webdl/hosters", MockFault.Status5xx());
        var link = rig.HosterLink("outage");

        var classified = await rig.Jobs.ClassifyAsync(link);
        Assert.Equal(RemoteInputKind.Undetermined, classified.Kind);

        var added = await rig.Jobs.AddAsync(new RemoteAddRequest { Input = link, Destination = rig.Destination });
        Assert.Null(added.Job);
        Assert.Equal(RemoteInputKind.Undetermined, added.Kind);
        Assert.Contains("TorBox could not be reached", added.Error);
        Assert.DoesNotContain("ordinary", added.Error);
        Assert.Empty(rig.Jobs.List());
        Assert.Equal(0, rig.Mock.Calls(CreateWeb));
        Assert.Equal(0, rig.Mock.HosterRequests);
    }

    [Fact]
    public async Task ASiteThatHasUsedUpItsDayWaitsWithoutSpendingACreation()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        rig.Mock.AddHoster("Spent", ["127.0.0.1", "localhost"], "/spent/", dailyLinkLimit: 100, dailyLinkUsed: 100);
        // First in the list, as the mock keeps them in order, so give it its own address.
        var link = rig.Mock.HosterUrl("spent/file").Replace("/hoster/", "/", StringComparison.Ordinal);
        var classified = await rig.Jobs.ClassifyAsync(link);
        Assert.Equal("Spent", classified.HosterName);
        Assert.Equal(0, classified.HosterDailyLinksLeft);
        Assert.NotNull(classified.HosterLargestFile);

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.Blocked, rig.View(job.Id).State);
        Assert.Contains("daily limit is reached", rig.View(job.Id).FailureMessage);
        Assert.Equal(0, rig.Mock.Calls(CreateWeb));
    }

    // ---- What comes back in a provider's words -----------------------------------------------------------

    [Fact]
    public async Task WhatWasSubmittedIsHiddenInEveryFormItIsQuotedIn()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("quoted2", MockTorBoxState.Ready);
        const string Password = "invented pass+word";
        var bare = link[(link.IndexOf("://", StringComparison.Ordinal) + 3)..];
        rig.Mock.Fail(CreateWeb, MockFault.Error("LINK_OFFLINE",
            $"Tried {bare} and {Uri.EscapeDataString(link)} with {Password} and {Uri.EscapeDataString(Password)}"));

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, LinkPassword = Password });
        await rig.RunAsync();

        var message = rig.View(job.Id).FailureMessage!;
        Assert.DoesNotContain("invented", message);
        Assert.DoesNotContain("quoted2", message);
        await rig.AssertNothingLeakedAsync(link, bare, Password, Uri.EscapeDataString(Password));
    }

    [Fact]
    public async Task APasswordQuotedByAFailedItemIsHiddenToo()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        const string Password = "invented-item-password";
        var (link, _, _) = rig.ScriptHoster("itemfails", MockTorBoxState.Failed($"Wrong password {Password} for it"));
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, LinkPassword = Password });
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.Failed, rig.View(job.Id).State);
        Assert.DoesNotContain(Password, rig.View(job.Id).FailureMessage);
        await rig.AssertNothingLeakedAsync(link, Password);
    }

    [Fact]
    public async Task AKeyAcrossTheLimitLeavesNoPartOfItselfBehind()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("straddle", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.Error("LINK_OFFLINE", new string('w', 480) + " " + MockKey + " after"));
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await rig.RunAsync();

        var message = rig.View(job.Id).FailureMessage!;
        Assert.DoesNotContain(MockKey[..8], message);
    }

    [Fact]
    public async Task VeryLongWordsFromTheProviderAreCutEverywhereTheyAreSaved()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file], MockTorBoxState.Failed(new string('x', 300_000)));
        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, job => job.State == "failed");
        Assert.True(failed.Failure!.Message.Length <= 1000, $"Kept {failed.Failure.Message.Length} characters.");
    }

    // ---- Paths that can't be made ------------------------------------------------------------------------

    [Fact]
    public async Task OneFileWhosePathCantBeMadeIsSkippedWithANoteAndTheOthersGoOn()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var blocked = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/in-the-way/b.bin", 4 * Kb, 27);
        var fine = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 4 * Kb, 28);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [blocked, fine]);
        Directory.CreateDirectory(Path.Combine(rig.Destination, "Pack"));
        await File.WriteAllTextAsync(Path.Combine(rig.Destination, "Pack", "in-the-way"), "a file, where a folder is needed");

        var job = await rig.AddAsync(new RemoteAddRequest { ExistingItem = new RemoteItemRef("torbox", RemoteItemKind.Torrent, item.Id.ToString()), Selection = RemoteSelectionMode.All });
        await rig.RunAsync();

        var view = rig.View(job.Id);
        Assert.Equal(RemoteJobState.LocalStarted, view.State);
        Assert.Contains(view.Notes, note => note.Contains("can't be created", StringComparison.Ordinal));
        await rig.AssertCompletedAsync(fine, "Pack", "a.bin");
    }

    [Fact]
    public async Task ATooLongPathIsAFileLeftOutWithANote()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var tooLong = MockTorBoxFile.Of(rig.Mock.NextFileId(), $"Pack/{new string('d', 300)}/b.bin", 4 * Kb, 29);
        var fine = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 4 * Kb, 30);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [tooLong, fine]);

        var job = await rig.AddAsync(new RemoteAddRequest { ExistingItem = new RemoteItemRef("torbox", RemoteItemKind.Torrent, item.Id.ToString()), Selection = RemoteSelectionMode.All });
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Single(rig.Children());
        Assert.Contains(rig.View(job.Id).Notes, note => note.Contains("isn't safe", StringComparison.Ordinal));
    }

    // ---- A remote job's words follow its package -----------------------------------------------------------

    [Fact]
    public async Task ALocalStartedJobSaysWhatItsDownloadsAreDoing()
    {
        await using var rig = await RemoteRig.CreateAsync(_server, connections: 1);
        rig.Mock.CdnBytesPerSecond = 128 * Kb;
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 400 * Kb, 31);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        var job = await rig.AddAsync(new RemoteAddRequest { ExistingItem = new RemoteItemRef("torbox", RemoteItemKind.Torrent, item.Id.ToString()), Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        var child = Assert.Single(rig.Children());
        await rig.Ctx.WaitFor(child.Id, candidate => candidate.State == "downloading", TimeSpan.FromSeconds(30));
        Assert.Equal("Downloading locally", rig.View(job.Id).Status);

        Assert.True((await rig.Ctx.Service.PauseAsync(child.Id)).Ok);
        await rig.Ctx.WaitFor(child.Id, candidate => candidate.State == "paused", TimeSpan.FromSeconds(30));
        Assert.Equal("Paused locally", rig.View(job.Id).Status);

        rig.Mock.CdnBytesPerSecond = null;
        Assert.True((await rig.Ctx.Service.ResumeAsync(child.Id)).Ok);
        await rig.Ctx.WaitFor(child.Id, candidate => candidate.State == "completed", TimeSpan.FromSeconds(30));
        await Eventually(() => rig.View(job.Id).Status == "Completed", "the words to follow");
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
    }

    [Fact]
    public async Task ALocalStartedJobSaysWhenItsDownloadsFailed()
    {
        await using var rig = await RemoteRig.CreateAsync(_server, connections: 1);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 400 * Kb, 32);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.Fail("GET torrents/requestdl", MockFault.Error("LINK_OFFLINE", "No."));
        var job = await rig.AddAsync(new RemoteAddRequest { ExistingItem = new RemoteItemRef("torbox", RemoteItemKind.Torrent, item.Id.ToString()), Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        var child = Assert.Single(rig.Children());
        await rig.Ctx.WaitFor(child.Id, candidate => candidate.State == "failed", TimeSpan.FromSeconds(30));
        Assert.Equal("1 of 1 local downloads failed", rig.View(job.Id).Status);
    }

    // ---- A finished part file --------------------------------------------------------------------------

    /// <summary>
    /// The rename fails on a locked file, leaving every byte in the part file, and the retry asks for
    /// what comes after the last one. The real CDN answers that with a 206 and no range, not a 416.
    /// </summary>
    [Fact]
    [Trait("Platform", "Windows")] // an open file blocks a rename only on Windows
    public async Task ScenarioF_AResumeAfterAFailedRenameFinishesOnTheRealCdnsAnswer()
    {
        FinalizeHookCheckpoints? checkpoints = null;
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 1, hooks: new ContextHooks
        {
            WrapCheckpoints = inner => checkpoints = new FinalizeHookCheckpoints(inner),
        });
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 423, 33);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);

        FileStream? holding = null;
        checkpoints!.OnFinalizing = () =>
        {
            checkpoints.OnFinalizing = null;
            var part = rig.Ctx.Service.GetSnapshot().Jobs.Single().PartFilePath;
            holding = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Task.CompletedTask;
        };

        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, job => job.State is "failed" or "completed", TimeSpan.FromSeconds(60));
        Assert.Equal("failed", failed.State);
        Assert.NotNull(holding);
        await holding!.DisposeAsync();

        Assert.True((await rig.Ctx.Service.ResumeAsync(added.Id)).Ok);
        var done = await rig.Ctx.WaitFor(added.Id, job => job.State is "failed" or "completed", TimeSpan.FromSeconds(30));
        AssertCompleted(done);
        Assert.Equal(Sha256Of(file.Content), await Hashing.Sha256FileAsync(done.FinalFilePath));
        Assert.Contains(rig.Mock.IssuedLinks, link => link.Ranges.Contains("bytes=423-"));
    }

    // ---- The claim ----------------------------------------------------------------------------------------

    private sealed class ClaimFailsOnceStore(IJobStore inner) : IJobStore
    {
        private int _failed;

        public Task<bool> TryClaimRequestAsync(string extensionId, string clientRequestId, Guid jobId, CancellationToken cancellationToken = default)
            => Interlocked.Exchange(ref _failed, 1) == 0
                ? throw new IOException("Invented disk trouble.")
                : inner.TryClaimRequestAsync(extensionId, clientRequestId, jobId, cancellationToken);

        public Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default) => inner.SaveAsync(job, cancellationToken);
        public Task InitializeAsync(CancellationToken cancellationToken = default) => inner.InitializeAsync(cancellationToken);
        public Task<IReadOnlyList<DownloadJob>> LoadAllAsync(CancellationToken cancellationToken = default) => inner.LoadAllAsync(cancellationToken);
        public Task<DownloadJob?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.LoadAsync(jobId, cancellationToken);
        public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.DeleteAsync(jobId, cancellationToken);
        public Task<Guid?> FindClaimedRequestAsync(string extensionId, string clientRequestId, CancellationToken cancellationToken = default)
            => inner.FindClaimedRequestAsync(extensionId, clientRequestId, cancellationToken);
        public Task DeleteClaimAsync(string extensionId, string clientRequestId, CancellationToken cancellationToken = default)
            => inner.DeleteClaimAsync(extensionId, clientRequestId, cancellationToken);
    }

    [Fact]
    public async Task AClaimThatCantBeWrittenLeavesNoJobBehindForARetryToDuplicate()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, hooks: new ContextHooks { WrapJobs = inner => new ClaimFailsOnceStore(inner) });
        var request = new AddJobRequest
        {
            Url = _server.BaseAddress + "/file/claimed.bin?size=1024&seed=1",
            DestinationDirectory = ctx.Destination,
            StartMode = "later",
            OwnerExtensionId = "invented-extension",
            ClientRequestId = "request-1",
        };

        var failed = await ctx.Service.AddJobAsync(request);
        Assert.False(failed.Accepted);
        Assert.Equal("persistence_failed", failed.ErrorCode);
        Assert.Empty(await new JsonJobStore(ctx.Paths).LoadAllAsync());

        var retried = await ctx.Service.AddJobAsync(request);
        Assert.True(retried.Accepted);
        Assert.Single(await new JsonJobStore(ctx.Paths).LoadAllAsync());
        var again = await ctx.Service.AddJobAsync(request);
        Assert.True(again.Duplicate);
        Assert.Single(await new JsonJobStore(ctx.Paths).LoadAllAsync());
    }

    // ---- The record store --------------------------------------------------------------------------------

    private static RemoteJobRecord AnyRecord() => new()
    {
        Id = Guid.NewGuid(),
        Provider = "torbox",
        Kind = RemoteItemKind.Torrent,
        ItemId = "1",
        State = RemoteJobState.Waiting,
    };

    [Fact]
    public async Task AFileThatCantBeReadForAMomentIsNotTreatedAsDamaged()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(root);
        paths.EnsureCreated();
        var store = new JsonRemoteJobStore(paths, readRetryDelay: TimeSpan.FromMilliseconds(100));
        var record = AnyRecord();
        await store.SaveAsync(record);
        var path = Path.Combine(paths.RemoteJobsDirectory, $"{record.Id:N}.json");

        // Held for a moment, then let go: it is read, and nothing moved.
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(150);
            await held.DisposeAsync();
        });
        var loaded = await store.LoadAllAsync();
        await release;
        Assert.Equal(record.Id, Assert.Single(loaded).Id);

        // Held throughout: skipped this time, kept where it is, and read the next time.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(await store.LoadAllAsync());
        }

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(paths.CorruptDirectory));
        Assert.Single(await store.LoadAllAsync());

        // Really damaged is still set aside.
        await File.WriteAllTextAsync(path, "{ not json");
        Assert.Empty(await store.LoadAllAsync());
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(paths.CorruptDirectory));

        Directory.Delete(root, recursive: true);
    }
}
