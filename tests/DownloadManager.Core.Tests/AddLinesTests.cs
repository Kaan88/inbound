using System.Collections.Concurrent;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

public sealed class AddLinesTests
{
    private const string HosterLink = "https://mega.nz/file/AbCdEf#SecretKeyValue";
    private const string DirectLink = "https://cdn1.tb-cdn.io/dl/1?token=aaaa-bbbb-cccc";
    private const string MagnetLink = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Big%20Buck%20Bunny";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static RemoteClassifyResult Kind(string kind, Action<RemoteClassifyResult>? configure = null)
    {
        var result = new RemoteClassifyResult { Ok = true, Kind = kind, Provider = "torbox", ProviderName = "TorBox" };
        if (kind == "folderLink")
        {
            result.ItemKind = "torrents";
            result.ItemId = "133998001";
        }

        configure?.Invoke(result);
        return result;
    }

    private static AddLine Line(string input, RemoteClassifyResult? result) => new(input) { Result = result };

    // ---- Finding inputs ----------------------------------------------------------------------------

    [Fact]
    public void WithoutAProviderOnlyWebLinksAreFoundAsBefore()
    {
        var found = LinkExtractor.HttpLinks($"see https://a.example/x.zip, and {MagnetLink} and C:\\t\\a.torrent\nHTTPS://a.example/X.ZIP");

        Assert.Equal(["https://a.example/x.zip"], found);
    }

    [Fact]
    public void WithAProviderMagnetsAndWholeLineTorrentPathsAreFoundInOrder()
    {
        var text = $"first https://a.example/x.zip.\r\n{MagnetLink}\r\n  C:\\t\\a.torrent  \r\nnot this C:\\t\\b.torrent one\n\\\\server\\c.torrent\nhttps://torbox.app/download?id=1&type=torrents";

        var found = LinkExtractor.Inputs(text);

        Assert.Equal(["https://a.example/x.zip", MagnetLink, "C:\\t\\a.torrent", "\\\\server\\c.torrent", "https://torbox.app/download?id=1&type=torrents"], found);
    }

    [Fact]
    public void LinksDifferingOnlyByCaseAreDifferentLinks()
    {
        var found = LinkExtractor.Inputs("https://mega.nz/file/a#Key\nhttps://mega.nz/file/a#kEY\nhttps://mega.nz/file/a#Key");

        Assert.Equal(["https://mega.nz/file/a#Key", "https://mega.nz/file/a#kEY"], found);
    }

    [Fact]
    public void NothingUsableIsNothing()
    {
        Assert.Empty(LinkExtractor.Inputs(null));
        Assert.Empty(LinkExtractor.Inputs("   "));
        Assert.Empty(LinkExtractor.Inputs("ftp://a.example/x just words"));
    }

    // ---- Routes ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ordinaryLink", "Direct")]
    [InlineData("providerDirectLink", "Direct")]
    [InlineData("magnet", "Provider")]
    [InlineData("torrentFile", "Provider")]
    [InlineData("hosterLink", "Provider")]
    [InlineData("folderLink", "FolderLink")]
    [InlineData("undetermined", "None")]
    [InlineData("unsupported", "None")]
    [InlineData("somethingFromALaterHost", "None")]
    [InlineData("", "None")]
    public void EachKindGoesItsOwnWay(string kind, string expected)
        => Assert.Equal(expected, Line(HosterLink, Kind(kind)).Route.ToString());

    [Fact]
    public void ALineWithNoAnswerGoesNowhereYet()
        => Assert.Equal(AddRoute.Checking, new AddLine(HosterLink).Route);

    [Fact]
    public void AFolderLinkWithNoItemCantBeUsed()
        => Assert.Equal(AddRoute.None, Line(HosterLink, Kind("folderLink", result => result.ItemId = null)).Route);

    [Fact]
    public void ALinkNobodyCouldClassifyIsDirectOnlyByExplicitChoice()
    {
        var line = Line("https://unknown.example/f", Kind("undetermined"));
        Assert.Equal(AddRoute.None, line.Route);

        line.DirectChosen = true;
        Assert.Equal(AddRoute.Direct, line.Route);
    }

