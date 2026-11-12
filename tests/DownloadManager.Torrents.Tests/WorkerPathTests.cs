namespace DownloadManager.Torrents.Tests;

/// <summary>
/// T4: where a Host looks for the torrent worker, with Linux paths too (these run on Windows, so the paths and the file
/// check are given rather than read from this machine).
/// </summary>
public sealed class WorkerPathTests
{
    // AppContext.BaseDirectory inside the AppImage: inbound-host is published into usr/lib/inbound/.
    private const string AppImageLib = "/tmp/.mount_InboundAbC123/usr/lib/inbound/";

    [Fact]
    public void Linux_finds_the_worker_next_to_the_host_in_the_appimage()
    {
        var seen = new List<string>();
        var found = TorrentSetup.FindWorker(AppImageLib, null, windows: false, path =>
        {
            seen.Add(path);
            return path == "/tmp/.mount_InboundAbC123/usr/lib/inbound/inbound-torrent";
        });
        Assert.Equal("/tmp/.mount_InboundAbC123/usr/lib/inbound/inbound-torrent", found);
        Assert.Equal(["/tmp/.mount_InboundAbC123/usr/lib/inbound/inbound-torrent"], seen);
    }

    [Fact]
    public void Linux_without_a_trailing_slash_and_without_the_worker()
    {
        Assert.Equal("/opt/inbound/inbound-torrent",
            TorrentSetup.FindWorker("/opt/inbound", null, windows: false, path => path == "/opt/inbound/inbound-torrent"));
        // The Windows name isn't looked for on Linux, and nothing there means no torrents.
        Assert.Null(TorrentSetup.FindWorker(AppImageLib, null, windows: false, path => path.EndsWith(".exe", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_variable_wins_and_must_exist()
    {
        Assert.Equal("/home/me/build/inbound-torrent",
            TorrentSetup.FindWorker(AppImageLib, "/home/me/build/inbound-torrent", windows: false, _ => true));
        Assert.Null(TorrentSetup.FindWorker(AppImageLib, "/home/me/build/inbound-torrent", windows: false,
            path => path.StartsWith(AppImageLib, StringComparison.Ordinal)));
    }

    [Fact]
    public void Windows_keeps_its_exe_name()
    {
        Assert.Equal(@"C:\Program Files\Inbound\app\inbound-torrent.exe",
            TorrentSetup.FindWorker(@"C:\Program Files\Inbound\app\", null, windows: true, _ => true));
        Assert.Equal(@"C:\app\inbound-torrent.exe", TorrentSetup.FindWorker(@"C:\app", null, windows: true, _ => true));
    }

    [Fact]
    public void The_appimage_script_puts_the_worker_where_the_host_looks()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "package-appimage.sh"));
        // The Host is published into $lib, and the worker is installed into that same folder under the Linux name.
        Assert.Contains("publish src/DownloadManager.Host.Linux \"$lib\"", script, StringComparison.Ordinal);
        Assert.Contains("install -m 755 \"$worker\" \"$lib/" + TorrentSetup.WorkerFileNameFor(windows: false) + "\"", script,
            StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root wasn't found.");
    }
}
