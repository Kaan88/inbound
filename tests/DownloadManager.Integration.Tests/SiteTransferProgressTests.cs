using System.Text.Json;
using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Runtime.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>SITES.11 R1: per-file progress from rclone's stats and the one time-left rule.</summary>
public sealed class SiteTransferProgressTests
{
    [Theory]
    [InlineData(1000, 0, 100, 10L)]
    [InlineData(1000, 950, 100, 1L)]      // 50 bytes left still takes a (rounded-up) second
    [InlineData(1000, 1000, 100, 0L)]
    [InlineData(1000, 1200, 100, 0L)]     // more reported than listed: nothing left
    [InlineData(1000, -5, 100, 10L)]      // a negative count is read as none done
    public void SecondsLeftRoundsUp(long size, long done, long speed, long expected) =>
        Assert.Equal(expected, SiteTransferTime.SecondsLeft(size, done, speed));

    [Theory]
    [InlineData(1000, 0, 0)]
    [InlineData(1000, 0, -1)]
    [InlineData(-1, 0, 100)]              // unknown size
    public void SecondsLeftIsUnknownWithoutSpeedOrSize(long size, long done, long speed) =>
        Assert.Null(SiteTransferTime.SecondsLeft(size, done, speed));

    [Fact]
    public void ReadsTheTransferringList()
    {
        var stats = JsonNode.Parse("""
            {"transferring":[
              {"name":"media/isos/a.iso","bytes":500,"speed":123.9,"size":1000},
              {"name":"b.bin","bytes":-3,"speed":-1},
              "not an object",
              {"bytes":7}
            ]}
            """)!.AsObject();
        var files = SiteTransfers.ReadTransferring(stats);
        Assert.Equal(3, files.Count);
        Assert.Equal(("media/isos/a.iso", 500L, 123L), files[0]);
        Assert.Equal(("b.bin", 0L, 0L), files[1]);
        Assert.Equal(("", 7L, 0L), files[2]);
        Assert.Empty(SiteTransfers.ReadTransferring(new JsonObject()));
    }

    [Fact]
    public void MatchesByTheSourceOnly()
    {
        // rclone 1.76 names a moving file by its srcRemote, whatever its dstRemote: a download of media/isos/a.iso
        // into a.iso is reported as media/isos/a.iso.
        var match = SiteTransfers.MatchTransferring(["media/isos/a.iso", "b.bin"], ["b.bin", "media/isos/a.iso"]);
        Assert.Equal([1, 0], match);
        // A target name is not a match, and nothing is paired by elimination.
        Assert.Equal([-1], SiteTransfers.MatchTransferring(["x/a"], ["a"]));
        Assert.Equal([0, -1], SiteTransfers.MatchTransferring(["x/a", "x/b"], ["x/a", "/root/x/b"]));
    }

    [Fact]
    public void AFileNotYetCopyingShowsNoProgress()
    {
        Assert.Equal([0, -1], SiteTransfers.MatchTransferring(["x/a", null], ["x/a"]));
        Assert.Equal([-1], SiteTransfers.MatchTransferring(["x/a"], []));
        // Two copies of one name (two batches never share a group, but a name is used once all the same).
        Assert.Equal([0, -1], SiteTransfers.MatchTransferring(["x/a", "x/a"], ["x/a"]));
    }

    [Fact]
    public void ThePickedNamesComeFromTheFilesFirstSegment()
    {
        static DownloadManager.Sites.SiteTransferItem Item(string path) => new() { Path = path };
        var (first, count) = SiteTransfers.PickedNames([Item("isos/a.iso"), Item("isos/sub/b.iso"), Item("notes.txt"), Item("empty"), Item("more/c")]);
        Assert.Equal(["isos", "notes.txt", "empty"], first);
        Assert.Equal(4, count);
        var none = SiteTransfers.PickedNames([]);
        Assert.Empty(none.First);
        Assert.Equal(0, none.Count);
    }

