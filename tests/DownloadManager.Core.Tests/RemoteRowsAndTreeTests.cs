using System.Globalization;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

public sealed class RemoteFileTreeTests
{
    private static RemoteFileDto File(string id, string? path, long size = 100, bool selectable = true, bool infected = false, string? note = null) => new()
    {
        FileId = id,
        RelativePath = path,
        Size = size,
        Selectable = selectable,
        Infected = infected,
        Note = note,
    };

    private static RemoteFileNode Find(RemoteFileTree tree, string name) => tree.Visible().First(node => node.Name == name);

    [Fact]
    public void FoldersComeFromThePathsAndSameNamesInDifferentFoldersStaySeparate()
    {
        var tree = RemoteFileTree.Build([
            File("1", "Show/sub/video.bin", 800),
            File("2", "Show/other/video.bin", 200),
            File("3", "Show/readme.txt", 4),
        ]);

        var rows = tree.Visible().Select(node => (node.Depth, node.Name)).ToList();
        Assert.Equal(
            [(0, "Show"), (1, "other"), (2, "video.bin"), (1, "sub"), (2, "video.bin"), (1, "readme.txt")],
            rows);
        Assert.Equal(1004, Find(tree, "Show").Size);
    }

    [Fact]
    public void BackslashesAndCaseDoNotMakeASecondFolder()
    {
        var tree = RemoteFileTree.Build([File("1", "Dir\\a.bin"), File("2", "dir/b.bin")]);

        Assert.Single(tree.Roots);
        Assert.Equal(2, tree.Roots[0].Children.Count);
    }

    [Fact]
    public void FilesAreOrderedNaturallyAndFoldersFirst()
    {
        var tree = RemoteFileTree.Build([File("1", "part10.bin"), File("2", "part2.bin"), File("3", "z/inner.bin")]);

        Assert.Equal(["z", "part2.bin", "part10.bin"], tree.Roots.Select(node => node.Name));
    }

    [Fact]
    public void WhatCannotBeChosenStaysOnShowWithItsNoteAndIsNeverChosen()
    {
        var tree = RemoteFileTree.Build([
            File("ok", "A/ok.bin"),
            File("sick", "A/sick.exe", infected: true, selectable: false, note: "TorBox marks it as infected."),
            File("empty", "A/empty.txt", size: 0, selectable: false, note: "It is empty."),
            File("unsafe", null, selectable: false, note: "Its path isn't safe to use."),
        ]);

        Assert.Equal(4, tree.FileCount);
        Assert.Equal(1, tree.ChoosableCount);
        Assert.Equal("TorBox marks it as infected.", Find(tree, "sick.exe").Note);
        Assert.Equal("It is empty.", Find(tree, "empty.txt").Note);
        Assert.Equal("Its path isn't safe to use.", Find(tree, "File with no safe path").Note);

        tree.SelectAll();
        Assert.Equal(["ok"], tree.ChosenFileIds);

        tree.SelectNone();
        tree.SetChecked(Find(tree, "A"), true);
        Assert.Equal(["ok"], tree.ChosenFileIds);
        Assert.Equal(true, Find(tree, "A").Checked);

        tree.SetChecked(Find(tree, "sick.exe"), true);
        Assert.Equal(["ok"], tree.ChosenFileIds);
    }

    [Fact]
    public void AFileTheHostCalledSelectableIsStillRefusedWhenItIsPlainlyNot()
    {
        var tree = RemoteFileTree.Build([
            File("sick", "sick.exe", infected: true),
            File("empty", "empty.txt", size: 0),
            File("nameless", "nameless.bin") ,
            File(string.Empty, "noid.bin"),
        ]);

        Assert.Equal(1, tree.ChoosableCount);
        Assert.NotNull(Find(tree, "sick.exe").Note);
        Assert.NotNull(Find(tree, "empty.txt").Note);
        Assert.NotNull(Find(tree, "noid.bin").Note);
    }

