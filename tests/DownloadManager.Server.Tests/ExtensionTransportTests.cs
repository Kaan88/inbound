using System.Net;
using System.Text.Json;
using DownloadManager.Contracts;

namespace DownloadManager.Server.Tests;

/// <summary>
/// EXT.R1 (stream C transport): what the extension's <c>ServerTransport</c> sends over <c>POST /api/ipc</c> with its
/// paired token, envelope for envelope as <c>extensions/chrome/src/background.ts</c> builds them: a handoff with the
/// browser's cookies, the popup's list and its actions on one download, the folder window's default folder and folder list.
/// </summary>
public sealed class ExtensionTransportTests
{
    private const string FirefoxOrigin = "moz-extension://5f2c8a8e-1111-2222-3333-444455556666";

    private static object Handoff(string requestId, string url, string? destination = null, string extensionId = BrowserBridge.UnpackedExtensionId) => new
    {
        requestId,
        extensionId,
        url,
        pageUrl = "https://site.test/page",
        referrer = "https://site.test/page",
        userAgent = "Mozilla/5.0 test",
        suggestedFileName = "file.zip",
        // "later" keeps the job from going out to the unreachable address.
        startMode = "later",
        isAutoIntercept = true,
        headers = new Dictionary<string, string> { ["Cookie"] = "session=s" },
        destinationDirectory = destination,
    };

    private static JsonElement Payload((HttpStatusCode Status, IpcMessage? Reply) answer, string type)
    {
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.NotNull(answer.Reply);
        Assert.Equal(type, answer.Reply!.Type);
        return answer.Reply.Payload!.Value;
    }

    [Fact]
    public async Task AHandoffWithTheTokenAddsTheDownloadInsideTheRootsAndThePopupCanActOnIt()
    {
        await using var server = await AuthServer.StartAsync();
        using var user = await AuthServer.SetUpAsync(server);
        var token = await PairingTests.PairAsync(server, await PairingTests.NewCodeAsync(user));
        using var extension = new AuthClient(server.Base) { Origin = PairingTests.ChromeOrigin };

        // The folder window's default folder for the server, then a folder inside it from the list.
        var folders = Payload(await extension.IpcAsync(MessageTypes.SettingsDownloadFolders, new { }, token), MessageTypes.SettingsDownloadFoldersResult);
        Assert.True(folders.GetProperty("ok").GetBoolean());
        var music = Directory.CreateDirectory(Path.Combine(server.Root, "music")).FullName;
        var listing = Payload(await extension.IpcAsync(MessageTypes.FoldersBrowse, new { path = server.Root }, token), MessageTypes.FoldersBrowseResult);
        Assert.Contains(listing.GetProperty("folders").EnumerateArray(), folder => folder.GetProperty("name").GetString() == "music");

        var requestId = Guid.NewGuid().ToString();
        var ack = Payload(await extension.IpcAsync(MessageTypes.HandoffRequest, Handoff(requestId, "http://127.0.0.1:9/file.zip", music), token), MessageTypes.HandoffAck);
        Assert.True(ack.GetProperty("accepted").GetBoolean());
        var jobId = ack.GetProperty("jobId").GetGuid();

        // The same request again (a resend after a lost answer) is the same download.
        var again = Payload(await extension.IpcAsync(MessageTypes.HandoffRequest, Handoff(requestId, "http://127.0.0.1:9/file.zip", music), token), MessageTypes.HandoffAck);
        Assert.True(again.GetProperty("duplicate").GetBoolean());
        Assert.Equal(jobId, again.GetProperty("jobId").GetGuid());

        // The popup's list: the download is there, in the folder chosen, and the snapshot has no settings.
        var snapshot = Payload(await extension.IpcAsync(MessageTypes.JobsQuery, new { }, token), MessageTypes.JobsSnapshot);
        Assert.False(snapshot.TryGetProperty("settings", out _));
        var job = snapshot.GetProperty("jobs").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == jobId);
        Assert.Equal(Path.GetFullPath(music), Path.GetFullPath(job.GetProperty("destinationDirectory").GetString()!));
        Assert.DoesNotContain("session=s", snapshot.GetRawText());

