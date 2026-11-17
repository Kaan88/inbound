using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Mobile.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Server.Tests;

/// <summary>
/// ANDROID.7, owner 2026-11-17 "pin at pairing": the real TLS handshakes of the shared client (<see cref="RemoteServerHttp"/>,
/// <see cref="WebSocketIpcClient"/>) against an https endpoint on loopback with a certificate it made itself (the server
/// has no https of its own; a reverse proxy gives it one). The pinned certificate is accepted, any other refused.
/// </summary>
public sealed class MobileCertificatePinTests
{
    private sealed class TlsServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TlsServer(WebApplication app, X509Certificate2 certificate)
        {
            _app = app;
            Certificate = certificate;
            Base = new Uri(app.Urls.First().TrimEnd('/') + "/");
        }

        public X509Certificate2 Certificate { get; }
        public Uri Base { get; }
        public string Pin => ServerCertificates.Fingerprint(Certificate);
        public int Pairs;

        public static async Task<TlsServer> StartAsync()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=inbound-test", key, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            // An exported and loaded copy: Windows TLS can't serve an ephemeral key.
            var certificate = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.UseHttps(certificate)));
            var app = builder.Build();
            TlsServer? self = null;
            app.UseWebSockets();
            app.MapPost("/api/pair", () =>
            {
                Interlocked.Increment(ref self!.Pairs);
                return Results.Json(new { token = "ibt_tls-test-only", id = "t1", role = "remoteClient" });
            });
            app.MapGet("/api/auth/status", () => Results.Json(new { hostKind = HostKinds.Server }));
            app.MapPost("/api/ipc", async (HttpContext context) =>
            {
                var message = await JsonSerializer.DeserializeAsync<IpcMessage>(context.Request.Body, IpcJson.Options);
                var reply = IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = true, Message = "pong" }, message!.Id);
                context.Response.ContentType = "application/json";
                await context.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(reply, IpcJson.Options));
            });
            app.Map("/api/ws", async (HttpContext context) =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var buffer = new byte[16 * 1024];
                await socket.ReceiveAsync(buffer, context.RequestAborted); // the client's hello
                foreach (var frame in new[]
                         {
                             IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { ProtocolVersion = Protocol.Version, HostKind = HostKinds.Server }),
                             IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto()),
                         })
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame, IpcJson.Options), WebSocketMessageType.Text, true, context.RequestAborted);
                try { await socket.ReceiveAsync(buffer, context.RequestAborted); }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
            });
            await app.StartAsync();
            self = new TlsServer(app, certificate);
            return self;
        }

        public ServerTarget Target(string? pin) =>
            ServerTarget.Remote("0123456789abcdef0123456789abcdef", "TLS test", Base, "ibt_tls-test-only", plainHttpConfirmed: false, pin);

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            Certificate.Dispose();
        }
    }

    private static IpcMessage Ping() => IpcMessage.Create(MessageTypes.HostPing, new EmptyPayload());

    [Fact]
    public async Task ThePinnedCertificateIsAcceptedOnTheHttpCallsAndTheSocket()
    {
        await using var server = await TlsServer.StartAsync();
        var target = server.Target(server.Pin);

        var reply = await RemoteServerHttp.SendIpcAsync(target, Ping());
        Assert.True(reply.GetPayload<HostActionResult>().Ok);

        await using var socket = await WebSocketIpcClient.ConnectAsync(target, new ClientHelloDto());
        Assert.Equal(MessageTypes.ServerHello, (await socket.ReadAsync())!.Type);
    }

    [Fact]
    public async Task AnotherPinOrNoPinIsRefusedAndSaysSo()
    {
        await using var server = await TlsServer.StartAsync();
        await using var other = await TlsServer.StartAsync();

        foreach (var target in new[] { server.Target(other.Pin), server.Target(null) })
        {
            await Assert.ThrowsAsync<ServerCertificateRefusedException>(() => RemoteServerHttp.SendIpcAsync(target, Ping()));
            await Assert.ThrowsAsync<ServerCertificateRefusedException>(() => WebSocketIpcClient.ConnectAsync(target, new ClientHelloDto()));
        }

        var refused = await RemoteServerHttp.PairAsync(server.Base, "pw", "phone", false, CancellationToken.None, other.Pin);
        Assert.False(refused.Ok);
        Assert.Contains("certificate", refused.Error);
        Assert.Equal(0, server.Pairs);
    }

    [Fact]
    public async Task ThePhonePairsByShowingTheCertificateThenTrustingItAndConnects()
    {
        await using var server = await TlsServer.StartAsync();
        var probed = await ServerCertificates.ProbeAsync(server.Base);
        Assert.Equal(server.Pin, probed.Fingerprint);
        Assert.False(probed.TrustedBySystem);
        Assert.Contains("inbound-test", probed.Subject);

        var work = Path.Combine(AppContext.BaseDirectory, "test-work", "mobile-tls-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hosts = new HostDirectory(new HostListFile(Path.Combine(work, "hosts.json")), new MemorySecrets(), "phone");
            var first = await hosts.PairAsync(new PairRequest(server.Base.ToString(), "pw", null, false));
            Assert.True(first.NeedsCertificateTrust);
            Assert.Equal(server.Pin, first.Certificate!.Fingerprint);
            Assert.Equal(0, server.Pairs); // no password went to a certificate nobody trusted

            var paired = await hosts.PairAsync(new PairRequest(server.Base.ToString(), "pw", null, false, first.Certificate.Fingerprint));
            Assert.True(paired.Ok, paired.Error);
            hosts.Select(paired.Host!.Id);
            Assert.Contains(server.Pin, await File.ReadAllTextAsync(Path.Combine(work, "hosts.json")));

            await using var connection = (RemoteHostConnection)HostConnections.ForCurrent(hosts, () => throw new InvalidOperationException());
            await connection.OpenAsync(_ => { }, CancellationToken.None);
            Assert.Equal(RemoteConnectionStatus.Connected, connection.State.Status);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public string? Read(string name) => _values.GetValueOrDefault(name);
        public void Write(string name, string secret) => _values[name] = secret;
        public void Delete(string name) => _values.Remove(name);
    }
}
