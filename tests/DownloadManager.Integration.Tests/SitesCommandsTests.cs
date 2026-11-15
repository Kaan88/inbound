using System.Security.Cryptography;
using DownloadManager.Contracts;
using DownloadManager.Runtime.Sites;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.2 (remote-sites.md, "The switch" and "Credentials and trust"): <c>sites.*</c> answered by a real
/// <see cref="SitesCommands"/> on its own state folder. Rclone is never found and the host-key probe is a fake, so nothing
/// starts and nothing connects.
/// </summary>
public sealed class SitesCommandsTests : IAsyncLifetime
{
    private const string Secret = "hunter2-Sites-secret-91";
    private const string Host = "srv.example";

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "sites-commands-" + Guid.NewGuid().ToString("N"));
    private readonly List<SitesCommands> _commands = [];
    private readonly List<string> _log = [];
    private readonly SshHostKey[] _keys =
    [
        new("ssh-ed25519", Blob("ssh-ed25519", 32)),
        new("ssh-rsa", Blob("ssh-rsa", 260)),
    ];

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

    [Fact]
    public async Task AFreshInstallIsOffAndRefusesEverythingButTheSwitch()
    {
        var commands = Open();

        var status = await Call<SitesStatusDto>(commands, SitesMessageTypes.Get);
        Assert.False(status.Enabled);
        Assert.Equal(SitesRcloneStates.Off, status.Rclone);

        var refused = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Site());
        Assert.False(refused.Ok);
        Assert.Equal(SiteProblems.Off, refused.Problem);
        Assert.False(Directory.Exists(SitesFolder) && Directory.EnumerateFiles(SitesFolder, "*", SearchOption.AllDirectories).Any());

        var list = await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest);
        Assert.Empty(list.Sites);

        var probe = await Call<SiteProbeResultDto>(commands, SitesMessageTypes.Probe, new SiteProbeRequest { Host = Host, Port = 22 });
        Assert.False(probe.Ok);
    }

    [Fact]
    public async Task TheSwitchPersistsAcrossAHostRestart()
    {
        var first = Open();
        var status = await Call<SitesStatusDto>(first, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true });
        Assert.True(status.Enabled);

        var second = Open();
        Assert.True((await Call<SitesStatusDto>(second, SitesMessageTypes.Get)).Enabled);
    }

    [Fact]
    public async Task AnOwnRclonePathMustBeAFullPath()
    {
        var commands = Open();

        var status = await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled,
            new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = "relative/rclone" });
        Assert.False(status.Enabled);
        Assert.Null(status.OwnRclonePath);
        Assert.Contains("full path", status.Error);

        var reopened = Open();
        var after = await Call<SitesStatusDto>(reopened, SitesMessageTypes.Get);
        Assert.False(after.Enabled);
        Assert.Null(after.OwnRclonePath);
    }

    [Fact]
    public async Task ASavedPasswordIsStoredSealedAndNeverReturned()
    {
        var commands = Open();
        await On(commands);

        var reply = await Send(commands, SitesMessageTypes.Save, Site());
        var result = reply.GetPayload<SiteResultDto>();
        Assert.True(result.Ok);
        Assert.NotNull(result.Site);
        Assert.True(result.Site.HasPassword);
        Assert.DoesNotContain(Secret, reply.Payload!.Value.GetRawText());

        foreach (var file in Directory.EnumerateFiles(SitesFolder, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(file));

        var list = await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest);
        var listed = Assert.Single(list.Sites);
        Assert.Equal(result.Site.Id, listed.Id);
        Assert.Equal("Office box", listed.Name);
        Assert.True(listed.HasPassword);
    }

    [Fact]
    public async Task AnEditWithANullPasswordKeepsItAndAnEmptyOneRemovesIt()
    {
        var commands = Open();
        await On(commands);
        var saved = (await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Site())).Site!;

        var kept = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Site(id: saved.Id, name: "Renamed", password: null));
        Assert.True(kept.Ok);
        Assert.True(kept.Site!.HasPassword);

        var cleared = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Site(id: saved.Id, name: "Renamed", password: ""));
        Assert.True(cleared.Ok);
        Assert.False(cleared.Site!.HasPassword);
    }

    [Fact]
    public async Task ProbeThenTrustStoresTheServersKeys()
    {
        var commands = Open(probe: Probe());
        await On(commands);
        var saved = (await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Site())).Site!;

        var probe = await Call<SiteProbeResultDto>(commands, SitesMessageTypes.Probe, new SiteProbeRequest { Host = Host, Port = 22 });
        Assert.True(probe.Ok);
        Assert.Equal(["ssh-ed25519", "ssh-rsa"], probe.Keys.Select(k => k.Type));
        Assert.All(probe.Keys, key => Assert.StartsWith("SHA256:", key.Fingerprint));

        var trusted = await Call<SiteResultDto>(commands, SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = saved.Id, Entries = [.. probe.Keys.Select(k => k.Entry)] });
        Assert.True(trusted.Ok);
        Assert.Equal(2, trusted.Site!.HostKeys.Count);
        Assert.Equal(probe.Keys.Select(k => k.Fingerprint), trusted.Site.HostKeys.Select(k => k.Fingerprint));
    }

    [Fact]
    public async Task AChangedHostForgetsTheKeysButARenameKeepsThem()
    {
        var commands = Open(probe: Probe());
        await On(commands);
        var saved = (await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Site())).Site!;
        var probe = await Call<SiteProbeResultDto>(commands, SitesMessageTypes.Probe, new SiteProbeRequest { Host = Host, Port = 22 });
        await Call<SiteResultDto>(commands, SitesMessageTypes.Trust, new SiteTrustRequest { Id = saved.Id, Entries = [.. probe.Keys.Select(k => k.Entry)] });

        var renamed = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Site(id: saved.Id, name: "Renamed box"));
        Assert.True(renamed.Ok);
        Assert.Equal(2, renamed.Site!.HostKeys.Count);

        var moved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Site(id: saved.Id, host: "other.example"));
        Assert.True(moved.Ok);
        Assert.Empty(moved.Site!.HostKeys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task TrustRefusesAnyCountOfKeysOutsideOneToSixteen(int count)
    {
        var commands = Open(probe: Probe());
        await On(commands);
        var saved = (await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Site())).Site!;
        var entry = _keys[0].HostKeysEntry;

        var refused = await Call<SiteResultDto>(commands, SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = saved.Id, Entries = Enumerable.Repeat(entry, count).ToList() });

        Assert.False(refused.Ok);
        Assert.Contains("between 1 and 16", refused.Message);
        Assert.Empty((await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest)).Sites.Single().HostKeys);
    }

    [Fact]
    public async Task TrustRefusesAnEntryThatIsNotAKey()
    {
        var commands = Open();
        await On(commands);
        var saved = (await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Site())).Site!;

        var refused = await Call<SiteResultDto>(commands, SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = saved.Id, Entries = ["not a key"] });

        Assert.False(refused.Ok);
        Assert.Empty((await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest)).Sites.Single().HostKeys);
    }

    [Fact]
    public async Task AKeySiteWithoutAPrivateKeyIsRefused()
    {
        var commands = Open();
        await On(commands);

        var keySite = Site(password: null);
        keySite.AuthMethod = SiteAuthMethods.Key;
        var refused = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, keySite);

        Assert.False(refused.Ok);
        Assert.Contains("private key", refused.Message);
        Assert.Empty((await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest)).Sites);
    }

    [Fact]
    public async Task ARemovedSiteIsGoneAndRemovingItAgainIsRefused()
    {
        var commands = Open();
        await On(commands);
        var saved = (await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Site())).Site!;

        var removed = await Call<SiteResultDto>(commands, SitesMessageTypes.Remove, new SiteIdRequest { Id = saved.Id });
        Assert.True(removed.Ok);
        Assert.Empty((await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest)).Sites);

        var again = await Call<SiteResultDto>(commands, SitesMessageTypes.Remove, new SiteIdRequest { Id = saved.Id });
        Assert.False(again.Ok);
    }

    [Fact]
    public async Task TestNeedsAnAcceptedKeyFirst()
    {
        var commands = Open();
        await On(commands);
        var saved = (await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Site())).Site!;

        var result = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = saved.Id });

        Assert.False(result.Ok);
        Assert.Equal(SiteProblems.HostKeyNotAccepted, result.Problem);
    }

    [Fact]
    public async Task TestWithoutRcloneSaysSoAndTheStatusReportsMissing()
    {
        var commands = Open(findRclone: _ => null, probe: Probe());
        await On(commands);
        var saved = (await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Site())).Site!;
        await Call<SiteProbeResultDto>(commands, SitesMessageTypes.Probe, new SiteProbeRequest { Host = Host, Port = 22 });
        await Call<SiteResultDto>(commands, SitesMessageTypes.Trust, new SiteTrustRequest { Id = saved.Id, Entries = [_keys[0].HostKeysEntry] });

        var result = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = saved.Id });

        Assert.False(result.Ok);
        Assert.Contains("rclone", result.Message, StringComparison.OrdinalIgnoreCase);
        var status = await Call<SitesStatusDto>(commands, SitesMessageTypes.Get);
        Assert.Equal(SitesRcloneStates.Missing, status.Rclone);
    }

    [Fact]
    public async Task AProbeFailureIsReportedAsAResultNotAnException()
    {
        var commands = Open(probe: Probe());
        await On(commands);

        var probe = await Call<SiteProbeResultDto>(commands, SitesMessageTypes.Probe, new SiteProbeRequest { Host = "bad.example", Port = 22 });

        Assert.False(probe.Ok);
        Assert.Equal("nope", probe.Message);
        Assert.Empty(probe.Keys);
    }

    [Fact]
    public async Task TheChangedNoticeFollowsEachChangeAndNotARefusal()
    {
        var commands = Open(probe: Probe());
        var changes = 0;
        commands.Changed += (_, _) => changes++;

        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true });
        Assert.Equal(1, changes);

        var saved = (await Call<SiteResultDto>(commands, SitesMessageTypes.Save, Site())).Site!;
        Assert.Equal(2, changes);

        var probe = await Call<SiteProbeResultDto>(commands, SitesMessageTypes.Probe, new SiteProbeRequest { Host = Host, Port = 22 });
        await Call<SiteResultDto>(commands, SitesMessageTypes.Trust, new SiteTrustRequest { Id = saved.Id, Entries = [.. probe.Keys.Select(k => k.Entry)] });
        Assert.Equal(3, changes);

        await Call<SiteResultDto>(commands, SitesMessageTypes.Remove, new SiteIdRequest { Id = saved.Id });
        Assert.Equal(4, changes);

        await Call<SiteResultDto>(commands, SitesMessageTypes.Remove, new SiteIdRequest { Id = saved.Id });
        Assert.Equal(4, changes);
    }

    private SitesCommands Open(Func<string?, string?>? findRclone = null,
        Func<string, int, CancellationToken, Task<IReadOnlyList<SshHostKey>>>? probe = null)
    {
        var commands = new SitesCommands(State, message => { lock (_log) _log.Add(message); },
            findRclone ?? (_ => null), probe: probe ?? Probe());
        _commands.Add(commands);
        return commands;
    }

    /// <summary>A fake probe: the two keys for <see cref="Host"/>, a failure for "bad.example".</summary>
    private Func<string, int, CancellationToken, Task<IReadOnlyList<SshHostKey>>> Probe()
    {
        IReadOnlyList<SshHostKey> keys = _keys;
        return async (host, port, cancellationToken) =>
        {
            if (host == "bad.example") throw new SshProbeException("nope");
            return keys;
        };
    }

    private static async Task On(SitesCommands commands)
    {
        var status = await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true });
        Assert.True(status.Enabled);
    }

    private static SiteSaveRequest Site(string? id = null, string name = "Office box", string host = Host, string? password = Secret) => new()
    {
        Id = id,
        Name = name,
        Kind = SiteKinds.Sftp,
        Host = host,
        Port = 22,
        User = "tester",
        AuthMethod = SiteAuthMethods.Password,
        Password = password,
        RemoteFolder = "/pub",
    };

    private static async Task<IpcMessage> Send(SitesCommands commands, string type, object payload) =>
        await commands.HandleAsync(IpcMessage.Create(type, payload), CancellationToken.None);

    private static async Task<T> Call<T>(SitesCommands commands, string type, object? payload = null) =>
        (await Send(commands, type, payload ?? new { })).GetPayload<T>();

    /// <summary>An SSH public key blob of that type: its type as an SSH string, then random key bytes.</summary>
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
