using DownloadManager.Contracts;
using DownloadManager.Mobile.TorBox;

namespace DownloadManager.Mobile.Tests;

public sealed class TorBoxDownloadsTests
{
    private static RemoteJobDto Job(string state = "waiting", string? stage = "processing", double progress = 0.37, string name = "Blender open movies",
        string provider = "torbox", int minutes = 0) => new()
    {
        Id = Guid.NewGuid(), Provider = provider, Kind = "torrents", State = state, Stage = stage, Progress = progress, Name = name,
        CreatedAt = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero).AddMinutes(minutes),
    };

    [Fact]
    public void A_fetching_job_reads_as_on_torbox_with_percent_and_where_it_goes_next()
    {
        var job = Job();
        Assert.True(TorBoxDownloads.Qualifies(job));
        Assert.Equal("On TorBox · 37% · then to this phone", TorBoxDownloads.Subtitle(job, null));
        Assert.Equal("On TorBox · 37% · then to Kitchen PC", TorBoxDownloads.Subtitle(job, " Kitchen PC "));
    }

    [Fact]
    public void Percent_is_left_out_when_the_provider_reports_none()
    {
        var job = Job(stage: "queued", progress: 0);
        Assert.True(TorBoxDownloads.Qualifies(job));
        Assert.Equal("On TorBox · then to this phone", TorBoxDownloads.Subtitle(job, ""));
    }

    [Theory]
    [InlineData("failed", "failed")]
    [InlineData("blocked", "failed")]
    [InlineData("localStarted", "ready")]
    [InlineData("awaitingSelection", "ready")]
    [InlineData("removed", "processing")]
    public void Jobs_that_left_torbox_or_failed_are_not_rows(string state, string stage) =>
        Assert.False(TorBoxDownloads.Qualifies(Job(state, stage)));

    [Fact]
    public void A_ready_job_a_held_job_and_other_providers_are_not_rows()
    {
        Assert.False(TorBoxDownloads.Qualifies(Job(stage: "ready")));
        var held = Job(progress: 0, stage: "queued");
        held.ApprovalNeeded = true;
        Assert.False(TorBoxDownloads.Qualifies(held));
        Assert.False(TorBoxDownloads.Qualifies(Job(provider: "other")));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0.5, 0.5)]
    [InlineData(7, 1)]
    [InlineData(double.NaN, 0)]
    public void Progress_is_clamped_to_zero_to_one(double given, double expected) =>
        Assert.Equal(expected, TorBoxDownloads.Progress(Job(progress: given)));

    [Fact]
    public void Rows_are_newest_first_and_the_search_matches_names()
    {
        var jobs = new[] { Job(name: "Old", minutes: 0), Job(name: "New thing", minutes: 5), Job("failed", "failed", name: "Gone", minutes: 9) };
        Assert.Equal(["New thing", "Old"], TorBoxDownloads.Rows(jobs, null).Select(r => r.Name));
        Assert.Equal(["New thing"], TorBoxDownloads.Rows(jobs, null, " THING ").Select(r => r.Name));
        Assert.All(TorBoxDownloads.Rows(jobs, null), r => Assert.Equal("On TorBox", r.Lead));
    }

    [Fact]
    public void A_job_without_a_name_uses_its_host_then_a_generic_word()
    {
        var job = Job(name: "");
        job.DisplayHost = "example.com";
        Assert.Equal("example.com", TorBoxDownloads.Rows([job], null)[0].Name);
        job.DisplayHost = null;
        Assert.Equal("TorBox item", TorBoxDownloads.Rows([job], null)[0].Name);
    }

    [Fact]
    public void Only_the_all_and_active_views_show_the_rows()
    {
        Assert.True(TorBoxDownloads.ShownIn(DownloadFilter.All));
        Assert.True(TorBoxDownloads.ShownIn(DownloadFilter.Active));
        Assert.False(TorBoxDownloads.ShownIn(DownloadFilter.Finished));
        Assert.False(TorBoxDownloads.ShownIn(DownloadFilter.Torrents));
    }
}