    [Fact]
    public void AFailedCheckIsAlsoDirectOnlyByExplicitChoice()
    {
        var line = Line("https://unknown.example/f", new RemoteClassifyResult { Error = RemoteText.ClassifyLost });
        Assert.Equal(AddRoute.None, line.Route);

        line.DirectChosen = true;
        Assert.Equal(AddRoute.Direct, line.Route);
    }

    [Fact]
    public void AHosterLinkIsNeverDirectWhateverIsChosen()
    {
        var session = NewSession(new FakeClassifier());
        session.Seed(HosterLink, Kind("hosterLink"));
        var line = session.Lines[0];

        session.ChooseDirect(line, true);

        Assert.False(line.DirectChosen);
        Assert.Equal(AddRoute.Provider, line.Route);
        Assert.False(RemoteText.AddLineWords(line, "TorBox").CanChooseDirect);
    }

    [Fact]
    public void AMagnetThatFailedToClassifyCantBeDirectEvenIfAsked()
    {
        var session = NewSession(new FakeClassifier());
        session.Seed(MagnetLink, new RemoteClassifyResult { Error = RemoteText.ClassifyLost });
        var line = session.Lines[0];

        session.ChooseDirect(line, true);

        Assert.Equal(AddRoute.None, line.Route);
        Assert.False(RemoteText.AddLineWords(line, "TorBox").CanChooseDirect);
        Assert.True(RemoteText.AddLineWords(line, "TorBox").CanRetry);
    }

    // ---- Words -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("folderLink", "Folder link")]
    [InlineData("magnet", "Magnet")]
    [InlineData("torrentFile", "Torrent file")]
    [InlineData("hosterLink", "Hoster through TorBox")]
    [InlineData("providerDirectLink", "Direct provider link")]
    [InlineData("ordinaryLink", "Ordinary link")]
    [InlineData("unsupported", "Unsupported")]
    [InlineData("undetermined", "Can't tell")]
    [InlineData("somethingFromALaterHost", "Unsupported")]
    public void EachKindHasItsWord(string kind, string word)
        => Assert.Equal(word, RemoteText.AddLineWords(Line("https://a.example/f.zip", Kind(kind)), "Fallback").Word);

    [Fact]
    public void UnsupportedAndCantTellSayWhy()
    {
        var unsupported = RemoteText.AddLineWords(Line("https://a.example", Kind("unsupported", result => result.Reason = "This magnet has no content hash.")), "TorBox");
        Assert.Contains("no content hash", unsupported.Detail);
        Assert.True(unsupported.IsProblem);

        var undetermined = RemoteText.AddLineWords(Line("https://a.example", Kind("undetermined", result => result.Reason = "TorBox could not be reached.")), "TorBox");
        Assert.Contains("could not be reached", undetermined.Detail);
        Assert.Contains("isn't treated as an ordinary link", undetermined.Detail);
        Assert.True(undetermined.CanRetry);
        Assert.True(undetermined.CanChooseDirect);
    }

    [Fact]
    public void AHosterSaysWhatTheProviderSaysAboutIt()
    {
        var view = RemoteText.AddLineWords(Line(HosterLink, Kind("hosterLink", result =>
        {
            result.HosterName = "Mega";
            result.HosterAvailable = true;
            result.HosterDailyLinksLeft = 97;
            result.HosterDailyBytesLeft = 5L * 1024 * 1024 * 1024;
            result.HosterLargestFile = 200L * 1024 * 1024 * 1024;
            result.HosterNote = "Slow at times";
        })), "TorBox");

        Assert.Contains("TorBox says this site is available.", view.Detail);
        Assert.Contains("TorBox notes: Slow at times.", view.Detail);
        Assert.Contains("97 links left today", view.Detail);
        Assert.Contains("5.0 GB left today", view.Detail);
        Assert.Contains("largest file 200.0 GB", view.Detail);
        Assert.False(view.IsProblem);
        Assert.True(view.CanTakeLinkPassword);
        Assert.False(view.CanChooseDirect);
    }

