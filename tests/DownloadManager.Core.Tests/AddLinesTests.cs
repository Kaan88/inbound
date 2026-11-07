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

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ATorrentPathOnItsOwnLineIsFoundHoweverLinesEnd(string end)
    {
        // A multi-line TextBox gives back bare CRs.
        var found = LinkExtractor.Inputs($"{MagnetLink}{end}C:\\t\\My File.torrent{end}https://a.example/x");

        Assert.Equal([MagnetLink, @"C:\t\My File.torrent", "https://a.example/x"], found);
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
    public void SelectAllIsOffOnOrMixedOverTheLinesThatCanBeSelected()
    {
        var first = Line("https://example.com/a.zip", Kind("ordinaryLink"));
        var second = Line("https://example.com/b.zip", Kind("ordinaryLink"));
        var stuck = Line("https://unknown.example/f", Kind("undetermined"));
        Assert.Equal(AddRoute.None, stuck.Route);

        Assert.Equal<bool?>(false, AddLineSession.SelectAllState([first, second, stuck]));

        first.IsSelected = true;
        Assert.Null(AddLineSession.SelectAllState([first, second, stuck]));

        second.IsSelected = true;
        Assert.Equal<bool?>(true, AddLineSession.SelectAllState([first, second, stuck]));
        Assert.Equal<bool?>(false, AddLineSession.SelectAllState([stuck]));
    }

    [Fact]
    public void ClickingSelectAllFromMixedSelectsEverything()
    {
        using var session = NewSession(new FakeClassifier());
        session.Seed("https://example.com/a.zip", Kind("ordinaryLink"));
        session.Seed("https://example.com/b.zip", Kind("ordinaryLink"));
        session.Seed("https://unknown.example/f", Kind("undetermined"));
        var lines = session.Lines;

        session.SelectAll(true);
        session.Select(lines[0], false);
        Assert.Null(AddLineSession.SelectAllState(lines));

        session.ToggleAll();
        Assert.Equal<bool?>(true, AddLineSession.SelectAllState(lines));

        session.ToggleAll();
        Assert.Equal<bool?>(false, AddLineSession.SelectAllState(lines));

        session.ToggleAll();
        Assert.Equal<bool?>(true, AddLineSession.SelectAllState(lines));
    }

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
    public async Task AddWaitsForTextThatHasNotReachedTheLinesYet()
    {
        var classifier = new FakeClassifier();
        var session = NewSession(classifier, debounce: TimeSpan.FromMilliseconds(50));
        session.SetTextNow("https://a.example/1");
        await UntilAsync(() => session.CanAdd);

        session.SetText("https://a.example/2");

        Assert.False(session.CanAdd);
        await UntilAsync(() => session.CanAdd && session.Lines is [{ Input: "https://a.example/2" }]);
    }

    [Fact]
    public async Task TextThatComesToTheSameLinesStillSaysSoSoAddComesBack()
    {
        var classifier = new FakeClassifier();
        var session = NewSession(classifier, debounce: TimeSpan.FromMilliseconds(20));
        session.SetTextNow("https://a.example/1");
        await UntilAsync(() => session.CanAdd);
        var told = 0;
        session.LinesChanged += () => told++;

        session.SetText("https://a.example/1 ");

        Assert.False(session.CanAdd);
        await UntilAsync(() => told == 1);
        Assert.True(session.CanAdd);
    }

    [Fact]
    public async Task TextGivenAtOnceReplacesTextStillWaiting()
    {
        var classifier = new FakeClassifier();
        var session = NewSession(classifier, debounce: TimeSpan.FromMilliseconds(20));

        session.SetText("https://a.example/old");
        session.SetTextNow("https://a.example/1");
        await UntilAsync(() => session.CanAdd);
        await Task.Delay(60);

        Assert.True(session.CanAdd);
        Assert.Equal("https://a.example/1", Assert.Single(session.Lines).Input);
    }

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

    [Fact]
    public void A_clean_add_is_a_count_and_nothing_is_said_when_nothing_was_added()
    {
        Assert.Equal("Added 1 item for TorBox.", RemoteText.AddedWords(0, 1, "TorBox"));
        Assert.Equal("Added 3 items for TorBox.", RemoteText.AddedWords(0, 3, "TorBox"));
        Assert.Equal("Added 1 download.", RemoteText.AddedWords(1, 0, "TorBox"));
        Assert.Equal("Added 2 downloads and 1 item for TorBox.", RemoteText.AddedWords(2, 1, "TorBox"));
        Assert.Equal(string.Empty, RemoteText.AddedWords(0, 0, "TorBox"));
    }

    [Fact]
    public void Something_added_again_is_never_called_added_and_the_Hosts_note_is_kept()
    {
        Assert.Equal("x: already on the list.", RemoteText.AlreadyListed("x", null));
        Assert.Equal("x: already on the list. The folder given wasn't used.", RemoteText.AlreadyListed("x", "The folder given wasn't used."));
        Assert.Equal("x: This is already on the list, so the folder given wasn't used.", RemoteText.AlreadyListed("x", "This is already on the list, so the folder given wasn't used."));
        Assert.DoesNotContain("Added", RemoteText.AlreadyListed("x", "The file choice wasn't used."), StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_lists_every_problem_after_what_was_added()
    {
        var problems = Enumerable.Range(1, 12).Select(index => $"link {index}: refused.").ToList();

        var words = RemoteText.AddReport(RemoteText.AddedWords(0, 2, "TorBox"), ["Added 1 of 2 downloads; 1 failed. Not found."], problems);
        var lines = words.Split(Environment.NewLine);

        Assert.Equal(14, lines.Length);
        Assert.Equal("Added 1 of 2 downloads; 1 failed. Not found.", lines[0]);
        Assert.Equal("Added 2 items for TorBox.", lines[1]);
        Assert.Equal("link 12: refused.", lines[^1]);
        Assert.Equal(["link 1: refused."], RemoteText.AddReport(string.Empty, [], ["link 1: refused."]).Split(Environment.NewLine));
    }

    [Fact]
    public void Adding_is_the_decision_so_every_add_carries_the_go_ahead()
    {
        var line = new AddLine("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567");

        var add = AddRequests.ForLine(line, @"D:\In", 3, startLater: true, linkPassword: null);
        Assert.True(add.ApproveUncached);
        Assert.Null(add.Selection);
        Assert.Equal(line.Input, add.Input);
        Assert.Equal(@"D:\In", add.Destination);
        Assert.Equal(3, add.QueueId);
        Assert.Equal("later", add.StartMode);
        Assert.Equal("startNow", AddRequests.ForLine(line, null, 0, startLater: false, linkPassword: null).StartMode);

        var container = AddRequests.ForContainer(Guid.NewGuid(), [1, 4], null, 2);
        Assert.True(container.ApproveUncached);
        Assert.Equal([1, 4], container.EntryIds);
        Assert.Equal(2, container.QueueId);
    }

    // ---- Files chosen before adding -------------------------------------------------------------------

    private const string TorrentPath = @"C:\Torrents\pack.torrent";

    private static RemoteFileDto FileOf(string id, string path, long size = 10, bool selectable = true)
        => new() { FileId = id, RelativePath = path, Size = size, Selectable = selectable };

    private static AddLine Ticked(string input, RemoteClassifyResult result) => new(input) { Result = result, IsSelected = true };

    [Theory]
    [InlineData(MagnetLink, true)]
    [InlineData(TorrentPath, true)]
    [InlineData(HosterLink, false)]
    [InlineData("https://example.com/file.zip", false)]
    public void OnlyMagnetsAndTorrentFilesAskForTheirFiles(string input, bool wants) => Assert.Equal(wants, AddChoices.WantsFiles(input));

    [Fact]
    public void FilesAreOfferedStraightAfterAddOnlyWhenThereIsARealChoice()
    {
        RemoteFileDto[] two = [FileOf("1", @"Pack\a.bin"), FileOf("2", @"Pack\b.bin")];
        var torrent = Line(TorrentPath, Kind("torrentFile", result => result.Files = [.. two]));
        Assert.Same(torrent.Result!.Files, AddChoices.FilesToChoose(torrent));
        Assert.NotNull(AddChoices.FilesToChoose(Line(MagnetLink, Kind("magnet", result => { result.Cached = true; result.Files = [.. two]; }))));

        // One that can be chosen beside one that can't is no choice at all.
        Assert.Null(AddChoices.FilesToChoose(Line(TorrentPath, Kind("torrentFile", result => result.Files = [FileOf("1", @"One\a.bin"), FileOf("2", @"One\b.bin", selectable: false)]))));
        Assert.Null(AddChoices.FilesToChoose(Line(TorrentPath, Kind("torrentFile", result => result.Files = [FileOf("1", @"One\a.bin"), FileOf("2", @"One\b.bin", size: 0)]))));
        Assert.Null(AddChoices.FilesToChoose(Line(MagnetLink, Kind("magnet", result => result.Cached = false))));
        Assert.Null(AddChoices.FilesToChoose(Line(HosterLink, Kind("hosterLink", result => result.Files = [.. two]))));
    }

    [Fact]
    public void TheBoxForChoosingLaterShowsOnlyForWhatCantBeChosenNowAndIsntKnownToBeCached()
    {
        Assert.True(AddChoices.ChooseLaterApplies(Ticked(MagnetLink, Kind("magnet", result => result.Cached = false))));
        // The cache couldn't be checked, so it may well not be.
        Assert.True(AddChoices.ChooseLaterApplies(Ticked(MagnetLink, Kind("magnet"))));
        // A .torrent file whose files couldn't be listed.
        Assert.True(AddChoices.ChooseLaterApplies(Ticked(TorrentPath, Kind("torrentFile"))));

        // Cached asks by itself, a listed .torrent file is chosen from at once, and a hoster link has nothing to choose.
        Assert.False(AddChoices.ChooseLaterApplies(Ticked(MagnetLink, Kind("magnet", result => result.Cached = true))));
        Assert.False(AddChoices.ChooseLaterApplies(Ticked(TorrentPath, Kind("torrentFile", result => result.Files = [FileOf("1", @"Pack\a.bin")]))));
        Assert.False(AddChoices.ChooseLaterApplies(Ticked(HosterLink, Kind("hosterLink"))));
        Assert.False(AddChoices.ChooseLaterApplies(Line(MagnetLink, Kind("magnet", result => result.Cached = false))));

        Assert.True(AddChoices.ChooseLaterApplies([Ticked(HosterLink, Kind("hosterLink")), Ticked(MagnetLink, Kind("magnet", result => result.Cached = false))]));
        Assert.False(AddChoices.ChooseLaterApplies([Ticked(HosterLink, Kind("hosterLink"))]));
    }

    [Fact]
    public void TheBoxSendsAskOnlyWhereItApplies()
    {
        var uncached = Ticked(MagnetLink, Kind("magnet", result => result.Cached = false));
        var cached = Ticked(MagnetLink, Kind("magnet", result => result.Cached = true));

        Assert.Equal("ask", AddRequests.ForLine(uncached, null, 0, false, null, AddChoices.Default(uncached, chooseLater: true)).Selection);
        Assert.Null(AddRequests.ForLine(uncached, null, 0, false, null, AddChoices.Default(uncached, chooseLater: false)).Selection);
        Assert.Null(AddRequests.ForLine(cached, null, 0, false, null, AddChoices.Default(cached, chooseLater: true)).Selection);
    }

    [Fact]
    public void AMagnetsChoiceGoesByIdAndATorrentFilesByPathAndSize()
    {
        RemoteFileDto[] files = [FileOf("0", @"Pack\sub\a.bin", 10), FileOf("1", @"Pack\b.bin", 20), FileOf("2", @"Pack\c.bin", 30)];
        var magnet = Ticked(MagnetLink, Kind("magnet", result => { result.Cached = true; result.Files = [.. files]; }));
        var torrent = Ticked(TorrentPath, Kind("torrentFile", result => result.Files = [.. files]));
        var some = new RemoteFileChoice(false, ["0", "2"]);

        var byId = AddRequests.ForLine(magnet, null, 0, false, null, AddFileChoice.From(magnet, some));
        Assert.Equal("chosen", byId.Selection);
        Assert.Equal(["0", "2"], byId.FileIds);
        Assert.Null(byId.FilePaths);

        var byPath = AddRequests.ForLine(torrent, null, 0, false, null, AddFileChoice.From(torrent, some));
        Assert.Equal("chosen", byPath.Selection);
        Assert.Null(byPath.FileIds);
        Assert.Equal([(@"Pack\sub\a.bin", 10L), (@"Pack\c.bin", 30L)], byPath.FilePaths!.Select(path => (path.Path, path.Size)));

        var all = AddRequests.ForLine(torrent, null, 0, false, null, AddFileChoice.From(torrent, new RemoteFileChoice(true, [])));
        Assert.Equal("all", all.Selection);
        Assert.Null(all.FileIds);
        Assert.Null(all.FilePaths);
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

    [Theory]
    [InlineData("ordinaryLink", nameof(ImportWay.Direct))]
    [InlineData("providerDirectLink", nameof(ImportWay.Direct))]
    [InlineData("hosterLink", nameof(ImportWay.Review))]
    [InlineData("undetermined", nameof(ImportWay.Review))]
    [InlineData("unsupported", nameof(ImportWay.Refused))]
    [InlineData("somethingNewerThanThisBuild", nameof(ImportWay.Refused))]
    public void AnImportedEntryGoesTheWayTheHostsAnswerSays(string kind, string expected)
        => Assert.Equal(expected, ImportRouting.WayFor("https://example.com/a", Kind(kind)).ToString());

    [Fact]
    public void AnImportedFolderLinkGoesToReviewOnlyWhenItNamesAnItem()
    {
        var named = Kind("folderLink", result => { result.Provider = "torbox"; result.ItemKind = "torrents"; result.ItemId = "7"; });
        Assert.Equal(ImportWay.Review, ImportRouting.WayFor("https://example.com/a", named));
        Assert.Equal(ImportWay.Refused, ImportRouting.WayFor("https://example.com/a", Kind("folderLink", result => result.ItemId = null)));
    }

    [Fact]
    public void AnImportedEntryNobodyAnsweredIsNotTreatedAsOrdinary()
        => Assert.Equal(ImportWay.Review, ImportRouting.WayFor(HosterLink, new RemoteClassifyResult { Error = RemoteText.ClassifyLost }));

    [Fact]
    public void WithNothingAskedAnImportedEntryIsAddedAsBefore()
        => Assert.Equal(ImportWay.Direct, ImportRouting.WayFor(HosterLink, null));

    [Fact]
    public async Task AnImportedListIsAskedAboutOnOneConnectionAndEachAddressOnce()
    {
        var classifier = new FakeClassifier { Answer = input => input == HosterLink ? Kind("hosterLink") : Kind("ordinaryLink") };

        var answers = await ImportRouting.ClassifyAllAsync(["https://example.com/a", HosterLink, "https://example.com/a"], classifier.Open);

        Assert.Equal(1, classifier.Opened);
        Assert.Equal(1, classifier.Disposed);
        Assert.Equal(["https://example.com/a", HosterLink], classifier.Asked);
        Assert.Equal("hosterLink", answers[HosterLink].Kind);
    }

    [Fact]
    public async Task ALostConnectionLeavesEveryRemainingEntryUnsureAndNoneDirect()
    {
        var classifier = new FakeClassifier { BreakAfterFirst = true };

        var answers = await ImportRouting.ClassifyAllAsync(["https://example.com/a", HosterLink], classifier.Open);

        Assert.All(answers, answer => Assert.Equal(ImportWay.Review, ImportRouting.WayFor(answer.Key, answer.Value)));
    }

    [Fact]
    public void TheImportSummarySaysHowManyWentEachWay()
    {
        Assert.Equal("Added 3 of 3 downloads.", RemoteText.ImportSummary(3, 3, 0, null, 0));
        Assert.Equal("Added 1 of 2 downloads; 1 failed. No.", RemoteText.ImportSummary(1, 2, 1, "No.", 0));

        var mixed = RemoteText.ImportSummary(2, 4, 0, null, 2);
        Assert.StartsWith("Added 2 of 4 downloads directly.", mixed);
        Assert.Contains("2 entries aren't added yet", mixed);
        Assert.Contains("don't carry over", mixed);
        Assert.Contains("1 entry isn't added yet", RemoteText.ImportSummary(0, 1, 0, null, 1));
    }

    [Fact]
    public void ADirectLinkNeverShowsItsTokenEvenWithNoAnswerFromTheHost()
    {
        var shown = RemoteText.AddressToShow(DirectLink, null);
        Assert.DoesNotContain("aaaa-bbbb-cccc", shown);
        Assert.Contains("cdn1.tb-cdn.io", shown);
    }

    [Fact]
    public void AKeyHeldAnywhereInALinkIsLeftOutByShowingOnlyTheHost()
    {
        const string link = "https://example.com/dl/aaaa-bbbb-cccc/file.zip?id=aaaa-bbbb-cccc";

        Assert.Equal("example.com", RemoteText.AddressToShow(link, Kind("providerDirectLink", result => result.DisplayHost = "example.com")));
        Assert.Equal("example.com", RemoteText.AddressToShow(link, Kind("providerDirectLink")));
    }

    [Fact]
    public void ADirectLinksLastPartIsNotOfferedAsTheServersName()
    {
        const string key = "aaaa-bbbb-cccc";
        var direct = Kind("providerDirectLink");

        Assert.Null(RemoteText.ServerNameToShow(key, $"https://example.com/dl/{key}", direct));
        Assert.Null(RemoteText.ServerNameToShow(key, $"https://example.com/dl/{key}?x=1", direct));
        Assert.Null(RemoteText.ServerNameToShow(key.ToUpperInvariant(), $"https://example.com/dl/{key}", direct));
        Assert.Null(RemoteText.ServerNameToShow("  ", $"https://example.com/dl/{key}", direct));

        // The engine replaces characters a file name can't hold, and the address may be percent-encoded.
        Assert.Null(RemoteText.ServerNameToShow("aaaa_bbbb", "https://example.com/dl/aaaa:bbbb", direct));
        Assert.Null(RemoteText.ServerNameToShow("aaaa bbbb", "https://example.com/dl/aaaa%20bbbb", direct));
        Assert.Null(RemoteText.ServerNameToShow("1", "https://cdn1.tb-cdn.io/dl/1?token=aaaa-bbbb-cccc", null));

        // A name the server really sent is different from the address, and is shown.
        Assert.Equal("movie.mkv", RemoteText.ServerNameToShow("movie.mkv", $"https://example.com/dl/{key}", direct));
    }

    [Fact]
    public void AnOrdinaryLinksServerNameIsShownEvenWhenItComesFromTheAddress()
        => Assert.Equal("a.zip", RemoteText.ServerNameToShow("a.zip", "https://example.com/a.zip", Kind("ordinaryLink")));

    [Fact]
    public void AnImportWhoseDialogCouldNotOpenSaysWhatWasLeftOutAndPromisesNoDialog()
    {
        var one = RemoteText.ImportReviewNotOpened(2, 3, 0, 1);
        Assert.Equal("Added 2 of 3 downloads directly. 1 entry wasn't added, because another dialog was open. Add it with Add Links.", one);

        var some = RemoteText.ImportReviewNotOpened(1, 5, 1, 3);
        Assert.Equal("Added 1 of 5 downloads directly; 1 failed. 3 entries weren't added, because another dialog was open. Add them with Add Links.", some);
    }

    [Fact]
    public void AnOrdinaryAddressIsShownAsItIs()
        => Assert.Equal("https://example.com/a.zip?id=1", RemoteText.AddressToShow("https://example.com/a.zip?id=1", Kind("ordinaryLink")));

    [Fact]
    public void AKeyThatCantBeReadIsGivenAsTheReasonInTheProvidersOwnName()
    {
        var notice = RemoteText.UnreadableKeyNotice([new RemoteAccountDto { Provider = "acme", ProviderName = "Acme Cloud", HasKey = true, KeyUnreadable = true }]);

        Assert.Contains("Acme Cloud API key can't be read", notice);
        Assert.Contains("aren't going through Acme Cloud", notice);
        Assert.Null(RemoteText.UnreadableKeyNotice([new RemoteAccountDto { Provider = "acme", ProviderName = "Acme Cloud" }]));
        Assert.Null(RemoteText.UnreadableKeyNotice([]));
    }

    [Fact]
    public void TheWordsAboutAProviderLinkUseItsOwnName()
    {
        Assert.Contains("Acme Cloud handles this site", RemoteText.ProviderOffer("Acme Cloud"));
        Assert.Contains("through Acme Cloud", RemoteText.RefreshRefused("Acme Cloud"));
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
