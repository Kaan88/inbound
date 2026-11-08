using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Persistence;

namespace DownloadManager.Server.Tests;

/// <summary>Review findings (D1b) about what the server does before it serves anything.</summary>
public sealed class ServerStartGuardTests
{
    [Fact]
    public async Task KestrelEndpointsFromTheEnvironmentDontOpenAPortTheLoopbackRuleDidntSee()
    {
        // The reviewer's reproduction: with this variable the server listened on 0.0.0.0 although its options said loopback.
        const string Variable = "Kestrel__Endpoints__Probe__Url";
        var port = FreePort();
        Environment.SetEnvironmentVariable(Variable, $"http://0.0.0.0:{port}");
        try
        {
            await using var server = await ServerHarness.StartAsync();
            Assert.NotEmpty(server.App.Addresses);
            Assert.All(server.App.Addresses, address => Assert.True(ServerOptions.IsLoopback(address.OriginalString), address.OriginalString));
            Assert.DoesNotContain(server.App.Addresses, address => address.Port == port);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, null);
        }
    }

    [Fact]
    public void TheAddressesKestrelReallyListensOnAreCheckedWithTheSameRule()
    {
        var options = new ServerOptions { Urls = ["http://127.0.0.1:0"], SignInRequired = false };
        Assert.Null(options.DescribeUnsafeBinding(["http://127.0.0.1:41234", "http://[::1]:41234"]));
        Assert.Contains(ServerOptions.AllowUnauthenticatedVariable, options.DescribeUnsafeBinding(["http://127.0.0.1:1", "http://0.0.0.0:41234"]));
        Assert.Null((options with { AllowUnauthenticated = true }).DescribeUnsafeBinding(["http://0.0.0.0:41234"]));
    }

    [Fact]
    public async Task ASecondServerOnTheSameDataFolderIsRefusedUntilTheFirstStops()
    {
        var work = ServerHarness.NewWork();
        await using (var first = await ServerHarness.StartAsync(work: work))
        {
            var refused = await Assert.ThrowsAsync<ServerStartException>(() => ServerHarness.StartAsync(work: work));
            Assert.Contains("already using the data folder", refused.Message);
            // The first one is unharmed.
            Assert.True((await first.AskAsync<HostActionResult>(MessageTypes.HostPing, new { })).Ok);
        }

        await using var again = await ServerHarness.StartAsync(work: work);
        Assert.True((await again.AskAsync<HostActionResult>(MessageTypes.HostPing, new { })).Ok);
    }

    [Fact]
    public async Task AMissingOrOtherKeyFileStopsTheStartAndTheSecretsSurviveUntilTheRightOneIsBack()
    {
        var work = ServerHarness.NewWork();
        var keyFile = Path.Combine(work, "data", AesGcmSecretProtector.DefaultFileName);
        var settingsFile = Path.Combine(work, "data", "settings.json");
        await using (var server = await ServerHarness.StartAsync(work: work))
        {
            var settings = (await server.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
            settings.SiteLogins.Add(new SiteLoginDto { Site = "files.example.invalid", Username = "someone", Password = "not-a-real-password" });
            Assert.True((await server.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings })).Ok);
        }

        var original = await File.ReadAllBytesAsync(keyFile);
        var sealedSettings = await File.ReadAllTextAsync(settingsFile);

        // Gone: refused, and no new key is made in its place, so putting the old one back is all it takes.
        File.Delete(keyFile);
        var missing = await Assert.ThrowsAsync<ServerStartException>(() => ServerHarness.StartAsync(work: work));
        Assert.Contains("missing", missing.Message);
        Assert.Contains(AesGcmSecretProtector.DiscardVariable, missing.Message);
        Assert.False(File.Exists(keyFile));

        // Another key: refused too, and nothing the stores hold was rewritten.
        await File.WriteAllBytesAsync(keyFile, Enumerable.Repeat((byte)7, AesGcmSecretProtector.KeyBytes).ToArray());
        var other = await Assert.ThrowsAsync<ServerStartException>(() => ServerHarness.StartAsync(work: work));
        Assert.Contains("another key", other.Message);
        Assert.Equal(sealedSettings, await File.ReadAllTextAsync(settingsFile));

        await File.WriteAllBytesAsync(keyFile, original);
        await using (var restored = await ServerHarness.StartAsync(work: work))
        {
            var login = Assert.Single((await restored.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!.SiteLogins);
            Assert.True(login.HasPassword);
        }

        // The owner's explicit "start anyway" starts, and says in the log that the secrets are lost.
        await File.WriteAllBytesAsync(keyFile, Enumerable.Repeat((byte)9, AesGcmSecretProtector.KeyBytes).ToArray());
        await using (var discarded = await ServerHarness.StartAsync(options => options with { DiscardUnreadableSecrets = true }, work: work))
        {
            Assert.Single((await discarded.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!.SiteLogins);
        }

        var log = string.Concat(Directory.GetFiles(Path.Combine(work, "data", "logs")).Select(File.ReadAllText));
        Assert.Contains("are discarded", log);
    }

    [Fact]
    public void ABlobWrittenByTheJsonStoresIsFoundWithItsPlusSignsEscaped()
    {
        var work = ServerHarness.NewWork();
        var data = Directory.CreateDirectory(Path.Combine(work, "data")).FullName;
        var first = AesGcmSecretProtector.OpenOrCreate(Path.Combine(work, "first.key"));
        var purpose = Encoding.UTF8.GetBytes("Test.Purpose.v1");
        // System.Text.Json writes + as an escape by default; the search has to read that spelling too.
        string blob;
        do blob = first.Protect("value", purpose);
        while (!blob.Contains('+'));
        var json = JsonSerializer.Serialize(new { secret = blob });
        Assert.DoesNotContain("+", json);
        File.WriteAllText(Path.Combine(data, "store.json"), json);

        Assert.Throws<SecretKeyMismatchException>(() =>
            AesGcmSecretProtector.OpenForDataFolder(Path.Combine(work, "second.key"), data, discardUnreadable: false));
        Assert.NotNull(AesGcmSecretProtector.OpenForDataFolder(Path.Combine(work, "first.key"), data, discardUnreadable: false));
    }

    [Fact]
    public void DiscardingOnceHoldsOnLaterStartsWhileOldBlobsStayButANewForeignKeyStillRefuses()
    {
        // Review finding: blobs that are never rewritten (a kept ProtectedUrl, a quarantined file) made every later start
        // refuse again after the one start with the discard variable.
        var work = ServerHarness.NewWork();
        var data = Directory.CreateDirectory(Path.Combine(work, "data")).FullName;
        var purpose = Encoding.UTF8.GetBytes("Test.Purpose.v1");
        var old = AesGcmSecretProtector.OpenOrCreate(Path.Combine(work, "old.key"));
        File.WriteAllText(Path.Combine(data, "jobs.json"), JsonSerializer.Serialize(new { url = old.Protect("a", purpose) }));
        Directory.CreateDirectory(Path.Combine(data, "corrupt"));
        File.WriteAllText(Path.Combine(data, "corrupt", "job.json"), JsonSerializer.Serialize(new { url = old.Protect("b", purpose) }));

        // The key file is lost: once with the discard choice, then without it.
        var keyFile = Path.Combine(data, AesGcmSecretProtector.DefaultFileName);
        Assert.Throws<SecretKeyMismatchException>(() => AesGcmSecretProtector.OpenForDataFolder(keyFile, data, discardUnreadable: false));
        Assert.NotNull(AesGcmSecretProtector.OpenForDataFolder(keyFile, data, discardUnreadable: true));
        Assert.True(File.Exists(keyFile + AesGcmSecretProtector.DiscardedFileSuffix));
        var current = AesGcmSecretProtector.OpenForDataFolder(keyFile, data, discardUnreadable: false);
        File.WriteAllText(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(new { password = current.Protect("c", purpose) }));
        Assert.NotNull(AesGcmSecretProtector.OpenForDataFolder(keyFile, data, discardUnreadable: false));

        // A later mix-up with a key that was never discarded is refused as before.
        var other = AesGcmSecretProtector.OpenOrCreate(Path.Combine(work, "other.key"));
        File.WriteAllText(Path.Combine(data, "queues.json"), JsonSerializer.Serialize(new { secret = other.Protect("d", purpose) }));
        Assert.Throws<SecretKeyMismatchException>(() => AesGcmSecretProtector.OpenForDataFolder(keyFile, data, discardUnreadable: false));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
