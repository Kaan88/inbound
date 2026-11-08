using System.Text;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;
using DownloadManager.Persistence;

namespace DownloadManager.Core.Tests;

/// <summary>
/// Design D9, section 5, the desktop side: server addresses and the plain-HTTP rule, the reconnect wait, the window's
/// own server list with its sealed tokens, what the window does with another server's data, Options when it shows
/// another server, and the progress windows' names.
/// </summary>
public sealed class RemoteServerRulesTests
{
    [Theory]
    [InlineData("homeserver", "http://homeserver:8080/")]
    [InlineData("  HomeServer:9000  ", "http://homeserver:9000/")]
    [InlineData("http://192.168.1.5:8080/downloads?x=1#top", "http://192.168.1.5:8080/")]
    [InlineData("http://homeserver", "http://homeserver/")]
    [InlineData("https://nas.example.org", "https://nas.example.org/")]
    [InlineData("https://nas.example.org:8443/web", "https://nas.example.org:8443/")]
    [InlineData("[::1]:8080", "http://[::1]:8080/")]
    public void AnAddressIsKeptInOneShape(string typed, string expected)
    {
        Assert.True(ServerAddress.TryNormalize(typed, out var address, out var error), error);
        Assert.Equal(expected, address.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://homeserver")]
    [InlineData("http://me:secret@homeserver:8080")]
    [InlineData("file:///C:/server")]
    public void AnAddressThatIsNoServersIsRefusedInWords(string typed)
    {
        Assert.False(ServerAddress.TryNormalize(typed, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain("secret", error);
    }

    [Theory]
    [InlineData("http://homeserver:8080/", true)]
    [InlineData("http://192.168.1.5:8080/", true)]
    [InlineData("https://homeserver:8080/", false)]
    [InlineData("http://localhost:8080/", false)]
    [InlineData("http://127.0.0.1:8080/", false)]
    [InlineData("http://127.0.0.2:8080/", false)]
    [InlineData("http://[::1]:8080/", false)]
    public void ATokenGoesToPlainHttpElsewhereOnlyOnceTheUserSaidSo(string address, bool asks)
    {
        var uri = new Uri(address);
        Assert.Equal(asks, ServerAddress.NeedsPlainHttpConfirmation(uri));
        Assert.Equal(!asks, ServerAddress.MaySend(uri, plainHttpConfirmed: false));
        Assert.True(ServerAddress.MaySend(uri, plainHttpConfirmed: true));
    }

    [Fact]
    public void TheSocketAndEndpointsAreOnTheServersOwnAddress()
    {
        Assert.Equal("ws://homeserver:8080/api/ws?hello=1", ServerAddress.WebSocketAddress(new Uri("http://homeserver:8080/")).ToString());
        Assert.Equal("wss://nas.example.org/api/ws?hello=1", ServerAddress.WebSocketAddress(new Uri("https://nas.example.org/")).ToString());
        Assert.Equal("http://homeserver:8080/api/pair/self", ServerAddress.Endpoint(new Uri("http://homeserver:8080/"), "/api/pair/self").ToString());
    }

    [Fact]
    public void ATargetNeverShowsItsToken()
    {
        var target = ServerTarget.Remote(new string('a', 32), "Home", new Uri("http://homeserver:8080/"), "dmrc_invented-token", plainHttpConfirmed: true);
        Assert.DoesNotContain("invented-token", target.ToString());
        Assert.False(target.IsLocal);
        Assert.True(ServerTarget.Local.IsLocal);
        Assert.Null(ServerTarget.Local.Token);
    }

    [Fact]
    public void TryingAServerAgainWaitsLongerEachTimeUpToAboutAMinute()
    {
        var backoff = new ReconnectBackoff();
        var waits = Enumerable.Range(0, 8).Select(_ => (int)backoff.Next().TotalSeconds).ToArray();
        Assert.Equal([2, 4, 8, 16, 32, 60, 60, 60], waits);
        Assert.Equal(TimeSpan.FromMinutes(1), backoff.Maximum);

        backoff.Reset();
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.Next());
    }

    [Fact]
    public void ARefusedTokenSaysToPairAgainAndNoOtherFailureDoes()
    {
        var target = ServerTarget.Remote(new string('b', 32), "Home", new Uri("http://homeserver:8080/"), "dmrc_invented", plainHttpConfirmed: true);
        var ended = new RemotePairingEndedException();
        Assert.Contains("Pair it again", ended.Message);
        Assert.Equal(ended.Message, RemoteConnectionText.Failure(target, ended));
        Assert.Equal("Couldn't reach Home. Trying again shortly.", RemoteConnectionText.Failure(target, new IOException("raw text with http://x")));
        Assert.Equal("Home didn't answer in time. Trying again shortly.", RemoteConnectionText.Failure(target, new TimeoutException()));
        Assert.Contains("Inbound server", RemoteConnectionText.Failure(target, new InvalidDataException()));
    }

    [Fact]
    [Trait("Platform", "Windows")] // DPAPI
    public void ATokenIsKeptSealedForThisUserAndOpensOnlyForItsServer()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = NewFolder();
        var path = Path.Combine(folder, ServerStore.FileName);
        var store = new ServerStore(path);
        const string token = "dmrc_invented-token-for-a-test";

        var saved = store.Add("  Home\u0007 server ", new Uri("http://homeserver:8080/"), token, "tok1", plainHttpConfirmed: true, HostKinds.Server);
        var other = store.Add("", new Uri("https://nas.example.org/"), "dmrc_another-invented-token", "tok2", plainHttpConfirmed: false, HostKinds.Windows);

        var text = File.ReadAllText(path);
        Assert.DoesNotContain(token, text);
        Assert.Contains(DpapiSecretProtector.BlobPrefix, text);
        Assert.False(File.Exists(path + ".tmp"));

        var reopened = new ServerStore(path);
        var target = reopened.Target(saved.Id);
        Assert.NotNull(target);
        Assert.Equal(token, target.Token);
        Assert.Equal("Home server", target.Name);
        Assert.Equal("http://homeserver:8080/", target.Address!.ToString());
        Assert.True(target.PlainHttpConfirmed);
        Assert.Equal("nas.example.org", reopened.Find(other.Id)!.Name);

        // A sealed token moved to another entry doesn't open there: it is bound to its server's id.
        var json = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        var servers = json["servers"]!.AsArray();
        servers[0]!["sealedToken"] = other.SealedToken;
        servers[1]!["sealedToken"] = saved.SealedToken;
        File.WriteAllText(path, json.ToJsonString());
        Assert.Null(reopened.Target(saved.Id));
        Assert.Null(reopened.Target(other.Id));
    }

    [Fact]
    [Trait("Platform", "Windows")] // DPAPI
    public void AServerCanBeRenamedPairedAgainAndRemoved()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(NewFolder(), ServerStore.FileName);
        var store = new ServerStore(path);
        var saved = store.Add("Home", new Uri("http://homeserver:8080/"), "dmrc_first-invented", "t1", plainHttpConfirmed: false, HostKinds.Server);

        store.SetLastShown(saved.Id);
        Assert.Equal(saved.Id, store.LastShown);
        store.Update(saved.Id, name: "Attic", plainHttpConfirmed: true);
        store.Repair(saved.Id, new Uri("http://attic:8080/"), "dmrc_second-invented", "t2", plainHttpConfirmed: true, hostKind: null);

        var target = store.Target(saved.Id)!;
        Assert.Equal("Attic", target.Name);
        Assert.Equal("dmrc_second-invented", target.Token);
        Assert.Equal("http://attic:8080/", target.Address!.ToString());
        Assert.Equal(HostKinds.Server, store.Find(saved.Id)!.HostKind);

        store.Remove(saved.Id);
        Assert.Empty(store.Load());
        Assert.Null(store.LastShown);
        Assert.Null(store.Target(saved.Id));
        store.SetLastShown("not-a-saved-id");
        Assert.Null(store.LastShown);
    }

    [Fact]
    public void ADamagedOrForeignListReadsAsEmptyOrWithoutTheBadEntries()
    {
        var path = Path.Combine(NewFolder(), ServerStore.FileName);
        var store = new ServerStore(path, new PlainProtector());
        File.WriteAllText(path, "{ not json");
        Assert.Empty(store.Load());

        var good = new string('c', 32);
        File.WriteAllText(path, $$"""
            { "version": 1, "lastShown": "{{good}}", "servers": [
              { "id": "{{good}}", "name": "Home", "address": "http://homeserver:8080/", "sealedToken": "plain:dmrc_x" },
              { "id": "{{good}}", "name": "Twice", "address": "http://homeserver:8080/" },
              { "id": "short", "name": "Bad id", "address": "http://homeserver:8080/" },
              { "id": "{{new string('d', 32)}}", "name": "Bad address", "address": "ftp://homeserver/" }
            ] }
            """);
        var servers = store.Load();
        Assert.Equal("Home", Assert.Single(servers).Name);
        Assert.Equal(good, store.LastShown);
        Assert.Equal("dmrc_x", store.Target(good)!.Token);
    }

    [Fact]
    public void ANameIsCleanedAndShortened()
    {
        var address = new Uri("http://homeserver:8080/");
        Assert.Equal("homeserver", ServerStore.CleanName("  \t ", address));
        Assert.Equal("Home", ServerStore.CleanName("Ho\nme", address));
        Assert.Equal(80, ServerStore.CleanName(new string('x', 200), address).Length);
    }

    [Fact]
    public void AnotherServersFilesAreNeverTouchedFromThisPc()
    {
        Assert.True(RemoteDataRules.MayUseFiles(remote: false));
        Assert.False(RemoteDataRules.MayUseFiles(remote: true));
        Assert.Null(RemoteDataRules.IconPath(remote: true, @"C:\downloads\movie.mkv"));
        Assert.Equal(@"C:\downloads\movie.mkv", RemoteDataRules.IconPath(remote: false, @"C:\downloads\movie.mkv"));
        Assert.False(RemoteDataRules.MayPlaySoundFiles(remote: true));
        Assert.True(RemoteDataRules.MayPlaySoundFiles(remote: false));
        Assert.True(RemoteDataRules.HostExitQuitsApp(remote: false));
        Assert.False(RemoteDataRules.HostExitQuitsApp(remote: true));
        Assert.True(RemoteDataRules.UsesServerFolderBrowser(remote: true));
        Assert.False(RemoteDataRules.UsesServerFolderBrowser(remote: false));

        Assert.Null(RemoteDataRules.LocalFileProblem(remote: false, "container", "Home"));
        Assert.Null(RemoteDataRules.LocalFileProblem(remote: true, "torrent", "Home"));
        Assert.Contains("Home", RemoteDataRules.LocalFileProblem(remote: true, "container", "Home"));
    }

    [Fact]
    public void OpenOpenWithAndOpenFolderAreOffForAnotherServersDownload()
    {
        var here = FileMenuRules.Plan(singleFile: true, package: false, sideRows: 0, removableSideRows: 0);
        var there = FileMenuRules.Plan(singleFile: true, package: false, sideRows: 0, removableSideRows: 0, remote: true);

        Assert.Null(here.ShellReason);
        Assert.Equal(RemoteDataRules.OnAnotherServer, there.ShellReason);
        // Delete, Redownload and the like still work: the server does them.
        Assert.Null(there.FileReason);
        Assert.Null(there.RemoveReason);
        // A reason of its own still comes first.
        var removed = FileMenuRules.Plan(singleFile: true, package: false, sideRows: 0, removableSideRows: 0, fileRemoved: true, remote: true);
        Assert.Equal(removed.OpenReason, removed.ShellReason);
    }

    [Theory]
    [InlineData(true, null, null, true, true)]
    [InlineData(true, null, false, true, false)]
    [InlineData(true, null, null, false, false)]
    [InlineData(false, null, null, true, false)]
    [InlineData(false, "cli", null, true, true)]
    [InlineData(false, "cli", false, true, false)]
    public void TheWindowOpensTheCompletionWindowForAnotherServerItself(bool remote, string? source, bool? job, bool setting, bool opens)
        => Assert.Equal(opens, RemoteDataRules.WindowOpensCompletion(remote, source, job, setting));

    [Fact]
    public void AServersOptionsShowOnlyWhatItsHelloAllows()
    {
        var docker = RemoteSettingsRules.PagesFor(["jobs", "settings", "folders"], HostKinds.Server);
        Assert.True(docker.Folders && docker.Proxy && docker.SiteLogins && docker.AutoResume && docker.Connection);
        Assert.False(docker.ProgramSettings);
        Assert.True(RemoteSettingsRules.PagesFor(["programSettings"], HostKinds.Server).ProgramSettings);

        var windows = RemoteSettingsRules.PagesFor(["jobs", "settings"], HostKinds.Windows);
        Assert.False(windows.Folders || windows.Proxy || windows.SiteLogins || windows.ProgramSettings || windows.AutoResume);
        Assert.True(windows.Connection && windows.Categories);
        Assert.Equal(windows, RemoteSettingsRules.PagesFor(null, null));
    }

    [Fact]
    public void ASaveOnAServersOptionsLeavesThisPcsChoicesAsTheServerHadThem()
    {
        var atOpen = new SettingsDto
        {
            Theme = "Dark",
            LaunchAtStartup = true,
            ClipboardMonitoring = false,
            BrowserIntegrationEnabled = false,
            AutoCheckUpdates = false,
            UpdateCheckIntervalHours = 72,
            NotifyOnCompletion = false,
            ShowCompletionDialog = false,
            PlayCompletionSound = false,
            MaxConnectionsPerHost = 4,
        };
        atOpen.EventSoundFiles["complete"] = "/srv/sound.wav";
        var edited = new SettingsDto { Theme = "Light", LaunchAtStartup = false, ClipboardMonitoring = true, MaxConnectionsPerHost = 6, UpdateCheckIntervalHours = 1 };

        var sent = RemoteSettingsRules.KeepServerValues(edited, atOpen);

        Assert.Equal("Dark", sent.Theme);
        Assert.True(sent.LaunchAtStartup);
        Assert.False(sent.ClipboardMonitoring);
        Assert.False(sent.BrowserIntegrationEnabled);
        Assert.False(sent.AutoCheckUpdates);
        Assert.Equal(72, sent.UpdateCheckIntervalHours);
        Assert.False(sent.NotifyOnCompletion);
        Assert.False(sent.ShowCompletionDialog);
        Assert.False(sent.PlayCompletionSound);
        Assert.Equal("/srv/sound.wav", sent.EventSoundFiles["complete"]);
        // The server's own settings are what was edited.
        Assert.Equal(6, sent.MaxConnectionsPerHost);
        // Every field this PC keeps is one KeepServerValues puts back.
        Assert.Equal(11, RemoteSettingsRules.ThisPcFields.Length);
    }

    [Fact]
    public void AProgressWindowForAnotherServerIsAnotherWindowAndCarriesNoToken()
    {
        var job = Guid.NewGuid();
        var id = new string('e', 32);
        var target = ServerTarget.Remote(id, "Home", new Uri("http://homeserver:8080/"), "dmrc_invented", plainHttpConfirmed: true);

        var arguments = WindowInstance.Arguments("job", job, target, manual: true);
        Assert.Equal($"--job {job:D} --manual --server {id}", arguments);
        Assert.DoesNotContain("dmrc", arguments);
        Assert.Equal($"--package {job:D}", WindowInstance.Arguments("package", job, ServerTarget.Local));

        Assert.Equal(id, WindowInstance.ServerId(arguments.Split(' ')));
        Assert.Equal(id, WindowInstance.ServerId(["--server", id.ToUpperInvariant()]));
        Assert.Null(WindowInstance.ServerId(["--server"]));
        Assert.Null(WindowInstance.ServerId(["--server", "..\\..\\x"]));
        Assert.Null(WindowInstance.ServerId(["--job", job.ToString()]));

        var local = WindowInstance.EventName("key", update: false, job, null, null);
        var remote = WindowInstance.EventName("key", update: false, job, null, id);
        Assert.Equal($"Local\\DownloadManager.UI.Show.key.Job.{job:D}", local);
        Assert.Equal(local + ".Server." + id, remote);
        Assert.Equal("Local\\DownloadManager.UI.Show.key", WindowInstance.EventName("key", false, null, null, id));
        Assert.Equal("Local\\DownloadManager.UI.Show.key.Update", WindowInstance.EventName("key", true, null, null, id));
    }

    private static string NewFolder()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "test-work", "servers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>For the parts that don't need DPAPI: a "seal" anyone can read, never used outside this test.</summary>
    private sealed class PlainProtector : ISecretProtector
    {
        public string Prefix => "plain:";

        public string Protect(string plaintext, ReadOnlySpan<byte> purpose) => Prefix + plaintext;

        public string? Unprotect(string protectedValue, ReadOnlySpan<byte> purpose) =>
            protectedValue.StartsWith(Prefix, StringComparison.Ordinal) ? protectedValue[Prefix.Length..] : null;
    }
}
