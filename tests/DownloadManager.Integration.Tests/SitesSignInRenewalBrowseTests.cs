using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Persistence;
using DownloadManager.Runtime.Sites;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.6c / review F5: a sign-in token rclone renews during a browse is stored, and the rest of that same browse (its
/// folder counts) builds rclone's remote from the renewed record. With the old record it would make the remote again from
/// the old token and undo the renewal. The rclone rc API is a fake here, so no provider is reached; the version check that
/// starts a browse uses the real rclone (INBOUND_RCLONE).
/// </summary>
public sealed class SitesSignInRenewalBrowseTests : IAsyncLifetime
{
    private const string FirstToken = """{"access_token":"renewal-first-1001","token_type":"bearer","expiry":"2030-01-01T00:00:00Z"}""";
    private const string SecondToken = """{"access_token":"renewal-second-2002","token_type":"bearer","expiry":"2030-01-01T00:00:00Z"}""";

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "sites-renewal-" + Guid.NewGuid().ToString("N"));
    private readonly List<SitesCommands> _commands = [];

    private string State => Path.Combine(_root, "state");

    private static string? Rclone =>
        Environment.GetEnvironmentVariable(RcloneSetup.RcloneVariable) is { Length: > 0 } path && File.Exists(path) ? path : null;

    public static string? Missing => Rclone is null
        ? "No rclone: set INBOUND_RCLONE to an rclone 1.75.0 or later (it is not in the repository)." : null;

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

    [SitesSignInRenewalFact]
    public async Task AFolderCountsBuildAfterARenewalUsesTheRenewedToken()
    {
        var handler = new FakeRc(SecondToken);
        var supervisor = new RcloneSupervisor(_ => Task.FromResult<IRcloneInstance>(new FakeInstance(handler)));
        var commands = new SitesCommands(State, _ => { }, findRclone: _ => Rclone,
            createSupervisor: (_, _) => supervisor,
            probe: (_, _, _) => Task.FromResult<IReadOnlyList<SshHostKey>>([]));
        _commands.Add(commands);

        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true });
        var saved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest { Name = "Cloud", Kind = SiteKinds.Dropbox });
        Assert.True(saved.Ok, saved.Message);
        var id = saved.Site!.Id;
        var signed = await Call<SiteSignInResultDto>(commands, SitesMessageTypes.SignIn,
            new SiteSignInRequest { Id = id, Token = $"Paste this into your machine --->\n{FirstToken}\n<---End paste" });
        Assert.True(signed.Ok, signed.Message);

        var browsed = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse,
            new SiteBrowseRequest { Id = id, IncludeFolderCounts = true });
        Assert.True(browsed.Ok, browsed.Message);
        var folder = Assert.Single(browsed.Items, item => item.Name == "docs");
        Assert.True(folder.IsFolder);
        Assert.Equal(2, folder.ChildCount);

        // One config/create: the remote was made once with the first token, never again from the old record after the
        // renewal (which would have put the old token back into rclone).
        var created = Assert.Single(handler.CreatedTokens);
        Assert.Contains("renewal-first-1001", created);
        Assert.Contains("renewal-second-2002", handler.HeldToken);
        Assert.Contains(handler.Requests, request => request.Path == "config/create");

        // The renewed token is in the site, sealed.
        var stored = await new SiteStore(State).GetAsync(id);
        Assert.NotNull(stored?.SealedSignIn);
        Assert.Contains("renewal-second-2002", SecretProtector.UnprotectSiteSecret(stored!.SealedSignIn!));
    }

    private static async Task<T> Call<T>(SitesCommands commands, string type, object payload) =>
        (await commands.HandleAsync(IpcMessage.Create(type, payload), CancellationToken.None)).GetPayload<T>();

    private sealed class FakeInstance(FakeRc handler) : IRcloneInstance
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RcloneClient Client { get; } = new(new Uri("http://127.0.0.1:9/"), "u", "rc-pass-for-tests", handler: handler);
        public int ProcessId => 4242;
        public Task Exited => _exited.Task;

        public Task<bool> StopAsync(TimeSpan grace)
        {
            _exited.TrySetResult();
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// rclone's rc API as far as a signed-in browse uses it: named remotes in memory (<c>config/create</c>, read back by
    /// <c>config/get</c>), and a listing whose first call renews the token, as a provider does when the access token expires.
    /// </summary>
    private sealed class FakeRc(string renewedToken) : HttpMessageHandler
    {
        private readonly object _sync = new();
        private int _lists;

        public List<Sent> Requests { get; } = [];
        public List<string> CreatedTokens { get; } = [];
        public string HeldToken { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var body = request.Content is null
                ? new JsonObject()
                : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            JsonObject answer;
            lock (_sync)
            {
                Requests.Add(new Sent(path, body.DeepClone().AsObject()));
                switch (path)
                {
                    case "config/create":
                        var given = body["parameters"]?["token"]?.GetValue<string>() ?? "";
                        CreatedTokens.Add(given);
                        HeldToken = given;
                        answer = new JsonObject();
                        break;
                    case "config/get":
                        answer = new JsonObject { ["type"] = "dropbox", ["token"] = HeldToken };
                        break;
                    case "operations/list":
                        // The provider renews the access token while the first folder is listed.
                        if (_lists++ == 0) HeldToken = renewedToken;
                        answer = new JsonObject
                        {
                            ["list"] = new JsonArray(
                                new JsonObject { ["Name"] = "docs", ["Path"] = "docs", ["IsDir"] = true },
                                new JsonObject { ["Name"] = "readme.txt", ["Path"] = "readme.txt", ["IsDir"] = false, ["Size"] = 5 }),
                        };
                        break;
                    default:
                        answer = new JsonObject();
                        break;
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
        }

        public sealed record Sent(string Path, JsonObject Body);
    }
}

public sealed class SitesSignInRenewalFactAttribute : FactAttribute
{
    public SitesSignInRenewalFactAttribute()
    {
        if (SitesSignInRenewalBrowseTests.Missing is { } missing) Skip = missing;
    }
}
