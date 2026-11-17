using DownloadManager.Contracts;
using DownloadManager.Mobile.Remote;

namespace DownloadManager.Mobile.Tests;

/// <summary>ANDROID.7: the host list, pairing and what the screens may offer for a host.</summary>
public sealed class HostDirectoryTests
{
    private const string Token = "ibt_secret-token-for-tests-only";

    private sealed class FakePairing
    {
        public List<(Uri Address, string Password, string Name, bool Confirmed)> Pairs { get; } = new();
        public List<ServerTarget> Unpaired { get; } = new();
        public RemotePairResult Result { get; set; } = new() { Token = Token, TokenId = "t1", HostKind = HostKinds.Windows };
        public bool UnpairWorks { get; set; } = true;

        public List<string?> Pins { get; } = new();

        public Task<RemotePairResult> Pair(Uri address, string password, string name, bool confirmed, string? pin, CancellationToken ct)
        {
            Pairs.Add((address, password, name, confirmed));
            Pins.Add(pin);
            return Task.FromResult(Result);
        }

        public Task<bool> Unpair(ServerTarget target, CancellationToken ct)
        {
            lock (Unpaired) Unpaired.Add(target);
            return Task.FromResult(UnpairWorks);
        }
    }

    private static (HostDirectory Hosts, MemoryHostList List, MemorySecrets Secrets, FakePairing Pairing) Make(MemoryHostList? list = null, MemorySecrets? secrets = null)
    {
        list ??= new MemoryHostList();
        secrets ??= new MemorySecrets();
        var pairing = new FakePairing();
        return (new HostDirectory(list, secrets, "Inbound on Test Phone", pair: pairing.Pair, unpair: pairing.Unpair), list, secrets, pairing);
    }

    [Fact]
    public async Task APairingWithoutANameTakesTheServersOwnNameElseTheAddress()
    {
        var (hosts, _, _, pairing) = Make();
        pairing.Result = new RemotePairResult { Token = Token, TokenId = "t1", HostKind = HostKinds.Server, ServerName = "Home NAS" };
        Assert.Equal("Home NAS", (await hosts.PairAsync(new PairRequest("127.0.0.1:9000", "pw", null, false))).Host!.Name);
        var (second, _, _, plain) = Make();
        plain.Result = new RemotePairResult { Token = Token, TokenId = "t2", HostKind = HostKinds.Server };
        Assert.Equal("127.0.0.1", (await second.PairAsync(new PairRequest("127.0.0.1:9000", "pw", null, false))).Host!.Name);
    }

    [Fact]
    public void AtFirstOnlyThisPhoneIsListedAndShown()
    {
        var (hosts, _, _, _) = Make();
        Assert.Single(hosts.Hosts);
        Assert.True(hosts.Current.IsPhone);
        Assert.False(hosts.Capabilities.IsRemote);
        Assert.Null(hosts.TargetFor(hosts.Current));
    }

    [Fact]
    public async Task PairingSavesTheServerWithoutItsTokenAndTheTokenOnlyInTheSecretStore()
    {
        var (hosts, list, secrets, pairing) = Make();

        var outcome = await hosts.PairAsync(new PairRequest("127.0.0.1:9000", "pw", "Desktop PC", false));

        Assert.True(outcome.Ok, outcome.Error);
        var (address, password, name, _) = Assert.Single(pairing.Pairs);
        Assert.Equal("http://127.0.0.1:9000/", address.AbsoluteUri);
        Assert.Equal("pw", password);
        Assert.Equal("Inbound on Test Phone", name);
        var saved = Assert.Single(list.Saved.Servers);
        Assert.Equal("Desktop PC", saved.Name);
        Assert.Equal(HostKinds.Windows, saved.HostKind);
        Assert.Equal(Token, Assert.Single(secrets.Values).Value);
        Assert.True(hosts.Current.IsPhone); // pairing does not switch
        var target = hosts.TargetFor(outcome.Host!)!;
        Assert.Equal(Token, target.Token);
        Assert.DoesNotContain(Token, target.ToString());
        Assert.DoesNotContain(Token, outcome.Host!.ToString());
    }

    [Fact]
    public async Task TheFileOnDiskHoldsNoToken()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "test-work", "hosts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = new HostListFile(Path.Combine(folder, "hosts.json"));
            var secrets = new MemorySecrets();
            var hosts = new HostDirectory(file, secrets, "phone", pair: new FakePairing().Pair);
            Assert.True((await hosts.PairAsync(new PairRequest("localhost:8080", "pw", null, false))).Ok);

