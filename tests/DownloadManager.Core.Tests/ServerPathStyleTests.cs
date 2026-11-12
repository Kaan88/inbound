using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>
/// UI.B17: while the window shows another server, a folder is checked, and its hint written, by that server's path
/// rules; this PC keeps Windows' rules.
/// </summary>
public sealed class ServerPathStyleTests
{
    [Theory]
    [InlineData(HostKinds.Server, null, nameof(PathStyle.Linux))]
    [InlineData(HostKinds.Windows, null, nameof(PathStyle.Windows))]
    [InlineData(null, null, nameof(PathStyle.Either))]
    [InlineData("other", "  ", nameof(PathStyle.Either))]
    // The server's own default folder settles it, whatever its kind says.
    [InlineData(HostKinds.Server, @"D:\Downloads", nameof(PathStyle.Windows))]
    [InlineData(HostKinds.Windows, "/downloads", nameof(PathStyle.Linux))]
    [InlineData(null, "/srv/dl", nameof(PathStyle.Linux))]
    [InlineData(null, "E:/dl", nameof(PathStyle.Windows))]
    // A folder that is no full path says nothing.
    [InlineData(HostKinds.Server, "downloads", nameof(PathStyle.Linux))]
    [InlineData(null, @"\\nas\share", nameof(PathStyle.Either))]
    public void AServersStyleComesFromItsDefaultFolderThenItsKind(string? hostKind, string? defaultFolder, string expected)
        => Assert.Equal(Enum.Parse<PathStyle>(expected), ServerPaths.StyleFor(hostKind, defaultFolder));

    [Theory]
    [InlineData("/downloads/torbox", nameof(PathStyle.Linux), true)]
    [InlineData("/", nameof(PathStyle.Linux), true)]
    [InlineData(" /downloads ", nameof(PathStyle.Linux), true)]
    [InlineData(@"D:\Downloads", nameof(PathStyle.Linux), false)]
    [InlineData("//nas/share", nameof(PathStyle.Linux), false)]
    [InlineData("downloads", nameof(PathStyle.Linux), false)]
    [InlineData(@"D:\Downloads", nameof(PathStyle.Windows), true)]
    [InlineData("D:/Downloads", nameof(PathStyle.Windows), true)]
    [InlineData("/downloads", nameof(PathStyle.Windows), false)]
    [InlineData(@"\\nas\share", nameof(PathStyle.Windows), false)]
    [InlineData("D:", nameof(PathStyle.Windows), false)]
    [InlineData("/downloads", nameof(PathStyle.Either), true)]
    [InlineData(@"D:\Downloads", nameof(PathStyle.Either), true)]
    [InlineData("//nas/share", nameof(PathStyle.Either), false)]
    [InlineData("", nameof(PathStyle.Either), false)]
    public void AServersFolderIsFullByItsOwnRules(string path, string style, bool full)
        => Assert.Equal(full, ServerPaths.IsFull(path, Enum.Parse<PathStyle>(style)));

    [Theory]
    [Trait("Platform", "Windows")] // this PC's rules are Windows' own
    [InlineData(@"D:\Downloads", true)]
    [InlineData(@"\\nas\share\dl", true)]
    [InlineData("/downloads", false)]
    [InlineData("downloads", false)]
    public void ThisPcKeepsWindowsRules(string path, bool full)
        => Assert.Equal(full, ServerPaths.IsFull(path, PathStyle.ThisPc));

    [Fact]
    public void AHintNeverOffersALinuxServerADriveLetter()
    {
        var linux = ServerPaths.FullFolderWords(PathStyle.Linux);
        Assert.Equal("a full folder path on the server, such as /downloads", linux);
        Assert.DoesNotContain(":\\", linux);

        Assert.Equal(@"a full folder path on the server, such as D:\Downloads", ServerPaths.FullFolderWords(PathStyle.Windows));
        Assert.Equal(@"a full folder path on the server, such as /downloads or D:\Downloads", ServerPaths.FullFolderWords(PathStyle.Either));
        Assert.Equal(@"a full folder path, such as D:\Downloads", ServerPaths.FullFolderWords(PathStyle.ThisPc));
    }

