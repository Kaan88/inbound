using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DownloadManager.Persistence;
using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

/// <summary>SiteRemote's fs object over a fake rc handler: options, key shapes, host-key and sealed-value refusals. No rclone.</summary>
public sealed class SiteRemoteTests
{
    private const string Password = "hunter2-pass";
    private const string Passphrase = "key-phrase-77";
    private const string RcPassword = "rc-pass-9876";
    private const string KeyText = "-----BEGIN OPENSSH PRIVATE KEY-----\r\nAAAA\r\n-----END OPENSSH PRIVATE KEY-----";
    private const string HostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl";
    private static readonly Uri Base = new("http://127.0.0.1:9/");

    static SiteRemoteTests() => TestSecrets.Ensure();

    private static SiteRecord Site(string? sealedPassword = null, string auth = SiteAuthMethods.Password, IReadOnlyList<string>? hostKeys = null) => new()
    {
        Id = SiteStore.NewId(),
        Name = "Files",
        Kind = SiteKinds.Sftp,
        Host = "files.example.com",
        Port = 2222,
        User = "alice",
        AuthMethod = auth,
        SealedPassword = sealedPassword,
        HostKeys = hostKeys is null ? [HostKey] : [.. hostKeys],
    };

    [Fact]
    public async Task A_password_site_builds_an_sftp_fs_with_the_obscured_password()
    {
        var handler = new FakeRc();
        using var client = new RcloneClient(Base, "rcuser", RcPassword, handler: handler);
        var site = Site(sealedPassword: SecretProtector.ProtectSiteSecret(Password));

        var fs = await SiteRemote.BuildAsync(site, client);

        Assert.Equal("sftp", fs["type"]!.GetValue<string>());
        Assert.Equal("files.example.com", fs["host"]!.GetValue<string>());
        Assert.Equal("2222", fs["port"]!.GetValue<string>());
        Assert.Equal("alice", fs["user"]!.GetValue<string>());
        Assert.Equal(HostKey, fs["host_keys"]!.GetValue<string>());
        Assert.Equal("/", fs["_root"]!.GetValue<string>());
        Assert.Equal("OBS:" + Password, fs["pass"]!.GetValue<string>());
        Assert.Null(fs["key_pem"]);
        Assert.Null(fs["key_file_pass"]);

        var obscure = Assert.Single(handler.Requests, r => r.Path == "/core/obscure");
        Assert.Equal(Password, obscure.Clear);
    }

    [Fact]
    public async Task Redact_hides_the_plain_password_and_the_obscured_value()
    {
        var handler = new FakeRc();
        using var client = new RcloneClient(Base, "rcuser", RcPassword, handler: handler);
        var site = Site(sealedPassword: SecretProtector.ProtectSiteSecret(Password));
        var fs = await SiteRemote.BuildAsync(site, client);

        var plain = client.Redact("login failed for " + Password);
        var obscured = client.Redact("sent pass=" + fs["pass"]!.GetValue<string>());

        Assert.DoesNotContain(Password, plain);
        Assert.Contains("***", plain);
        Assert.DoesNotContain(Password, obscured);
        Assert.DoesNotContain("OBS:", obscured);
        Assert.Contains("***", obscured);
    }

    [Fact]
    public async Task A_key_site_gives_key_pem_with_literal_backslash_n_and_no_line_breaks()
    {
        var handler = new FakeRc();
        using var client = new RcloneClient(Base, "rcuser", RcPassword, handler: handler);
        var site = Site(auth: SiteAuthMethods.Key) with
        {
            SealedPrivateKey = SecretProtector.ProtectSiteSecret(KeyText),
        };

        var fs = await SiteRemote.BuildAsync(site, client);

        var pem = fs["key_pem"]!.GetValue<string>();
        Assert.Equal("-----BEGIN OPENSSH PRIVATE KEY-----\\nAAAA\\n-----END OPENSSH PRIVATE KEY-----", pem);
        Assert.DoesNotContain('\r', pem);
        Assert.DoesNotContain('\n', pem);
        Assert.Null(fs["pass"]);
        Assert.Null(fs["key_file_pass"]);
    }

    [Fact]
    public async Task A_key_site_with_a_passphrase_gives_key_file_pass_obscured()
    {
        var handler = new FakeRc();
        using var client = new RcloneClient(Base, "rcuser", RcPassword, handler: handler);
        var site = Site(auth: SiteAuthMethods.Key) with
        {
            SealedPrivateKey = SecretProtector.ProtectSiteSecret(KeyText),
            SealedKeyPassphrase = SecretProtector.ProtectSiteSecret(Passphrase),
        };

        var fs = await SiteRemote.BuildAsync(site, client);

        Assert.Equal("OBS:" + Passphrase, fs["key_file_pass"]!.GetValue<string>());
        Assert.Null(fs["pass"]);
        Assert.Contains(handler.Requests, r => r.Path == "/core/obscure" && r.Clear == Passphrase);
    }

    [Fact]
    public async Task A_key_site_without_a_passphrase_has_no_key_file_pass()
    {
        var handler = new FakeRc();
        using var client = new RcloneClient(Base, "rcuser", RcPassword, handler: handler);
        var site = Site(auth: SiteAuthMethods.Key) with
        {
            SealedPrivateKey = SecretProtector.ProtectSiteSecret(KeyText),
            SealedKeyPassphrase = null,
        };

        var fs = await SiteRemote.BuildAsync(site, client);

        Assert.Null(fs["key_file_pass"]);
        Assert.Null(fs["pass"]);
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/core/obscure");
    }

    [Fact]
    public async Task A_site_with_no_host_keys_is_refused_before_rclone_is_asked()
    {
        var handler = new FakeRc();
        using var client = new RcloneClient(Base, "rcuser", RcPassword, handler: handler);
        var site = Site(sealedPassword: SecretProtector.ProtectSiteSecret(Password), hostKeys: []);

        var ex = await Assert.ThrowsAsync<SiteStoreException>(() => SiteRemote.BuildAsync(site, client));

        Assert.Contains("not been accepted", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_sealed_value_that_does_not_open_is_refused_on_this_PC()
    {
        var handler = new FakeRc();
        using var client = new RcloneClient(Base, "rcuser", RcPassword, handler: handler);
        var site = Site(sealedPassword: "aesgcm:v1:garbage");

        var ex = await Assert.ThrowsAsync<SiteStoreException>(() => SiteRemote.BuildAsync(site, client));

        Assert.Contains("can't be opened on this PC", ex.Message);
        Assert.DoesNotContain("aesgcm:v1:garbage", ex.Message);
    }

    /// <summary>Answers core/obscure as rclone's "OBS:" stand-in, and records every request (copied out before the handler returns).</summary>
    private sealed class FakeRc : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            string? clear = path == "/core/obscure" ? JsonNode.Parse(body)?["clear"]?.GetValue<string>() : null;
            Requests.Add(new Sent(path, body, clear));

            var answer = path == "/core/obscure"
                ? new JsonObject { ["obscured"] = "OBS:" + clear }.ToJsonString()
                : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private sealed record Sent(string Path, string Body, string? Clear);
}
