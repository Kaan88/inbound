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

        // UI.B30: a new day changes the list, so it is pushed and an open window shows the day.
        _now = _now.AddDays(1);
        Assert.True(seen.Note(ExtensionBrowsers.Firefox));
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

    // UI.B31: the native host's own logs, read once, for extensions in use before it named its browser.

    private static readonly string[] SampleLines =
    [
        "2026-11-11T03:30:19.4654270+03:00 Native host started. Caller=chromium PID=4356",
        "2026-11-11T03:30:19.6000000+03:00 Forwarded host.ping",
        "2026-11-12T04:54:04.2123283+03:00 Native host started. Caller=firefox PID=117612",
        "2026-11-12T14:52:39.9086731+03:00 Native host started. Caller=chromium PID=33784",
        // 0.7.2: the browser is named on the next line, so this chromium start is Edge's and no chromium sighting.
        "2026-11-12T23:02:12.9424481+03:00 Native host started. Caller=chromium PID=95076",
        "2026-11-12T23:02:12.9627631+03:00 Browser=edge",
        "2026-11-12T23:03:29.9092015+03:00 Native host started. Caller=firefox PID=18288",
        "2026-11-12T23:03:29.9289190+03:00 Browser=firefox",
        "2026-11-12T23:04:00.0000000+03:00 Native host started. Caller=(refused) PID=1",
        "not a log line",
        "",
    ];

    [Fact]
    public void Log_lines_give_each_browser_its_last_time_and_an_unnamed_chromium_start_counts_as_chromium()
    {
        var found = NativeHostLogSightings.Parse(SampleLines);
        Assert.Equal(DateTimeOffset.Parse("2026-11-12T14:52:39.9086731+03:00"), found[ExtensionBrowsers.Chromium]);
        Assert.Equal(DateTimeOffset.Parse("2026-11-12T23:02:12.9627631+03:00"), found[ExtensionBrowsers.Edge]);
        Assert.Equal(DateTimeOffset.Parse("2026-11-12T23:03:29.9289190+03:00"), found[ExtensionBrowsers.Firefox]);
        Assert.False(found.ContainsKey(ExtensionBrowsers.Chrome));
        Assert.Equal(3, found.Count);
    }

    [Fact]
    public void Two_native_hosts_writing_at_once_each_keep_their_own_browser()
    {
        var found = NativeHostLogSightings.Parse(
        [
            "2026-11-12T10:00:00.000+00:00 Native host started. Caller=chromium PID=1",
            "2026-11-12T10:00:00.010+00:00 Native host started. Caller=chromium PID=2",
            "2026-11-12T10:00:00.020+00:00 Browser=chrome",
            "2026-11-12T10:00:00.030+00:00 Browser=edge",
        ]);
        Assert.Equal([ExtensionBrowsers.Chrome, ExtensionBrowsers.Edge], found.Keys.Order(StringComparer.Ordinal));
    }

    private string Logs => Path.Combine(_root, "logs");

    private void WriteLog(string day, params string[] lines)
    {
        Directory.CreateDirectory(Logs);
        File.WriteAllLines(Path.Combine(Logs, $"native-host-{day}.log"), lines);
    }

    private ExtensionSightings OpenWithLogs() => new(_root, now: () => _now, nativeHostLogs: Logs);

    [Fact]
    public void Without_the_file_the_logs_are_read_once_and_kept()
    {
        WriteLog("20261111", SampleLines[..2]);
        WriteLog("20261112", SampleLines[2..]);
        var seen = OpenWithLogs().Snapshot();
        Assert.Equal([ExtensionBrowsers.Chromium, ExtensionBrowsers.Edge, ExtensionBrowsers.Firefox], seen.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(ExtensionSightings.LogsReadKey, File.ReadAllText(FilePath), StringComparison.Ordinal);

        // Read once: a log written afterwards is not read again.
        WriteLog("20261112", "2026-11-12T08:00:00.000+00:00 Native host started. Caller=chromium PID=3", "2026-11-12T08:00:00.010+00:00 Browser=chrome");
        Assert.DoesNotContain(ExtensionBrowsers.Chrome, OpenWithLogs().Snapshot().Keys);
    }

    [Fact]
    public void A_file_from_0_7_2_without_the_mark_gets_what_the_logs_add_and_keeps_its_newer_days()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FilePath, """{ "firefox": "2026-11-12T21:00:00+00:00" }""");
        WriteLog("20261112", SampleLines);
        var seen = OpenWithLogs().Snapshot();
        Assert.Equal(DateTimeOffset.Parse("2026-11-12T21:00:00+00:00"), seen[ExtensionBrowsers.Firefox]);
        Assert.True(seen.ContainsKey(ExtensionBrowsers.Chromium));
        Assert.True(seen.ContainsKey(ExtensionBrowsers.Edge));
        Assert.DoesNotContain(ExtensionSightings.LogsReadKey, seen.Keys);
    }

    [Fact]
    public void Logs_older_than_thirty_days_and_no_logs_at_all_add_nothing_but_still_count_as_read()
    {
        WriteLog("20261001", "2026-10-01T08:00:00.000+00:00 Native host started. Caller=firefox PID=3");
        Assert.Empty(OpenWithLogs().Snapshot());
        Assert.True(File.Exists(FilePath));

        Directory.Delete(_root, recursive: true);
        Assert.Empty(new ExtensionSightings(_root, now: () => _now, nativeHostLogs: Path.Combine(_root, "none")).Snapshot());
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void A_mark_kept_across_a_later_sighting_means_the_logs_stay_unread()
    {
        WriteLog("20261112", SampleLines);
        var first = OpenWithLogs();
        Assert.True(first.Note(ExtensionBrowsers.Chrome));
        WriteLog("20261112", "2026-11-12T08:00:00.000+00:00 Native host started. Caller=chromium PID=3", "2026-11-12T08:00:00.010+00:00 Browser=edge");
        var again = OpenWithLogs().Snapshot();
        Assert.True(again.ContainsKey(ExtensionBrowsers.Chrome));
        Assert.True(again.ContainsKey(ExtensionBrowsers.Chromium));
        Assert.Contains(ExtensionSightings.LogsReadKey, File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_file_is_not_replaced_by_the_logs()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FilePath, "{ not json");
        WriteLog("20261112", SampleLines);
        Assert.Empty(OpenWithLogs().Snapshot());
        Assert.Equal("{ not json", File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData(ExtensionBrowsers.Chromium)]
    [InlineData(ExtensionBrowsers.Unknown)]
    public void Chromium_and_unknown_are_kept_like_browsers(string name)
    {
        Assert.True(Open().Note(name));
        Assert.Equal([name], Open().Snapshot().Keys);
    }
}