    [Fact]
    public void AHosterThatIsDownOrUsedUpStartsUnticked()
    {
        var session = NewSession(new FakeClassifier());
        session.Seed("https://a.example/1", Kind("hosterLink", result => result.HosterAvailable = false));
        session.Seed("https://a.example/2", Kind("hosterLink", result => result.HosterDailyLinksLeft = 0));
        session.Seed("https://a.example/3", Kind("hosterLink", result => result.HosterAvailable = true));

        Assert.False(session.Lines[0].IsSelected);
        Assert.False(session.Lines[1].IsSelected);
        Assert.True(session.Lines[2].IsSelected);
        Assert.Contains("isn't available right now", RemoteText.AddLineWords(session.Lines[0], "TorBox").Detail);
        Assert.Contains("no links left today", RemoteText.AddLineWords(session.Lines[1], "TorBox").Detail);
        Assert.True(RemoteText.AddLineWords(session.Lines[0], "TorBox").IsProblem);
    }

    [Theory]
    [InlineData(HosterLink, "hosterLink")]
    [InlineData(DirectLink, "providerDirectLink")]
    [InlineData(MagnetLink, "magnet")]
    [InlineData(HosterLink, "undetermined")]
    [InlineData(HosterLink, "unsupported")]
    [InlineData(DirectLink, "ordinaryLink")]
    public void NoLineEverShowsASecretOfItsAddress(string input, string kind)
    {
        var view = RemoteText.AddLineWords(Line(input, Kind(kind)), "TorBox");
        var shown = string.Join("|", view.Name, view.Word, view.Detail, view.ToolTip, view.AccessibleName);

        Assert.DoesNotContain("SecretKeyValue", shown);
        Assert.DoesNotContain("aaaa-bbbb-cccc", shown);
        Assert.DoesNotContain("0123456789abcdef", shown);
        Assert.DoesNotContain("AbCdEf", kind == "ordinaryLink" ? string.Empty : shown);
    }

    [Fact]
    public void LinesShowingOnlyAHostAreToldApartByPosition()
    {
        var session = NewSession(new FakeClassifier());
        session.Seed("https://mega.nz/file/one#a", Kind("hosterLink"));
        Assert.Equal("mega.nz", RemoteText.AddLineWords(Line("https://mega.nz/file/one#a", Kind("hosterLink")), "TorBox").Name);
        session.Seed("https://mega.nz/file/two#b", Kind("hosterLink"));

        Assert.Equal("mega.nz (link 1)", RemoteText.AddLineWords(session.Lines[0], "TorBox").Name);
        Assert.Equal("mega.nz (link 2)", RemoteText.AddLineWords(session.Lines[1], "TorBox").Name);
    }

    [Fact]
    public void ALineStillBeingCheckedShowsOnlyItsHost()
    {
        var view = RemoteText.AddLineWords(new AddLine(HosterLink), "TorBox");

        Assert.Equal("mega.nz", view.Name);
        Assert.Equal("Checking…", view.Word);
        Assert.DoesNotContain("AbCdEf", view.ToolTip);
    }

    [Fact]
    public void ANameFromTheHostHasAddressesTakenOut()
    {
        var view = RemoteText.AddLineWords(Line(HosterLink, Kind("magnet", result => result.Name = "look at https://evil.example/x?token=abc")), "TorBox");

        Assert.DoesNotContain("evil.example", view.Name);
        Assert.DoesNotContain("abc", view.Name);
    }

    [Fact]
    public void ATorrentFileShowsItsFileName()
    {
        Assert.Equal("a.torrent", RemoteText.AddLineWords(new AddLine("C:\\t\\a.torrent"), "TorBox").Name);
    }

    [Fact]
    public void ALinkTheHostFailedToClassifyExplainsAndNeverSaysOrdinary()
    {
        var view = RemoteText.AddLineWords(Line("https://a.example/f", new RemoteClassifyResult { Error = RemoteText.ClassifyLost }), "TorBox");

        Assert.Equal("Can't tell", view.Word);
        Assert.Contains(RemoteText.ClassifyLost, view.Detail);
        Assert.False(view.CanSelect);
    }

