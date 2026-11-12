using System.Text.Json;
using System.Text.Json.Nodes;
using DownloadManager.Core;

namespace DownloadManager.Torrents.Tests;

/// <summary>T3's rules in Core: file priorities (and files saved before them), seeding goals and encryption words.</summary>
public sealed class T3RulesTests
{
    [Fact]
    public void TheFileListArrivingAgainKeepsEachFilesPriority()
    {
        // The file list comes again whenever the torrent starts (walk 2026-11-12: High chosen before Start came back Normal).
        var metadata = new JsonObject
        {
            ["files"] = new JsonArray(
                new JsonObject { ["index"] = 0, ["path"] = @"pack\film.mkv", ["size"] = 10L },
                new JsonObject { ["index"] = 1, ["path"] = @"pack\extra.mkv", ["size"] = 5L },
                new JsonObject { ["index"] = 2, ["path"] = @"pack\readme.txt", ["size"] = 1L }),
        };
        TorrentFile[] previous =
        [
            new(0, "pack/film.mkv", 10, true) { Level = TorrentFilePriority.High },
            new(1, "pack/extra.mkv", 5, false),
            new(2, "pack/readme.txt", 1, true) { Level = TorrentFilePriority.Low },
        ];
        var merged = TorrentDownloadEngine.MergeFiles(metadata, previous);
        Assert.Equal(TorrentFilePriority.High, merged[0].Level);
        Assert.False(merged[1].Wanted);
        Assert.Null(merged[1].Level);
        Assert.Equal(TorrentFilePriority.Low, merged[2].Level);
        Assert.Equal("pack/film.mkv", merged[0].Path);
        Assert.All(TorrentDownloadEngine.MergeFiles(metadata, null), file => Assert.Null(file.Level));
    }

    [Fact]
    public void FilesSavedBeforePrioritiesReadAsNormalOrSkip()
    {
        var old = JsonSerializer.Deserialize<List<TorrentFile>>("""[{"Index":0,"Path":"a.bin","Size":10,"Wanted":true},{"Index":1,"Path":"b.bin","Size":10,"Wanted":false}]""")!;
        Assert.Equal([TorrentFilePriority.Normal, TorrentFilePriority.Skip], old.Select(f => f.Priority));
        Assert.Equal([4, 0], TorrentLayout.PrioritiesOf(old));
    }

    [Fact]
    public void APriorityIsKeptAndWantedFollowsIt()
    {
        var file = new TorrentFile(0, "a.bin", 10, true);
        var high = file.WithPriority(TorrentFilePriority.High);
        Assert.True(high.Wanted);
        Assert.Equal(TorrentFilePriority.High, high.Priority);
        var skipped = high.WithPriority(TorrentFilePriority.Skip);
        Assert.False(skipped.Wanted);
        Assert.Equal(TorrentFilePriority.Skip, skipped.Priority);

        var again = JsonSerializer.Deserialize<TorrentFile>(JsonSerializer.Serialize(file.WithPriority(TorrentFilePriority.Low)))!;
        Assert.Equal(TorrentFilePriority.Low, again.Priority);
        Assert.Equal([7, 1, 4, 0], TorrentLayout.PrioritiesOf([
            file.WithPriority(TorrentFilePriority.High), file with { Index = 1, Level = TorrentFilePriority.Low },
            file with { Index = 2 }, file with { Index = 3, Wanted = false },
        ]));
    }

    [Theory]
    [InlineData("skip", TorrentFilePriority.Skip)]
    [InlineData("Low", TorrentFilePriority.Low)]
    [InlineData(" normal ", TorrentFilePriority.Normal)]
    [InlineData("HIGH", TorrentFilePriority.High)]
    public void PriorityWordsAreRead(string text, TorrentFilePriority expected)
    {
        Assert.Equal(expected, TorrentFilePriorities.Parse(text));
        Assert.Equal(text.Trim().ToLowerInvariant(), TorrentFilePriorities.Text(expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("urgent")]
    public void AnUnknownPriorityWordIsNone(string? text) => Assert.Null(TorrentFilePriorities.Parse(text));

    [Fact]
    public void ATorrentsSeedingGoalOverridesOptions()
    {
        var global = new TorrentSeedPolicy(2, TimeSpan.FromHours(10));
        var info = new TorrentJobInfo();
        Assert.True(TorrentSeedGoals.Seeds(info, seedAfterDownload: true));
        Assert.False(TorrentSeedGoals.Seeds(info, seedAfterDownload: false));
        Assert.Same(global, TorrentSeedGoals.PolicyFor(info, global));

        info.SeedGoal = TorrentSeedGoals.Never;
        Assert.False(TorrentSeedGoals.Seeds(info, seedAfterDownload: true));

        info.SeedGoal = TorrentSeedGoals.Ratio;
        info.SeedGoalValue = 1.5;
        Assert.True(TorrentSeedGoals.Seeds(info, seedAfterDownload: false));
        Assert.Equal(new TorrentSeedPolicy(1.5, TimeSpan.Zero), TorrentSeedGoals.PolicyFor(info, global));

        info.SeedGoal = TorrentSeedGoals.Hours;
        info.SeedGoalValue = 3;
        Assert.Equal(new TorrentSeedPolicy(0, TimeSpan.FromHours(3)), TorrentSeedGoals.PolicyFor(info, global));
    }

    [Theory]
    [InlineData("prefer", "enabled")]
    [InlineData("require", "forced")]
    [InlineData("off", "disabled")]
    public void EncryptionWordsMapToTheWorkersWords(string mode, string worker)
    {
        Assert.True(TorrentEncryptionModes.IsKnown(mode));
        Assert.Equal(worker, TorrentEncryptionModes.WorkerWord(mode));
    }

    [Fact]
    public void TheProxyNeverShowsItsPasswordInText()
    {
        var proxy = new TorrentProxy("socks5", "127.0.0.1", 1080, "user", "secret");
        Assert.DoesNotContain("secret", proxy.ToString());
        Assert.DoesNotContain("user", proxy.ToString());
    }
}
