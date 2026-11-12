using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>T1 step 6b: the window's <c>--open-torrent</c> argument and its request queue.</summary>
public sealed class TorrentOpenRequestsTests : IDisposable
{
    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=a%20b&tr=udp://t:1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inbound-open-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void Parse_takes_a_magnet_or_a_full_torrent_path_after_the_argument()
    {
        Assert.Equal(Magnet, TorrentOpenRequests.Parse(["app.exe", "--open-torrent", Magnet]));
        Assert.Equal(@"C:\Users\me\My Files\a b.torrent",
            TorrentOpenRequests.Parse(["app.exe", "--OPEN-TORRENT", @"C:\Users\me\My Files\a b.torrent"]));
        Assert.Equal(@"C:\x.torrent", TorrentOpenRequests.Parse(["app.exe", "--open-torrent", "\"C:\\x.torrent\""]));
    }

    [Fact]
    public void Parse_turns_a_local_file_address_from_a_linux_desktop_into_its_path()
    {
        // T4: the Linux desktop entry passes %u, which some desktops fill with a file:// address.
        var (address, path) = OperatingSystem.IsWindows()
            ? ("file:///C:/My%20Files/a%20b.torrent", @"C:\My Files\a b.torrent")
            : ("file:///home/me/My%20Files/a%20b.torrent", "/home/me/My Files/a b.torrent");
        Assert.Equal(path, TorrentOpenRequests.Parse(["inbound", "--open-torrent", address]));
        Assert.Equal(Magnet, TorrentOpenRequests.FromFileUri(Magnet));
        // Another machine's file stays an address, which isn't accepted.
        Assert.Null(TorrentOpenRequests.Parse(["inbound", "--open-torrent", "file://server/share/a.torrent"]));
    }

    [Theory]
    [InlineData("relative.torrent")]
    [InlineData(@"C:\notes.txt")]
    [InlineData("https://example.com/a.torrent")]
    [InlineData("magnet:")]
    [InlineData("")]
    [InlineData("--page")]
    public void Parse_refuses_anything_else(string value)
    {
        Assert.Null(TorrentOpenRequests.Parse(["app.exe", "--open-torrent", value]));
    }

    [Fact]
    public void Parse_without_the_argument_or_its_value_is_null()
    {
        Assert.Null(TorrentOpenRequests.Parse(["app.exe", "--page", "settings"]));
        Assert.Null(TorrentOpenRequests.Parse(["app.exe", "--open-torrent"]));
        Assert.Null(TorrentOpenRequests.Parse(["app.exe", Magnet]));
    }

    [Fact]
    public void Queued_requests_come_back_once_oldest_first_without_duplicates()
    {
        var now = DateTimeOffset.UtcNow;
        TorrentOpenRequests.Enqueue(_dir, Magnet, now.AddSeconds(-2));
        TorrentOpenRequests.Enqueue(_dir, @"C:\a.torrent", now.AddSeconds(-1));
        TorrentOpenRequests.Enqueue(_dir, Magnet, now);

        Assert.Equal([Magnet, @"C:\a.torrent"], TorrentOpenRequests.Drain(_dir, now));
        Assert.Empty(TorrentOpenRequests.Drain(_dir, now));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Stale_and_invalid_requests_are_dropped_and_deleted()
    {
        var now = DateTimeOffset.UtcNow;
        TorrentOpenRequests.Enqueue(_dir, Magnet, now - TorrentOpenRequests.MaxAge - TimeSpan.FromSeconds(1));
        TorrentOpenRequests.Enqueue(_dir, "notes.txt", now);
        File.WriteAllText(Path.Combine(_dir, "garbage.request"), Magnet);

        Assert.Empty(TorrentOpenRequests.Drain(_dir, now));
        Assert.Empty(Directory.GetFiles(_dir, "*.request"));
    }

    [Fact]
    public void Held_inputs_keep_their_order_and_appear_once()
    {
        var held = new List<string> { Magnet };
        TorrentOpenRequests.Hold(held, [@"C:\a.torrent", Magnet, @"C:\a.torrent", @"C:\b.torrent"]);
        Assert.Equal([Magnet, @"C:\a.torrent", @"C:\b.torrent"], held);
    }

    [Fact]
    public void A_missing_queue_folder_is_empty()
    {
        Assert.Empty(TorrentOpenRequests.Drain(_dir, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Focus_follows_the_inputs()
    {
        Assert.Equal("magnet", TorrentOpenRequests.Focus([Magnet]));
        Assert.Equal("torrentFiles", TorrentOpenRequests.Focus([@"C:\a.torrent"]));
        Assert.Equal("mixed", TorrentOpenRequests.Focus([Magnet, @"C:\a.torrent"]));
    }
}
