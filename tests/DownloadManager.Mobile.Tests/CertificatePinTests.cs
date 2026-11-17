using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DownloadManager.Contracts;
using DownloadManager.Mobile.Remote;

namespace DownloadManager.Mobile.Tests;

/// <summary>ANDROID.7, owner 2026-11-17 "pin at pairing": which https certificates are accepted, and the pairing flow around it.</summary>
public sealed class CertificatePinTests
{
    internal static X509Certificate2 SelfSigned(string name = "homeserver", DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddYears(1));
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void ACertificateTheSystemTrustsIsAcceptedWithOrWithoutAPin()
    {
        using var cert = SelfSigned();
        Assert.True(ServerCertificates.Accept(SslPolicyErrors.None, cert, null, Now));
        using var other = SelfSigned("other");
        Assert.True(ServerCertificates.Accept(SslPolicyErrors.None, cert, ServerCertificates.Fingerprint(other), Now));
    }

    [Fact]
    public void ThePinnedCertificateIsAcceptedThoughTheSystemDoesNotTrustIt()
    {
        using var cert = SelfSigned();
        var pin = ServerCertificates.Fingerprint(cert);
        Assert.True(ServerCertificates.Accept(SslPolicyErrors.RemoteCertificateChainErrors, cert, pin, Now));
        // The pin as typed in lower case or without colons is the same pin.
        Assert.True(ServerCertificates.Accept(SslPolicyErrors.RemoteCertificateChainErrors, cert, pin.Replace(":", "").ToLowerInvariant(), Now));
    }

    [Fact]
    public void AnotherCertificateIsRefusedEvenWithTheSameName()
    {
        using var pinned = SelfSigned();
        using var changed = SelfSigned();
        Assert.False(ServerCertificates.Accept(SslPolicyErrors.RemoteCertificateChainErrors, changed, ServerCertificates.Fingerprint(pinned), Now));
    }

    [Fact]
    public void WithoutAPinAnUntrustedCertificateIsRefused()
    {
        using var cert = SelfSigned();
        Assert.False(ServerCertificates.Accept(SslPolicyErrors.RemoteCertificateChainErrors, cert, null, Now));
        Assert.False(ServerCertificates.Accept(SslPolicyErrors.RemoteCertificateChainErrors, cert, "not a fingerprint", Now));
        Assert.False(ServerCertificates.Accept(SslPolicyErrors.RemoteCertificateNotAvailable, null, ServerCertificates.Fingerprint(cert), Now));
    }

    [Fact]
    public void APinnedCertificateOutOfItsDatesIsRefused()
    {
        using var expired = SelfSigned(notBefore: Now.AddYears(-2), notAfter: Now.AddDays(-1));
        Assert.False(ServerCertificates.Accept(SslPolicyErrors.RemoteCertificateChainErrors, expired, ServerCertificates.Fingerprint(expired), Now));
    }

    [Fact]
    public void TheFingerprintIsTheSitesForm()
    {
        using var cert = SelfSigned();
        var fingerprint = ServerCertificates.Fingerprint(cert);
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", fingerprint);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(cert.RawData)), fingerprint.Replace(":", ""));
        Assert.Null(ServerCertificates.NormalizePin("AB:CD"));
    }

    private sealed class Calls
    {
        public List<string?> Pins { get; } = new();
        public int Probes { get; set; }
        public ServerCertificateInfo Seen { get; set; } = new() { Fingerprint = new string('A', 64), Subject = "CN=homeserver", NotAfter = DateTimeOffset.UtcNow.AddYears(1) };

        public Task<RemotePairResult> Pair(Uri a, string p, string n, bool c, string? pin, CancellationToken ct)
        {
            Pins.Add(pin);
            return Task.FromResult(new RemotePairResult { Token = "ibt_test-only", TokenId = "t" });
        }

        public Task<ServerCertificateInfo> Probe(Uri a, CancellationToken ct)
        {
            Probes++;
            return Task.FromResult(Seen);
        }
    }

    private static (HostDirectory, MemoryHostList, Calls) Make()
    {
        var calls = new Calls();
        var list = new MemoryHostList();
        return (new HostDirectory(list, new MemorySecrets(), "phone", pair: calls.Pair, probe: calls.Probe, unpair: (_, _) => Task.FromResult(true)), list, calls);
    }

    [Fact]
    public async Task AnHttpsServerTheSystemDoesNotTrustShowsItsCertificateAndNothingIsSent()
    {
        var (hosts, list, calls) = Make();
        using var cert = SelfSigned();
        calls.Seen = new ServerCertificateInfo { Fingerprint = ServerCertificates.Fingerprint(cert), Subject = cert.Subject, NotAfter = cert.NotAfter, TrustedBySystem = false };

        var outcome = await hosts.PairAsync(new PairRequest("https://homeserver:8443", "pw", null, false));

        Assert.False(outcome.Ok);
        Assert.True(outcome.NeedsCertificateTrust);
        Assert.Equal(ServerCertificates.Fingerprint(cert), outcome.Certificate!.Fingerprint);
        Assert.Empty(calls.Pins);
        Assert.Empty(list.Saved.Servers);
    }

    [Fact]
    public async Task TrustingTheCertificatePairsWithThePinAndKeepsItWithTheHost()
    {
        var (hosts, list, calls) = Make();
        using var cert = SelfSigned();
        var pin = ServerCertificates.Fingerprint(cert);

        var outcome = await hosts.PairAsync(new PairRequest("https://homeserver:8443", "pw", null, false, TrustedFingerprint: pin));

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(pin, Assert.Single(calls.Pins));
        Assert.Equal(0, calls.Probes);
        Assert.Equal(pin, Assert.Single(list.Saved.Servers).CertificatePin);
        Assert.Equal(pin, hosts.TargetFor(outcome.Host!)!.CertificatePin);
    }

