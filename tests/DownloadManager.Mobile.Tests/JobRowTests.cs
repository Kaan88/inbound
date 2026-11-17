using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class JobRowTests
{
    [Fact]
    public void Downloading_with_size_shows_a_determinate_bar_and_rate_and_eta()
    {
        var row = Jobs.Row(Jobs.Make("downloading", "a.iso", total: 1_000_000_000, received: 250_000_000, rate: 5_000_000));
        Assert.Equal(StatusKind.Active, row.Kind);
        Assert.True(row.ShowBar);
        Assert.False(row.Indeterminate);
        Assert.Equal(0.25, row.Progress, 3);
        Assert.Equal("250 MB of 1.0 GB · 5.0 MB/s · 2 min left", row.Subtitle);
        Assert.Equal("a.iso", row.Name);
    }

    [Fact]
    public void Downloading_without_size_is_indeterminate_and_shows_received_bytes_only()
    {
        var row = Jobs.Row(Jobs.Make("downloading", total: null, received: 1_500_000, rate: 500_000));
        Assert.Equal(StatusKind.Active, row.Kind);
        Assert.True(row.ShowBar);
        Assert.True(row.Indeterminate);
        Assert.Equal(0, row.Progress);
        Assert.Equal("1.5 MB · 500 KB/s", row.Subtitle);
    }

    [Fact]
    public void Downloading_with_size_zero_counts_as_unknown_size()
    {
        var row = Jobs.Row(Jobs.Make("downloading", total: 0, received: 10));
        Assert.True(row.Indeterminate);
        Assert.Equal("10 B", row.Subtitle);
    }

    [Fact]
    public void Downloading_at_rate_zero_leaves_out_rate_and_eta()
    {
        var row = Jobs.Row(Jobs.Make("downloading", total: 2_000, received: 1_000, rate: 0));
        Assert.Equal("1.0 KB of 2.0 KB", row.Subtitle);
    }

    [Theory]
    [InlineData("connecting")]
    [InlineData("verifying")]
    public void Connecting_and_verifying_are_active_with_an_indeterminate_bar_even_when_the_size_is_known(string state)
    {
        var row = Jobs.Row(Jobs.Make(state, total: 4_000_000, received: 1_000_000));
        Assert.Equal(StatusKind.Active, row.Kind);
        Assert.True(row.ShowBar);
        Assert.True(row.Indeterminate);
        Assert.Equal("1.0 MB of 4.0 MB", row.Subtitle);
    }

    [Fact]
    public void Paused_with_size_keeps_the_bar_at_the_position()
    {
        var row = Jobs.Row(Jobs.Make("paused", total: 4_000_000, received: 1_000_000));
        Assert.Equal(StatusKind.Waiting, row.Kind);
        Assert.True(row.ShowBar);
        Assert.False(row.Indeterminate);
        Assert.Equal(0.25, row.Progress, 3);
        Assert.Equal("Paused · 1.0 MB of 4.0 MB", row.Subtitle);
    }

    [Fact]
    public void Paused_without_size_has_no_bar()
    {
        var row = Jobs.Row(Jobs.Make("paused", total: null, received: 1_000));
        Assert.False(row.ShowBar);
        Assert.Equal("Paused", row.Subtitle);
    }

    [Fact]
    public void Completed_shows_the_size_and_a_full_progress_value_without_a_bar()
    {
        var row = Jobs.Row(Jobs.Make("completed", total: 3_200_000, received: 3_200_000));
        Assert.Equal(StatusKind.Done, row.Kind);
        Assert.False(row.ShowBar);
        Assert.Equal(1, row.Progress);
        Assert.Equal("Finished · 3.2 MB", row.Subtitle);
    }

    [Fact]
    public void Completed_without_a_known_total_uses_the_received_bytes()
    {
        Assert.Equal("Finished · 900 B", Jobs.Row(Jobs.Make("completed", total: null, received: 900)).Subtitle);
    }

    [Fact]
    public void Failed_shows_the_note_and_is_a_failure()
    {
        var row = Jobs.Row(Jobs.Make("failed", note: "Server answered 404"));
        Assert.Equal(StatusKind.Failed, row.Kind);
        Assert.False(row.ShowBar);
        Assert.Equal("Failed · Server answered 404", row.Subtitle);
    }

    [Fact]
    public void Failed_without_a_note_says_only_failed()
    {
        Assert.Equal("Failed", Jobs.Row(Jobs.Make("failed")).Subtitle);
        Assert.Equal("Failed", Jobs.Row(Jobs.Make("failed", note: "")).Subtitle);
    }

    [Fact]
    public void Cancelled_is_shown_like_a_failure_with_its_own_word()
    {
        var row = Jobs.Row(Jobs.Make("cancelled", note: "Cancelled by you"));
        Assert.Equal(StatusKind.Failed, row.Kind);
        Assert.Equal("Cancelled · Cancelled by you", row.Subtitle);
        Assert.Equal("Cancelled", Jobs.Row(Jobs.Make("cancelled")).Subtitle);
    }

    [Fact]
    public void Retrywaiting_waits_without_a_bar()
    {
        var row = Jobs.Row(Jobs.Make("retrywaiting", total: 100, received: 50));
        Assert.Equal(StatusKind.Waiting, row.Kind);
        Assert.False(row.ShowBar);
        Assert.Equal("Waiting to retry", row.Subtitle);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("scheduled")]
    [InlineData("")]
    [InlineData("something-new")]
    public void Queued_and_unknown_states_are_neutral(string state)
    {
        var row = Jobs.Row(Jobs.Make(state));
        Assert.Equal(StatusKind.Neutral, row.Kind);
        Assert.False(row.ShowBar);
        Assert.Equal("Queued", row.Subtitle);
    }

    [Fact]
    public void Queued_with_a_known_size_shows_it()
    {
        Assert.Equal("Queued · 12.3 MB", Jobs.Row(Jobs.Make("queued", total: 12_300_000)).Subtitle);
    }

    [Fact]
    public void State_words_are_matched_without_regard_to_case()
    {
        Assert.Equal(StatusKind.Done, Jobs.Row(Jobs.Make("Completed", total: 1)).Kind);
    }

    [Fact]
    public void A_row_without_a_file_name_shows_its_address()
    {
        var job = Jobs.Make("queued", name: "");
        Assert.Equal(job.Url, Jobs.Row(job).Name);
    }

    [Fact]
    public void Progress_never_leaves_zero_to_one()
    {
        Assert.Equal(1, Jobs.Row(Jobs.Make("downloading", total: 100, received: 500)).Progress);
    }

    [Fact]
    public void Rate_history_grows_while_transferring_and_is_capped()
    {
        var row = new JobRow { Id = Guid.NewGuid() };
        for (var i = 0; i < JobRow.MaxRates + 10; i++)
            row.ApplyFrom(Jobs.Make("downloading", total: 1000, received: i, rate: i + 1));
        Assert.Equal(JobRow.MaxRates, row.RateHistory.Count);
        Assert.Equal(JobRow.MaxRates + 10, row.RateHistory[^1]);
        row.ApplyFrom(Jobs.Make("paused", total: 1000, received: 5));
        Assert.Equal(JobRow.MaxRates, row.RateHistory.Count);
    }

    [Fact]
    public void Property_changes_are_raised_only_when_a_value_changes()
    {
        var row = new JobRow { Id = Guid.NewGuid() };
        var job = Jobs.Make("downloading", total: 1000, received: 100, rate: 10);
        row.ApplyFrom(job);
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        row.ApplyFrom(job);
        Assert.Empty(raised);
        job.ReceivedBytes = 200;
        row.ApplyFrom(job);
        Assert.Contains(nameof(JobRow.Progress), raised);
        Assert.Contains(nameof(JobRow.Subtitle), raised);
    }

    [Fact]
    public void A_new_file_name_alone_is_raised()
    {
        var row = new JobRow { Id = Guid.NewGuid() };
        var job = Jobs.Make("queued");
        job.FileName = "";
        row.ApplyFrom(job);
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        job.FileName = "named.iso";
        row.ApplyFrom(job);
        Assert.Equal("named.iso", row.Name);
        Assert.Equal([nameof(JobRow.Name)], raised);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1000, "1.0 KB")]
    [InlineData(1_500_000, "1.5 MB")]
    [InlineData(150_000_000, "150 MB")]
    [InlineData(2_000_000_000_000, "2.0 TB")]
    public void Bytes_are_written_in_decimal_units(long bytes, string text) => Assert.Equal(text, Fmt.Bytes(bytes));

    [Theory]
    [InlineData(30, "30 s left")]
    [InlineData(0.2, "1 s left")]
    [InlineData(600, "10 min left")]
    [InlineData(7200, "2 h left")]
    public void Eta_is_written_in_the_largest_fitting_unit(double seconds, string text) => Assert.Equal(text, Fmt.Eta(seconds));
}