    /// <summary>
    /// T11: a progress window in the main window's process says where its own download is; the shown server, the main
    /// window's, stays as it was. In this class so no other test changes the shown server meanwhile.
    /// </summary>
    [Fact]
    public void AProgressWindowForThisPcLeavesTheShownServerAtTheMainWindowsServer()
    {
        var before = ShownServer.Target;
        try
        {
            var server = ServerTarget.Remote("0123456789abcdef0123456789abcdef", "minipc", new Uri("http://minipc:8080/"), "token", false);
            ShownServer.Target = server;
            ShownServer.Describe(HostKinds.Server, null);

            // This PC's progress window reads this PC's file icons; another server's window doesn't.
            Assert.False(ShownServer.IsRemoteFor(ownRemote: false));
            Assert.True(ShownServer.IsRemoteFor(ownRemote: true));
            // The main window's own rows still follow the shown server.
            Assert.True(ShownServer.IsRemoteFor(null));

            Assert.Same(server, ShownServer.Target);
            Assert.Equal(PathStyle.Linux, ShownServer.PathStyle);

            ShownServer.Target = ServerTarget.Local;
            Assert.True(ShownServer.IsRemoteFor(ownRemote: true));
            Assert.False(ShownServer.IsRemoteFor(null));
        }
        finally
        {
            ShownServer.Target = before;
        }
    }

    [Fact]
    public void TheShownServerForgetsTheLastOnesPathsWhenAnotherIsShown()
    {
        var before = ShownServer.Target;
        try
        {
            var linux = ServerTarget.Remote("0123456789abcdef0123456789abcdef", "minipc", new Uri("http://minipc:8080/"), "token", false);
            ShownServer.Target = linux;
            Assert.Equal(PathStyle.Either, ShownServer.PathStyle);
            ShownServer.Describe(HostKinds.Server, null);
            Assert.Equal(PathStyle.Linux, ShownServer.PathStyle);
            // A null or empty value keeps what was known.
            ShownServer.Describe(null, "");
            Assert.Equal(PathStyle.Linux, ShownServer.PathStyle);
            ShownServer.Describe(null, @"D:\Downloads");
            Assert.Equal(PathStyle.Windows, ShownServer.PathStyle);

            ShownServer.Target = ServerTarget.Local;
            Assert.Equal(PathStyle.ThisPc, ShownServer.PathStyle);
            ShownServer.Target = linux;
            Assert.Equal(PathStyle.Either, ShownServer.PathStyle);
        }
        finally
        {
            ShownServer.Target = before;
        }
    }

    [Fact]
    public void ALinuxServersExtractionFolderIsTaken()
    {
        var saved = new ArchiveSettingsDto { Destination = "nextToArchive" };
        var form = ArchiveForm.FromSettings(saved) with { DestinationIndex = ArchiveForm.DestinationFolderIndex, Folder = "/downloads/extracted" };

        Assert.NotNull(ArchiveForm.Changes(saved, form, out var error, PathStyle.Linux));
        Assert.Null(error);

        Assert.Null(ArchiveForm.Changes(saved, form with { Folder = @"D:\Out" }, out var windows, PathStyle.Linux));
        Assert.Equal("The extraction folder must be a full folder path on the server, such as /downloads.", windows);
    }

    [Fact]
    public void ALinuxServersArchiveGetsAPasswordByItsPath()
    {
        var file = new ArchiveSelectedFile(Guid.NewGuid(), "movie.rar", "/downloads/movie.rar", null);

        Assert.Equal("/downloads/movie.rar", ArchiveRules.Plan([file], [], null, true, PathStyle.Linux).Password?.FilePath);
        Assert.Null(ArchiveRules.Plan([file], [], null, true, PathStyle.Windows).Password);
    }

    [Theory]
    [InlineData(nameof(PathStyle.Linux), "/opt/tools/done.sh", true)]
    [InlineData(nameof(PathStyle.Linux), @"C:\Tools\done.exe", false)]
    [InlineData(nameof(PathStyle.Windows), @"C:\Tools\done.exe", true)]
    [InlineData(nameof(PathStyle.Windows), "/opt/tools/done.sh", false)]
    [InlineData(nameof(PathStyle.Either), "/opt/tools/done.sh", true)]
    [InlineData(nameof(PathStyle.Either), @"C:\Tools\done.exe", true)]
    public void AServersProgramIsCheckedByItsStyle(string style, string path, bool accepted)
        => Assert.Equal(accepted, RemoteDataRules.ServerProgramPathProblem(path, Enum.Parse<PathStyle>(style)) is null);

    [Fact]
    public void AProgramHintIsInTheServersStyle()
    {
        Assert.Contains("/opt/tools/done.sh", RemoteDataRules.ServerProgramPathProblem("done", PathStyle.Linux));
        Assert.Contains(@"C:\Tools\done.cmd", RemoteDataRules.ServerProgramPathProblem("done", PathStyle.Windows));
    }
}