    [Theory]
    [InlineData(RemoteText.HostLost, RemoteText.ClassifyLost)]
    [InlineData(RemoteText.HostTooSlow, RemoteText.ClassifyLost)]
    [InlineData(RemoteText.HostNotRunning, RemoteText.HostNotRunning)]
    public void ALostClassifyDoesntSayItMayHaveGoneThrough(string error, string expected)
        => Assert.Equal(expected, RemoteText.ClassifyFailure(error));

    // ---- The session ------------------------------------------------------------------------------

    [Fact]
    public async Task TextIsDebouncedAndEachLineAskedAboutOnce()
    {
        var classifier = new FakeClassifier();
        var session = NewSession(classifier, debounce: TimeSpan.FromMilliseconds(50));

        session.SetText("https://a.example/1");
        session.SetText("https://a.example/1\nhttps://a.example/2");
        session.SetText("https://a.example/1\nhttps://a.example/2\nhttps://a.example/3");
        await UntilAsync(() => session.Lines.Count == 3 && session.Lines.All(line => !line.IsChecking));

        Assert.Equal(["https://a.example/1", "https://a.example/2", "https://a.example/3"], classifier.Asked);
        Assert.Equal(1, classifier.Opened);

        session.SetText("https://a.example/2\nhttps://a.example/3\nhttps://a.example/4");
        await UntilAsync(() => session.Lines.Count == 3 && session.Lines[2].Input.EndsWith('4') && session.Lines.All(line => !line.IsChecking));

        Assert.Equal(4, classifier.Asked.Count);
        Assert.Equal("https://a.example/4", classifier.Asked[3]);
    }

    [Fact]
    public async Task TheConnectionClosesWhenThereIsNothingLeftToAsk()
    {
        var classifier = new FakeClassifier();
        var session = NewSession(classifier);

        session.SetTextNow("https://a.example/1");
        await UntilAsync(() => classifier.Disposed == 1);

        session.SetTextNow("https://a.example/1\nhttps://a.example/2");
        await UntilAsync(() => classifier.Disposed == 2);
        Assert.Equal(2, classifier.Opened);
    }

    [Fact]
    public async Task NothingChosenAndStillUnknownCanBeAdded()
    {
        var classifier = new FakeClassifier { Hold = true };
        var session = NewSession(classifier);

        session.SetTextNow("https://a.example/1\nhttps://a.example/2");
        Assert.False(session.CanAdd);
        Assert.Empty(session.Chosen);

        classifier.Release("https://a.example/1");
        await UntilAsync(() => !session.Lines[0].IsChecking);

        // One answer is in and one is not. The unanswered one is still chosen, so it holds things up.
        Assert.False(session.CanAdd);
        Assert.Equal(["https://a.example/1"], session.Chosen.Select(line => line.Input));

        session.Select(session.Lines[1], false);
        Assert.True(session.CanAdd);
    }

    [Fact]
    public async Task ALineThatCantBeUsedCantBeChosen()
    {
        var classifier = new FakeClassifier { Answer = _ => Kind("unsupported") };
        var session = NewSession(classifier);

        session.SetTextNow("https://a.example/1");
        await UntilAsync(() => !session.Lines[0].IsChecking);

        Assert.False(session.Lines[0].IsSelected);
        session.Select(session.Lines[0], true);
        Assert.False(session.Lines[0].IsSelected);
        Assert.False(session.CanAdd);
    }

    [Fact]
    public async Task TheUsersChoiceSurvivesTheAnswer()
    {
        var classifier = new FakeClassifier { Hold = true };
        var session = NewSession(classifier);
        session.SetTextNow("https://a.example/1");

        session.Select(session.Lines[0], false);
        classifier.Release("https://a.example/1");
        await UntilAsync(() => !session.Lines[0].IsChecking);

        Assert.False(session.Lines[0].IsSelected);
    }

    [Fact]
    public async Task ChoosingDirectIsWhatMakesAnUndeterminedLineAddable()
    {
        var classifier = new FakeClassifier { Answer = _ => Kind("undetermined") };
        var session = NewSession(classifier);
        session.SetTextNow("https://a.example/1");
        await UntilAsync(() => !session.Lines[0].IsChecking);
        var line = session.Lines[0];
        Assert.False(session.CanAdd);

        session.ChooseDirect(line, true);

        Assert.True(line.IsSelected);
        Assert.Equal(AddRoute.Direct, line.Route);
        Assert.True(session.CanAdd);
        Assert.Contains("because you chose that", RemoteText.AddLineWords(line, "TorBox").Detail);

        session.ChooseDirect(line, false);
        Assert.False(line.IsSelected);
        Assert.False(session.CanAdd);
    }

