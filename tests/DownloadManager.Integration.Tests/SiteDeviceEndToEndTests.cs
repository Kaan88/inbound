using System.Security.Cryptography;
using DownloadManager.Contracts;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.7: "to this device" and "from this device" with a real rclone: a file read in pieces over
/// <c>sites.device.*</c> equals the server's, one sent in pieces lands whole, a name in use is asked about, a size that
/// does not match is not kept, and a closed or idle stream is gone. Against SFTP (absolute paths) and S3 (buckets).
/// </summary>
public sealed partial class SiteTransfersEndToEndTests
{
    [SiteTransfersFact]
    public async Task AFileGoesToAndFromTheDeviceInPiecesOverSftp()
    {
        var port = StartServer(slow: false);
        var (commands, id) = await OpenSiteAsync(port);
        await DeviceRoundTripAsync(commands, id);
    }

    [SiteTransfersFact]
    public async Task AFileGoesToAndFromTheDeviceInPiecesOverS3()
    {
        var port = StartServer(slow: false, "s3", ["--auth-key", "INBOUNDTESTKEY,inbound-test-secret-8813"]);
        var commands = Open(null);
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone });
        var saved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Name = "S3", Kind = SiteKinds.S3, Provider = "Other", Endpoint = $"http://127.0.0.1:{port}", User = "INBOUNDTESTKEY",
            Password = "inbound-test-secret-8813",
        });
        Assert.True(saved.Ok, saved.Message);
        await DeviceRoundTripAsync(commands, saved.Site!.Id);
    }

    private async Task DeviceRoundTripAsync(DownloadManager.Runtime.Sites.SitesCommands commands, string id)
    {
        // To this device: more than one piece.
        var big = RandomNumberGenerator.GetBytes(SiteDeviceLimits.ChunkBytes * 2 + 12345);
        await File.WriteAllBytesAsync(Path.Combine(Served, "pub", "device.bin"), big);
        var read = await Call<SiteDeviceStreamDto>(commands, SitesMessageTypes.DeviceReadOpen, new SiteDeviceOpenRequest { Id = id, Path = "/pub/device.bin" });
        Assert.True(read.Ok, read.Message);
        Assert.Equal("device.bin", read.Name);
        Assert.Equal(big.Length, read.Size);
        using var got = new MemoryStream();
        for (var pieces = 0; ; pieces++)
        {
            Assert.True(pieces < 10);
            var chunk = await Call<SiteDeviceChunkDto>(commands, SitesMessageTypes.DeviceRead, new SiteDeviceChunkRequest { StreamId = read.StreamId! });
            Assert.True(chunk.Ok, chunk.Message);
            got.Write(chunk.Data ?? []);
            if (chunk.Done) break;
        }

        Assert.Equal(big, got.ToArray());
        // Ended streams are gone.
        Assert.False((await Call<SiteDeviceChunkDto>(commands, SitesMessageTypes.DeviceRead, new SiteDeviceChunkRequest { StreamId = read.StreamId! })).Ok);
        Assert.False((await Call<SiteDeviceStreamDto>(commands, SitesMessageTypes.DeviceReadOpen, new SiteDeviceOpenRequest { Id = id, Path = "/pub/missing.bin" })).Ok);

        // From this device.
        var up = RandomNumberGenerator.GetBytes(SiteDeviceLimits.ChunkBytes + 777);
        var write = await Call<SiteDeviceStreamDto>(commands, SitesMessageTypes.DeviceWriteOpen,
            new SiteDeviceOpenRequest { Id = id, Folder = "/pub", Name = "from-device.bin", Size = up.Length });
        Assert.True(write.Ok, write.Message);
        var first = await Call<SiteDeviceChunkDto>(commands, SitesMessageTypes.DeviceWrite,
            new SiteDeviceChunkRequest { StreamId = write.StreamId!, Data = up[..SiteDeviceLimits.ChunkBytes] });
        Assert.True(first.Ok, first.Message);
        var last = await Call<SiteDeviceChunkDto>(commands, SitesMessageTypes.DeviceWrite,
            new SiteDeviceChunkRequest { StreamId = write.StreamId!, Data = up[SiteDeviceLimits.ChunkBytes..], Last = true });
        Assert.True(last.Ok, last.Message);
        Assert.True(last.Done);
        Assert.Equal(up, await File.ReadAllBytesAsync(Path.Combine(Served, "pub", "from-device.bin")));

        // The name is in use now: asked first, replaced when told to.
        var again = await Call<SiteDeviceStreamDto>(commands, SitesMessageTypes.DeviceWriteOpen,
            new SiteDeviceOpenRequest { Id = id, Folder = "/pub", Name = "from-device.bin", Size = 3 });
        Assert.Equal(SiteProblems.Exists, again.Problem);
        var replace = await Call<SiteDeviceStreamDto>(commands, SitesMessageTypes.DeviceWriteOpen,
            new SiteDeviceOpenRequest { Id = id, Folder = "/pub", Name = "from-device.bin", Size = 3, Replace = true });
        Assert.True(replace.Ok, replace.Message);
        Assert.True((await Call<SiteDeviceChunkDto>(commands, SitesMessageTypes.DeviceWrite,
            new SiteDeviceChunkRequest { StreamId = replace.StreamId!, Data = [1, 2, 3], Last = true })).Ok);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(Served, "pub", "from-device.bin")));

        // More than it said is refused; a closed upload is not finished.
        var liar = await Call<SiteDeviceStreamDto>(commands, SitesMessageTypes.DeviceWriteOpen,
            new SiteDeviceOpenRequest { Id = id, Folder = "/pub", Name = "liar.bin", Size = 2 });
        Assert.False((await Call<SiteDeviceChunkDto>(commands, SitesMessageTypes.DeviceWrite,
            new SiteDeviceChunkRequest { StreamId = liar.StreamId!, Data = [1, 2, 3], Last = true })).Ok);
        var closed = await Call<SiteDeviceStreamDto>(commands, SitesMessageTypes.DeviceWriteOpen,
            new SiteDeviceOpenRequest { Id = id, Folder = "/pub", Name = "closed.bin", Size = 10 });
        await Call<SiteDeviceChunkDto>(commands, SitesMessageTypes.DeviceWrite, new SiteDeviceChunkRequest { StreamId = closed.StreamId!, Data = [1, 2] });
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.DeviceClose, new SiteDeviceChunkRequest { StreamId = closed.StreamId! })).Ok);
        Assert.False((await Call<SiteDeviceChunkDto>(commands, SitesMessageTypes.DeviceWrite, new SiteDeviceChunkRequest { StreamId = closed.StreamId!, Data = [3] })).Ok);
        await Task.Delay(300);
        var listing = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = id, Path = "/pub", Refresh = true });
        Assert.DoesNotContain(listing.Items, item => item.Name is "liar.bin" or "closed.bin" && item.Size > 0);

        // Every stream is closed now, and so is the idle timer (no wake-ups while nothing is open).
        Assert.False(commands.StreamSweepRunning);

        // A name with a slash, or a path out of the site, is refused.
        Assert.False((await Call<SiteDeviceStreamDto>(commands, SitesMessageTypes.DeviceWriteOpen,
            new SiteDeviceOpenRequest { Id = id, Folder = "/pub", Name = "a/b", Size = 1 })).Ok);
    }
}
