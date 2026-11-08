using System.Diagnostics;
using DownloadManager.Contracts;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// The real native host against a sandboxed Host: who may start it (EXT.E1), and what the popup and
/// the folder window may ask through it (EXT.R1, EXT.R2). Nothing here touches the installed app.
/// </summary>
public sealed class NativeHostBridgeTests : IAsyncLifetime
{
    private TestServerHost _server = null!;

    public async Task InitializeAsync() => _server = await TestServerHost.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private Task<SandboxHost> StartHostAsync() => SandboxHost.StartAsync(_server.TorBox.ApiBase);

    // ---- Who may start the bridge ---------------------------------------------------------------

    [Theory]
    [InlineData("chromium", "chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/", "--parent-window=0")]
    [InlineData("chromium", "chrome-extension://pknddmneejadnnngffojnoecojhdfalc/")]
    [InlineData("firefox", @"C:\Users\me\AppData\Local\Programs\Inbound Download Manager\native-host\com.kaank.downloadmanager.firefox.json", "inbound-bridge@kaan88")]
    public void A_trusted_extension_in_any_supported_browser_is_accepted(string expected, params string[] arguments) =>
        Assert.Equal(expected, BrowserBridge.TrustedCaller(arguments));

    [Theory]
    [InlineData]
    [InlineData("--parent-window=0")]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/", "--parent-window=0")]
    [InlineData("chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/evil")]
    // A trusted origin next to a forged one is still forged.
    [InlineData("chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/", "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    [InlineData(@"C:\x\manifest.json", "someone-else@example.com")]
    [InlineData(@"C:\x\manifest.json", "INBOUND-BRIDGE@KAAN88")]
    [InlineData("inbound-bridge@kaan88")]
    [InlineData("inbound-bridge@kaan88", @"C:\x\manifest.json")]
    [InlineData(@"C:\x\manifest.json", "moz-extension://0d1c2b3a-0000-4000-8000-000000000000/", "inbound-bridge@kaan88")]
    public void Anything_else_is_refused_including_no_caller_at_all(params string[] arguments) =>
        Assert.Null(BrowserBridge.TrustedCaller(arguments));

    [Theory]
    [InlineData("--parent-window=0")]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/", "--parent-window=0")]
    [InlineData(@"C:\x\manifest.json", "someone-else@example.com")]
    public async Task The_native_host_exits_before_reaching_the_Host_for_an_untrusted_caller(params string[] arguments)
    {
        await using var host = await StartHostAsync();
        using var native = host.StartNativeHost(arguments);
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await native.WaitForExitAsync(wait.Token);
            Assert.Equal(2, native.ExitCode);
            // Nothing was said to the caller either.
            Assert.Equal(-1, native.StandardOutput.BaseStream.ReadByte());
        }
        finally
        {
            if (!native.HasExited) native.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Firefox_with_the_trusted_add_on_ID_reaches_the_Host()
    {
        await using var host = await StartHostAsync();
        using var native = host.StartNativeHost(Path.Combine(host.Root, BrowserBridge.FirefoxManifestFileName), BrowserBridge.FirefoxExtensionId);
        try
        {
            var reply = await AskAsync(native, MessageTypes.HostPing, new { });
            Assert.True(reply.GetPayload<HostActionResult>().Ok);
        }
        finally { await StopAsync(native); }
    }

    // ---- What the popup and the folder window may ask --------------------------------------------

    [Fact]
    public async Task The_folder_window_gets_the_default_folder_and_favorites_and_nothing_else()
    {
        await using var host = await StartHostAsync();
        await using (var setup = await host.ConnectAsync())
        {
            var settings = (await setup.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
            settings.FavoriteFolders = [new FavoriteFolderDto { Name = "Music", Path = Path.Combine(host.Root, "music") }];
            settings.RecentUrls = ["https://private.test/history-entry"];
            Assert.True((await setup.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings })).Ok);
        }

        using var native = host.StartNativeHost();
        try
        {
            var reply = await AskAsync(native, MessageTypes.SettingsDownloadFolders, new { });
            Assert.Equal(MessageTypes.SettingsDownloadFoldersResult, reply.Type);
            var folders = reply.GetPayload<DownloadFoldersResult>();
            Assert.True(folders.Ok);
            Assert.Equal(Path.GetFullPath(host.Destination), Path.GetFullPath(folders.DefaultFolder));
            Assert.Equal("Music", Assert.Single(folders.Favorites).Name);
            var raw = reply.Payload!.Value.GetRawText();
            Assert.DoesNotContain("history-entry", raw);
            Assert.DoesNotContain("recentUrls", raw, StringComparison.OrdinalIgnoreCase);

            // The whole settings stay out of reach.
            var refused = await AskAsync(native, MessageTypes.SettingsGet, new { });
            Assert.Equal(MessageTypes.HostActionResult, refused.Type);
            Assert.False(refused.GetPayload<HostActionResult>().Ok);
        }
        finally { await StopAsync(native); }
    }

    [Fact]
    public async Task The_popup_can_pause_resume_and_delete_a_download_and_its_file()
    {
        await using var host = await StartHostAsync();
        await using var connection = await host.ConnectAsync();
        var added = await connection.AskAsync<AddJobResponse>(MessageTypes.JobAdd, new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/popup.bin?size={8 * 1024 * 1024}&seed=77&bps=262144",
            DestinationDirectory = host.Destination,
            Source = "cli",
        });
        Assert.True(added.Accepted, added.Error);
        var id = added.Job!.Id;

        using var native = host.StartNativeHost();
        try
        {
            // Some bytes on disk first, so deleting the file has something to delete.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while ((await AskAsync(native, MessageTypes.JobsQuery, new { })).GetPayload<SnapshotDto>().Jobs.Single(job => job.Id == id).ReceivedBytes == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "The download never started.");
                await Task.Delay(100);
            }

            var paused = (await AskAsync(native, MessageTypes.JobPause, new JobActionRequest { JobId = id })).GetPayload<JobActionResponse>();
            Assert.True(paused.Ok, paused.Error);
            Assert.Equal("paused", paused.Job!.State);

            var resumed = (await AskAsync(native, MessageTypes.JobResume, new JobActionRequest { JobId = id })).GetPayload<JobActionResponse>();
            Assert.True(resumed.Ok, resumed.Error);

            var stopped = (await AskAsync(native, MessageTypes.JobPause, new JobActionRequest { JobId = id })).GetPayload<JobActionResponse>();
            Assert.True(stopped.Ok, stopped.Error);
            var part = stopped.Job!.PartFilePath;
            Assert.True(File.Exists(part), part);

            var removed = (await AskAsync(native, MessageTypes.JobRemove, new JobActionRequest { JobId = id, DeleteFile = true })).GetPayload<JobActionResponse>();
            Assert.True(removed.Ok, removed.Error);
            var snapshot = (await AskAsync(native, MessageTypes.JobsQuery, new { })).GetPayload<SnapshotDto>();
            Assert.DoesNotContain(snapshot.Jobs, job => job.Id == id);
            Assert.False(File.Exists(part), "Delete the file too removes the partial file.");

            // Still refused: adding a download directly, or changing the settings.
            foreach (var type in new[] { MessageTypes.JobAdd, MessageTypes.SettingsUpdate, MessageTypes.JobsPauseAll, MessageTypes.HostShutdown })
            {
                var refused = await AskAsync(native, type, new { });
                Assert.Equal(MessageTypes.HostActionResult, refused.Type);
                Assert.Contains("browser extension cannot send", refused.GetPayload<HostActionResult>().Message);
            }
        }
        finally { await StopAsync(native); }
    }

    private static async Task<IpcMessage> AskAsync(Process native, string type, object payload)
    {
        var request = IpcMessage.Create(type, payload);
        await IpcFraming.WriteAsync(native.StandardInput.BaseStream, request);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var frame = await IpcFraming.ReadAsync(native.StandardOutput.BaseStream, wait.Token) ?? throw new IOException("The native host closed.");
            if (frame.ReplyTo == request.Id) return frame;
        }
    }

    private static async Task StopAsync(Process native)
    {
        if (!native.HasExited) native.Kill(entireProcessTree: true);
        await native.WaitForExitAsync();
    }
}