    [Fact]
    public async Task RetryAsksAgainAndForgetsTheChoiceAboutTheOldAnswer()
    {
        var answers = new Queue<RemoteClassifyResult>([Kind("undetermined"), Kind("hosterLink")]);
        var classifier = new FakeClassifier { Answer = _ => answers.Dequeue() };
        var session = NewSession(classifier);
        session.SetTextNow("https://a.example/1");
        await UntilAsync(() => !session.Lines[0].IsChecking);
        var line = session.Lines[0];
        session.ChooseDirect(line, true);

        session.Retry(line);
        await UntilAsync(() => !line.IsChecking);

        Assert.False(line.DirectChosen);
        Assert.Equal(AddRoute.Provider, line.Route);
        Assert.True(line.IsSelected);
        Assert.Equal(2, classifier.Asked.Count);
    }

    [Fact]
    public async Task AddMagnetRefusesAnythingElseWithoutAsking()
    {
        var classifier = new FakeClassifier { Answer = _ => Kind("magnet") };
        var session = NewSession(classifier, AddFocus.Magnet);

        session.SetTextNow($"https://a.example/f.zip\n{MagnetLink}");
        await UntilAsync(() => !session.Lines[1].IsChecking);

        Assert.Equal([MagnetLink], classifier.Asked);
        Assert.Equal(AddRoute.None, session.Lines[0].Route);
        Assert.Contains("isn't a magnet", RemoteText.AddLineWords(session.Lines[0], "TorBox").Detail);
        Assert.Equal(AddRoute.Provider, session.Lines[1].Route);
        Assert.Equal("No magnet was found in this text.", RemoteText.AddSummary([], AddFocus.Magnet, hasText: true).Text);
    }

    [Fact]
    public async Task ADirectLinkAlreadyListedFollowsTheSkipBox()
    {
        var classifier = new FakeClassifier { Answer = _ => Kind("ordinaryLink") };
        var session = NewSession(classifier, listed: link => link.EndsWith('1'));

        session.SetTextNow("https://a.example/1\nhttps://a.example/2");
        await UntilAsync(() => session.Lines.All(line => !line.IsChecking));

        Assert.False(session.Lines[0].IsSelected);
        Assert.True(session.Lines[1].IsSelected);
        Assert.Contains("Already in the list", RemoteText.AddLineWords(session.Lines[0], "TorBox").Detail);

        session.SkipListed = false;
        Assert.True(session.Lines[0].IsSelected);
        session.SkipListed = true;
        Assert.False(session.Lines[0].IsSelected);
    }

    [Fact]
    public async Task ABrokenConnectionTellsEveryLineAndOffersARetry()
    {
        var classifier = new FakeClassifier { BreakAfterFirst = true };
        var session = NewSession(classifier);

        session.SetTextNow("https://a.example/1\nhttps://a.example/2\nmagnet:?xt=urn:btih:abc");
        await UntilAsync(() => session.Lines.All(line => !line.IsChecking));

        Assert.All(session.Lines, line => Assert.Equal(AddRoute.None, line.Route));
        Assert.Single(classifier.Asked);
        Assert.True(RemoteText.AddLineWords(session.Lines[2], "TorBox").CanRetry);
        Assert.False(session.CanAdd);
    }

    [Fact]
    public async Task ClosingTheSessionCancelsTheCallAndClosesTheConnection()
    {
        var classifier = new FakeClassifier { Hold = true };
        var session = NewSession(classifier);
        session.SetTextNow("https://a.example/1");
        await UntilAsync(() => classifier.Asked.Count == 1);

        session.Dispose();

        await UntilAsync(() => classifier.Disposed == 1);
        Assert.True(classifier.WasCancelled);
    }