    [Fact]
    public void AFolderShowsAllNoneOrPartAndCountsUpward()
    {
        var tree = RemoteFileTree.Build([File("1", "A/B/one.bin", 10), File("2", "A/B/two.bin", 20), File("3", "A/three.bin", 40)]);

        tree.SetChecked(Find(tree, "one.bin"), true);
        Assert.Null(Find(tree, "B").Checked);
        Assert.Null(Find(tree, "A").Checked);
        Assert.Equal(10, tree.ChosenBytes);

        tree.SetChecked(Find(tree, "two.bin"), true);
        Assert.Equal(true, Find(tree, "B").Checked);
        Assert.Null(Find(tree, "A").Checked);

        tree.SetChecked(Find(tree, "A"), true);
        Assert.Equal(true, Find(tree, "A").Checked);
        Assert.True(tree.AllChosen);
        Assert.Equal(70, tree.ChosenBytes);

        tree.SetChecked(Find(tree, "B"), false);
        Assert.Equal(false, Find(tree, "B").Checked);
        Assert.Equal(["3"], tree.ChosenFileIds);
    }

    [Fact]
    public void ChoosingTheSameThingTwiceCountsOnce()
    {
        var tree = RemoteFileTree.Build([File("1", "a.bin", 10)]);

        tree.SetChecked(Find(tree, "a.bin"), true);
        tree.SetChecked(Find(tree, "a.bin"), true);

        Assert.Equal(1, tree.ChosenCount);
        Assert.Equal(10, tree.ChosenBytes);
    }

    [Fact]
    public void TheChoiceSaysAllOnlyWhenEverythingChoosableIsChosen()
    {
        var tree = RemoteFileTree.Build([File("1", "a.bin"), File("2", "b.bin"), File("3", "c.exe", infected: true, selectable: false)]);

        tree.SetChecked(Find(tree, "a.bin"), true);
        Assert.False(tree.Choice.All);
        Assert.Equal(["1"], tree.Choice.FileIds);

        tree.SelectAll();
        Assert.True(tree.Choice.All);
        Assert.Empty(tree.Choice.FileIds);
    }

    [Fact]
    public void AClosedFolderHidesItsContentsAndOpeningShowsThem()
    {
        var tree = RemoteFileTree.Build([File("1", "A/B/one.bin"), File("2", "A/two.bin")]);
        var folder = Find(tree, "B");

        folder.IsExpanded = false;
        Assert.DoesNotContain(tree.Visible(), node => node.Name == "one.bin");

        folder.IsExpanded = true;
        Assert.Contains(tree.Visible(), node => node.Name == "one.bin");
        Assert.Equal(["one.bin"], RemoteFileTree.VisibleUnder(folder).Select(node => node.Name));
    }

    [Fact]
    public void ALargeItemOpensOnlyItsFirstLevel()
    {
        var files = Enumerable.Range(0, 400).Select(i => File(i.ToString(CultureInfo.InvariantCulture), $"Root/sub/{i}.bin")).ToList();

        var tree = RemoteFileTree.Build(files);

        Assert.True(Find(tree, "Root").IsExpanded);
        Assert.False(Find(tree, "sub").IsExpanded);
        Assert.Equal(2, tree.Visible().Count());
    }

    [Fact]
    public void AnEarlierChoiceComesBackAfterTheListIsReadAgain()
    {
        var files = new[] { File("1", "a.bin"), File("2", "b.bin"), File("9", "c.exe", infected: true, selectable: false) };
        var tree = RemoteFileTree.Build(files);

        tree.Restore(["2", "9", "gone"]);

        Assert.Equal(["2"], tree.ChosenFileIds);
    }

