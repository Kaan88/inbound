using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>UI.B20: Progress Details and the main window sample a download's speed by the same rule.</summary>
public sealed class SpeedSampleTests
{
    private static readonly DateTimeOffset Updated = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_last_rate_is_held_between_updates_until_it_goes_stale()
    {
        // Updates arrive irregularly; every one-second sample until the next one carries the same figure.
        for (var second = 0; second <= 10; second++)
            Assert.Equal(800_000, SpeedSample.JobRate("downloading", 800_000, Updated, Updated.AddSeconds(second)));
        Assert.Equal(0, SpeedSample.JobRate("downloading", 800_000, Updated, Updated.AddSeconds(11)));
    }

    [Theory]
    [InlineData("connecting", 5_000d, 5_000d)]
    [InlineData("retryWaiting", null, 0d)]
    [InlineData("paused", 5_000d, 0d)]
    [InlineData("completed", 5_000d, 0d)]
    [InlineData("downloading", -1d, 0d)]
    [InlineData("downloading", double.NaN, 0d)]
    public void Quiet_or_stopped_downloads_count_as_zero_not_a_gap(string state, double? rate, double expected) =>
        Assert.Equal(expected, SpeedSample.JobRate(state, rate, Updated, Updated));

    /// <summary>Review 4:N4: a server whose clock is 30 s behind this PC; its rates count, as the web UI's ServerClock does.</summary>
    [Fact]
    public void A_server_clock_behind_this_pc_still_counts_its_rates()
    {
        var here = Updated.AddSeconds(30);
        var clock = new ServerClock();
        Assert.Equal(0, SpeedSample.JobRate("downloading", 800_000, Updated, clock.ServerNow(here)));

        // A stale event first (a removed job), then the fresh one: the smallest gap wins.
        clock.Observe(Updated.AddMinutes(-5), here);
        clock.Observe(Updated, here);
        Assert.Equal(TimeSpan.FromSeconds(30), clock.Offset);
        Assert.Equal(800_000, SpeedSample.JobRate("downloading", 800_000, Updated, clock.ServerNow(here.AddSeconds(3))));
        Assert.Equal(0, SpeedSample.JobRate("downloading", 800_000, Updated, clock.ServerNow(here.AddSeconds(11))));

        // A server ahead of this PC.
        var ahead = new ServerClock();
        ahead.Observe(Updated.AddSeconds(20), Updated);
        Assert.Equal(800_000, SpeedSample.UploadRate("downloading", false, 800_000, Updated.AddSeconds(20), ahead.ServerNow(Updated.AddSeconds(5))));

        // Another Host: the old offset is forgotten and the next event sets it afresh, even a larger one.
        clock.Reset();
        Assert.Equal(TimeSpan.Zero, clock.Offset);
        clock.Observe(Updated, Updated.AddMinutes(2));
        Assert.Equal(TimeSpan.FromMinutes(2), clock.Offset);
    }

    /// <summary>Review 5:E1: the rows whose speed and status go stale when the Host is lost.</summary>
    [Theory]
    [InlineData("downloading", false, true)]
    [InlineData("retryWaiting", false, true)]
    [InlineData("completed", true, true)]
    [InlineData("completed", false, false)]
    [InlineData("paused", false, false)]
    [InlineData("queued", false, false)]
    public void Rows_that_move_are_the_ones_a_lost_host_leaves_unknown(string state, bool seeding, bool moves)
        => Assert.Equal(moves, SpeedSample.Moves(state, seeding));

    [Fact]
    public void Only_a_lost_connection_is_a_gap()
    {
        Assert.Null(SpeedSample.Total(false, [1, 2]));
        Assert.Equal(0d, SpeedSample.Total(true, []));
        Assert.Equal(3d, SpeedSample.Total(true, [1, 2]));
    }

    // ---- T8: the torrents' upload ----

    [Fact]
    public void A_downloading_torrents_upload_holds_like_its_download_then_goes_stale()
    {
        Assert.Equal(700, SpeedSample.UploadRate("downloading", false, 700, Updated, Updated.AddSeconds(10)));
        Assert.Equal(0, SpeedSample.UploadRate("downloading", false, 700, Updated, Updated.AddSeconds(11)));
    }