    [Fact]
    public async Task ALineGoneFromTheTextIgnoresItsLateAnswer()
    {
        var classifier = new FakeClassifier { Hold = true };
        var session = NewSession(classifier);
        session.SetTextNow("https://a.example/1");
        await UntilAsync(() => classifier.Asked.Count == 1);
        var gone = session.Lines[0];

        session.SetTextNow("https://a.example/2");
        classifier.Release("https://a.example/1");
        classifier.Release("https://a.example/2");
        await UntilAsync(() => !session.Lines[0].IsChecking);

        Assert.True(gone.IsChecking);
    }

    [Fact]
    public void ASummarySaysWhatIsGoingOn()
    {
        var waiting = new AddLine("https://a.example/1") { IsSelected = true };
        var done = Line("https://a.example/2", Kind("ordinaryLink"));
        done.IsSelected = true;
        var stuck = Line("https://a.example/3", Kind("unsupported"));

        Assert.Equal(string.Empty, RemoteText.AddSummary([], AddFocus.Links, hasText: false).Text);
        Assert.True(RemoteText.AddSummary([], AddFocus.Links, hasText: true).IsProblem);
        Assert.Equal("Checking 1 of 2…", RemoteText.AddSummary([waiting, done], AddFocus.Links, true).Text);
        Assert.Equal("1 of 2 selected. 1 can't be added as it is.", RemoteText.AddSummary([done, stuck], AddFocus.Links, true).Text);
    }

    // ---- What comes after adding -------------------------------------------------------------------

    private static RemoteJobDto Job(string state, Action<RemoteJobDto>? configure = null)
    {
        var job = new RemoteJobDto { Id = Guid.NewGuid(), Provider = "torbox", ProviderName = "TorBox", State = state, Kind = "torrents" };
        configure?.Invoke(job);
        return job;
    }

    [Fact]
    public void NotCachedAsksForApproval()
    {
        var view = RemoteText.AddOutcome(Job("intended", job => job.ApprovalNeeded = true), "x", null, DateTimeOffset.Now);

        Assert.Equal("Not cached, needs TorBox to fetch it", view.Word);
        Assert.True(view.NeedsApproval);
        Assert.False(view.IsChecking);
    }

    [Fact]
    public void CachedStaysSaidOnceItIsReadyAndSaysWhatComesNext()
    {
        var ready = RemoteText.AddOutcome(Job("awaitingSelection", job => job.Cached = true), "x", null, DateTimeOffset.Now);
        Assert.Equal("Cached on TorBox", ready.Word);
        Assert.Contains("Ready to download", ready.Detail);
        Assert.True(ready.NeedsFiles);
        Assert.Contains("choose the files", RemoteText.AddOutcomeSummary([ready], "TorBox"));

        var local = RemoteText.AddOutcome(Job("localStarted", job => { job.Cached = true; job.Status = "Downloading locally"; }), "x", null, DateTimeOffset.Now);
        Assert.Equal("Cached on TorBox", local.Word);
        Assert.Contains("Downloading locally", local.Detail);
    }

    [Theory]
    [InlineData("intended")]
    [InlineData("submitting")]
    [InlineData("waiting")]
    public void CachedSaysSo(string state)
    {
        var view = RemoteText.AddOutcome(Job(state, job => job.Cached = true), "x", null, DateTimeOffset.Now);

        Assert.Equal("Cached on TorBox", view.Word);
        Assert.False(view.NeedsApproval);
        Assert.False(view.IsChecking);
    }

    [Fact]
    public void BeforeTheHostHasLookedItIsStillBeingChecked()
    {
        var view = RemoteText.AddOutcome(Job("intended"), "x", null, DateTimeOffset.Now);

        Assert.Equal("Checking TorBox…", view.Word);
        Assert.True(view.IsChecking);
    }

    [Fact]
    public void AFailureShowsTheRealCauseAndOffersNoOtherRoute()
    {
        var view = RemoteText.AddOutcome(Job("failed", job => { job.FailureKind = "rejected"; job.FailureMessage = "Hoster is down"; }), "x", null, DateTimeOffset.Now);

        Assert.Equal("Failed on TorBox", view.Word);
        Assert.Contains("Hoster is down", view.Detail);
        Assert.True(view.IsProblem);
        Assert.False(view.NeedsApproval);
        Assert.False(view.NeedsLinkPassword);
    }