    [Fact]
    public void AnAddressInAPathIsNotShown()
    {
        var tree = RemoteFileTree.Build([File("1", "folder/https://cdn.example.test/file?token=abc", 10)]);

        Assert.DoesNotContain(tree.Visible(), node => node.Name.Contains("token", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSummarySaysWhatIsChosenAndWhatCannotBe()
    {
        var tree = RemoteFileTree.Build([File("1", "a.bin", 2048), File("2", "b.bin", 2048), File("3", "c.exe", infected: true, selectable: false)]);

        Assert.Equal("0 of 2 files selected. 1 can't be selected", tree.Summary());
        tree.SetChecked(Find(tree, "a.bin"), true);
        Assert.StartsWith("1 of 2 files selected, ", tree.Summary());
    }
}

public sealed class RemoteRulesTests
{
    private static RemoteJobDto Job(string state, string? stage = null, string? failureKind = null, bool approval = false) => new()
    {
        Id = Guid.NewGuid(),
        Provider = "torbox",
        ProviderName = "TorBox",
        Kind = "torrents",
        State = state,
        Stage = stage,
        FailureKind = failureKind,
        ApprovalNeeded = approval,
        ItemId = "42",
    };

    // TB.F13: Download in My provider closes the window once the Host has the job on its way, and only then.
    [Theory]
    [InlineData("intended", true)]
    [InlineData("submitting", true)]
    [InlineData("waiting", true)]
    [InlineData("localStarted", true)]
    [InlineData("failed", false)]
    [InlineData("blocked", false)]
    [InlineData("removed", false)]
    public void ADownloadThatStartedClosesTheWindowAndOneThatFailedAtOnceDoesNot(string state, bool started)
        => Assert.Equal(started, RemoteRules.DownloadStarted(new RemoteAddResultDto { Ok = true, Job = Job(state) }));

    [Fact]
    public void ARefusalOrANoteKeepsTheWindowOpen()
    {
        Assert.False(RemoteRules.DownloadStarted(new RemoteAddResultDto { Error = "No." }));
        Assert.False(RemoteRules.DownloadStarted(new RemoteAddResultDto { Ok = true }));
        Assert.False(RemoteRules.DownloadStarted(new RemoteAddResultDto { Ok = true, Existing = true, Note = "Your file choice was not used.", Job = Job("waiting") }));
    }

    [Theory]
    [InlineData("intended", true)]
    [InlineData("submitting", true)]
    [InlineData("submissionUnknown", true)]
    [InlineData("waiting", true)]
    [InlineData("awaitingSelection", true)]
    [InlineData("blocked", true)]
    [InlineData("failed", true)]
    [InlineData("localStarted", false)]
    [InlineData("removed", false)]
    public void OnlyWorkWithNoLocalDownloadsYetGetsARow(string state, bool row)
        => Assert.Equal(row, RemoteRules.IsRow(Job(state)));

    [Theory]
    [InlineData("intended", true)]
    [InlineData("submissionUnknown", true)]
    [InlineData("waiting", true)]
    [InlineData("blocked", true)]
    [InlineData("awaitingSelection", true)]
    [InlineData("submitting", false)]
    [InlineData("failed", false)]
    [InlineData("localStarted", false)]
    public void CheckNowIsOfferedOnlyWhereTheHostTakesIt(string state, bool offered)
        => Assert.Equal(offered, RemoteRules.Actions(Job(state)).CheckNow);

    [Fact]
    public void EachRowOffersWhatItIsWaitingFor()
    {
        Assert.Equal(RemoteRowAction.SelectFiles, RemoteRules.Actions(Job("awaitingSelection")).Primary);
        Assert.Equal(RemoteRowAction.Approve, RemoteRules.Actions(Job("intended", approval: true)).Primary);
        Assert.Equal(RemoteRowAction.EnterLinkPassword, RemoteRules.Actions(Job("failed", failureKind: "linkPasswordRequired")).Primary);
        Assert.Equal(RemoteRowAction.Retry, RemoteRules.Actions(Job("failed", failureKind: "rejected")).Primary);
        Assert.Equal(RemoteRowAction.Retry, RemoteRules.Actions(Job("blocked")).Primary);
        Assert.Equal(RemoteRowAction.CheckNow, RemoteRules.Actions(Job("waiting")).Primary);
        Assert.Equal(RemoteRowAction.None, RemoteRules.Actions(Job("submitting")).Primary);
    }

    [Fact]
    public void AHeldCopiedLinkIsAddedByEnterAndDoesNotClaimToKnowItIsUncached()
    {
        var job = Job("intended", approval: true);
        job.HeldForApproval = true;
        job.ItemId = null;
        job.Name = "copied.bin";

        var actions = RemoteRules.Actions(job);
        Assert.Equal(RemoteRowAction.Approve, actions.Primary);
        Assert.False(actions.CheckNow);

        var row = RemoteText.JobRow(job, DateTimeOffset.UtcNow);
        Assert.Equal("Copied link", row.Status);
        Assert.Equal("Enter adds it to TorBox.", row.Detail);
        Assert.Equal("Add to TorBox", RemoteText.AddTo(job));
        Assert.DoesNotContain("cached", row.Detail, StringComparison.OrdinalIgnoreCase);

        // One the command line added without its go-ahead, or one saved before adding was the decision.
        job.HeldForApproval = false;
        row = RemoteText.JobRow(job, DateTimeOffset.UtcNow);
        Assert.Equal("Not started", row.Status);
        Assert.Equal("Enter starts it on TorBox.", row.Detail);
    }

    [Fact]
    public void ApprovingAndThePasswordAreOnlyOfferedWhenAsked()
    {
        Assert.False(RemoteRules.Actions(Job("waiting", approval: false)).Approve);
        Assert.False(RemoteRules.Actions(Job("waiting", approval: true)).Approve);
        Assert.False(RemoteRules.Actions(Job("failed", failureKind: "rejected")).EnterLinkPassword);
    }

    [Fact]
    public void BrowsingNeedsAKeyThatCanBeReadAndACatalog()
    {
        RemoteAccountDto Account(bool key, bool unreadable = false, string capability = "catalog") => new()
        {
            Provider = "torbox",
            HasKey = key,
            KeyUnreadable = unreadable,
            Capabilities = [capability],
            ItemKinds = ["torrents"],
        };

        Assert.True(RemoteRules.CanBrowse(Account(true)));
        Assert.False(RemoteRules.CanBrowse(Account(false)));
        Assert.False(RemoteRules.CanBrowse(Account(true, unreadable: true)));
        Assert.False(RemoteRules.CanBrowse(Account(true, capability: "account")));
        Assert.False(RemoteRules.CanBrowse(new RemoteAccountDto { HasKey = true, Capabilities = ["catalog"] }));
    }

    [Fact]
    public void AnItemIsLinkedToItsActiveJobBeforeAFailedOne()
    {
        var item = new RemoteItemDto { Provider = "torbox", Kind = "torrents", ItemId = "42" };
        var failed = Job("failed");
        failed.UpdatedAt = DateTimeOffset.UtcNow;
        var active = Job("waiting");
        active.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var other = Job("waiting");
        other.ItemId = "43";
        var sameIdOtherKind = Job("waiting");
        sameIdOtherKind.Kind = "webdl";

        Assert.Same(active, RemoteRules.JobFor([failed, other, active, sameIdOtherKind], item));
        Assert.Same(failed, RemoteRules.JobFor([failed, other, sameIdOtherKind], item));
        Assert.Null(RemoteRules.JobFor([other, sameIdOtherKind], item));
    }

    [Fact]
    public void DeletingFromTheProviderNeedsAJobThatKnowsItsItem()
    {
        var unknown = Job("waiting");
        unknown.ItemId = null;

        Assert.True(RemoteRules.CanDeleteFromProvider(Job("failed")));
        Assert.False(RemoteRules.CanDeleteFromProvider(unknown));
        Assert.False(RemoteRules.CanDeleteFromProvider(null));
    }

    private static RemoteJobDto Asking(string state, string mode)
    {
        var job = Job(state);
        job.SelectionMode = mode;
        return job;
    }

    [Theory]
    [InlineData("waiting", "askWhenReady", "SelectFiles")]
    [InlineData("intended", "askIfCached", "SelectFiles")]
    [InlineData("submitting", "askWhenReady", "SelectFiles")]
    [InlineData("submissionUnknown", "askIfCached", "SelectFiles")]
    [InlineData("waiting", "all", "CheckNow")]
    [InlineData("waiting", "chosen", "CheckNow")]
    [InlineData("intended", "all", "CheckNow")]
    [InlineData("awaitingSelection", "askWhenReady", "SelectFiles")]
    [InlineData("awaitingSelection", "chosen", "SelectFiles")]
    [InlineData("failed", "askWhenReady", "Retry")]
    [InlineData("blocked", "askWhenReady", "Retry")]
    public void ARowThatWouldAskOffersItsFilesBeforeTheProviderIsDone(string state, string mode, string primary)
        => Assert.Equal(Enum.Parse<RemoteRowAction>(primary), RemoteRules.Actions(Asking(state, mode)).Primary);

    [Fact]
    public void ACopiedLinkOrOneNotStartedIsAddedFirstEvenWhenItWouldAsk()
    {
        // A held folder link asks by default, and nobody has added it yet.
        var held = Asking("intended", "askWhenReady");
        held.ApprovalNeeded = true;
        held.HeldForApproval = true;
        Assert.False(RemoteRules.CanChooseEarly(held));
        Assert.Equal(RemoteRowAction.Approve, RemoteRules.Actions(held).Primary);

        held.HeldForApproval = false;
        Assert.False(RemoteRules.CanChooseEarly(held));
        Assert.Equal(RemoteRowAction.Approve, RemoteRules.Actions(held).Primary);
    }

    // ---- Several rows at once ----------------------------------------------------------------------

    private static RemoteJobDto Copied()
    {
        var job = Job("intended", approval: true);
        job.HeldForApproval = true;
        return job;
    }

    [Fact]
    public void EnterOnSeveralRowsAddsThemAllOrChoosesTheirFilesInTurnAndLeavesAMixAlone()
    {
        Assert.Equal(RemoteRowAction.None, RemoteRules.EnterFor([]));

        // One row: its own next step, whatever it is.
        Assert.Equal(RemoteRowAction.Retry, RemoteRules.EnterFor([Job("failed", failureKind: "rejected")]));
        Assert.Equal(RemoteRowAction.CheckNow, RemoteRules.EnterFor([Job("waiting")]));

        Assert.Equal(RemoteRowAction.Approve, RemoteRules.EnterFor([Copied(), Copied(), Job("intended", approval: true)]));
        Assert.Equal(RemoteRowAction.SelectFiles, RemoteRules.EnterFor([Job("awaitingSelection"), Job("awaitingSelection"), Asking("waiting", "askWhenReady")]));

        // Several waiting for different things, or for anything else, are left alone.
        Assert.Equal(RemoteRowAction.None, RemoteRules.EnterFor([Copied(), Job("awaitingSelection")]));
        Assert.Equal(RemoteRowAction.None, RemoteRules.EnterFor([Job("failed", failureKind: "rejected"), Job("blocked")]));
        Assert.Equal(RemoteRowAction.None, RemoteRules.EnterFor([Job("waiting"), Job("waiting")]));
    }

    [Fact]
    public void AMenuItemStandsForSeveralRowsOnlyWhenEveryOneTakesIt()
    {
        Assert.True(RemoteRules.AllCan([Copied(), Copied()], actions => actions.Approve));
        Assert.False(RemoteRules.AllCan([Copied(), Job("waiting")], actions => actions.Approve));
        Assert.False(RemoteRules.AllCan([], actions => actions.Approve));
        Assert.True(RemoteRules.AllCan([Job("awaitingSelection"), Asking("waiting", "askIfCached")], actions => actions.SelectFiles));
        Assert.False(RemoteRules.AllCan([Job("awaitingSelection"), Copied()], actions => actions.SelectFiles));

        // Adding several names their provider only when they share one.
        var elsewhere = Copied();
        elsewhere.Provider = "fetcher";
        elsewhere.ProviderName = "Fetcher";
        Assert.Equal("Add to TorBox", RemoteText.AddTo([Copied(), Copied()]));
        Assert.Equal("Add", RemoteText.AddTo([Copied(), elsewhere]));
    }

    [Fact]
    public void DownloadAllFilesIsForSeveralRowsWaitingForAChoiceAndCoversOnlyThose()
    {
        var ready = Job("awaitingSelection");
        var other = Job("awaitingSelection");
        var early = Asking("waiting", "askWhenReady");

        Assert.False(RemoteRules.OffersDownloadAll([ready]));
        Assert.False(RemoteRules.OffersDownloadAll([ready, early]));
        Assert.True(RemoteRules.OffersDownloadAll([ready, other, early, Copied()]));
        Assert.Equal([ready, other], RemoteRules.TakingAllFiles([ready, early, other, Copied()]));
        Assert.Equal("All files of 2 items will download.", RemoteText.AllFilesTaken(2));
        Assert.Equal("All its files will download.", RemoteText.AllFilesTaken(1));
    }

    [Fact]
    public async Task SeveralRowsFilesAreChosenOneAfterAnotherAndCancelEndsIt()
    {
        var first = Job("awaitingSelection");
        var dealtWith = Job("awaitingSelection");
        var gone = Job("awaitingSelection");
        var third = Job("awaitingSelection");
        var fourth = Job("awaitingSelection");
        var now = new Dictionary<Guid, RemoteJobDto> { [first.Id] = first, [dealtWith.Id] = Job("localStarted"), [third.Id] = third, [fourth.Id] = fourth };
        var asked = new List<Guid>();

        // One was dealt with elsewhere meanwhile and one has left the list: both are passed over. Cancel on "third"
        // ends it, so the last is never asked.
        var answers = new Queue<bool>([true, false]);
        await RemoteRules.WalkFileChoicesAsync([first.Id, dealtWith.Id, gone.Id, third.Id, fourth.Id], id => now.GetValueOrDefault(id), job =>
        {
            asked.Add(job.Id);
            return Task.FromResult(answers.Dequeue());
        });

        Assert.Equal([first.Id, third.Id], asked);
    }

    [Fact]
    public void AWalkGoesDownTheListWhateverOrderTheRowsWereClickedIn()
    {
        var top = Job("awaitingSelection");
        var middle = Job("awaitingSelection");
        var bottom = Job("awaitingSelection");
        var elsewhere = Job("awaitingSelection");

        Assert.Equal([top.Id, middle.Id, bottom.Id, elsewhere.Id], RemoteRules.InListOrder([bottom, elsewhere, top, middle], [Guid.NewGuid(), top.Id, middle.Id, Guid.NewGuid(), bottom.Id]));
    }
}

public sealed class RemoteRowTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.FromHours(3));

    private static RemoteJobDto Job(string state, string? stage = null) => new()
    {
        Id = Guid.NewGuid(),
        Provider = "torbox",
        ProviderName = "TorBox",
        Kind = "torrents",
        State = state,
        Stage = stage,
        Name = "Some Name",
    };

    private static RemoteRowView Row(RemoteJobDto job) => RemoteText.JobRow(job, Now, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("waiting", "queued", "Waiting for TorBox")]
    [InlineData("waiting", "processing", "Processing on TorBox")]
    [InlineData("waiting", "stalled", "Stalled on TorBox")]
    [InlineData("waiting", "ready", "Ready to download")]
    [InlineData("awaitingSelection", "ready", "Choose files")]
    [InlineData("submitting", null, "Adding to TorBox…")]
    [InlineData("blocked", null, "TorBox account busy")]
    [InlineData("failed", null, "Failed on TorBox")]
    public void StagesAreWordsNamedAfterTheProvider(string state, string? stage, string words)
        => Assert.Equal(words, Row(Job(state, stage)).Status);

    [Fact]
    public void TheProvidersNameComesFromTheHost()
    {
        var job = Job("waiting", "processing");
        job.ProviderName = "Somewhere Else";

        var row = Row(job);

        Assert.Equal("Processing on Somewhere Else", row.Status);
        Assert.DoesNotContain("TorBox", row.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProvidersProgressIsLabelledAsItsOwn()
    {
        var job = Job("waiting", "processing");
        job.Progress = 0.45;
        job.SpeedBytesPerSecond = 3 * 1024 * 1024;
        job.EtaSeconds = 125;

        var row = Row(job);

        Assert.DoesNotContain("%", row.Status, StringComparison.Ordinal);
        Assert.Contains("TorBox reports", row.Detail, StringComparison.Ordinal);
        Assert.Contains("45%", row.Detail, StringComparison.Ordinal);
        Assert.Contains("about 2m 5s left", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowSaysWhenItIsNextChecked()
    {
        var job = Job("waiting", "queued");
        job.NextCheckAt = Now.AddMinutes(5);
        Assert.Contains("Next check ", Row(job).Detail, StringComparison.Ordinal);

        job.NextCheckAt = Now.AddSeconds(-1);
        Assert.Contains("Checking soon", Row(job).Detail, StringComparison.Ordinal);

        var chosen = Job("awaitingSelection");
        chosen.NextCheckAt = Now.AddMinutes(5);
        Assert.DoesNotContain("Next check", Row(chosen).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureShowsItsRealCause()
    {
        var job = Job("failed");
        job.FailureKind = "accountLimit";
        job.FailureMessage = "TorBox says the daily limit for this site is used up.";

        var row = Row(job);

        Assert.Contains("daily limit", row.Detail, StringComparison.Ordinal);
        Assert.Contains("daily limit", row.ToolTip, StringComparison.Ordinal);
        Assert.Equal(RemoteRowTone.Failed, row.Tone);
    }

    [Fact]
    public void AFailureWithNoWordsStillSaysWhatKindItWas()
    {
        var job = Job("failed");
        job.FailureKind = "rateLimited";

        Assert.Contains("TorBox is asking for fewer requests", Row(job).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressInWhatTheHostSentIsNotShown()
    {
        var job = Job("failed");
        job.Name = "https://cdn.example.test/file?token=abc";
        job.FailureMessage = "Failed on https://cdn.example.test/file?token=abc";
        job.Notes = ["See https://cdn.example.test/file?token=abc"];
        job.Status = "https://cdn.example.test/file?token=abc";
        job.RawState = "https://cdn.example.test/file?token=abc";

        var row = Row(job);

        foreach (var text in new[] { row.Name, row.Status, row.Detail, row.ToolTip, row.AccessibleName })
        {
            Assert.DoesNotContain("token", text, StringComparison.Ordinal);
            Assert.DoesNotContain("cdn.example", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ARowWithNoNameFallsBackToItsHostThenItsKind()
    {
        var hosted = Job("intended");
        hosted.Name = null;
        hosted.DisplayHost = "mega.nz";
        Assert.Equal("mega.nz", Row(hosted).Name);

        var bare = Job("intended");
        bare.Name = null;
        Assert.Equal("Torrent on TorBox", Row(bare).Name);
    }

    [Fact]
    public void AnItemNothingWasSentForSaysEnterStartsIt()
    {
        var job = Job("intended");
        job.ApprovalNeeded = true;
        job.NextCheckAt = Now.AddMinutes(1);

        var row = Row(job);

        Assert.Equal("Not started", row.Status);
        Assert.Contains("Enter starts it", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("approv", row.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Next check", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowWaitingForAChoiceSaysChooseFilesAndNothingMoreUnlessSomethingIsMissing()
    {
        var job = Job("awaitingSelection", "ready");
        job.Status = "Ready to download. Choose the files.";
        var row = Row(job);
        Assert.Equal("Choose files", row.Status);
        Assert.Equal(string.Empty, row.Detail);

        job.FailureMessage = "1 of the chosen files isn't in TorBox's list. Choose again.";
        job.Status = job.FailureMessage;
        row = Row(job);
        Assert.Equal("Choose files", row.Status);
        Assert.Equal("1 of the chosen files isn't in TorBox's list. Choose again.", row.Detail);
        Assert.Equal(RemoteRowTone.Waiting, row.Tone);
    }

    [Fact]
    public void AnEarlyChoiceIsSaidInPassingBecauseTheRowCantShowIt()
    {
        Assert.Equal("All files will download when ready.", RemoteText.EarlyChoice(all: true));
        Assert.Equal("Your choice will download when ready.", RemoteText.EarlyChoice(all: false));
        Assert.Equal("Download all when ready", RemoteText.DownloadAllWhenReady);
    }

    [Fact]
    public void TheDialogsSayWhatTheyWillAndWillNotDo()
    {
        var job = Job("intended");

        Assert.Contains("doesn't touch TorBox", RemoteText.RemoveQuestion([job]), StringComparison.Ordinal);
        Assert.Contains("Some Name", RemoteText.RemoveQuestion([job]), StringComparison.Ordinal);
        Assert.Contains("these 2 items", RemoteText.RemoveQuestion([job, job]), StringComparison.Ordinal);
        Assert.Contains("can't be undone", RemoteText.DeleteQuestion("Some Name", "TorBox"), StringComparison.Ordinal);
        Assert.Contains("Some Name", RemoteText.DeleteQuestion("Some Name", "TorBox"), StringComparison.Ordinal);
        Assert.Contains("isn't an archive password", RemoteText.LinkPasswordHint(job), StringComparison.Ordinal);
    }

    // ---- Items on the account -------------------------------------------------------------------

    private static RemoteItemDto Item(string stage = "ready", bool cached = false, string? error = null) => new()
    {
        Provider = "torbox",
        Kind = "torrents",
        ItemId = "42",
        Name = "An Item",
        Size = 2048,
        FileCount = 3,
        Stage = stage,
        Cached = cached,
        Error = error,
        CreatedAt = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero),
        ExpiresAt = new DateTimeOffset(2026, 12, 5, 12, 0, 0, TimeSpan.Zero),
    };

    [Theory]
    [InlineData("ready", true, "Cached on TorBox")]
    [InlineData("ready", false, "Ready to download")]
    [InlineData("queued", false, "Waiting for TorBox")]
    [InlineData("metadataPending", false, "Waiting for TorBox to list the files")]
    [InlineData("processing", false, "Processing on TorBox")]
    [InlineData("stalled", false, "Stalled on TorBox")]
    public void AnItemsStatusIsWords(string stage, bool cached, string words)
        => Assert.Equal(words, RemoteText.Item(Item(stage, cached), "TorBox", null, culture: CultureInfo.InvariantCulture).Status);

    [Fact]
    public void AnUnknownStageShowsTheProvidersOwnWordAndIsNotReady()
    {
        var item = Item("unknown");
        item.RawState = "frobnicating";

        var view = RemoteText.Item(item, "TorBox", null, culture: CultureInfo.InvariantCulture);

        Assert.Equal("TorBox reports: frobnicating", view.Status);
    }

    [Fact]
    public void AFailedItemCanNotBeDownloadedAndSaysWhy()
    {
        var view = RemoteText.Item(Item("failed", error: "Hoster is down"), "TorBox", null, culture: CultureInfo.InvariantCulture);

        Assert.Equal("Failed on TorBox", view.Status);
        Assert.Equal("Hoster is down", view.Problem);
        Assert.False(view.CanDownload);
        Assert.Equal(RemoteRowTone.Failed, view.Tone);
    }

    [Fact]
    public void AnItemStillBeingWorkedOnCanStillBeAsked()
        => Assert.True(RemoteText.Item(Item("processing"), "TorBox", null, culture: CultureInfo.InvariantCulture).CanDownload);

    [Fact]
    public void AnItemShowsItsSizeFilesAndDates()
    {
        var view = RemoteText.Item(Item(), "TorBox", null, culture: CultureInfo.InvariantCulture);

        Assert.Equal("Torrent", view.Type);
        Assert.Equal("2.0 KB", view.Size);
        Assert.Equal("3", view.Files);
        Assert.Contains("2026", view.Added, StringComparison.Ordinal);
        Assert.Contains("2026", view.Expires, StringComparison.Ordinal);
        Assert.Contains("3 files", view.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownSizeAndDateStayBlank()
    {
        var item = Item();
        item.Size = 0;
        item.FileCount = 0;
        item.ExpiresAt = null;

        var view = RemoteText.Item(item, "TorBox", null, culture: CultureInfo.InvariantCulture);

        Assert.Empty(view.Size);
        Assert.Empty(view.Files);
        Assert.Empty(view.Expires);
    }

    [Fact]
    public void ALinkedJobsWordsAreNewerThanTheList()
    {
        var job = Job("waiting", "processing");

        Assert.Equal("Processing on TorBox", RemoteText.Item(Item(), "TorBox", job, culture: CultureInfo.InvariantCulture).Status);

        var started = Job("localStarted");
        started.Status = "Downloading locally";
        Assert.Equal("Downloading locally", RemoteText.Item(Item(), "TorBox", started, culture: CultureInfo.InvariantCulture).Status);
        Assert.Equal("Paused locally", RemoteText.Item(Item(), "TorBox", started, "Paused locally", CultureInfo.InvariantCulture).Status);
    }

    [Fact]
    public void AFailedJobDoesNotHideWhatTheListSaysButShowsItsCause()
    {
        var job = Job("failed");
        job.FailureMessage = "It went wrong.";

        var view = RemoteText.Item(Item(cached: true), "TorBox", job, culture: CultureInfo.InvariantCulture);

        Assert.Equal("Cached on TorBox", view.Status);
        Assert.Equal("It went wrong.", view.Problem);
        Assert.True(view.CanDownload);
    }

    [Fact]
    public void LocalDownloadsAreSummedUpInWords()
    {
        static JobDto File(string state) => new() { State = state };

        Assert.Null(RemoteText.LocalWords([]));
        Assert.Equal("Completed", RemoteText.LocalWords([File("completed"), File("completed")]));
        Assert.Equal("1 of 2 local downloads failed", RemoteText.LocalWords([File("completed"), File("failed")]));
        Assert.Equal("Downloading locally", RemoteText.LocalWords([File("downloading")]));
        Assert.Equal("Downloading locally, 1 of 2 done", RemoteText.LocalWords([File("completed"), File("connecting")]));
        Assert.Equal("Paused locally", RemoteText.LocalWords([File("completed"), File("paused")]));
        Assert.Equal("Waiting to download locally", RemoteText.LocalWords([File("queued"), File("paused")]));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(-5, "")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536 * 1024, "1.5 MB")]
    public void BytesUseBinaryUnitsAndAnUnknownSizeStaysBlank(long bytes, string words)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(words, RemoteText.Bytes(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
