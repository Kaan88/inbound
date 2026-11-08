using System.Net;
using DownloadManager.Contracts;

namespace DownloadManager.Server.Tests;

/// <summary>
/// <c>POST /api/upload/container</c>: a container from the web UI's Add menu, kept beside the torrent uploads and named
/// only by its reference, which <c>remote.container.preview</c> takes. The roots don't hold the uploads folder.
/// </summary>
public sealed class ContainerUploadTests
{
    [Fact]
    public async Task AContainerUploadNeedsASessionKeepsItsKindAndIsPreviewedByItsReference()
    {
        await using var server = await AuthServer.StartAsync();
        using var anonymous = new AuthClient(server.Base);
        using var refused = await anonymous.SendAsync(HttpMethod.Post, "/api/upload/container?name=links.rsdf", null,
            request => request.Content = new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        using var client = await AuthServer.SetUpAsync(server);
        using var uploaded = await client.SendAsync(HttpMethod.Post, "/api/upload/container?name=My%20links.RSDF", null,
            request => request.Content = new ByteArrayContent("not really a container"u8.ToArray()));
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var text = await uploaded.Content.ReadAsStringAsync();
        Assert.DoesNotContain(server.Data.Replace("\\", "\\\\"), text);
        var reference = (await client.JsonAsync(uploaded)).GetProperty("upload").GetString()!;
        Assert.Matches("^upload:[0-9a-f]{32}$", reference);
        var path = Path.Combine(server.Data, TorrentUploads.FolderName, reference[TorrentUploads.ReferencePrefix.Length..] + ".rsdf");
        Assert.True(File.Exists(path));

        // Read as that .rsdf file, though the uploads folder is none of the server's roots; typed, its path is refused.
        var (status, reply) = await client.IpcAsync(MessageTypes.RemoteContainerPreview, new RemoteContainerPreviewRequest { Path = reference, FileName = "My links.rsdf" });
        Assert.Equal(HttpStatusCode.OK, status);
        var preview = reply!.GetPayload<RemoteContainerPreviewResult>();
        Assert.Equal("rsdf", preview.Format);
        Assert.NotNull(preview.Error);
        Assert.DoesNotContain("outside", preview.Error, StringComparison.OrdinalIgnoreCase);
        var (_, typed) = await client.IpcAsync(MessageTypes.RemoteContainerPreview, new RemoteContainerPreviewRequest { Path = path });
        Assert.Contains("outside", typed!.GetPayload<RemoteContainerPreviewResult>().Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnlyContainerKindsAndSizesAreTaken()
    {
        await using var server = await AuthServer.StartAsync();
        using var client = await AuthServer.SetUpAsync(server);

        foreach (var name in new[] { "links.txt", "links", "links.torrent", "" })
        {
            using var wrong = await client.SendAsync(HttpMethod.Post, "/api/upload/container?name=" + Uri.EscapeDataString(name), null,
                request => request.Content = new ByteArrayContent([1, 2, 3]));
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        using var empty = await client.SendAsync(HttpMethod.Post, "/api/upload/container?name=a.ccf", null,
            request => request.Content = new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var large = await client.SendAsync(HttpMethod.Post, "/api/upload/container?name=a.dlc", null,
            request => request.Content = new ByteArrayContent(new byte[16 * 1024 * 1024 + 1]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(server.Data, TorrentUploads.FolderName)));
    }
}