    [Fact]
    public void A_seeding_torrents_upload_counts_as_the_Host_last_said()
    {
        // Seeding updates don't move UpdatedAt; the Host sets the rate to 0 when seeding stops.
        Assert.Equal(1500, SpeedSample.UploadRate("completed", true, 1500, Updated, Updated.AddHours(3)));
        Assert.Equal(0, SpeedSample.UploadRate("completed", true, 0, Updated, Updated));
    }

    [Theory]
    [InlineData("completed", false, 900d)] // finished, not seeding: a figure left over
    [InlineData("paused", false, 900d)]
    [InlineData("paused", true, 900d)] // "seeding" is only for a finished torrent
    [InlineData("downloading", false, -5d)]
    [InlineData("downloading", false, double.NaN)]
    [InlineData("completed", true, double.PositiveInfinity)]
    public void Anything_else_sends_nothing(string state, bool seeding, double rate)
        => Assert.Equal(0, SpeedSample.UploadRate(state, seeding, rate, Updated, Updated));

    [Theory]
    [InlineData(true, "downloading", false, true)]
    [InlineData(true, "connecting", false, true)]
    [InlineData(true, "retryWaiting", false, true)]
    [InlineData(true, "completed", true, true)]
    [InlineData(true, "completed", false, false)]
    [InlineData(true, "paused", false, false)]
    [InlineData(true, "queued", false, false)]
    [InlineData(false, "downloading", false, false)] // an ordinary download shows no upload line
    public void The_upload_line_shows_while_a_torrent_downloads_or_seeds(bool torrent, string state, bool seeding, bool expected)
        => Assert.Equal(expected, SpeedSample.TorrentRuns(torrent, state, seeding));

    [Theory]
    [InlineData("downloading", false, 0d, true)]
    [InlineData("completed", true, 1d, true)] // seeding and sending
    [InlineData("completed", true, 0.5d, false)] // seeding, sending nothing: nothing wakes
    [InlineData("completed", true, double.NaN, false)]
    [InlineData("completed", false, 800d, false)]
    [InlineData("paused", false, 800d, false)]
    public void Sampling_runs_while_a_download_moves_or_a_seeding_torrent_sends(string state, bool seeding, double upload, bool expected)
        => Assert.Equal(expected, SpeedSample.KeepsSampling(state, seeding, upload));

    [Fact]
    public void The_bar_shows_the_upload_sample_or_else_the_upload_worked_out_now()
    {
        Assert.Equal(300d, SpeedSample.UploadFigure(300, 0));
        Assert.Equal(0d, SpeedSample.UploadFigure(0, 500));
        // No upload sample yet (a seeding torrent that sends nothing, in a window just opened): what the jobs say, as the web UI.
        Assert.Equal(0d, SpeedSample.UploadFigure(null, 0));
        Assert.Null(SpeedSample.UploadFigure(null, null));
    }

    [Fact]
    public void The_hosts_upload_hour_is_placed_only_when_it_lines_up_with_the_download_hour()
    {
        long[] download = [0, 1000, 2000];
        Assert.True(SpeedGraphUpload.UploadLinesUp(download, [5, 6, 7]));
        Assert.False(SpeedGraphUpload.UploadLinesUp(download, [5, 6]));
        Assert.False(SpeedGraphUpload.UploadLinesUp(download, []));
        Assert.False(SpeedGraphUpload.UploadLinesUp(download, null)); // a Host before T8
    }

    [Fact]
    public void While_upload_shows_the_graph_is_named_for_both()
    {
        Assert.Equal("Download speed", SpeedGraphUpload.Heading("Download speed", false));
        Assert.Equal("Download and upload speed", SpeedGraphUpload.Heading("Download speed", true));
        Assert.Equal("Speed", SpeedGraphUpload.Heading("Speed", true)); // another window's own heading stays
        Assert.Equal("Download speed history", SpeedGraphUpload.PlotName("Download speed", false));
        Assert.Equal("Download and upload speed history", SpeedGraphUpload.PlotName("Download speed", true));
        Assert.Equal("Hide download speed graph", SpeedGraphUpload.Toggle(true, false));
        Assert.Equal("Show download and upload speed graph", SpeedGraphUpload.Toggle(false, true));
    }
}
