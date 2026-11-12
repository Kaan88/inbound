using DownloadManager.Host;
using Xunit;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// T4: the Linux magnet and .torrent handler's desktop entry and mimeapps.list clean-up. Every folder is a throwaway one
/// under the temp folder and xdg-mime is recorded, not run: the user's real ~/.local and ~/.config are never touched.
/// Nothing here ran on a real Linux desktop.
/// </summary>
public sealed class LinuxTorrentHandlerTests : IDisposable
{
    private const string AppImage = "/home/me/Apps/Inbound-0.6.6-x86_64.AppImage";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inbound-linux-handler-" + Guid.NewGuid().ToString("N"));
    private readonly List<IReadOnlyList<string>> _calls = [];

    private string Applications => Path.Combine(_root, "share", "applications");
    private string ConfigList => Path.Combine(_root, "config", "mimeapps.list");
    private string OldList => Path.Combine(Applications, "mimeapps.list");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private LinuxTorrentHandler Handler(string command = AppImage, bool xdgMimeSucceeds = true) =>
        new(Applications, [ConfigList, OldList], command, arguments =>
        {
            _calls.Add(arguments);
            return xdgMimeSucceeds;
        });

    [Fact]
    public void A_failed_xdg_mime_is_tried_again_until_it_succeeds()
    {
        var path = Path.Combine(Applications, LinuxTorrentHandler.DesktopId);
        Assert.Throws<IOException>(() => Handler(xdgMimeSucceeds: false).Register());
        // The entry is there for the desktop's own settings, marked so the defaults are still set later.
        Assert.Contains(LinuxTorrentHandler.PendingKey, File.ReadAllText(path), StringComparison.Ordinal);

        // A Host restart (or the next settings change) tries again.
        Assert.Throws<IOException>(() => Handler(xdgMimeSucceeds: false).Register());
        Assert.Equal(2, _calls.Count);

        Assert.True(Handler().Register());
        Assert.Equal(3, _calls.Count);
        Assert.Equal(Handler().DesktopFileText(), File.ReadAllText(path));
        Assert.False(Handler().Register());
        Assert.Equal(3, _calls.Count);
    }

