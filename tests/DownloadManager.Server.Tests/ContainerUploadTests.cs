using System.Net;
using DownloadManager.Contracts;

namespace DownloadManager.Server.Tests;

/// <summary>
/// <c>POST /api/upload/container</c>: a container from the web UI's Add menu, kept beside the torrent uploads and named
/// only by its reference, which <c>remote.container.preview</c> takes. The roots don't hold the uploads folder. The
/// file's name comes in a header, so it stays out of the address.
/// </summary>
public sealed class ContainerUploadTests
{
    private static Action<HttpRequestMessage> Upload(string? name, HttpContent content) => request =>
    {
        request.Content = content;
        if (name is not null) request.Headers.Add(TorrentUploads.FileNameHeader, Uri.EscapeDataString(name));
    };

    [Fact]
    public async Task AContainerUploadNeedsASessionKeepsItsKindAndIsPreviewedByItsReference()
    {
        await using var server = await AuthServer.StartAsync();
        using var anonymous = new AuthClient(server.Base);
        using var refused = await anonymous.SendAsync(HttpMethod.Post, "/api/upload/container", null,
            Upload("links.rsdf", new ByteArrayContent([1, 2, 3])));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        using var client = await AuthServer.SetUpAsync(server);
        using var uploaded = await client.SendAsync(HttpMethod.Post, "/api/upload/container", null,
            Upload("My links ü.RSDF", new ByteArrayContent("not really a container"u8.ToArray())));
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

        // Gone (cleaned up after a day): said so, not taken for the wrong kind of file.
        File.Delete(path);
        var (_, gone) = await client.IpcAsync(MessageTypes.RemoteContainerPreview, new RemoteContainerPreviewRequest { Path = reference });
        Assert.Equal("The uploaded file is no longer there. Upload it again.", gone!.GetPayload<RemoteContainerPreviewResult>().Error);
    }

    [Fact]
    public async Task OnlyContainerKindsAndSizesAreTaken()
    {
        await using var server = await AuthServer.StartAsync();
        using var client = await AuthServer.SetUpAsync(server);

        foreach (var name in new[] { "links.txt", "links", "links.torrent", "", null })
        {
            using var wrong = await client.SendAsync(HttpMethod.Post, "/api/upload/container", null,
                Upload(name, new ByteArrayContent([1, 2, 3])));
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        // The name is read from the header only, never from the address.
        using var inQuery = await client.SendAsync(HttpMethod.Post, "/api/upload/container?name=a.ccf", null,
            Upload(null, new ByteArrayContent([1, 2, 3])));
        Assert.Equal(HttpStatusCode.BadRequest, inQuery.StatusCode);

        using var empty = await client.SendAsync(HttpMethod.Post, "/api/upload/container", null,
            Upload("a.ccf", new ByteArrayContent([])));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var large = await client.SendAsync(HttpMethod.Post, "/api/upload/container", null,
            Upload("a.dlc", new ByteArrayContent(new byte[16 * 1024 * 1024 + 1])));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        Assert.Equal("This file is too large to be a container.", (await client.JsonAsync(large)).GetProperty("error").GetString());
        Assert.Empty(Directory.GetFiles(Path.Combine(server.Data, TorrentUploads.FolderName)));
    }

    [Fact]
    public async Task ABodySentWithoutALengthIsStoppedAtTheLimitWithTooLarge()
    {
        await using var server = await AuthServer.StartAsync();
        using var client = await AuthServer.SetUpAsync(server);

        using var large = await client.SendAsync(HttpMethod.Post, "/api/upload/container", null,
            Upload("a.dlc", new UnsizedContent(new byte[16 * 1024 * 1024 + 1])));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(server.Data, TorrentUploads.FolderName)));

        using var small = await client.SendAsync(HttpMethod.Post, "/api/upload/container", null,
            Upload("a.dlc", new UnsizedContent([1, 2, 3])));
        Assert.Equal(HttpStatusCode.OK, small.StatusCode);
    }

    /// <summary>A body whose length isn't known in advance, so it is sent chunked, with no Content-Length.</summary>
    private sealed class UnsizedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