    [Fact]
    public void ALinkPasswordFailureAsksForOne()
    {
        var view = RemoteText.AddOutcome(Job("failed", job => job.FailureKind = "linkPasswordRequired"), "x", null, DateTimeOffset.Now);

        Assert.True(view.NeedsLinkPassword);
    }

    [Fact]
    public void AJobThatLeftTheListSaysSo()
        => Assert.Equal("Removed from the list", RemoteText.AddOutcome(null, "x", null, DateTimeOffset.Now).Word);

    [Fact]
    public void WhatTheHostSaidOnAddingIsShown()
    {
        var view = RemoteText.AddOutcome(Job("waiting"), "x", "This is already on the list.", DateTimeOffset.Now);

        Assert.Contains("already on the list", view.Detail);
    }

    [Fact]
    public void TheWordingSaysItUsesTheQuotaAndThatNothingIsSentBeforeApproval()
    {
        var words = RemoteText.ApproveExplanation("TorBox");

        Assert.Contains("uses your account's quota", words);
        Assert.Contains("Nothing is sent to TorBox until you approve it", words);
    }

    [Fact]
    public void TheOutcomeSummaryFollowsTheRows()
    {
        var checking = RemoteText.AddOutcome(Job("intended"), "x", null, DateTimeOffset.Now);
        var waiting = RemoteText.AddOutcome(Job("intended", job => job.ApprovalNeeded = true), "x", null, DateTimeOffset.Now);
        var cached = RemoteText.AddOutcome(Job("waiting", job => job.Cached = true), "x", null, DateTimeOffset.Now);

        Assert.Equal("Asking TorBox about 1 item…", RemoteText.AddOutcomeSummary([checking, waiting], "TorBox"));
        Assert.Equal("1 item is waiting for your approval.", RemoteText.AddOutcomeSummary([waiting, cached], "TorBox"));
        Assert.StartsWith("Nothing here needs you", RemoteText.AddOutcomeSummary([cached], "TorBox"));
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private readonly Lock _ui = new();

    private AddLineSession NewSession(FakeClassifier classifier, AddFocus focus = AddFocus.Links, Func<string, bool>? listed = null, TimeSpan? debounce = null)
        => new(focus, classifier.Open, listed ?? (_ => false), action => { lock (_ui) action(); }, debounce ?? TimeSpan.FromMilliseconds(1));

    private async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            lock (_ui)
            {
                if (condition()) return;
            }

            if (DateTime.UtcNow > deadline) Assert.Fail("It never happened.");
            await Task.Delay(5);
        }
    }

    private sealed class FakeClassifier
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _held = new();
        private readonly List<string> _asked = [];
        private readonly Lock _sync = new();
        private int _opened;
        private int _disposed;

        public Func<string, RemoteClassifyResult> Answer { get; set; } = _ => Kind("ordinaryLink");
        public bool Hold { get; set; }
        public bool BreakAfterFirst { get; set; }
        public bool WasCancelled { get; private set; }
        public int Opened => Volatile.Read(ref _opened);
        public int Disposed => Volatile.Read(ref _disposed);

        public List<string> Asked
        {
            get { lock (_sync) return _asked.ToList(); }
        }

        public IRemoteClassifier Open()
        {
            Interlocked.Increment(ref _opened);
            return new Channel(this);
        }

        public void Release(string input) => _held.GetOrAdd(input, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

        private sealed class Channel(FakeClassifier owner) : IRemoteClassifier
        {
            public bool Broken { get; private set; }

            public async Task<RemoteClassifyResult> ClassifyAsync(string text, CancellationToken cancellationToken)
            {
                lock (owner._sync) owner._asked.Add(text);
                if (owner.Hold)
                {
                    try
                    {
                        await owner._held.GetOrAdd(text, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        owner.WasCancelled = true;
                        throw;
                    }
                }

                if (owner.BreakAfterFirst)
                {
                    Broken = true;
                    return new RemoteClassifyResult { Error = RemoteText.ClassifyLost };
                }

                return owner.Answer(text);
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner._disposed);
                return ValueTask.CompletedTask;
            }
        }
    }
}
