using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>UI.R29 direction B: the progress window's tile figures.</summary>
public sealed class ProgressOverviewTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Tiles_show_the_number_with_its_unit_apart()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try { TileFigures(); }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    private static void TileFigures()
    {
        Assert.Equal(("2.6", "/ 4.2 GB"), ProgressOverviewText.Of((long)(2.6 * 1024 * 1024 * 1024), (long)(4.2 * 1024 * 1024 * 1024)));
        Assert.Equal(("6.2", "MB"), ProgressOverviewText.Split((long)(6.2 * 1024 * 1024)));
        Assert.Equal("4m 23s", ProgressOverviewText.Duration(TimeSpan.FromSeconds(263)));
        Assert.Equal("1h 5m", ProgressOverviewText.Duration(TimeSpan.FromMinutes(65)));
        Assert.Equal("—", ProgressOverviewText.Duration(null));
        Assert.Equal("under 1s", ProgressOverviewText.Duration(TimeSpan.Zero));
        Assert.Equal("2d 3h", ProgressOverviewText.Duration(TimeSpan.FromHours(51)));
        Assert.Null(ProgressOverviewText.Took(Start, null));
        Assert.Equal("HTTP 403", ProgressOverviewText.ErrorName(403, "http"));
        Assert.Equal("Disk full", ProgressOverviewText.ErrorName(null, "diskFull"));
    }

    /// <summary>UI.R31: one size and one moment format everywhere: the list's.</summary>
    [Fact]
    public void Sizes_and_moments_read_as_the_list_writes_them()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("0 B", UiFormat.Bytes(0));
            Assert.Equal("900 B", UiFormat.Bytes(900));
            Assert.Equal("2.9 MB", UiFormat.Bytes(3_000_000));
            Assert.Equal("858.3 MB", UiFormat.Bytes(900_000_000));
            Assert.Equal("287.6 GB", UiFormat.Bytes((long)(287.56 * 1024 * 1024 * 1024)));
            Assert.Equal("4.0 KB", UiFormat.Bytes(4096));
            var local = new DateTimeOffset(new DateTime(2026, 11, 9, 19, 19, 28, DateTimeKind.Local));
            Assert.Equal("Nov 09 19:19:28 2026", UiFormat.Moment(local));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    /// <summary>T1: Progress Details for a torrent: its tiles and its Files tab's rows.</summary>
    [Fact]
    public void A_torrent_reads_as_received_of_wanted_with_peers_and_file_progress()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(("2.6", "of 4.2 GB"), ProgressOverviewText.OfWords((long)(2.6 * 1024 * 1024 * 1024), (long)(4.2 * 1024 * 1024 * 1024)));
            Assert.Equal(("41", "(12 seeds)"), ProgressOverviewText.Peers(41, 12));
            Assert.Equal(("1", "(1 seed)"), ProgressOverviewText.Peers(1, 1));
            Assert.Equal(("0", "(0 seeds)"), ProgressOverviewText.Peers(-1, 0));

            var running = new DownloadManager.Contracts.TorrentFileDto { Index = 0, Path = "Show/Episode 1.mkv", Size = 1000, Wanted = true, Done = 710 };
            Assert.Equal("71%", ProgressOverviewText.FileProgress(running, torrentComplete: false));
            Assert.Equal("Show\\Episode 1.mkv, 1000 B, 71%", ProgressOverviewText.FileRowName(running, torrentComplete: false));

            // Not running, so no bytes known: unknown while it is unfinished, done once the torrent is.
            var stopped = new DownloadManager.Contracts.TorrentFileDto { Index = 1, Path = "readme.txt", Size = 10, Wanted = true };
            Assert.Equal(string.Empty, ProgressOverviewText.FileProgress(stopped, torrentComplete: false));
            Assert.Equal("readme.txt, 10 B", ProgressOverviewText.FileRowName(stopped, torrentComplete: false));
            Assert.Equal("Done", ProgressOverviewText.FileProgress(stopped, torrentComplete: true));

            var skipped = new DownloadManager.Contracts.TorrentFileDto { Index = 2, Path = "sample.mkv", Size = 10, Wanted = false };
            Assert.Equal("Skipped", ProgressOverviewText.FileProgress(skipped, torrentComplete: true));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }
}