    [Fact]
    public void AFilesFailureSaysWhoseItIsAndWhetherItIsTriedAgain()
    {
        static DownloadManager.Sites.SiteTransferRecord Batch(string direction) => new()
        {
            Direction = direction, SiteName = "nas.home.lan", RemoteFolder = "/media/isos", LocalFolder = @"C:\In",
        };
        var item = new DownloadManager.Sites.SiteTransferItem { Path = "a.iso" };
        var upload = Batch(DownloadManager.Sites.SiteTransferDirections.Upload);

        // Captured from rclone 1.76.0 against rclone's read-only FTP server: the server's 4xx words, and the usual tries
        // (FTP's 4xx is "not now"); not "no longer on this PC", though the words say "file does not exist".
        var (error, retry) = SiteTransfers.FileFailure(
            new DownloadManager.Sites.RcloneCallException("operations/copyfile", 500, "update stor: 450 \"error during transfer: file does not exist\""), upload, item);
        Assert.Equal("nas.home.lan did not take the file in /media/isos: error during transfer: file does not exist", error);
        Assert.True(retry);

        // A source deleted on this PC meanwhile: no longer on this PC, not tried again.
        (error, retry) = SiteTransfers.FileFailure(
            new DownloadManager.Sites.RcloneCallException("operations/copyfile", 500, "object not found"), upload, item);
        Assert.Equal("The file is no longer on this PC.", error);
        Assert.False(retry);

        // A write the server refuses: said so, not tried again.
        (error, retry) = SiteTransfers.FileFailure(
            new DownloadManager.Sites.RcloneCallException("operations/copyfile", 500, "Update Create failed: sftp: \"Permission denied\" (SSH_FX_PERMISSION_DENIED)"), upload, item);
        Assert.Equal("nas.home.lan refused the write to /media/isos. Check that this sign-in may write there.", error);
        Assert.False(retry);

        // A file another program holds open: the usual tries.
        (_, retry) = SiteTransfers.FileFailure(new DownloadManager.Sites.RcloneCallException("operations/copyfile", 500,
            "failed to open source object: open C:\\In\\a.iso: The process cannot access the file because it is being used by another process."), upload, item);
        Assert.True(retry);

        // A download whose file left the server.
        (error, retry) = SiteTransfers.FileFailure(
            new DownloadManager.Sites.RcloneCallException("operations/copyfile", 500, "object not found"), Batch(DownloadManager.Sites.SiteTransferDirections.Download), item);
        Assert.Equal("The file is no longer on the server.", error);
        Assert.False(retry);
    }

    [Fact]
    public void TheNewFieldsTravelInTheContract()
    {
        var dto = new SiteTransferDto
        {
            Id = "t",
            SecondsLeft = 12,
            Moving = [new SiteTransferMovingDto { Path = "a.iso", Size = 10, BytesDone = 4, Speed = 2, SecondsLeft = 3 }],
            Picked = ["isos"],
            PickedCount = 1,
            FilesStopped = 2,
            FirstFailedPath = "isos/b.iso",
            FirstFailedError = "No space left.",
            RetryAt = DateTimeOffset.UnixEpoch,
            RetryReason = "The server can't be reached.",
        };
        var json = JsonSerializer.Serialize(dto, IpcJsonContext.Default.SiteTransferDto);
        var back = JsonSerializer.Deserialize(json, IpcJsonContext.Default.SiteTransferDto)!;
        Assert.Equal(12, back.SecondsLeft);
        var file = Assert.Single(back.Moving);
        Assert.Equal(("a.iso", 10L, 4L, 2L, 3L), (file.Path, file.Size, file.BytesDone, file.Speed, file.SecondsLeft!.Value));
        Assert.Equal(["isos"], back.Picked);
        Assert.Equal((1, 2), (back.PickedCount, back.FilesStopped));
        Assert.Equal(("isos/b.iso", "No space left."), (back.FirstFailedPath, back.FirstFailedError));
        Assert.Equal((DateTimeOffset.UnixEpoch, "The server can't be reached."), (back.RetryAt!.Value, back.RetryReason));
        var file2 = JsonSerializer.Deserialize(JsonSerializer.Serialize(new SiteTransferFileRequest { Id = "t", Path = "a" }, IpcJsonContext.Default.SiteTransferFileRequest),
            IpcJsonContext.Default.SiteTransferFileRequest)!;
        Assert.Equal(("t", "a"), (file2.Id, file2.Path));

        // An older Host sends neither: the fields read as "none".
        var old = JsonSerializer.Deserialize("""{"id":"t","state":"running"}""", IpcJsonContext.Default.SiteTransferDto)!;
        Assert.Null(old.SecondsLeft);
        Assert.Empty(old.Moving);
        Assert.Empty(old.Picked);
        Assert.Equal(0, old.PickedCount);
        Assert.Equal(0, old.FilesStopped);
        Assert.Null(old.RetryAt);
        Assert.Null(old.FirstFailedPath);
    }
}
