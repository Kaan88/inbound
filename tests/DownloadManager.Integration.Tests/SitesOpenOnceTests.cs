using System.Security.Cryptography;
using DownloadManager.Contracts;
using DownloadManager.Runtime.Sites;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.11 R1, "Open an address once" (sites-rework.md, R1 design item 7): the address rule, and the unsaved site on a real
/// <see cref="SitesCommands"/> with no rclone and a fake key probe, so nothing connects.
/// </summary>
public sealed class SitesOpenOnceTests : IAsyncLifetime
{
    private const string Secret = "open-once-secret-4471";
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "sites-open-once-" + Guid.NewGuid().ToString("N"));
    private readonly List<SitesCommands> _commands = [];
    private readonly SshHostKey _key = new("ssh-ed25519", Blob("ssh-ed25519", 32));

    private string State => Path.Combine(_root, "state");
    private string SitesFolder => Path.Combine(State, "sites");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(State);
        PortableSecrets.UseAKeyFileOffWindows();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var commands in _commands) await commands.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    // ---- The address rule ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("sftp://me@nas.home.lan:2222/media/isos", "sftp", "nas.home.lan", 2222, "me", null, "/media/isos")]
    [InlineData("sftp://nas.home.lan", "sftp", "nas.home.lan", 22, "", null, "/")]
    [InlineData("ftp://files.example.org/pub/", "ftp", "files.example.org", 21, "", "none", "/pub/")]
    [InlineData("ftps://u@files.example.org", "ftp", "files.example.org", 21, "u", "explicit", "/")]
    [InlineData("sftp://me@[::1]:22022/x%20y", "sftp", "::1", 22022, "me", null, "/x y")]
    public void AnAddressIsReadAsASite(string text, string kind, string host, int port, string user, string? tls, string folder)
    {
        var address = SiteAddress.Parse(text, out var problem);
        Assert.Null(problem);
        Assert.Equal((kind, host, port, user, tls, folder), (address!.Kind, address.Host, address.Port, address.User, address.Tls, address.Folder));
    }

    [Fact]
    public void AWebDavAddressKeepsItsPathInTheServersAddressWithoutTheUser()
    {
        var address = SiteAddress.Parse("https://me@cloud.example.net/remote.php/dav/files/me", out _)!;
        Assert.Equal(("webdav", "https://cloud.example.net/remote.php/dav/files/me", "me", 443, "/"),
            (address.Kind, address.Url, address.User, address.Port, address.Folder));
        Assert.True(address.Encrypted);
        Assert.False(SiteAddress.Parse("http://cloud.example.net/dav", out _)!.Encrypted);
        Assert.False(SiteAddress.Parse("ftp://files.example.org", out _)!.Encrypted);
    }

    [Theory]
    [InlineData("sftp://me:pw@nas/")]          // a password
    [InlineData("sftp://me:@nas/")]            // an empty one is still one
    [InlineData("ftp://files.example.org/?a=1")]
    [InlineData("https://cloud.example.net/#x")]
    [InlineData("smb://nas/share")]
    [InlineData("nas.home.lan")]
    [InlineData("")]
    [InlineData("sftp://nas/a\u0001b")]
    public void AnAddressThatCantBeOpenedIsRefusedWithAReason(string text)
    {
        Assert.Null(SiteAddress.Parse(text, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void AVeryLongAddressIsRefused()
    {
        Assert.Null(SiteAddress.Parse("sftp://nas/" + new string('a', SiteAddress.MaxLength), out var problem));
        Assert.NotNull(problem);
    }

    // ---- The unsaved site --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAddressOpensAsAnUnsavedSiteThatIsListedButNeverWritten()
    {
        var commands = await OnAsync();
        var reply = await Send(commands, SitesMessageTypes.OpenOnce, new SiteOpenOnceRequest { Address = "ftp://me@files.example.org/pub", Password = Secret });
        var opened = reply.GetPayload<SiteResultDto>();
        Assert.True(opened.Ok, opened.Message);
        var site = opened.Site!;
        Assert.True(site.Unsaved);
        Assert.False(site.Encrypted); // ftp:// is plain, said before anything is sent
        Assert.Equal(("files.example.org", "files.example.org", 21, "me", "/pub", "none"), (site.Name, site.Host, site.Port, site.User, site.RemoteFolder, site.Tls));
        Assert.True(site.HasPassword);
        Assert.DoesNotContain(Secret, reply.Payload!.Value.GetRawText());
        Assert.Empty(SiteFiles());

        var listed = Assert.Single((await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest)).Sites);
        Assert.Equal((site.Id, true), (listed.Id, listed.Unsaved));

        // The same address again is the same site, not a second one.
        var again = await Call<SiteResultDto>(commands, SitesMessageTypes.OpenOnce, new SiteOpenOnceRequest { Address = "ftp://me@FILES.example.org/other" });
        Assert.Equal(site.Id, again.Site!.Id);

        // A Host restart forgets it.
        var restarted = await OnAsync();
        Assert.Empty((await Call<SitesListDto>(restarted, SitesMessageTypes.ListRequest)).Sites);
    }

    [Fact]
    public async Task AnUnsavedSftpSiteAsksForTheKeyFirstAndThenForAPasswordWithoutTryingTheAgent()
    {
        var commands = await OnAsync();
        var site = (await Call<SiteResultDto>(commands, SitesMessageTypes.OpenOnce, new SiteOpenOnceRequest { Address = "sftp://me@nas.home.lan/media" })).Site!;
        Assert.Empty(site.HostKeys);

        var first = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = site.Id });
        Assert.Equal(SiteProblems.HostKeyNotAccepted, first.Problem);

        var trusted = await Call<SiteResultDto>(commands, SitesMessageTypes.Trust, new SiteTrustRequest { Id = site.Id, Entries = [_key.HostKeysEntry] });
        Assert.True(trusted.Ok, trusted.Message);
        Assert.True(trusted.Site!.Unsaved);
        Assert.Empty(SiteFiles()); // trusting a key changes the memory copy only

        // No password: refused before rclone is asked (it would offer this PC's ssh-agent keys to the typed server).
        var second = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = site.Id });
        Assert.Equal(SiteProblems.PasswordNeeded, second.Problem);

        var withPassword = await Call<SiteResultDto>(commands, SitesMessageTypes.OpenOnce, new SiteOpenOnceRequest { Id = site.Id, Password = Secret });
        Assert.True(withPassword.Ok, withPassword.Message);
        Assert.True(withPassword.Site!.HasPassword);
        Assert.Single(withPassword.Site.HostKeys); // the accepted key stays
    }

    [Fact]
    public async Task RememberWritesTheSiteWithItsPasswordAndKeyAndCloseForgetsOnlyUnsavedSites()
    {
        var commands = await OnAsync();
        var site = (await Call<SiteResultDto>(commands, SitesMessageTypes.OpenOnce,
            new SiteOpenOnceRequest { Address = "sftp://me@nas.home.lan/media", Password = Secret })).Site!;
        await Call<SiteResultDto>(commands, SitesMessageTypes.Trust, new SiteTrustRequest { Id = site.Id, Entries = [_key.HostKeysEntry] });

        var kept = await Call<SiteResultDto>(commands, SitesMessageTypes.Keep, new SiteKeepRequest { Id = site.Id, Name = "NAS" });
        Assert.True(kept.Ok, kept.Message);
        Assert.Equal(("NAS", false, true), (kept.Site!.Name, kept.Site.Unsaved, kept.Site.HasPassword));
        var file = Assert.Single(SiteFiles());
        var text = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain("unsaved", text, StringComparison.OrdinalIgnoreCase);
        // Read back as the store reads it (the JSON escapes a + in the key's base64, so the text can't be searched).
        var stored = await new SiteStore(State).GetAsync(site.Id);
        Assert.Equal([_key.HostKeysEntry], stored!.HostKeys);
        Assert.False(stored.Unsaved);

        // Now a saved site: not kept twice, not closed, and its password is not reachable through openOnce.
        Assert.False((await Call<SiteResultDto>(commands, SitesMessageTypes.Keep, new SiteKeepRequest { Id = site.Id })).Ok);
        Assert.False((await Call<SiteResultDto>(commands, SitesMessageTypes.Close, new SiteIdRequest { Id = site.Id })).Ok);
        Assert.False((await Call<SiteResultDto>(commands, SitesMessageTypes.OpenOnce, new SiteOpenOnceRequest { Id = site.Id, Password = "x" })).Ok);
        Assert.Single(SiteFiles());

        // Survives a restart, as any saved site.
        var restarted = await OnAsync();
        var listed = Assert.Single((await Call<SitesListDto>(restarted, SitesMessageTypes.ListRequest)).Sites);
        Assert.Equal(("NAS", false), (listed.Name, listed.Unsaved));

        var other = (await Call<SiteResultDto>(restarted, SitesMessageTypes.OpenOnce, new SiteOpenOnceRequest { Address = "https://cloud.example.net/dav" })).Site!;
        var closed = await Call<SiteResultDto>(restarted, SitesMessageTypes.Close, new SiteIdRequest { Id = other.Id });
        Assert.True(closed.Ok, closed.Message);
        Assert.Equal(site.Id, Assert.Single((await Call<SitesListDto>(restarted, SitesMessageTypes.ListRequest)).Sites).Id);
        Assert.Single(SiteFiles());
    }

    [Fact]
    public async Task NoRcloneRemoteIsBuiltForAnUnsavedSftpSiteWithoutAPassword()
    {
        // Every caller (browse, transfers, folder changes, device streams) builds its remote here, so none can let rclone
        // offer this PC's ssh-agent keys to a typed server.
        var site = new SiteRecord
        {
            Id = SiteStore.NewId(), Name = "nas", Kind = SiteKinds.Sftp, Host = "nas.home.lan", User = "me",
            HostKeys = [_key.HostKeysEntry], Unsaved = true,
        };
        var refused = await Assert.ThrowsAsync<SiteStoreException>(() => SiteRemote.BuildAsync(site, null!));
        Assert.Contains("password", refused.Message);
    }

    [Fact]
    public async Task ASiteSavedFromTheWebMustHoldItsOwnPasswordOrKey()
    {
        // SITES.11: OwnSignInOnly (set by the Host for every web session) refuses an SFTP site rclone would sign in to
        // with this PC's ssh-agent keys; at this PC the same site saves as before.
        var commands = await OnAsync();
        SiteSaveRequest Box(bool web, string? password) => new()
        {
            Name = "Box", Kind = SiteKinds.Sftp, Host = "srv.example", Port = 22, User = "me",
            AuthMethod = SiteAuthMethods.Password, Password = password, RemoteFolder = "/", OwnSignInOnly = web,
        };

        var refused = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Box(web: true, password: null));
        Assert.False(refused.Ok);
        Assert.Contains("password", refused.Message);
        Assert.Empty(SiteFiles());

        var withPassword = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Box(web: true, password: Secret));
        Assert.True(withPassword.Ok, withPassword.Message);

        // An edit from the web that empties the password is refused too; the site keeps it.
        var emptied = Box(web: true, password: "");
        emptied.Id = withPassword.Site!.Id;
        Assert.False((await Call<SiteResultDto>(commands, SitesMessageTypes.Save, emptied)).Ok);
        Assert.True((await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest)).Sites.Single().HasPassword);

        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Box(web: false, password: null))).Ok);
    }

    [Fact]
    public async Task AStoredKeyStaysWithItsServerWhenTheWebEditsTheSite()
    {
        // Review: a web session must not point a key site saved at this PC at a server it chose and sign in there with the key.
        var commands = await OnAsync();
        SiteSaveRequest Keyed(string host, bool web, string? key) => new()
        {
            Name = "Keyed", Kind = SiteKinds.Sftp, Host = host, Port = 22, User = "me", AuthMethod = SiteAuthMethods.Key,
            PrivateKey = key, RemoteFolder = "/", OwnSignInOnly = web,
        };
        var saved = (await Call<SiteResultDto>(commands, SitesMessageTypes.Save,
            Keyed("srv.example", web: false, "-----BEGIN OPENSSH PRIVATE KEY-----\ninvented\n-----END OPENSSH PRIVATE KEY-----"))).Site!;

        var moved = Keyed("elsewhere.example", web: true, key: null);
        moved.Id = saved.Id;
        var refused = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, moved);
        Assert.False(refused.Ok);
        Assert.Equal("srv.example", (await new SiteStore(State).GetAsync(saved.Id))!.Host);

        // The same server from the web keeps it; another server from this PC keeps it, as before.
        var same = Keyed("srv.example", web: true, key: null);
        same.Id = saved.Id;
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.Save, same)).Ok);
        var here = Keyed("elsewhere.example", web: false, key: null);
        here.Id = saved.Id;
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.Save, here)).Ok);
    }

    [Fact]
    public async Task AnAddressWithoutAPasswordCantBeRemembered()
    {
        // Review: kept, it would no longer be guarded as unsaved and rclone would offer this PC's ssh-agent keys.
        var commands = await OnAsync();
        var site = (await Call<SiteResultDto>(commands, SitesMessageTypes.OpenOnce, new SiteOpenOnceRequest { Address = "sftp://me@nas.home.lan/" })).Site!;
        var kept = await Call<SiteResultDto>(commands, SitesMessageTypes.Keep, new SiteKeepRequest { Id = site.Id });
        Assert.False(kept.Ok);
        Assert.Contains("password", kept.Message);
        Assert.Empty(SiteFiles());
    }

    [Fact]
    public async Task AtMostEightAddressesAreOpenAtOnce()
    {
        var commands = await OnAsync();
        for (var i = 0; i < SiteStore.MaxUnsaved; i++)
            Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.OpenOnce, new SiteOpenOnceRequest { Address = $"ftp://host{i}.example" })).Ok);
        var ninth = await Call<SiteResultDto>(commands, SitesMessageTypes.OpenOnce, new SiteOpenOnceRequest { Address = "ftp://host9.example" });
        Assert.False(ninth.Ok);
        Assert.Contains("close one", ninth.Message);
        Assert.Equal(SiteStore.MaxUnsaved, (await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest)).Sites.Count);
    }

    [Fact]
    public async Task FtpsOnPort990IsImplicitTls()
    {
        var commands = await OnAsync();
        var site = (await Call<SiteResultDto>(commands, SitesMessageTypes.OpenOnce, new SiteOpenOnceRequest { Address = "ftps://files.example.org:990" })).Site!;
        Assert.Equal((SiteTlsModes.Implicit, 990, true), (site.Tls, site.Port, site.Encrypted));
    }

    [Fact]
    public async Task AnEditOfAnUnsavedSiteStaysInMemory()
    {
        var commands = await OnAsync();
        var site = (await Call<SiteResultDto>(commands, SitesMessageTypes.OpenOnce, new SiteOpenOnceRequest { Address = "ftp://files.example.org" })).Site!;
        var edited = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Id = site.Id, Name = "Renamed", Kind = SiteKinds.Ftp, Host = "files.example.org", Port = 21, Tls = SiteTlsModes.None,
            AuthMethod = SiteAuthMethods.Password, RemoteFolder = "/",
        });
        Assert.True(edited.Ok, edited.Message);
        Assert.Equal(("Renamed", true), (edited.Site!.Name, edited.Site.Unsaved));
        Assert.Empty(SiteFiles());
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------------

    private IEnumerable<string> SiteFiles() => Directory.Exists(SitesFolder)
        ? Directory.EnumerateFiles(SitesFolder, "*.json").Where(f => SiteStore.IsId(Path.GetFileNameWithoutExtension(f)))
        : [];

    private async Task<SitesCommands> OnAsync()
    {
        IReadOnlyList<SshHostKey> keys = [_key];
        var commands = new SitesCommands(State, _ => { }, _ => null, probe: (_, _, _) => Task.FromResult(keys));
        _commands.Add(commands);
        var status = await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true });
        Assert.True(status.Enabled);
        return commands;
    }

    private static async Task<IpcMessage> Send(SitesCommands commands, string type, object payload) =>
        await commands.HandleAsync(IpcMessage.Create(type, payload), CancellationToken.None);

    private static async Task<T> Call<T>(SitesCommands commands, string type, object? payload = null) =>
        (await Send(commands, type, payload ?? new { })).GetPayload<T>();

    private static byte[] Blob(string type, int keyBytes)
    {
        var name = System.Text.Encoding.ASCII.GetBytes(type);
        var blob = new byte[4 + name.Length + keyBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(blob, (uint)name.Length);
        name.CopyTo(blob, 4);
        RandomNumberGenerator.Fill(blob.AsSpan(4 + name.Length));
        return blob;
    }
}