    [Fact]
    public async Task AnHttpsServerTheSystemTrustsPairsWithoutAPin()
    {
        var (hosts, list, calls) = Make();
        calls.Seen = new ServerCertificateInfo { Fingerprint = new string('B', 64), TrustedBySystem = true };

        var outcome = await hosts.PairAsync(new PairRequest("https://homeserver:8443", "pw", null, false));

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Null(Assert.Single(calls.Pins));
        Assert.Null(Assert.Single(list.Saved.Servers).CertificatePin);
    }

    [Fact]
    public async Task AnExpiredCertificateCantBeTrusted()
    {
        var (hosts, _, calls) = Make();
        calls.Seen = new ServerCertificateInfo { Fingerprint = new string('C', 64), CannotTrust = "The server's certificate expired." };

        var outcome = await hosts.PairAsync(new PairRequest("https://homeserver:8443", "pw", null, false));

        Assert.False(outcome.NeedsCertificateTrust);
        Assert.Equal("The server's certificate expired.", outcome.Error);
        Assert.Empty(calls.Pins);
    }

    [Fact]
    public async Task PlainHttpIsNeverProbed()
    {
        var (hosts, _, calls) = Make();
        Assert.True((await hosts.PairAsync(new PairRequest("127.0.0.1:8080", "pw", null, false))).Ok);
        Assert.Equal(0, calls.Probes);
    }

    [Fact]
    public async Task ARefusedCertificateStopsTheConnectionWithWordsToPairAgain()
    {
        var server = new FakeServer().Fails(new ServerCertificateRefusedException("Home server")).Accepts();
        await using var connection = server.Connection();
        await connection.OpenAsync(_ => { }, CancellationToken.None);

        Assert.Equal(RemoteConnectionStatus.Stopped, connection.State.Status);
        Assert.Contains("certificate", connection.State.Message);
        Assert.Contains("connect to it again", connection.State.Message);
        // Not for good any more (Fable finding 3): a captive portal looks the same, so it is tried again at the longest wait.
        await Until.TrueAsync(() => connection.State.Status == RemoteConnectionStatus.Connected, "tried again");
        Assert.Equal(2, server.Attempts);
    }
}

/// <summary>ANDROID.7, owner 2026-11-17 "one-shot figures": what the host list asks each host once.</summary>
public sealed class HostFiguresTests
{
    [Fact]
    public void FiguresCountRunningAndWaitingDownloadsAndAddTheirSpeed()
    {
        var snapshot = new SnapshotDto
        {
            Jobs =
            [
                Jobs.Make("downloading", rate: 1_000_000), Jobs.Make("connecting"), Jobs.Make("downloading", rate: 500_000),
                Jobs.Make("queued"), Jobs.Make("completed"), Jobs.Make("paused"),
            ],
        };

        Assert.Equal(new HostFigures(3, 1, 1_500_000), HostFigures.From(snapshot));
    }

    [Fact]
    public void TheRowSaysWhatTheMockupSays()
    {
        var now = DateTimeOffset.UtcNow;
        var desktop = new HostEntry { Id = "d", Name = "Desktop PC", Address = "http://desk:8080/", HostKind = HostKinds.Windows };
        Assert.Equal("Windows · 5 active · 32.0 MB/s", HostWords.WithFigures(desktop, new HostFigures(5, 0, 32_000_000), now));
        var server = new HostEntry { Id = "s", Name = "Home server", Address = "http://home:8080/", HostKind = HostKinds.Server, LastSeenAt = now };
        Assert.Equal("Server · 12 queued", HostWords.WithFigures(server, new HostFigures(0, 12, 0), now));
        Assert.Equal("Server · last seen now · not reached now", HostWords.WithFigures(server, null, now));
        Assert.Equal("2 active · 14.6 MB/s", HostWords.WithFigures(new HostEntry(), new HostFigures(2, 0, 14_600_000), now));
    }

    [Fact]
    public async Task AServerIsAskedOnceWithAShortDeadlineAndNoRetry()
    {
        var asked = 0;
        var hosts = new HostDirectory(new MemoryHostList(), new MemorySecrets(), "phone",
            pair: (_, _, _, _, _, _) => Task.FromResult(new RemotePairResult { Token = "ibt_test-only" }),
            ask: (_, request, ct) =>
            {
                asked++;
                Assert.Equal(MessageTypes.JobsQuery, request.Type);
                return Task.FromResult(IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto { Jobs = [Jobs.Make("downloading", rate: 10)] }, request.Id));
            });
        var host = (await hosts.PairAsync(new PairRequest("127.0.0.1:9000", "pw", null, false))).Host!;

        Assert.Equal(new HostFigures(1, 0, 10), await hosts.FiguresAsync(host));
        Assert.Equal(1, asked);
        Assert.Null(await hosts.FiguresAsync(hosts.Hosts[0])); // the phone's figures come from its own Host
    }

    [Fact]
    public async Task AServerThatDoesNotAnswerGivesNoFigures()
    {
        var asked = 0;
        var hosts = new HostDirectory(new MemoryHostList(), new MemorySecrets(), "phone",
            pair: (_, _, _, _, _, _) => Task.FromResult(new RemotePairResult { Token = "ibt_test-only" }),
            ask: async (_, _, ct) =>
            {
                asked++;
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException();
            });
        var host = (await hosts.PairAsync(new PairRequest("127.0.0.1:9000", "pw", null, false))).Host!;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)); // the sheet closed

        Assert.Null(await hosts.FiguresAsync(host, cancel.Token));
        Assert.Equal(1, asked);
    }
}
