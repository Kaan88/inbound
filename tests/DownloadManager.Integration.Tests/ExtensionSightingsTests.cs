using DownloadManager.Contracts;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

/// <summary>UI.R41: extensions-seen.json, written at most once per browser per day, and read back after a restart.</summary>
public sealed class ExtensionSightingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dm-seen-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private ExtensionSightings Open() => new(_root, now: () => _now);

    private string FilePath => Path.Combine(_root, ExtensionSightings.FileName);

    [Fact]
    public void Nothing_is_seen_until_a_browser_comes_and_then_it_is_kept_across_a_restart()
    {
        Assert.Empty(Open().Snapshot());
        Assert.False(File.Exists(FilePath));

        Assert.True(Open().Note(ExtensionBrowsers.Chrome));
        var again = Open().Snapshot();
        Assert.Equal(_now, again[ExtensionBrowsers.Chrome]);
    }

    [Fact]
    public void The_same_browser_on_the_same_day_writes_nothing_and_a_new_day_does()
    {
        var seen = Open();
        Assert.True(seen.Note(ExtensionBrowsers.Firefox));
        var written = File.GetLastWriteTimeUtc(FilePath);
        File.SetLastWriteTimeUtc(FilePath, written.AddHours(-1));

        _now = _now.AddHours(10);
        Assert.False(seen.Note(ExtensionBrowsers.Firefox));
        Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(FilePath));
        Assert.Equal(_now.AddHours(-10), seen.Snapshot()[ExtensionBrowsers.Firefox]);

        _now = _now.AddDays(1);
        Assert.False(seen.Note(ExtensionBrowsers.Firefox));
        Assert.Equal(_now, Open().Snapshot()[ExtensionBrowsers.Firefox]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Chrome")]
    [InlineData("opera")]
    public void A_name_that_isnt_one_of_the_three_is_ignored(string? browser)
    {
        Assert.False(Open().Note(browser));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void A_damaged_file_counts_as_none_seen_and_is_replaced_by_the_next_sighting()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FilePath, "{ not json");
        var seen = Open();
        Assert.Empty(seen.Snapshot());
        Assert.True(seen.Note(ExtensionBrowsers.Edge));
        Assert.Equal([ExtensionBrowsers.Edge], Open().Snapshot().Keys);
    }

    [Fact]
    public void Names_in_the_file_that_arent_browsers_are_left_out()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FilePath, """{ "chrome": "2026-11-01T00:00:00+00:00", "other": "2026-11-01T00:00:00+00:00" }""");
        Assert.Equal([ExtensionBrowsers.Chrome], Open().Snapshot().Keys);
    }
}
