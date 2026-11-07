using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;
using Xunit;

namespace DownloadManager.Core.Tests;

/// <summary>What only showed on the real window against a real account's list and real downloads.</summary>
public class LiveWalkFindingsTests
{
    [Theory]
    [InlineData(0.4, "1s")]
    [InlineData(1, "1s")]
    [InlineData(25, "25s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m 0s")]
    [InlineData(61, "1m 1s")]
    [InlineData(119, "1m 59s")]
    [InlineData(3599, "59m 59s")]
    [InlineData(3600, "1h 0m")]
    [InlineData(90000, "25h 0m")]
    public void Time_left_says_what_is_left(double seconds, string words) =>
        Assert.Equal(words, DurationText.TimeLeft(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Nothing_left_or_nothing_known_says_nothing()
    {
        Assert.Equal(string.Empty, DurationText.TimeLeft(null));
        Assert.Equal(string.Empty, DurationText.TimeLeft(TimeSpan.Zero));
        Assert.Equal(string.Empty, DurationText.TimeLeft(TimeSpan.FromSeconds(-3)));
    }

    [Fact]
    public void A_sorted_list_reads_the_rest_of_the_account_and_stops_when_there_is_no_more()
    {
        Assert.True(RemoteItemSort.NeedsTheRest("files", hasMore: true, lastPageCount: 50));
        Assert.False(RemoteItemSort.NeedsTheRest(null, hasMore: true, lastPageCount: 50));
        Assert.False(RemoteItemSort.NeedsTheRest("files", hasMore: false, lastPageCount: 50));
        Assert.False(RemoteItemSort.NeedsTheRest("files", hasMore: true, lastPageCount: 0));
    }

    [Fact]
    public void Each_way_a_sorted_list_can_come_to_show_part_of_the_account_goes_on_reading()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "DownloadManager.Desktop.WinUI", "MyProviderWindow.cs"));
        Assert.Equal(3, source.Split("RemoteItemSort.NeedsTheRest(").Length - 1);
    }

    [Fact]
    public void Rows_read_before_the_accounts_arrive_get_the_providers_own_name_when_they_do()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "DownloadManager.Desktop.WinUI", "MainWindow.Remote.cs"));
        var handler = source[source.IndexOf("state.Accounts.CollectionChanged", StringComparison.Ordinal)..];
        handler = handler[..handler.IndexOf("};", StringComparison.Ordinal)];
        Assert.Contains("ShowProviderSources();", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_sorted_while_it_reads_on_shows_the_first_rows_of_the_order()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "DownloadManager.Desktop.WinUI", "MyProviderWindow.cs"));
        Assert.Contains("if (onShow && more && _sortKey is not null) ShowWhereTheOrderPuts(page);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_files_header_has_room_for_its_word_and_the_sort_arrow()
    {
        var files = ListColumnSets.ProviderItems().Columns.Single(column => column.Key == "files");
        Assert.True(files.DefaultWidth >= 58, $"At {files.DefaultWidth} the header reads F... once the arrow is on show.");
    }

    [Fact]
    public void No_window_lets_WinUI_float_a_shortcuts_name_over_it()
    {
        var folder = Path.Combine(Root(), "src", "DownloadManager.Desktop.WinUI");
        var rootsWithShortcuts = new[] { ("MainWindow.xaml.cs", "RootGrid"), ("MyProviderWindow.cs", "_root"), ("PackageWindow.cs", "_root"), ("SettingsWindow.cs", "_root") };
        foreach (var (file, root) in rootsWithShortcuts)
        {
            var source = File.ReadAllText(Path.Combine(folder, file));
            Assert.Contains($"{root}.KeyboardAccelerators.Add(", source, StringComparison.Ordinal);
            Assert.Contains($"{root}.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;", source, StringComparison.Ordinal);
        }

        // A window that takes up shortcuts on its root later has to be in the list above.
        foreach (var path in Directory.EnumerateFiles(folder, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var source = File.ReadAllText(path);
            foreach (var root in new[] { "_root", "RootGrid", "Root" })
            {
                if (!source.Contains($"{root}.KeyboardAccelerators.Add(", StringComparison.Ordinal)) continue;
                Assert.Contains(rootsWithShortcuts, known => known.Item1 == Path.GetFileName(path));
            }
        }
    }

    [Fact]
    public void A_host_or_a_comment_with_no_full_stop_doesnt_run_into_the_next_sentence()
    {
        Assert.Equal("archive.org · The container says 552.6 KB.", RemoteText.ContainerDetail(["archive.org", "The container says 552.6 KB."]));
        Assert.Equal("Public test archives · This container states an archive password.", RemoteText.ContainerDetail(["Public test archives", "This container states an archive password."]));
        Assert.Equal("The container says 1 KB. It starts unticked.", RemoteText.ContainerDetail(["The container says 1 KB.", "It starts unticked."]));
        Assert.Equal(string.Empty, RemoteText.ContainerDetail([]));
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root wasn't found.");
    }
}
