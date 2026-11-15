using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DownloadManager.Contracts;

namespace DownloadManager.Server.Tests;

/// <summary>
/// SITES.7 on the server: Sites for a signed-in browser, held to the server's folders, and the "this device" endpoints
/// (<c>GET</c> and <c>POST /api/sites/device</c>) against a real rclone serving WebDAV. Needs INBOUND_RCLONE (the server
/// finds rclone there, as the image finds its own); skipped without it.
/// </summary>
public sealed class SiteDeviceStreamTests
{
    private static string? Rclone => Environment.GetEnvironmentVariable("INBOUND_RCLONE") is { Length: > 0 } path && File.Exists(path) ? path : null;

    [Fact]
    public async Task ABrowserBrowsesAndMovesFilesToAndFromTheDevice()
    {
        if (Rclone is null) return; // nothing to run against
        var served = Path.Combine(ServerHarness.NewWork(), "served");
        Directory.CreateDirectory(Path.Combine(served, "pub"));
        var file = RandomNumberGenerator.GetBytes(SiteDeviceLimits.ChunkBytes + 4321);
        await File.WriteAllBytesAsync(Path.Combine(served, "pub", "file.bin"), file);
        var port = FreePort();
        using var server = Serve(served, port);

        await using var app = await AuthServer.StartAsync();
        using var anonymous = new AuthClient(app.Base);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/sites/device?id=x&path=/a")).StatusCode);
        using var client = await AuthServer.SetUpAsync(app);

        // A program to run is not chosen from a browser; the switch is.
        var (_, own) = await client.IpcAsync(SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone });
        Assert.Equal(MessageTypes.HostActionResult, own!.Type);
        var (_, on) = await client.IpcAsync(SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true });
        Assert.True(on!.GetPayload<SitesStatusDto>().Enabled);

        // A site's own folder must be one of the server's.
        var (_, outside) = await client.IpcAsync(SitesMessageTypes.Save, new SiteSaveRequest
        {
            Name = "DAV", Kind = "webdav", Url = $"http://127.0.0.1:{port}/", Vendor = "other", User = "u", Password = "pw-7731",
            LocalFolder = Path.GetTempPath(),
        });
        Assert.Equal(MessageTypes.HostActionResult, outside!.Type);
        var (_, saved) = await client.IpcAsync(SitesMessageTypes.Save, new SiteSaveRequest
        {
            Name = "DAV", Kind = "webdav", Url = $"http://127.0.0.1:{port}/", Vendor = "other", User = "u", Password = "pw-7731",
        });
        var site = saved!.GetPayload<SiteResultDto>();
        Assert.True(site.Ok, site.Message);
        var id = site.Site!.Id;

        // To this device.
        using var got = await client.GetAsync($"/api/sites/device?id={id}&path={Uri.EscapeDataString("/pub/file.bin")}");
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        Assert.Equal("file.bin", got.Content.Headers.ContentDisposition?.FileNameStar ?? got.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(file, await got.Content.ReadAsByteArrayAsync());
        using var missing = await client.GetAsync($"/api/sites/device?id={id}&path=/pub/none.bin");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        // From this device; the name in a header.
        var up = RandomNumberGenerator.GetBytes(SiteDeviceLimits.ChunkBytes * 2 + 1);
        Action<HttpRequestMessage> Body(string name, byte[] bytes) => request =>
        {
            request.Content = new ByteArrayContent(bytes);
            request.Headers.Add(SiteDeviceStreams.FileNameHeader, Uri.EscapeDataString(name));
        };
        using var sent = await client.SendAsync(HttpMethod.Post, $"/api/sites/device?id={id}&folder=/pub", null, Body("from phone ü.bin", up));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal(up, await File.ReadAllBytesAsync(Path.Combine(served, "pub", "from phone ü.bin")));
        using var exists = await client.SendAsync(HttpMethod.Post, $"/api/sites/device?id={id}&folder=/pub", null, Body("from phone ü.bin", [1]));
        Assert.Equal(HttpStatusCode.Conflict, exists.StatusCode);
        using var replaced = await client.SendAsync(HttpMethod.Post, $"/api/sites/device?id={id}&folder=/pub&replace=1", null, Body("from phone ü.bin", [1]));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(Path.Combine(served, "pub", "from phone ü.bin")));
        await client.IpcAsync(SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = false });
    }

    private static KillOnDispose Serve(string folder, int port)
    {
        var start = new ProcessStartInfo(Rclone!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "serve", "webdav", folder, "--addr", $"127.0.0.1:{port}", "--user", "u", "--pass", "pw-7731", "--config", "", "--dir-cache-time", "0s" })
            start.ArgumentList.Add(arg);
        var process = Process.Start(start)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var probe = new TcpClient();
                probe.Connect(IPAddress.Loopback, port);
                return new KillOnDispose(process);
            }
            catch (SocketException)
            {
                Thread.Sleep(100);
            }
        }

        process.Kill();
        throw new InvalidOperationException("rclone serve did not start");
    }

    private sealed class KillOnDispose(Process process) : IDisposable
    {
        public void Dispose()
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
