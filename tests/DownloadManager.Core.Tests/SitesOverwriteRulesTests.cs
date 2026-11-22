using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;
using DownloadManager.Sites;

namespace DownloadManager.Core.Tests;

/// <summary>SITES.11 R5: the overwrite question, one file at a time (overwrite-mockup.png).</summary>
public sealed class SitesOverwriteRulesTests
{
    private static readonly DateTimeOffset Older = new(2025, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Newer = new(2025, 11, 9, 19, 19, 0, TimeSpan.Zero);

    private static SitesCommandRules.ConflictPair Pair(DateTimeOffset? source, DateTimeOffset? target, string name = "notes.txt", bool folder = false) =>
        new(name, folder, new SitesCommandRules.ConflictSide(2048, source), new SitesCommandRules.ConflictSide(2048, target));

    [Fact]
    public void TheTitleSaysWhichCopyIsNewerAndForWhichDirection()
    {
        Assert.Equal("notes.txt is newer on the server", SitesCommandRules.OverwriteTitle(Pair(Older, Newer), upload: true));
        Assert.Equal("notes.txt is older on the server", SitesCommandRules.OverwriteTitle(Pair(Newer, Older), upload: true));
        Assert.Equal("notes.txt is already on the server", SitesCommandRules.OverwriteTitle(Pair(Older, Older.AddSeconds(1)), upload: true));
        Assert.Equal("notes.txt is already on this PC", SitesCommandRules.OverwriteTitle(Pair(null, Newer), upload: false));
        Assert.Equal("notes.txt is newer on this PC", SitesCommandRules.OverwriteTitle(Pair(Older, Newer), upload: false));
        Assert.Equal("docs is already on the server", SitesCommandRules.OverwriteTitle(Pair(Older, Newer, "docs", folder: true), upload: true));
    }

    [Fact]
    public void TheRowsShowSizeDateAndNewerOnTheNewerSideOnly()
    {
        var rows = SitesCommandRules.OverwriteRows(Pair(Older, Newer), upload: true);
        Assert.Equal("On this PC", rows[0].Label);
        Assert.Equal("On the server", rows[1].Label);
        Assert.StartsWith("2.0 KB · ", rows[0].Text);
        Assert.DoesNotContain("(newer)", rows[0].Text);
        Assert.EndsWith(" (newer)", rows[1].Text);

        var down = SitesCommandRules.OverwriteRows(Pair(Newer, Older), upload: false);
        Assert.Equal("On the server", down[0].Label);
        Assert.EndsWith(" (newer)", down[0].Text);
        Assert.Equal("On this PC", down[1].Label);

        Assert.Equal("not known", SitesCommandRules.OverwriteRows(new SitesCommandRules.ConflictPair("a", false, null, null), upload: true)[0].Text);
        Assert.Empty(SitesCommandRules.OverwriteRows(Pair(Older, Newer, "docs", folder: true), upload: true));
    }

    [Fact]
    public void SkipIsRecommendedUnlessTheTargetIsOlder()
    {
        var newer = SitesCommandRules.OverwriteChoices(Pair(Older, Newer), upload: true, "notes (2).txt", preciseTimes: true);
        Assert.Equal(["skip", "replace", "keepBoth"], newer.Select(c => c.Value));
        Assert.Equal("Skip, keep the server copy", newer[0].Label);
        Assert.Equal("Replace the server copy with the older file", newer[1].Label);
        Assert.Equal("Keep both, upload as notes (2).txt", newer[2].Label);
        Assert.Equal([true, false, false], newer.Select(c => c.Recommended));

        var older = SitesCommandRules.OverwriteChoices(Pair(Newer, Older), upload: true, "notes (2).txt", preciseTimes: true);
        Assert.Equal("Replace the server copy with the newer file", older[1].Label);
        Assert.Equal([false, true, false], older.Select(c => c.Recommended));

        var same = SitesCommandRules.OverwriteChoices(Pair(Older, Older), upload: false, "notes (2).txt", preciseTimes: true);
        Assert.Equal("Skip, keep the copy on this PC", same[0].Label);
        Assert.Equal("Replace the copy on this PC", same[1].Label);
        Assert.Equal("Keep both, download as notes (2).txt", same[2].Label);
        Assert.True(same[0].Recommended);

        Assert.True(SitesCommandRules.OverwriteChoices(Pair(null, null), upload: true, "x", preciseTimes: true)[0].Recommended);
        Assert.True(SitesCommandRules.OverwriteChoices(Pair(Newer, Older, "docs", folder: true), upload: true, "x", preciseTimes: true)[0].Recommended);
    }

    [Fact]
    public void ReplaceIsRecommendedOnlyForATargetOlderByMoreThanADayOnPreciseTimes()
    {
        static bool[] Marks(DateTimeOffset source, DateTimeOffset target, bool precise) =>
            [.. SitesCommandRules.OverwriteChoices(Pair(source, target), upload: true, null, precise).Select(c => c.Recommended)];

        // FTP times (minutes, local time, no year) never recommend Replace, however old the target looks.
        Assert.Equal([true, false, false], Marks(Newer, Older, precise: false));
        // A day or less apart: Skip, even on precise times (a clock or a zone off by hours).
        Assert.Equal([true, false, false], Marks(Older.AddDays(1), Older, precise: true));
        Assert.Equal([true, false, false], Marks(Older.AddHours(5), Older, precise: true));
        Assert.Equal([false, true, false], Marks(Older.AddDays(1).AddMinutes(1), Older, precise: true));
        // The title still says which is older.
        Assert.Equal("notes.txt is older on the server", SitesCommandRules.OverwriteTitle(Pair(Older.AddHours(5), Older), upload: true));

        Assert.False(SitesCommandRules.TimesArePrecise("ftp"));
        Assert.False(SitesCommandRules.TimesArePrecise("FTP"));
        Assert.False(SitesCommandRules.TimesArePrecise(""));
        Assert.False(SitesCommandRules.TimesArePrecise(null));
        foreach (var kind in new[] { "sftp", "webdav", "s3", "drive", "onedrive", "dropbox", "b2" })
            Assert.True(SitesCommandRules.TimesArePrecise(kind), kind);
    }

    [Fact]
    public void TwoSecondsApartIsTheSameMomentAndAnyMoreIsNot()
    {
        Assert.Equal("notes.txt is already on the server", SitesCommandRules.OverwriteTitle(Pair(Older, Older.AddSeconds(2)), upload: true));
        Assert.Equal("notes.txt is already on the server", SitesCommandRules.OverwriteTitle(Pair(Older.AddSeconds(2), Older), upload: true));
        Assert.Equal("notes.txt is newer on the server", SitesCommandRules.OverwriteTitle(Pair(Older, Older.AddSeconds(2).AddMilliseconds(1)), upload: true));
        Assert.Equal("notes.txt is older on the server", SitesCommandRules.OverwriteTitle(Pair(Older.AddSeconds(2).AddMilliseconds(1), Older), upload: true));
    }

    [Fact]
    public void KeepBothPromisesANameOnlyWhenTheHostIsSureToGiveIt()
    {
        Assert.Equal("notes (2).txt", SitesCommandRules.ShownKeepBothName("notes.txt", ["notes.txt"], targetComplete: true));
        Assert.Equal("notes (3).txt", SitesCommandRules.ShownKeepBothName("notes.txt", ["notes.txt", "notes (2).txt"], targetComplete: true));
        // Partly listed: "notes (2).txt" may be on a page not loaded.
        Assert.Null(SitesCommandRules.ShownKeepBothName("notes.txt", ["notes.txt"], targetComplete: false));
        // The name is there in another case only, or a candidate is: the Host's look may count it either way.
        Assert.Null(SitesCommandRules.ShownKeepBothName("notes.txt", ["NOTES.TXT"], targetComplete: true));
        Assert.Null(SitesCommandRules.ShownKeepBothName("notes.txt", ["notes.txt", "Notes (2).txt"], targetComplete: true));
        Assert.Equal("notes (3).txt", SitesCommandRules.ShownKeepBothName("notes.txt", ["notes.txt", "notes (2).txt", "NOTES (4).TXT"], targetComplete: true));

        var choices = SitesCommandRules.OverwriteChoices(Pair(Older, Newer), upload: true, null, preciseTimes: true);
        Assert.Equal("Keep both, upload under a numbered name", choices[2].Label);
        Assert.Equal("Keep both, download under a numbered name", SitesCommandRules.OverwriteChoices(Pair(Older, Newer), upload: false, "", preciseTimes: true)[2].Label);
    }

    [Fact]
    public void UnlistedNamesFollowTheTickOnlyWhenTheTargetIsPartlyListed()
    {
        Assert.Equal("replace", SitesCommandRules.ChoiceForUnlisted(targetComplete: false, "replace"));
        Assert.Equal("keepBoth", SitesCommandRules.ChoiceForUnlisted(targetComplete: false, "keepBoth"));
        Assert.Equal("skip", SitesCommandRules.ChoiceForUnlisted(targetComplete: false, null));
        Assert.Equal("skip", SitesCommandRules.ChoiceForUnlisted(targetComplete: true, "replace"));
        Assert.Equal("skip", SitesCommandRules.ChoiceForUnlisted(targetComplete: false, "overwriteEverything"));
    }

    [Fact]
    public void TheKeepBothNameIsTheHostsNameForTheFirstFreeNumber()
    {
        Assert.Equal("notes (2).txt", SitesCommandRules.KeepBothName("notes.txt", ["notes.txt"], ignoreCase: false));
        Assert.Equal("notes (3).txt", SitesCommandRules.KeepBothName("notes.txt", ["notes.txt", "notes (2).txt"], ignoreCase: false));
        Assert.Equal("notes (3).txt", SitesCommandRules.KeepBothName("notes.txt", ["NOTES (2).TXT"], ignoreCase: true));
        Assert.Equal("notes (2).txt", SitesCommandRules.KeepBothName("notes.txt", ["NOTES (2).TXT"], ignoreCase: false));
        Assert.Equal("archive.tar (2).gz", SitesCommandRules.KeepBothName("archive.tar.gz", [], ignoreCase: false));
        Assert.Equal("README (2)", SitesCommandRules.KeepBothName("README", [], ignoreCase: false));
        Assert.Equal(".env (2)", SitesCommandRules.KeepBothName(".env", [], ignoreCase: false));
        // The same rule as the Host's: its first candidate is the one shown.
        foreach (var name in new[] { "a.iso", "a.b.c", "x", ".h", "dir.d" })
            Assert.Equal(SiteTransferPaths.Numbered(name, 2), SitesCommandRules.KeepBothName(name, [], ignoreCase: false));
    }

    [Fact]
    public void PairsAreTheNamesThatAreThereMatchedWithoutCaseForUploads()
    {
        var picked = new List<SiteTransferNameDto>
        {
            new() { Name = "a.iso" }, new() { Name = "B.txt" }, new() { Name = "docs", IsFolder = true }, new() { Name = "new.bin" },
        };
        var sources = new Dictionary<string, SitesCommandRules.ConflictSide> { ["a.iso"] = new(10, Older) };
        List<KeyValuePair<string, SitesCommandRules.ConflictSide>> there =
        [
            new("A.ISO", new(11, Newer)), new("b.txt", new(5, null)), new("docs", new(-1, null)),
        ];
        var pairs = SitesCommandRules.ConflictPairs(picked, sources, there, ignoreCase: true);
        Assert.Equal(["a.iso", "B.txt", "docs"], pairs.Select(p => p.Name));
        Assert.Equal(11, pairs[0].Target!.Value.Size);
        Assert.Equal(10, pairs[0].Source!.Value.Size);
        Assert.Null(pairs[1].Source);
        Assert.True(pairs[2].IsFolder);
        Assert.Equal(["docs"], SitesCommandRules.ConflictPairs(picked, sources, there, ignoreCase: false).Select(p => p.Name));
    }

    [Fact]
    public void ChoicesGoOutAsOneBatchPerChoiceAndSkippedFilesStayHome()
    {
        var picked = new List<SiteTransferNameDto>
        {
            new() { Name = "a" }, new() { Name = "b" }, new() { Name = "c" }, new() { Name = "docs", IsFolder = true }, new() { Name = "new" }, new() { Name = "d" },
        };
        var choices = new Dictionary<string, string> { ["a"] = "skip", ["b"] = "replace", ["c"] = "keepBoth", ["docs"] = "skip", ["d"] = "replace" };
        var batches = SitesCommandRules.ConflictBatches(picked, choices);
        Assert.Equal(["skip", "replace", "keepBoth"], batches.Select(b => b.Conflict));
        // Skip: the folder (its inside may be new) and the name nothing was there for; "a" stays home.
        Assert.Equal(["docs", "new"], batches[0].Names.Select(n => n.Name));
        Assert.Equal(["b", "d"], batches[1].Names.Select(n => n.Name));
        Assert.Equal(["c"], batches[2].Names.Select(n => n.Name));

        // Every file skipped and nothing else: nothing to send.
        Assert.Empty(SitesCommandRules.ConflictBatches([new SiteTransferNameDto { Name = "a" }], new Dictionary<string, string> { ["a"] = "skip" }));
        // No choices of their own: all go with the other choice (the "may be there" question).
        var all = SitesCommandRules.ConflictBatches(picked, new Dictionary<string, string>(), "keepBoth");
        Assert.Single(all);
        Assert.Equal("keepBoth", all[0].Conflict);
        Assert.Equal(6, all[0].Names.Count);

        // A non-Skip other choice: names with no choice of their own go with it, beside the ones that have one.
        var mixed = SitesCommandRules.ConflictBatches(picked, new Dictionary<string, string> { ["a"] = "skip", ["b"] = "keepBoth" }, "replace");
        Assert.Equal(["replace", "keepBoth"], mixed.Select(b => b.Conflict));
        Assert.Equal(["c", "docs", "new", "d"], mixed[0].Names.Select(n => n.Name));
        Assert.Equal(["b"], mixed[1].Names.Select(n => n.Name));

        // A choice the Host would not know goes as Skip, never as an overwrite.
        var odd = SitesCommandRules.ConflictBatches(picked, new Dictionary<string, string> { ["a"] = "overwrite" }, "whatever");
        Assert.Single(odd);
        Assert.Equal("skip", odd[0].Conflict);
        Assert.Equal(6, odd[0].Names.Count);
    }

    [Fact]
    public void TheApplyToOthersLabelCountsTheRest()
    {
        Assert.Null(SitesCommandRules.ApplyToOthersLabel(0, false));
        Assert.Equal("Do this for the other file", SitesCommandRules.ApplyToOthersLabel(1, false));
        Assert.Equal("Do this for the other 3 files", SitesCommandRules.ApplyToOthersLabel(3, false));
        Assert.Equal("Do this for the other 2 items", SitesCommandRules.ApplyToOthersLabel(2, true));
    }
}
