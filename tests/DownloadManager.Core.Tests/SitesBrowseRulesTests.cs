using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>SITES.3: paths, breadcrumbs, text and paging of the Sites browse view.</summary>
public sealed class SitesBrowseRulesTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/", "/")]
    [InlineData("home", "/home")]
    [InlineData("/home/", "/home")]
    [InlineData("//home///user//", "/home/user")]
    [InlineData("/backup\\2026", "/backup\\2026")]
    [InlineData("/a/./b", "/a/b")]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("/..", "/")]
    [InlineData("/a/../../b", "/b")]
    public void Normalize(string? input, string expected) => Assert.Equal(expected, SitesBrowseRules.Normalize(input));

    [Theory]
    [InlineData("/", null)]
    [InlineData("/a", "/")]
    [InlineData("/a/b", "/a")]
    [InlineData("/a/b/c/", "/a/b")]
    [InlineData(null, null)]
    public void Parent(string? input, string? expected) => Assert.Equal(expected, SitesBrowseRules.Parent(input));

    [Theory]
    [InlineData("/", "a", "/a")]
    [InlineData("/a", "b", "/a/b")]
    [InlineData("/a/", "b", "/a/b")]
    [InlineData("/a", "", "/a")]
    [InlineData("/a", "/b/", "/a/b")]
    [InlineData(null, "x y", "/x y")]
    public void Join(string? folder, string name, string expected) => Assert.Equal(expected, SitesBrowseRules.Join(folder, name));

    [Fact]
    public void CrumbsStartAtTheRootAndEachOpensItsOwnPath()
    {
        var crumbs = SitesBrowseRules.Crumbs("/srv/data/2026");
        Assert.Equal(["/", "srv", "data", "2026"], crumbs.Select(crumb => crumb.Name));
        Assert.Equal(["/", "/srv", "/srv/data", "/srv/data/2026"], crumbs.Select(crumb => crumb.Path));
        Assert.Single(SitesBrowseRules.Crumbs("/"));
        Assert.Single(SitesBrowseRules.Crumbs(null));
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/a/b", "b")]
    public void LeafName(string path, string expected) => Assert.Equal(expected, SitesBrowseRules.LeafName(path));

    [Fact]
    public void SizeIsEmptyForFoldersAndUnknownSizes()
    {
        Assert.Equal(string.Empty, SitesBrowseRules.SizeText(4096, isFolder: true));
        Assert.Equal(string.Empty, SitesBrowseRules.SizeText(-1, isFolder: false));
        Assert.Equal("0 B", SitesBrowseRules.SizeText(0, isFolder: false));
        Assert.Equal(UiFormat.Bytes(5_000_000), SitesBrowseRules.SizeText(5_000_000, isFolder: false));
    }

    [Fact]
    public void ModifiedIsEmptyWithoutADate()
    {
        Assert.Equal(string.Empty, SitesBrowseRules.ModifiedText(null));
        Assert.Equal(string.Empty, SitesBrowseRules.ModifiedText(DateTimeOffset.MinValue));
        var moment = new DateTimeOffset(2026, 11, 9, 19, 19, 28, TimeSpan.Zero);
        Assert.Equal(SitesBrowseRules.DateText(moment.LocalDateTime, DateTime.Now), SitesBrowseRules.ModifiedText(moment));
    }

    [Theory]
    [InlineData(0, 500, 1200, true)]
    [InlineData(500, 500, 1200, true)]
    [InlineData(1000, 200, 1200, false)]
    [InlineData(0, 0, 0, false)]
    [InlineData(0, 3, 3, false)]
    public void MoreWaitsWhileLessThanTheTotalHasCome(int offset, int loaded, int total, bool expected)
        => Assert.Equal(expected, SitesBrowseRules.HasMore(offset, loaded, total));

    [Fact]
    public void NextOffsetIsWhatIsAlreadyHere() => Assert.Equal(700, SitesBrowseRules.NextOffset(500, 200));

    [Fact]
    public void CountText()
    {
        Assert.Equal("Empty folder", SitesBrowseRules.CountText(0, 0));
        Assert.Equal("1 item", SitesBrowseRules.CountText(1, 1));
        Assert.Contains("3", SitesBrowseRules.CountText(3, 3));
        Assert.StartsWith("Showing", SitesBrowseRules.CountText(500, 1200));
    }

    [Fact]
    public void StarsCompareByNormalisedPathAndKeepEachOnce()
    {
        string[] stars = ["/b/", "/a", "/a", "c"];
        Assert.True(SitesBrowseRules.IsStarred(stars, "/b"));
        Assert.True(SitesBrowseRules.IsStarred(stars, "/c/"));
        Assert.False(SitesBrowseRules.IsStarred(stars, "/d"));
        Assert.False(SitesBrowseRules.IsStarred(null, "/a"));
        Assert.Equal(["/a", "/b", "/c"], SitesBrowseRules.OrderedStars(stars));
    }

    [Fact]
    public void HostKeyProblemsPointToOptions()
    {
        var changed = SitesBrowseRules.ProblemText("hostKeyChanged", null, "SHA256:new", ["SHA256:old"]);
        Assert.Contains("Options > Sites", changed);
        Assert.Contains("SHA256:new", changed);
        Assert.Contains("SHA256:old", changed);
        Assert.Contains("Options > Sites", SitesBrowseRules.ProblemText("hostKeyNotAccepted", null, null, null));
        Assert.Contains("Options > Sites", SitesBrowseRules.ProblemText("off", null, null, null));
        Assert.True(SitesBrowseRules.PointsToOptions("hostKeyChanged"));
        Assert.False(SitesBrowseRules.PointsToOptions(null));
        Assert.False(SitesBrowseRules.PointsToOptions("other"));
    }

    [Fact]
    public void AnOrdinaryFailureShowsTheHostsWords()
    {
        Assert.Equal("Permission denied.", SitesBrowseRules.ProblemText(null, " Permission denied. ", null, null));
        Assert.Equal("The folder could not be read.", SitesBrowseRules.ProblemText(null, null, null, null));
    }

    [Fact]
    public void AStarIsCalledByItsNameAndByItsParentWhenTheNameRepeats()
    {
        List<string> stars = ["/srv/pub/big", "/home/me/docs", "/srv/old/docs", "/"];
        Assert.Equal("big", SitesBrowseRules.StarLabel("/srv/pub/big", stars));
        Assert.Equal("docs (in me)", SitesBrowseRules.StarLabel("/home/me/docs", stars));
        Assert.Equal("docs (in old)", SitesBrowseRules.StarLabel("/srv/old/docs", stars));
        Assert.Equal("/", SitesBrowseRules.StarLabel("/", stars));
    }

    // ---- SITES.11 R2a ----

    [Theory]
    [InlineData("sftp", "SFTP")]
    [InlineData("ftp", "FTP")]
    [InlineData("webdav", "WebDAV")]
    [InlineData("drive", "Drive")]
    [InlineData("s3", "S3")]
    [InlineData("b2x", "B2X")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void KindChipReadsAsTheMockupDoes(string? kind, string expected)
        => Assert.Equal(expected, SitesBrowseRules.KindChipText(kind));

    [Fact]
    public void DatesHaveNoSecondsAndTheYearOnlyWhenNotThisYear()
    {
        var now = new DateTime(2026, 11, 17, 9, 0, 0);
        Assert.Equal("Nov 05 14:02", SitesBrowseRules.DateText(new DateTime(2026, 11, 5, 14, 2, 59), now));
        Assert.Equal("Dec 31 2025", SitesBrowseRules.DateText(new DateTime(2025, 12, 31, 23, 59, 0), now));
        Assert.Equal("Jan 01 00:00", SitesBrowseRules.DateText(new DateTime(2026, 1, 1, 0, 0, 0), now));
    }

    private static SitesBrowseRules.SortItem Item(string name, bool folder = false, long size = -1, int day = 1, string? here = null)
        => new(name, folder, size, new DateTimeOffset(2026, 11, day, 0, 0, 0, TimeSpan.Zero), here);

    [Fact]
    public void FoldersComeFirstWhateverTheColumnOrDirection()
    {
        var items = new List<SitesBrowseRules.SortItem> { Item("b.iso", size: 5), Item("zeta", folder: true), Item("a.iso", size: 9), Item("alpha", folder: true) };
        foreach (var key in new[] { SitesBrowseRules.SortName, SitesBrowseRules.SortSize, SitesBrowseRules.SortModified, SitesBrowseRules.SortHere })
        {
            foreach (var ascending in new[] { true, false })
            {
                var sorted = items.ToList();
                sorted.Sort((a, b) => SitesBrowseRules.CompareItems(a, b, key, ascending));
                Assert.True(sorted[0].IsFolder && sorted[1].IsFolder, $"{key} {ascending}");
            }
        }
    }

    [Fact]
    public void ColumnsSortAscendingAndDescendingWithNameAsTheTiebreak()
    {
        var items = new List<SitesBrowseRules.SortItem> { Item("c", size: 5, day: 3), Item("a", size: 9, day: 1), Item("b", size: 5, day: 2) };
        List<string> Order(string key, bool asc)
        {
            var sorted = items.ToList();
            sorted.Sort((x, y) => SitesBrowseRules.CompareItems(x, y, key, asc));
            return sorted.Select(item => item.Name).ToList();
        }

        Assert.Equal(["a", "b", "c"], Order(SitesBrowseRules.SortName, true));
        Assert.Equal(["c", "b", "a"], Order(SitesBrowseRules.SortName, false));
        Assert.Equal(["b", "c", "a"], Order(SitesBrowseRules.SortSize, true));
        Assert.Equal(["a", "b", "c"], Order(SitesBrowseRules.SortSize, false));
        Assert.Equal(["a", "b", "c"], Order(SitesBrowseRules.SortModified, true));
        Assert.Equal(["c", "b", "a"], Order(SitesBrowseRules.SortModified, false));
    }

    [Fact]
    public void ClickingAColumnTurnsItRoundOrStartsAscending()
    {
        Assert.Equal((SitesBrowseRules.SortName, false), SitesBrowseRules.NextSort(SitesBrowseRules.SortName, true, SitesBrowseRules.SortName));
        Assert.Equal((SitesBrowseRules.SortSize, true), SitesBrowseRules.NextSort(SitesBrowseRules.SortName, false, SitesBrowseRules.SortSize));
    }

    [Fact]
    public void HereSortsByHowMuchIsAlreadyHere()
    {
        var a = Item("a", here: SiteHereStates.NotHere);
        var b = Item("b", here: SiteHereStates.Same);
        Assert.True(SitesBrowseRules.CompareItems(a, b, SitesBrowseRules.SortHere, ascending: true) < 0);
        Assert.True(SitesBrowseRules.CompareItems(a, b, SitesBrowseRules.SortHere, ascending: false) > 0);
    }

    [Theory]
    [InlineData("ubuntu-26.04.iso", "UBUNTU", true)]
    [InlineData("ubuntu-26.04.iso", " 26.04 ", true)]
    [InlineData("ubuntu-26.04.iso", "debian", false)]
    [InlineData("ubuntu-26.04.iso", "", true)]
    [InlineData("ubuntu-26.04.iso", null, true)]
    [InlineData("ubuntu-26.04.iso", "   ", true)]
    public void FilterMatchesPartOfANameWithoutRegardToCase(string name, string? filter, bool expected)
        => Assert.Equal(expected, SitesBrowseRules.FilterMatches(name, filter));

    [Fact]
    public void HereTonePicksTheColour()
    {
        Assert.Equal("same", SitesBrowseRules.HereTone(SiteHereStates.Same));
        Assert.Equal("older", SitesBrowseRules.HereTone(SiteHereStates.OlderHere));
        Assert.Equal("other", SitesBrowseRules.HereTone(SiteHereStates.NotHere));
        Assert.Equal("other", SitesBrowseRules.HereTone(null));
    }

    [Fact]
    public void ServerFooterReadsAsInTheMockup()
    {
        var gb = UiFormat.Bytes(5_800_000_000);
        Assert.Equal($"1 of 6 selected · {gb} · 2 already on this PC",
            SitesBrowseRules.ServerFooter(6, 6, 1, 5_800_000_000, 2, linked: true, serverOnly: false, linkedFolder: @"D:\Downloads\ISOs"));
        Assert.Equal($"1 of 6 selected · {gb} · downloads go to D:\\Downloads\\ISOs",
            SitesBrowseRules.ServerFooter(6, 6, 1, 5_800_000_000, 2, linked: true, serverOnly: true, linkedFolder: @"D:\Downloads\ISOs"));
        Assert.Equal($"1 of 6 selected · {gb} · downloads go by your category rules",
            SitesBrowseRules.ServerFooter(6, 6, 1, 5_800_000_000, 0, linked: false, serverOnly: true, linkedFolder: null));
        Assert.Equal("6 items · 2 already on this PC",
            SitesBrowseRules.ServerFooter(6, 6, 0, 0, 2, linked: true, serverOnly: false, linkedFolder: @"D:\x"));
    }

    [Fact]
    public void ServerFooterLeavesOutWhatDoesNotApply()
    {
        // No linked folder: no "already on this PC". Only folders selected: no size.
        Assert.Equal("6 items", SitesBrowseRules.ServerFooter(6, 6, 0, 0, 0, linked: false, serverOnly: false, linkedFolder: null));
        Assert.Equal("2 of 6 selected", SitesBrowseRules.ServerFooter(6, 6, 2, 0, 0, linked: false, serverOnly: false, linkedFolder: null));
        Assert.Equal("Empty folder", SitesBrowseRules.ServerFooter(0, 0, 0, 0, 0, linked: true, serverOnly: false, linkedFolder: @"D:\x"));
        Assert.Equal("Showing 500 of 1,200 items", SitesBrowseRules.ServerFooter(500, 1200, 0, 0, 0, linked: false, serverOnly: false, linkedFolder: null)
            .Replace("\u00a0", " "));
    }

    [Fact]
    public void LocalFooterCountsFilesAndFolders()
    {
        Assert.Equal($"3 files and 1 folder · {UiFormat.Bytes(5_500_000_000)}", SitesBrowseRules.LocalFooter(3, 1, 5_500_000_000, 0, 0));
        Assert.Equal($"1 of 4 selected · {UiFormat.Bytes(3_400_000_000)}", SitesBrowseRules.LocalFooter(3, 1, 5_500_000_000, 1, 3_400_000_000));
        Assert.Equal("2 folders", SitesBrowseRules.LocalFooter(0, 2, 0, 0, 0));
        Assert.Equal("1 file", SitesBrowseRules.CountPhrase(1, 0));
        Assert.Equal("Empty folder", SitesBrowseRules.LocalFooter(0, 0, 0, 0, 0));
    }

    [Fact]
    public void LinkedLineNamesBothFolders()
    {
        Assert.Equal(@"Linked folders: D:\Downloads\ISOs follows /media/isos", SitesBrowseRules.LinkedLine(@"D:\Downloads\ISOs", "media/isos"));
        Assert.Null(SitesBrowseRules.LinkedLine(null, "/media"));
        Assert.Null(SitesBrowseRules.LinkedLine("  ", "/media"));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 60)]
    [InlineData(40, 60)]
    public void RetryBacksOffTenThirtySixtyThenEverySixty(int failures, int seconds)
        => Assert.Equal(seconds, SitesBrowseRules.RetryDelaySeconds(failures));

    [Fact]
    public void OnlyAServerThatDidNotAnswerIsRetriedByItself()
    {
        Assert.False(SitesBrowseRules.IsCantReach(null));
        Assert.False(SitesBrowseRules.IsCantReach(""));
        Assert.True(SitesBrowseRules.IsCantReach(SiteProblems.CantReach));
        Assert.False(SitesBrowseRules.IsCantReach("hostKeyChanged"));
        Assert.False(SitesBrowseRules.IsCantReach("signInNeeded"));
        Assert.False(SitesBrowseRules.IsCantReach("certificateNotTrusted"));
        Assert.False(SitesBrowseRules.IsCantReach("listingChanged"));
        Assert.Equal("Can't reach nas.home.lan", SitesBrowseRules.TroubleTitle(SiteProblems.CantReach, "nas.home.lan"));
        // No named problem is not "did not answer": the Host says cantReach only when the server did not answer.
        Assert.Equal("Can't connect to nas.home.lan", SitesBrowseRules.TroubleTitle(null, "nas.home.lan"));
        Assert.Equal("Can't connect to nas.home.lan", SitesBrowseRules.TroubleTitle("signInNeeded", "nas.home.lan"));
        Assert.Equal("Trying again at 14:32.", SitesBrowseRules.RetryAtText(new DateTime(2026, 11, 17, 14, 32, 50)));
        Assert.Equal("Trying again at 09:05.", SitesBrowseRules.RetryAtText(new DateTime(2026, 11, 17, 9, 5, 0)));
        Assert.Equal("The server did not answer.", SitesBrowseRules.TroubleWhy(SiteProblems.CantReach, null, null, null));
        Assert.Equal("It did not answer on port 22.", SitesBrowseRules.TroubleWhy(SiteProblems.CantReach, " It did not answer on port 22. ", null, null));
        Assert.Equal("The folder could not be read.", SitesBrowseRules.TroubleWhy(null, null, null, null));
    }

    [Fact]
    public void LastListedSaysHowLongAgo()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("Last listed just now", SitesBrowseRules.LastListedText(now.AddSeconds(-20), now));
        Assert.Equal("Last listed 2 min ago", SitesBrowseRules.LastListedText(now.AddMinutes(-2), now));
        Assert.Equal("Last listed 3 h ago", SitesBrowseRules.LastListedText(now.AddHours(-3), now));
        Assert.Equal("Last listed 2 d ago", SitesBrowseRules.LastListedText(now.AddDays(-2), now));
    }

    private static readonly SitesBrowseRules.PaneLine Up = new("..", true, -1, IsParent: true);

    [Fact]
    public void TheParentLineIsNeverSentRenamedOrDeleted()
    {
        var iso = new SitesBrowseRules.PaneLine("a.iso", false, 100);
        // Only ".." selected: nothing to download, upload, rename or delete.
        Assert.Empty(SitesBrowseRules.SelectedForAction([Up]));
        // ".." and one file: the file alone, so Rename (exactly one) renames the file, never "..".
        Assert.Equal([iso], SitesBrowseRules.SelectedForAction([Up, iso]));
        // A name of "." or ".." is not acted on even without the parent flag; a name selected twice goes once.
        Assert.Empty(SitesBrowseRules.SelectedForAction([new("..", true, -1), new(".", true, -1)]));
        Assert.Single(SitesBrowseRules.SelectedForAction([iso, iso]));
    }

    [Fact]
    public void FooterCountsLeaveOutTheParentLine()
    {
        SitesBrowseRules.PaneLine[] shown =
        [
            Up,
            new("backups", true, -1, SiteHereStates.NotHere),
            new("a.iso", false, 5_800, SiteHereStates.NotHere),
            new("b.iso", false, 2_100, SiteHereStates.Same),
            new("c.iso", false, 3_400, SiteHereStates.Same),
            new("unknown", false, -1),
        ];
        var counts = SitesBrowseRules.FooterCounts(shown, [Up, shown[1], shown[2]]);
        Assert.Equal(5, counts.Shown);
        Assert.Equal(2, counts.Selected);
        Assert.Equal(5_800, counts.SelectedBytes);
        Assert.Equal(2, counts.Same);
        Assert.Equal(4, counts.Files);
        Assert.Equal(1, counts.Folders);
        Assert.Equal(11_300, counts.Bytes);

        var onlyUp = SitesBrowseRules.FooterCounts([Up], [Up]);
        Assert.Equal(new SitesBrowseRules.FooterCount(0, 0, 0, 0, 0, 0, 0), onlyUp);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(4, 5, true)]
    [InlineData(5, 3, false)]
    [InlineData(2, 10, false)]
    [InlineData(1, 9, true)]
    public void AutomaticTriesStopAfterFiveOrTenMinutes(int tries, int minutes, bool allowed)
    {
        Assert.Equal(allowed, SitesBrowseRules.AutoRetryAllowed(tries, TimeSpan.FromMinutes(minutes)));
        Assert.Contains("Retry now", SitesBrowseRules.NotRetryingText);
    }

    [Fact]
    public void ConnectionKeyChangesOnlyWithTheConnection()
    {
        static SiteDto Site() => new() { Id = "s", Name = "nas", Kind = "sftp", Host = "nas", Port = 22, User = "me", HasPassword = false, RemoteFolder = "/media" };
        var key = SitesBrowseRules.ConnectionKey(Site());
        var renamed = Site();
        renamed.Name = "NAS";
        renamed.Stars = ["/media"];
        renamed.Reach = "unreachable";
        renamed.LastUsedAt = DateTimeOffset.Now;
        Assert.Equal(key, SitesBrowseRules.ConnectionKey(renamed));
        var password = Site();
        password.HasPassword = true;
        Assert.NotEqual(key, SitesBrowseRules.ConnectionKey(password));
        var port = Site();
        port.Port = 2222;
        Assert.NotEqual(key, SitesBrowseRules.ConnectionKey(port));
        var trusted = Site();
        trusted.HostKeys = [new SiteHostKeyDto { Type = "ssh-ed25519", Fingerprint = "SHA256:x" }];
        Assert.NotEqual(key, SitesBrowseRules.ConnectionKey(trusted));
    }

    [Fact]
    public void HereAnswersAreKeptForTwoMinutes()
    {
        var cache = new SiteHereCache();
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        cache.Put("s", "/media/", "isos", SiteHereStates.Same, now);
        Assert.True(cache.TryGet("s", "/media", "isos", now.AddSeconds(119), out var here));
        Assert.Equal(SiteHereStates.Same, here);
        Assert.False(cache.TryGet("s", "/media", "isos", now.AddMinutes(2), out _));
        Assert.False(cache.TryGet("other", "/media", "isos", now, out _));

        cache.Put("s", "/media", "isos", SiteHereStates.Same, now);
        cache.Put("s", "/other", "isos", SiteHereStates.Same, now);
        cache.Forget("s", "/media");
        Assert.False(cache.TryGet("s", "/media", "isos", now, out _));
        Assert.True(cache.TryGet("s", "/other", "isos", now, out _));
    }
}
