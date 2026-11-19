using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DownloadManager.Persistence;
using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

/// <summary>
/// SITES.6c with a real rclone (INBOUND_RCLONE): a signed-in site becomes a named in-memory remote, a token rclone renews
/// is handed back to be stored, a new sign-in replaces the remote, and no token shows in an error. Nothing reaches the
/// provider: the tokens are made up and nothing is listed.
/// </summary>
public sealed class SiteSignInRemoteTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "inbound-sites-tests", Guid.NewGuid().ToString("N"));

    static SiteSignInRemoteTests() => TestSecrets.Ensure();

    public void Dispose()
    {
        try { Directory.Delete(_state, recursive: true); }
        catch (IOException) { }
    }

    private static string Token(string access) =>
        new JsonObject { ["access_token"] = access, ["token_type"] = "bearer", ["refresh_token"] = "refresh-" + access, ["expiry"] = "2030-01-01T00:00:00Z" }.ToJsonString();

    private static SiteRecord Site(string kind, string token, string? id = null) => new()
    {
        Id = id ?? SiteStore.NewId(),
        Name = "Cloud",
        Kind = kind,
        Host = "api.dropboxapi.com",
        Port = 443,
        AuthMethod = SiteAuthMethods.SignIn,
        SealedSignIn = SecretProtector.ProtectSiteSecret(new JsonObject { ["token"] = token }.ToJsonString()),
    };

    [Fact]
    public void Synthetic_sign_in_token_roundtrips_and_all_forms_are_redacted()
    {
        using var client = new RcloneClient(new Uri("http://127.0.0.1:9/"), "u", "rc-pass-1234");
        const string access = "synthetic-access-123";
        const string refresh = "synthetic-refresh-456";
        const string token = "{\"access_token\":\"synthetic-access-123\",\"refresh_token\":\"synthetic-refresh-456\",\"extra\":\"quote \\\" slash \\\\ newline \\n unicode 雪\"}";
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            writer.WriteStringValue(token);
        var quotedToken = Encoding.UTF8.GetString(buffer.ToArray());
        var settings = "{\"token\":" + quotedToken + "}";

        var parsed = SiteRemote.ParseSignIn(settings, client);

        Assert.Equal(token, parsed["token"]!.GetValue<string>());
        foreach (var secret in new[] { settings, token, quotedToken[1..^1], access, refresh })
        {
            var redacted = client.Redact("error: " + secret);
            Assert.DoesNotContain(secret, redacted);
            Assert.Contains("***", redacted);
        }
    }

    [RcloneFact]
    public async Task A_signed_in_site_is_a_named_remote_and_a_renewed_token_is_handed_back()
    {
        await using var rclone = await RcloneProcess.StartAsync(RcloneBinary.Path!, _state, _ => { });
        var client = rclone.Client;
        var first = Token("access-first-0001");
        var site = Site(SiteKinds.Dropbox, first);
        JsonObject? renewed = null;
        string? renewedFor = null;
        client.SignInRenewed = (id, _, settings, _) =>
        {
            renewedFor = id;
            renewed = settings;
            return Task.CompletedTask;
        };

        var fs = await SiteRemote.BuildAsync(site, client);
        Assert.Equal(SiteRemote.RemoteName(site), fs["_name"]!.GetValue<string>());
        Assert.Equal("/", fs["_root"]!.GetValue<string>());
        var held = await client.CallAsync("config/get", new JsonObject { ["name"] = SiteRemote.RemoteName(site) });
        Assert.Equal("dropbox", held["type"]!.GetValue<string>());
        Assert.Equal(first, held["token"]!.GetValue<string>());
        // Every form of the token is a secret: no error text carries it.
        Assert.DoesNotContain("access-first-0001", client.Redact($"failed with {first} and access-first-0001"));

        // Used again unchanged: nothing is made again, nothing handed back.
        await SiteRemote.BuildAsync(site, client);
        Assert.Null(renewed);

        // rclone renews the token (as it does when the access token expires): the next use hands it back to be stored.
        var second = Token("access-second-0002");
        await client.CallAsync("config/update", new JsonObject
        {
            ["name"] = SiteRemote.RemoteName(site),
            ["parameters"] = new JsonObject { ["token"] = second },
            ["opt"] = new JsonObject { ["nonInteractive"] = true, ["noObscure"] = true },
        });
        await SiteRemote.BuildAsync(site, client);
        Assert.Equal(site.Id, renewedFor);
        Assert.Equal(second, renewed!["token"]!.GetValue<string>());

        // Signed in again since (a new stored token): the remote is made from it.
        renewed = null;
        var third = Token("access-third-0003");
        var again = site with { SealedSignIn = SecretProtector.ProtectSiteSecret(new JsonObject { ["token"] = third }.ToJsonString()) };
        await SiteRemote.BuildAsync(again, client);
        Assert.Null(renewed);
        held = await client.CallAsync("config/get", new JsonObject { ["name"] = SiteRemote.RemoteName(site) });
        Assert.Equal(third, held["token"]!.GetValue<string>());
    }

    [RcloneFact]
    public async Task An_own_app_registration_goes_to_the_remote_and_its_secret_is_withheld()
    {
        await using var rclone = await RcloneProcess.StartAsync(RcloneBinary.Path!, _state, _ => { });
        var client = rclone.Client;
        var site = Site(SiteKinds.Drive, Token("access-drive-0004")) with
        {
            ClientId = "my-app.apps.example",
            SealedClientSecret = SecretProtector.ProtectSiteSecret("client-secret-xyz-55"),
        };

        await SiteRemote.BuildAsync(site, client);
        var held = await client.CallAsync("config/get", new JsonObject { ["name"] = SiteRemote.RemoteName(site) });
        Assert.Equal("drive", held["type"]!.GetValue<string>());
        Assert.Equal("my-app.apps.example", held["client_id"]!.GetValue<string>());
        Assert.DoesNotContain("client-secret-xyz-55", client.Redact("secret client-secret-xyz-55"));
    }

    [Fact]
    public async Task A_site_without_a_sign_in_asks_for_one()
    {
        using var client = new RcloneClient(new Uri("http://127.0.0.1:9/"), "u", "rc-pass-1234");
        var site = Site(SiteKinds.OneDrive, Token("x")) with { SealedSignIn = null };
        await Assert.ThrowsAsync<SiteSignInNeededException>(() => SiteRemote.BuildAsync(site, client));
    }

    [Fact]
    public async Task A_b2_site_builds_a_b2_fs_with_its_key_withheld()
    {
        using var client = new RcloneClient(new Uri("http://127.0.0.1:9/"), "u", "rc-pass-1234");
        var site = new SiteRecord
        {
            Id = SiteStore.NewId(), Name = "B2", Kind = SiteKinds.B2, Host = "api.backblazeb2.com", Port = 443, User = "key-id-001",
            AuthMethod = SiteAuthMethods.Password, SealedPassword = SecretProtector.ProtectSiteSecret("b2-application-key-77"),
        };
        var fs = await SiteRemote.BuildAsync(site, client);
        Assert.Equal("b2", fs["type"]!.GetValue<string>());
        Assert.Equal("key-id-001", fs["account"]!.GetValue<string>());
        Assert.Equal("b2-application-key-77", fs["key"]!.GetValue<string>());
        Assert.DoesNotContain("b2-application-key-77", client.Redact("bad key b2-application-key-77"));
    }
}