            var text = File.ReadAllText(file.Path);
            Assert.DoesNotContain(Token, text);
            Assert.DoesNotContain("pw", text.Replace("\"pairedAt\"", ""));
            var again = new HostDirectory(file, secrets, "phone");
            Assert.Equal(2, again.Hosts.Count);
            Assert.Equal("localhost", again.Hosts[1].Name);
            Assert.Equal(Token, again.TargetFor(again.Hosts[1])!.Token);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task PlainHttpToAnotherMachineNeedsTheUsersYesBeforeThePasswordIsSent()
    {
        var (hosts, list, _, pairing) = Make();

        var refused = await hosts.PairAsync(new PairRequest("homeserver:8080", "pw", null, false));
        Assert.False(refused.Ok);
        Assert.True(refused.NeedsPlainHttpConfirmation);
        Assert.Empty(pairing.Pairs);

        var allowed = await hosts.PairAsync(new PairRequest("homeserver:8080", "pw", null, true));
        Assert.True(allowed.Ok);
        Assert.True(Assert.Single(list.Saved.Servers).PlainHttpConfirmed);
        Assert.True(Assert.Single(pairing.Pairs).Confirmed);
    }

    [Theory]
    [InlineData("", "Type the server's address")]
    [InlineData("ftp://x", "http://")]
    [InlineData("http://user:pw@host", "Leave the user name")]
    public async Task ABadAddressIsRefusedWithoutAnyCall(string address, string words)
    {
        var (hosts, _, _, pairing) = Make();
        var outcome = await hosts.PairAsync(new PairRequest(address, "pw", null, true));
        Assert.Contains(words, outcome.Error);
        Assert.Empty(pairing.Pairs);
    }

    [Fact]
    public async Task AFailedPairingSavesNothing()
    {
        var (hosts, list, secrets, pairing) = Make();
        pairing.Result = new RemotePairResult { Error = "Wrong password." };

        var outcome = await hosts.PairAsync(new PairRequest("127.0.0.1:9000", "nope", null, false));

        Assert.Equal("Wrong password.", outcome.Error);
        Assert.Empty(list.Saved.Servers);
        Assert.Empty(secrets.Values);
    }

    [Fact]
    public async Task ATokenThePhoneCantStoreIsForgottenOnTheServer()
    {
        var (hosts, list, secrets, pairing) = Make();
        secrets.FailWrites = true;

        var outcome = await hosts.PairAsync(new PairRequest("127.0.0.1:9000", "pw", null, false));

        Assert.False(outcome.Ok);
        Assert.Empty(list.Saved.Servers);
        await Until.TrueAsync(() => { lock (pairing.Unpaired) return pairing.Unpaired.Count == 1; }, "the server was told to forget it");
        Assert.Equal(Token, pairing.Unpaired[0].Token);
    }

    [Fact]
    public async Task AListThatCantBeSavedUndoesThePairing()
    {
        var (hosts, list, secrets, pairing) = Make();
        list.FailSaves = true;

        var outcome = await hosts.PairAsync(new PairRequest("127.0.0.1:9000", "pw", null, false));

        Assert.False(outcome.Ok);
        Assert.Single(hosts.Hosts);
        Assert.Empty(secrets.Values);
        await Until.TrueAsync(() => { lock (pairing.Unpaired) return pairing.Unpaired.Count == 1; }, "the server was told to forget it");
    }

    [Fact]
    public async Task PairingAgainWithTheSameAddressKeepsTheEntryAndForgetsTheOldToken()
    {
        var (hosts, list, secrets, pairing) = Make();
        var first = await hosts.PairAsync(new PairRequest("127.0.0.1:9000", "pw", "Desk", false));
        Assert.True(hosts.Select(first.Host!.Id));
        pairing.Result = new RemotePairResult { Token = "ibt_second-token", TokenId = "t2" };

        var second = await hosts.PairAsync(new PairRequest("http://127.0.0.1:9000/", "pw", null, false));

        Assert.Equal(first.Host.Id, second.Host!.Id);
        Assert.Equal("Desk", second.Host.Name);
        Assert.Single(list.Saved.Servers);
        Assert.Equal("ibt_second-token", Assert.Single(secrets.Values).Value);
        Assert.Equal(first.Host.Id, hosts.Current.Id);
        await Until.TrueAsync(() => { lock (pairing.Unpaired) return pairing.Unpaired.Count == 1; }, "the old token was forgotten");
        Assert.Equal(Token, pairing.Unpaired[0].Token);
    }

