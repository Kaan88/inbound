using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>
/// U-WR-10 and the UI.B16 review: what the server folder chooser lists and says. U-WR-12: what a refused Move/Rename
/// on a server says.
/// </summary>
public sealed class ServerFolderListingTests
{
    [Fact]
    public void RootsAreListedWithTheirWholePaths()
    {
        var view = ServerFolderListing.For(new FolderBrowseResult { Ok = true, Roots = [@"D:\srv-dl", @"D:\srv-dl2"] });

        Assert.Equal([@"D:\srv-dl", @"D:\srv-dl2"], view.Rows.Select(row => row.Name));
        Assert.Equal([@"D:\srv-dl", @"D:\srv-dl2"], view.Rows.Select(row => row.Path));
        Assert.Null(view.Empty);
        Assert.False(view.CanGoUp);
        Assert.False(view.CanCreate);
    }

    [Fact]
    public void AFoldersContentsAreListedByNameAndOpenedByPath()
    {
        var view = ServerFolderListing.For(new FolderBrowseResult
        {
            Ok = true,
            Path = "/downloads",
            Parent = null,
            Roots = ["/downloads"],
            Folders = [new FolderEntryDto { Name = "films", Path = "/downloads/films" }],
        });

        Assert.Equal("films", Assert.Single(view.Rows).Name);
        Assert.Equal("/downloads/films", view.Rows[0].Path);
        Assert.True(view.CanGoUp);
        Assert.True(view.CanCreate);
        Assert.Null(view.Message);
    }

    [Fact]
    public void AnEmptyFolderSaysSo()
    {
        var view = ServerFolderListing.For(new FolderBrowseResult { Ok = true, Path = "/downloads/empty", Roots = ["/downloads"] });

        Assert.Empty(view.Rows);
        Assert.Equal(ServerFolderListing.EmptyFolder, view.Empty);
        Assert.Equal(ServerFolderListing.NoRoots, ServerFolderListing.For(new FolderBrowseResult { Ok = true }).Empty);
    }

    [Fact]
    public void ALongListIsANoticeNotAnError()
    {
        var view = ServerFolderListing.For(new FolderBrowseResult { Ok = true, Path = "/big", Truncated = true });

        Assert.Equal(ServerFolderListing.TruncatedNotice, view.Message?.Text);
        Assert.False(view.Message!.IsError);
        Assert.Equal(ServerFolderListing.NoticeBrush, view.Message.Brush);
    }

    [Fact]
    public void OnlyAProblemIsRed()
    {
        var refused = ServerFolderListing.Refusal(new FolderBrowseResult { Error = "That folder is outside the folders this server may use." });
        Assert.True(refused.IsError);
        Assert.Equal(ServerFolderListing.ErrorBrush, refused.Brush);
        Assert.Equal(ServerFolderListing.CantOpen, ServerFolderListing.Refusal(new FolderBrowseResult { Error = " " }).Text);

        Assert.Equal(ServerFolderListing.ErrorBrush, ServerFolderListing.NothingChosen.Brush);
        Assert.Equal(ServerFolderListing.NoticeBrush, ServerFolderListing.NewFolderNeedsAFolder.Brush);
        Assert.NotEqual(ServerFolderListing.ErrorBrush, ServerFolderListing.NoticeBrush);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("/downloads", true)]
    public void ChoosingNeedsAFolder(string? typed, bool expected) => Assert.Equal(expected, ServerFolderListing.CanChoose(typed));

    [Fact]
    public void AMoveOutsideTheRootsListsTheServersFolders()
    {
        // Told by its code, so rewording the server's sentence can't lose the list.
        var refused = new JobActionResponse { Ok = false, Error = "Any words at all.", ErrorCode = JobActionErrorCodes.OutsideRoots };

        Assert.True(MoveRefusal.IsOutsideRoots(refused));
        var text = MoveRefusal.Text(refused.Error, ["/downloads", "/media"], "NAS");

        Assert.StartsWith(refused.Error, text);
        Assert.Contains("The folders NAS may use:", text);
        Assert.Contains("/downloads", text);
        Assert.Contains("/media", text);
    }

    [Fact]
    public void AnyOtherRefusalIsShownAsItIs()
    {
        // Without the code, even the server's own outside-roots words (an older server) get no list.
        Assert.False(MoveRefusal.IsOutsideRoots(new JobActionResponse { Ok = false, Error = "That folder is outside the folders this server may use." }));
        Assert.False(MoveRefusal.IsOutsideRoots(new JobActionResponse { Ok = false, Error = "A file with that name already exists." }));
        Assert.False(MoveRefusal.IsOutsideRoots(new JobActionResponse { Ok = true, ErrorCode = JobActionErrorCodes.OutsideRoots }));
        Assert.False(MoveRefusal.IsOutsideRoots(null));
        Assert.Equal("Disk full.", MoveRefusal.Text(" Disk full. ", null, "NAS"));
        Assert.Equal("NAS refused the move.", MoveRefusal.Text(null, [], "NAS"));
    }
}