        // The popup's Delete, one download, the file kept.
        var removed = Payload(await extension.IpcAsync(MessageTypes.JobRemove, new { jobId, deleteFile = false }, token), MessageTypes.JobActionResult);
        Assert.True(removed.GetProperty("ok").GetBoolean());
        snapshot = Payload(await extension.IpcAsync(MessageTypes.JobsQuery, new { }, token), MessageTypes.JobsSnapshot);
        Assert.DoesNotContain(snapshot.GetProperty("jobs").EnumerateArray(), item => item.GetProperty("id").GetGuid() == jobId);
    }

    [Fact]
    public async Task AHandoffOutsideTheRootsOrFromAnotherExtensionIsRefusedAndAddsNothing()
    {
        await using var server = await AuthServer.StartAsync();
        using var user = await AuthServer.SetUpAsync(server);
        var token = await PairingTests.PairAsync(server, await PairingTests.NewCodeAsync(user));
        using var extension = new AuthClient(server.Base) { Origin = PairingTests.ChromeOrigin };

        // A folder of this PC's (a Windows path picked for the wrong target) or anywhere outside the roots.
        var outside = Path.Combine(server.Work, "elsewhere");
        Directory.CreateDirectory(outside);
        var refused = Payload(await extension.IpcAsync(MessageTypes.HandoffRequest, Handoff(Guid.NewGuid().ToString(), "http://127.0.0.1:9/a.zip", outside), token), MessageTypes.HandoffAck);
        Assert.False(refused.GetProperty("accepted").GetBoolean());
        Assert.Equal("invalid_destination", refused.GetProperty("errorCode").GetString());

        var stranger = Payload(await extension.IpcAsync(MessageTypes.HandoffRequest,
            Handoff(Guid.NewGuid().ToString(), "http://127.0.0.1:9/b.zip", extensionId: "abcdefghijklmnopabcdefghijklmnop"), token), MessageTypes.HandoffAck);
        Assert.False(stranger.GetProperty("accepted").GetBoolean());
        Assert.Equal("untrusted_extension", stranger.GetProperty("errorCode").GetString());

        var snapshot = Payload(await extension.IpcAsync(MessageTypes.JobsQuery, new { }, token), MessageTypes.JobsSnapshot);
        Assert.Equal(0, snapshot.GetProperty("jobs").GetArrayLength());

        // Without the token the same handoff never reaches the Host: the extension's origin alone opens nothing.
        Assert.Equal(HttpStatusCode.Forbidden, (await extension.IpcAsync(MessageTypes.HandoffRequest, Handoff(Guid.NewGuid().ToString(), "http://127.0.0.1:9/c.zip"))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await extension.IpcAsync(MessageTypes.HandoffRequest, Handoff(Guid.NewGuid().ToString(), "http://127.0.0.1:9/c.zip"), "ibt_wrong")).Status);
    }

    [Fact]
    public async Task FirefoxSendsTheSameWayAndTheDefaultFolderIsUsedWhenNoneIsGiven()
    {
        await using var server = await AuthServer.StartAsync();
        using var user = await AuthServer.SetUpAsync(server);
        var token = await PairingTests.PairAsync(server, await PairingTests.NewCodeAsync(user), "Firefox on Linux");
        using var firefox = new AuthClient(server.Base) { Origin = FirefoxOrigin };

        var ack = Payload(await firefox.IpcAsync(MessageTypes.HandoffRequest,
            Handoff(Guid.NewGuid().ToString(), "http://127.0.0.1:9/f.zip", extensionId: BrowserBridge.FirefoxExtensionId), token), MessageTypes.HandoffAck);
        Assert.True(ack.GetProperty("accepted").GetBoolean());
        var folder = ack.GetProperty("job").GetProperty("destinationDirectory").GetString()!;
        Assert.StartsWith(Path.GetFullPath(server.Root), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);

        // The paired browser shows under its name, and was just used.
        var tokens = (await user.JsonAsync(await user.GetAsync("/api/auth/tokens"))).GetProperty("tokens");
        Assert.Equal("Firefox on Linux", tokens[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.String, tokens[0].GetProperty("lastUsedAt").ValueKind);
    }
}