    [Fact]
    public async Task SwitchingIsSavedAndRaisesChangedAndAnUnknownHostIsRefused()
    {
        var (hosts, list, _, _) = Make();
        var paired = (await hosts.PairAsync(new PairRequest("127.0.0.1:9000", "pw", null, false))).Host!;
        var changes = 0;
        hosts.Changed += () => changes++;

        Assert.True(hosts.Select(paired.Id));
        Assert.Equal(paired.Id, hosts.Current.Id);
        await hosts.SavedAsync();
        Assert.Equal(paired.Id, list.Saved.CurrentId);
        Assert.True(hosts.Capabilities.IsRemote);
        Assert.False(hosts.Capabilities.CanOpenFiles);
        Assert.Equal(1, changes);
        Assert.False(hosts.Select("nope"));
        Assert.True(hosts.Select(HostEntry.PhoneId));
        Assert.True(hosts.Capabilities.CanOpenFiles);
    }

    [Fact]
    public async Task RemovingTheShownServerShowsThePhoneDeletesItsTokenAndUnpairs()
    {
        var (hosts, list, secrets, pairing) = Make();
        var paired = (await hosts.PairAsync(new PairRequest("127.0.0.1:9000", "pw", null, false))).Host!;
        hosts.Select(paired.Id);

        Assert.True(await hosts.RemoveAsync(paired.Id));

        Assert.True(hosts.Current.IsPhone);
        Assert.Empty(list.Saved.Servers);
        Assert.Empty(secrets.Values);
        Assert.Equal(Token, Assert.Single(pairing.Unpaired).Token);
    }

    [Fact]
    public async Task AServerWhoseTokenCantBeReadGetsAConnectionThatSaysToPairAgain()
    {
        var (hosts, _, secrets, _) = Make();
        var paired = (await hosts.PairAsync(new PairRequest("127.0.0.1:9000", "pw", "Desk", false))).Host!;
        hosts.Select(paired.Id);
        secrets.Values.Clear(); // the Keystore key was reset

        await using var connection = HostConnections.ForCurrent(hosts, () => throw new InvalidOperationException("not the phone"));

        var status = Assert.IsAssignableFrom<IRemoteStatus>(connection);
        Assert.Equal(RemoteConnectionStatus.Stopped, status.State.Status);
        Assert.Contains("Desk", status.State.Message);
        await Assert.ThrowsAsync<RemoteServerException>(() => connection.RequestAsync(IpcMessage.Create(MessageTypes.HostPing, new { }), CancellationToken.None));
    }

    [Fact]
    public async Task TheConnectionForTheShownHostIsThePhonesOrTheServersAndNotesWhenItWasSeen()
    {
        var (hosts, list, _, _) = Make();
        var phone = new FakeHostConnection();
        Assert.Same(phone, HostConnections.ForCurrent(hosts, () => phone));

        var paired = (await hosts.PairAsync(new PairRequest("127.0.0.1:9000", "pw", null, false))).Host!;
        hosts.Select(paired.Id);
        list.Saved.Servers[0].LastSeenAt = null;
        var server = new FakeServer().Accepts(s => { s.Hello(kind: HostKinds.Linux); s.Snapshot(); });
        await using var connection = HostConnections.ForCurrent(hosts, () => phone, connect: server.ConnectAsync);

        Assert.IsType<RemoteHostConnection>(connection);
        await connection.OpenAsync(_ => { }, CancellationToken.None);
        await Until.TrueAsync(() => hosts.Hosts[1].HostKind == HostKinds.Linux, "the kind was noted");
        await hosts.SavedAsync();
        Assert.Equal(HostKinds.Linux, list.Saved.Servers[0].HostKind);
    }

    [Fact]
    public void TheSubtitleSaysTheKindAndWhenItWasLastSeen()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var host = new HostEntry { Id = "x", Name = "Home server", Address = "http://homeserver:8080/", HostKind = HostKinds.Server, LastSeenAt = now.AddMinutes(-5) };
        Assert.Equal("Server · last seen 5 min ago", HostWords.Subtitle(host, now));
        host.HostKind = null;
        host.LastSeenAt = null;
        Assert.Equal("homeserver:8080", HostWords.Subtitle(host, now));
        Assert.Equal("Files are saved on Home server", HostWords.SavedOn("Home server"));
    }
}
