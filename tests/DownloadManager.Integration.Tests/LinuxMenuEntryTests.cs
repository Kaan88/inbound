using DownloadManager.Host;
using Xunit;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// X.7: the AppImage's application-menu entry. Every folder is a throwaway one under the temp folder and
/// update-desktop-database is recorded, not run. Nothing here ran on a real Linux desktop menu.
/// </summary>
public sealed class LinuxMenuEntryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inbound-linux-menu-" + Guid.NewGuid().ToString("N"));
    private readonly List<IReadOnlyList<string>> _calls = [];

    private string Applications => Path.Combine(_root, "share", "applications");
    private string IconDirectory => Path.Combine(_root, "share", "inbound");
    private string DesktopFile => Path.Combine(Applications, "inbound.desktop");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private string MakeFile(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private LinuxMenuEntry Entry(string? appImage, string? icon = null, List<string>? log = null) =>
        new(Applications, IconDirectory, appImage, icon, arguments =>
        {
            _calls.Add(arguments);
            return true;
        }, log is null ? null : log.Add);

    [Fact]
    public void Writes_the_entry_from_an_AppImage()
    {
        var appImage = MakeFile("apps/Inbound.AppImage");
        Assert.True(Entry(appImage).Install());
        var text = File.ReadAllText(DesktopFile);
        Assert.Contains("[Desktop Entry]\n", text, StringComparison.Ordinal);
        Assert.Contains("Type=Application\n", text, StringComparison.Ordinal);
        Assert.Contains("Name=Inbound Download Manager\n", text, StringComparison.Ordinal);
        Assert.Contains("Exec=\"" + appImage + "\" %U\n", text, StringComparison.Ordinal);
        Assert.Contains("Terminal=false\n", text, StringComparison.Ordinal);
        Assert.Contains("Categories=Network;FileTransfer;\n", text, StringComparison.Ordinal);
        Assert.Contains("StartupWMClass=DownloadManager.Desktop.Uno\n", text, StringComparison.Ordinal);
        Assert.Contains("X-Inbound-Managed=true\n", text, StringComparison.Ordinal);
        Assert.Single(_calls);
        Assert.Equal([Applications], _calls[0]);
    }

    [Fact]
    public void Does_nothing_without_an_AppImage()
    {
        Assert.False(Entry(null).Install());
        Assert.False(Entry(Path.Combine(_root, "missing.AppImage")).Install());
        Assert.False(Directory.Exists(Applications));
        Assert.Empty(_calls);
    }

    [Fact]
    public void Rewrites_when_the_AppImage_path_changes()
    {
        var first = MakeFile("apps/Inbound-1.AppImage");
        var second = MakeFile("apps/Inbound-2.AppImage");
        Assert.True(Entry(first).Install());
        Assert.True(Entry(second).Install());
        Assert.Contains(second, File.ReadAllText(DesktopFile), StringComparison.Ordinal);
        Assert.DoesNotContain(first, File.ReadAllText(DesktopFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Does_not_rewrite_when_unchanged()
    {
        var appImage = MakeFile("apps/Inbound.AppImage");
        Assert.True(Entry(appImage).Install());
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(DesktopFile, old);
        _calls.Clear();
        Assert.False(Entry(appImage).Install());
        Assert.Equal(old, File.GetLastWriteTimeUtc(DesktopFile));
        Assert.Empty(_calls);
    }

    [Fact]
    public void Leaves_a_foreign_entry_alone_and_says_so_once()
    {
        var appImage = MakeFile("apps/Inbound.AppImage");
        const string Foreign = "[Desktop Entry]\nType=Application\nName=Mine\nExec=other\n";
        MakeFile("share/applications/inbound.desktop", Foreign);
        var log = new List<string>();
        Assert.False(Entry(appImage, log: log).Install());
        Assert.Equal(Foreign, File.ReadAllText(DesktopFile));
        Assert.Single(log);
        Assert.False(Entry(appImage, log: log).Remove());
        Assert.Equal(Foreign, File.ReadAllText(DesktopFile));
        Assert.Empty(_calls);
    }

    [Fact]
    public void Quotes_a_path_with_spaces()
    {
        var appImage = MakeFile("my apps/Inbound $1.AppImage");
        Entry(appImage).Install();
        Assert.Contains("Exec=\"" + appImage.Replace("$", "\\\\$", StringComparison.Ordinal) + "\" %U\n",
            File.ReadAllText(DesktopFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Copies_the_icon_when_the_source_exists()
    {
        var appImage = MakeFile("apps/Inbound.AppImage");
        var icon = MakeFile("appdir/inbound.png", "png-bytes");
        Assert.True(Entry(appImage, icon).Install());
        var installed = Path.Combine(IconDirectory, "inbound.png");
        Assert.Equal("png-bytes", File.ReadAllText(installed));
        Assert.Contains("Icon=" + installed + "\n", File.ReadAllText(DesktopFile), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(IconDirectory, "*.tmp"));
    }

    [Fact]
    public void Falls_back_to_the_themed_icon_without_the_png()
    {
        var appImage = MakeFile("apps/Inbound.AppImage");
        Assert.True(Entry(appImage, Path.Combine(_root, "appdir", "inbound.png")).Install());
        Assert.Contains("Icon=folder-download\n", File.ReadAllText(DesktopFile), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(IconDirectory, "inbound.png")));
    }

    [Fact]
    public void Remove_deletes_only_a_marked_entry_and_the_icon()
    {
        var appImage = MakeFile("apps/Inbound.AppImage");
        var icon = MakeFile("appdir/inbound.png");
        var entry = Entry(appImage, icon);
        entry.Install();
        Assert.True(entry.Remove());
        Assert.False(File.Exists(DesktopFile));
        Assert.False(File.Exists(Path.Combine(IconDirectory, "inbound.png")));
        Assert.False(entry.Remove());
    }
}
