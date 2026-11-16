using DownloadManager.Contracts;
using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

/// <summary>SITES.5: the "On this PC" column against a linked folder.</summary>
public sealed class SiteHereTests : IDisposable
{
    private readonly string _linked = Path.Combine(Path.GetTempPath(), "inbound-here-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Remote = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    public SiteHereTests() => Directory.CreateDirectory(Path.Combine(_linked, "docs"));

    public void Dispose()
    {
        try { Directory.Delete(_linked, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void TheStartFolderMatchesTheLinkedFolder()
    {
        Assert.Equal(Path.Combine(_linked, "a.txt"), SiteHere.LocalPath(_linked, "/pub", "/pub", "a.txt"));
        Assert.Equal(Path.Combine(_linked, "docs", "deep", "a.txt"), SiteHere.LocalPath(_linked, "/pub", "/pub/docs/deep", "a.txt"));
        Assert.Equal(Path.Combine(_linked, "pub", "a.txt"), SiteHere.LocalPath(_linked, "", "/pub", "a.txt"));
        // Outside the start folder, without a linked folder, or a name Windows can't hold: not compared.
        Assert.Null(SiteHere.LocalPath(_linked, "/pub", "/", "a.txt"));
        Assert.Null(SiteHere.LocalPath(_linked, "/pub", "/public", "a.txt"));
        Assert.Null(SiteHere.LocalPath(null, "/pub", "/pub", "a.txt"));
        Assert.Null(SiteHere.LocalPath("relative", "/pub", "/pub", "a.txt"));
        if (OperatingSystem.IsWindows()) Assert.Null(SiteHere.LocalPath(_linked, "/pub", "/pub", "a:b"));
    }

    [Fact]
    public void FilesCompareBySizeAndTime()
    {
        Write("same.bin", 10, Remote);
        Write("same-later.bin", 10, Remote.AddMinutes(5));
        Write("older.bin", 10, Remote.AddMinutes(-5));
        Write("cut.bin", 4, Remote);
        Write("bigger.bin", 20, Remote);
        Write("odd-second.bin", 10, Remote.AddSeconds(-1));

        Assert.Equal(SiteHereStates.Same, State("same.bin", 10));
        Assert.Equal(SiteHereStates.Same, State("same-later.bin", 10));
        Assert.Equal(SiteHereStates.OlderHere, State("older.bin", 10));
        Assert.Equal(SiteHereStates.OlderHere, State("older.bin", 99));
        Assert.Equal(SiteHereStates.SomeOfIt, State("cut.bin", 10));
        Assert.Equal(SiteHereStates.Different, State("bigger.bin", 10));
        Assert.Equal(SiteHereStates.Same, State("odd-second.bin", 10));
        Assert.Equal(SiteHereStates.NotHere, State("missing.bin", 10));
        // A server that gives no size: the time decides.
        Assert.Equal(SiteHereStates.Same, State("same.bin", -1));
        // A folder here where the server has a file.
        Assert.Equal(SiteHereStates.Different, State("docs", 10));
    }

    [Fact]
    public void FoldersSayOnlyWhetherTheyAreHere()
    {
        Assert.Equal(SiteHereStates.FolderHere, SiteHere.Of(_linked, "/pub", "/pub", new SiteItemDto { Name = "docs", IsFolder = true }));
        Assert.Equal(SiteHereStates.NotHere, SiteHere.Of(_linked, "/pub", "/pub", new SiteItemDto { Name = "music", IsFolder = true }));
        // A file here where the server has a folder.
        Write("notes", 3, Remote);
        Assert.Equal(SiteHereStates.Different, SiteHere.Of(_linked, "/pub", "/pub", new SiteItemDto { Name = "notes", IsFolder = true }));
    }

    private string? State(string name, long size) =>
        SiteHere.Of(_linked, "/pub", "/pub", new SiteItemDto { Name = name, Size = size, Modified = Remote });

    private void Write(string name, int size, DateTimeOffset modified)
    {
        var path = Path.Combine(_linked, name);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
    }
}