    [Fact]
    public void Remove_keeps_a_symlinked_mimeapps_list_a_link()
    {
        var dotfiles = Path.Combine(_root, "dotfiles");
        Directory.CreateDirectory(dotfiles);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigList)!);
        var real = Path.Combine(dotfiles, "mimeapps.list");
        File.WriteAllText(real, "[Default Applications]\nx-scheme-handler/magnet=inbound-torrent.desktop;\ntext/html=firefox.desktop;\n");
        try
        {
            File.CreateSymbolicLink(ConfigList, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows without Developer Mode can't make symlinks; Linux CI can.
            return;
        }

        const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(real, privateMode);

        Assert.True(Handler().Remove());

        Assert.NotNull(new FileInfo(ConfigList).LinkTarget);
        Assert.Equal("[Default Applications]\ntext/html=firefox.desktop;\n", File.ReadAllText(real));
        Assert.False(File.Exists(real + ".tmp"));
        if (!OperatingSystem.IsWindows()) Assert.Equal(privateMode, File.GetUnixFileMode(real));
    }

    [Fact]
    public void Register_writes_the_entry_and_sets_both_defaults_once()
    {
        var handler = Handler();
        Assert.True(handler.Register());

        var text = File.ReadAllText(Path.Combine(Applications, LinuxTorrentHandler.DesktopId));
        Assert.Contains("Exec=\"/home/me/Apps/Inbound-0.6.6-x86_64.AppImage\" --open-torrent %u\n", text, StringComparison.Ordinal);
        Assert.Contains("MimeType=x-scheme-handler/magnet;application/x-bittorrent;\n", text, StringComparison.Ordinal);
        Assert.Contains("NoDisplay=true\n", text, StringComparison.Ordinal);
        var call = Assert.Single(_calls);
        Assert.Equal(["default", "inbound-torrent.desktop", "x-scheme-handler/magnet", "application/x-bittorrent"], call);

        // Again at the next start: nothing changes, and a default the user gave another app since stays.
        Assert.False(Handler().Register());
        Assert.Single(_calls);
    }

    [Fact]
    public void A_moved_appimage_rewrites_the_entry_without_taking_the_defaults_again()
    {
        Assert.True(Handler().Register());
        Assert.True(Handler("/home/me/Downloads/Inbound-0.6.7-x86_64.AppImage").Register());
        Assert.Contains("Exec=\"/home/me/Downloads/Inbound-0.6.7-x86_64.AppImage\" --open-torrent %u",
            File.ReadAllText(Path.Combine(Applications, LinuxTorrentHandler.DesktopId)), StringComparison.Ordinal);
        Assert.Single(_calls);
    }

    [Fact]
    public void Exec_quotes_a_path_with_spaces_and_dollars()
    {
        var text = Handler("/home/me/My $Apps/inbound").DesktopFileText();
        Assert.Contains("Exec=\"/home/me/My \\\\$Apps/inbound\" --open-torrent %u", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_takes_out_only_our_entry_and_ids()
    {
        Assert.True(Handler().Register());
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigList)!);
        File.WriteAllText(ConfigList, string.Join('\n',
            "[Default Applications]",
            "x-scheme-handler/magnet=inbound-torrent.desktop;",
            "application/x-bittorrent=org.qbittorrent.qBittorrent.desktop;inbound-torrent.desktop;",
            "text/html=firefox.desktop;",
            "",
            "[Added Associations]",
            "x-scheme-handler/magnet=inbound-torrent.desktop;transmission-gtk.desktop;",
            "# x-scheme-handler/magnet=inbound-torrent.desktop;") + "\n");
        File.WriteAllText(OldList, "[Default Applications]\nx-scheme-handler/magnet=transmission-gtk.desktop;\n");

        Assert.True(Handler().Remove());

        Assert.False(File.Exists(Path.Combine(Applications, LinuxTorrentHandler.DesktopId)));
        Assert.Equal(string.Join('\n',
            "[Default Applications]",
            "application/x-bittorrent=org.qbittorrent.qBittorrent.desktop;",
            "text/html=firefox.desktop;",
            "",
            "[Added Associations]",
            "x-scheme-handler/magnet=transmission-gtk.desktop;",
            "# x-scheme-handler/magnet=inbound-torrent.desktop;") + "\n", File.ReadAllText(ConfigList));
        // Another app's default is left as it was.
        Assert.Equal("[Default Applications]\nx-scheme-handler/magnet=transmission-gtk.desktop;\n", File.ReadAllText(OldList));
        Assert.False(Handler().Remove());
    }

    [Fact]
    public void Remove_with_nothing_there_changes_nothing()
    {
        Assert.False(Handler().Remove());
        Assert.False(Directory.Exists(_root));
        Assert.Empty(_calls);
    }

    [Fact]
    public void Remove_leaves_a_file_of_that_name_that_isnt_ours()
    {
        Directory.CreateDirectory(Applications);
        var path = Path.Combine(Applications, LinuxTorrentHandler.DesktopId);
        File.WriteAllText(path, "[Desktop Entry]\nExec=something-else %u\n");
        Assert.False(Handler().Remove());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void The_folders_follow_the_xdg_variables()
    {
        var home = "/home/me";
        Assert.EndsWith(Path.Combine(".local", "share", "applications"), LinuxTorrentHandler.ApplicationsDirectory(home, null), StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine(".local", "share", "applications"), LinuxTorrentHandler.ApplicationsDirectory(home, "relative"), StringComparison.Ordinal);
        Assert.StartsWith("/data", LinuxTorrentHandler.ApplicationsDirectory(home, "/data"), StringComparison.Ordinal);
        var lists = LinuxTorrentHandler.MimeAppsLists(home, null, "/home/me/.local/share/applications");
        Assert.EndsWith(Path.Combine(".config", "mimeapps.list"), lists[0], StringComparison.Ordinal);
        Assert.StartsWith("/home/me/.local/share/applications", lists[1], StringComparison.Ordinal);
        Assert.StartsWith("/cfg", LinuxTorrentHandler.MimeAppsLists(home, "/cfg", "/a")[0], StringComparison.Ordinal);
    }
}
