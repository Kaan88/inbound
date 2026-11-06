using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// The real Host process, composed the way it ships, spoken to over its real pipe, with the mock in
/// place of the provider. Nothing here reaches the real provider, the installed app or real state:
/// each test has its own profile, and every value is invented.
/// </summary>
public sealed class HostPipeRemoteTests : IAsyncLifetime
{
    private const string Key = "5e2f7c1a-93bd-4a60-8c17-0fd2b6e4a913";
    private const int Kb = 1024;

    private TestServerHost _server = null!;
    private MockTorBox Mock => _server.TorBox;

    public async Task InitializeAsync()
    {
        _server = await TestServerHost.StartAsync();
        Mock.ApiKey = Key;
        Mock.AddHoster("Pretend", ["pretend.invalid", "127.0.0.1", "localhost"], "/hoster/");
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private Task<SandboxHost> StartHostAsync(Action<SandboxHost>? beforeStart = null) => SandboxHost.StartAsync(Mock.ApiBase, beforeStart);

    private static async Task<HostConnection> WithKeyAsync(SandboxHost host)
    {
        var connection = await host.ConnectAsync();
        var result = await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountSetKey, new RemoteSetKeyRequest { Key = Key });
        Assert.True(result.Ok, result.Error);
        return connection;
    }

    private static async Task<RemoteJobDto> UntilAsync(HostConnection connection, Guid id, Func<RemoteJobDto, bool> done, string what, bool checkNow = false)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var list = await connection.AskAsync<RemoteListResult>(MessageTypes.RemoteList, new { });
            var job = list.Jobs.FirstOrDefault(candidate => candidate.Id == id);
            if (job is not null && done(job))
            {
                return job;
            }

            if (job?.State == "failed")
            {
                throw new InvalidOperationException($"Failed while waiting for {what}: {job.FailureKind}: {job.FailureMessage}");
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited too long for {what}. Last: {job?.State}: {job?.Status}");
            }

            if (checkNow)
            {
                await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteCheckNow, new RemoteCheckNowRequest { JobId = id });
            }

            await Task.Delay(checkNow ? 250 : 50);
        }
    }

    private static async Task<IReadOnlyList<JobDto>> UntilProviderJobsFinishAsync(HostConnection connection, int count)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var jobs = (await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs
                .Where(job => job.Url.StartsWith("remote://", StringComparison.Ordinal)).ToList();
            Assert.DoesNotContain(jobs, job => job.State == "failed");
            if (jobs.Count == count && jobs.All(job => job.State == "completed"))
            {
                return jobs;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited too long for {count} jobs: {string.Join(", ", jobs.Select(job => job.State))}");
            }

            await Task.Delay(50);
        }
    }

    private static string Sha(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private static void AssertSameFile(SandboxHost host, MockTorBoxFile file, params string[] relative)
        => Assert.Equal(Sha(file.Content), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine([host.Destination, .. relative])))));

    private static string Md5(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private MockTorBoxFile NewFile(string name, int size, int seed) => MockTorBoxFile.Of(Mock.NextFileId(), name, size, seed);

    // ---- Composition --------------------------------------------------------------------------

    [Fact]
    public async Task TheHostComesUpInASandboxOnProtocolOneAndOffersTheRemoteCommands()
    {
        await using var host = await StartHostAsync();
        await using var connection = await host.ConnectAsync();

        Assert.Contains("remote", connection.Hello.Capabilities);
        Assert.Equal(1, connection.Hello.ProtocolVersion);
        Assert.Contains(host.LogLines(), line => line.Contains("Using another TorBox API address", StringComparison.Ordinal));
        Assert.Contains(host.LogLines(), line => line.Contains(host.DataDirectory.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) || line.Contains(host.DataDirectory, StringComparison.OrdinalIgnoreCase));

        var result = await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountGet, new { });
        Assert.True(result.Ok);
        var account = Assert.Single(result.Accounts);
        Assert.Equal("torbox", account.Provider);
        Assert.Equal("TorBox", account.ProviderName);
        Assert.False(account.HasKey);
        Assert.False(account.KeyUnreadable);
        Assert.Contains("hosterList", account.Capabilities);
        Assert.Equal(["torrents", "webdl", "usenet"], account.ItemKinds);

        // Nothing was called: no key, no refresh.
        Assert.Equal(0, Mock.TotalApiCalls);

        await host.StopAsync();
        Assert.Equal(0, host.LastExitCode);
        Assert.Contains(host.LogLines(), line => line.Contains("Host stopped.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAddressOutsideThisComputerIsIgnoredSoTheKeyCanNotGoThere()
    {
        // Nothing here makes a call, because there is no key: the log says what would have been used.
        await using var host = await SandboxHost.StartAsync("https://example.invalid/v1/api/");
        await using var connection = await host.ConnectAsync();
        var lines = host.LogLines();
        Assert.Contains(lines, line => line.Contains(SandboxHost.ApiVariable + " was ignored", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("example.invalid", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("Using another TorBox API address", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASandboxProfileWhoseFolderCanNotBeUsedStopsInsteadOfUsingTheRealState()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", "blocked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var blocker = Path.Combine(root, "a-file");
        await File.WriteAllTextAsync(blocker, "not a folder");
        try
        {
            var info = new ProcessStartInfo(SandboxHost.HostExecutable, "--background") { UseShellExecute = false, CreateNoWindow = true };
            info.Environment["DOWNLOADMANAGER_INSTANCE"] = "t" + Guid.NewGuid().ToString("N");
            info.Environment["DOWNLOADMANAGER_DATA"] = Path.Combine(blocker, "data");
            using var process = Process.Start(info)!;
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(wait.Token);
            Assert.Equal(5, process.ExitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- The key ------------------------------------------------------------------------------

    [Fact]
    public async Task TheKeyGoesInAndNothingButHasKeyComesBack()
    {
        await using var host = await StartHostAsync();
        await using var connection = await host.ConnectAsync();
        await using var listener = await host.ConnectAsync();

        var saved = await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountSetKey, new RemoteSetKeyRequest { Key = Key });
        Assert.True(saved.Ok, saved.Error);
        Assert.True(Assert.Single(saved.Accounts).HasKey);

        var valid = await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountValidate, new { });
        Assert.True(valid.Ok, valid.Error);
        var account = Assert.Single(valid.Accounts);
        Assert.Equal("Pro", account.Plan);
        Assert.True(account.Premium);
        Assert.Equal(2030, account.PremiumUntil?.Year);
        Assert.NotNull(account.CheckedAt);

        // Last known answer, with no call this time. And on request, a new one.
        var calls = Mock.Calls("GET user/me");
        var cached = await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountGet, new { });
        Assert.Equal("Pro", Assert.Single(cached.Accounts).Plan);
        Assert.Equal(calls, Mock.Calls("GET user/me"));
        Mock.AccountPlan = 1;
        var fresh = await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountGet, new RemoteAccountRequest { Refresh = true });
        Assert.Equal("Essential", Assert.Single(fresh.Accounts).Plan);

        // The bearer header got it, so the client is using what was sent.
        Assert.Contains($"Bearer {Key}", Mock.AuthorizationHeaders);

        // A wrong one is kept and reported, in plain words.
        const string wrong = "9a9b9c9d-0000-4111-8222-333344445555";
        Assert.True((await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountSetKey, new RemoteSetKeyRequest { Key = wrong })).Ok);
        var refused = await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountValidate, new { });
        Assert.False(refused.Ok);
        Assert.Equal("authentication", refused.FailureKind);
        Assert.Contains("refused the API key", refused.Error);
        Assert.True(Assert.Single(refused.Accounts).HasKey);
        Assert.Null(Assert.Single(refused.Accounts).Plan);

        // A key with a space never gets saved, and its text isn't repeated.
        var bad = await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountSetKey, new RemoteSetKeyRequest { Key = "has a-space" });
        Assert.False(bad.Ok);
        Assert.DoesNotContain("a-space", bad.Error);

        var cleared = await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountClearKey, new { });
        Assert.True(cleared.Ok);
        Assert.False(Assert.Single(cleared.Accounts).HasKey);
        Assert.False(File.Exists(Path.Combine(host.DataDirectory, "remote", "account-torbox.json")));

        // Every client heard, and not one of them heard the key.
        await Eventually(() => listener.Events.Count(message => message.Type == MessageTypes.RemoteChanged) >= 3);
        var change = listener.Events.First(message => message.Type == MessageTypes.RemoteChanged).GetPayload<RemoteChangedDto>();
        Assert.Equal("account", change.Change);
        await host.StopAsync();
        host.AssertNowhere(Key, wrong);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("""{"schemaVersion":1,"provider":"torbox","protectedKey":"bm90IGNpcGhlcnRleHQ=","updatedAt":"2026-11-06T00:00:00+00:00"}""")]
    public async Task ASavedKeyThatCanNotBeReadIsItsOwnStateAndTheHostStaysUp(string saved)
    {
        await using var host = await StartHostAsync(sandbox =>
        {
            var remote = Path.Combine(sandbox.DataDirectory, "remote");
            Directory.CreateDirectory(remote);
            File.WriteAllText(Path.Combine(remote, "account-torbox.json"), saved);
        });
        await using var connection = await host.ConnectAsync();

        var account = Assert.Single((await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountGet, new RemoteAccountRequest { Refresh = true })).Accounts);
        Assert.False(account.HasKey);
        Assert.True(account.KeyUnreadable);

        var check = await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountValidate, new { });
        Assert.False(check.Ok);
        Assert.Contains("can't be read", check.Error);
        Assert.Equal(0, Mock.TotalApiCalls);

        // Entering it again is the way out.
        var again = await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountSetKey, new RemoteSetKeyRequest { Key = Key });
        Assert.True(Assert.Single(again.Accounts).HasKey);
        Assert.False(Assert.Single(again.Accounts).KeyUnreadable);
        Assert.True((await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountValidate, new { })).Ok);
    }

    // ---- The whole way through ----------------------------------------------------------------

    [Fact]
    public async Task ScenariosAC_E_RunOverThePipeAndNoSecretTurnsUpAnywhere()
    {
        const string fragment = "invented-fragment-c";
        const string linkPassword = "invented-link-password";
        const string passkey = "invented-tracker-passkey";
        await using var host = await StartHostAsync();
        await using var listener = await host.ConnectAsync();
        await using var connection = await WithKeyAsync(host);

        // Scenario A: a folder link, files chosen, folders kept, nothing submitted.
        var readme = NewFile("Pack/readme.txt", 40 * Kb, 11);
        var first = NewFile("Pack/sub/a/same.bin", 96 * Kb, 12);
        var second = NewFile("Pack/sub/b/same.bin", 96 * Kb, 13);
        var skipped = NewFile("Pack/skipped.bin", 8 * Kb, 14);
        var pack = Mock.AddItem(RemoteItemKind.Torrent, "Pack", [readme, first, second, skipped]);
        var folderLink = $"https://torbox.app/download?id={pack.Id}&type=torrents&name=Elsewhere";

        var classified = await connection.AskAsync<RemoteClassifyResult>(MessageTypes.RemoteClassify, new RemoteClassifyRequest { Text = folderLink });
        Assert.Equal("folderLink", classified.Kind);
        Assert.Equal("torbox", classified.Provider);
        Assert.Equal("torrents", classified.ItemKind);

        var a = (await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto { Input = folderLink, Destination = host.Destination })).Job!;
        var waiting = await UntilAsync(connection, a.Id, job => job.State == "awaitingSelection", "the choice of files in scenario A");
        Assert.Equal("Pack", waiting.Name);

        var files = await connection.AskAsync<RemoteFilesResultDto>(MessageTypes.RemoteFiles, new RemoteJobIdRequest { JobId = a.Id });
        Assert.True(files.Ok, files.Error);
        Assert.Equal(4, files.Files.Count);
        Assert.Contains(files.Files, file => file.RelativePath == Path.Combine("Pack", "sub", "a", "same.bin"));

        var chosen = await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteSelect, new RemoteSelectRequest
        {
            JobId = a.Id,
            FileIds = [readme.Id.ToString(), first.Id.ToString(), second.Id.ToString()],
        });
        Assert.True(chosen.Ok, chosen.Error);
        await UntilAsync(connection, a.Id, job => job.State == "localStarted", "local jobs in scenario A");
        await UntilProviderJobsFinishAsync(connection, 3);
        AssertSameFile(host, readme, "Pack", "readme.txt");
        AssertSameFile(host, first, "Pack", "sub", "a", "same.bin");
        AssertSameFile(host, second, "Pack", "sub", "b", "same.bin");
        Assert.False(File.Exists(Path.Combine(host.Destination, "Pack", "skipped.bin")));
        Assert.Empty(Mock.Submissions);

        // Scenario C: an uncached hoster link, approved, submitted once, polled, started when ready.
        var hosterLink = Mock.HosterUrl($"file/uncached.bin#{fragment}");
        var webFile = NewFile("uncached/uncached.bin", 64 * Kb, 21);
        Mock.ScriptSubmission(Md5(hosterLink), [webFile], [
            MockTorBoxState.Queued,
            MockTorBoxState.Downloading(0.5, eta: 5),
            MockTorBoxState.Ready,
        ]);

        var hoster = await connection.AskAsync<RemoteClassifyResult>(MessageTypes.RemoteClassify, new RemoteClassifyRequest { Text = hosterLink });
        Assert.Equal("hosterLink", hoster.Kind);
        Assert.Equal("Pretend", hoster.HosterName);
        Assert.True(hoster.HosterAvailable);

        var c = (await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Input = hosterLink,
            Destination = host.Destination,
            Selection = "all",
            LinkPassword = linkPassword,
        })).Job!;
        var held = await UntilAsync(connection, c.Id, job => job.ApprovalNeeded, "the question about an uncached link");
        Assert.True(held.HasLinkPassword);
        Assert.Contains("Not cached on TorBox", held.Status);
        Assert.Empty(Mock.Submissions);

        var approved = await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteRetry, new RemoteRetryRequest { JobId = c.Id, ApproveUncached = true });
        Assert.True(approved.Ok, approved.Error);
        await UntilAsync(connection, c.Id, job => job.State == "waiting", "the submission in scenario C");
        var submitted = Assert.Single(Mock.Submissions);
        Assert.Equal(hosterLink, submitted.Link);
        Assert.Equal(linkPassword, submitted.Password);

        var started = await UntilAsync(connection, c.Id, job => job.State == "localStarted", "scenario C to be ready", checkNow: true);
        Assert.Equal("127.0.0.1", started.DisplayHost);
        await UntilProviderJobsFinishAsync(connection, 4);
        AssertSameFile(host, webFile, "uncached", "uncached.bin");
        Assert.Single(Mock.Submissions);
        Assert.Equal(0, Mock.HosterRequests);

        // Scenario E: a magnet, cache checked, approved, waited for, chosen from, downloaded.
        var hash = new string('c', 40);
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn=Pretty%20Name&tr=http%3A%2F%2Ftracker.invalid%2F{passkey}";
        var wanted = NewFile("Pretty Name/sub/wanted.bin", 96 * Kb, 31);
        var other = NewFile("Pretty Name/other.bin", 16 * Kb, 32);
        Mock.ScriptSubmission(hash, [wanted, other], [.. Enumerable.Repeat(MockTorBoxState.MetaData, 8), MockTorBoxState.Downloading(0.5), MockTorBoxState.Ready]);

        var e = (await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto { Input = magnet, Destination = host.Destination })).Job!;
        Assert.Equal("Pretty Name", e.Name);
        await UntilAsync(connection, e.Id, job => job.ApprovalNeeded, "the question about the magnet");
        Assert.Single(Mock.Submissions);
        Assert.True((await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteRetry, new RemoteRetryRequest { JobId = e.Id, ApproveUncached = true })).Ok);
        await UntilAsync(connection, e.Id, job => job.State == "waiting", "the magnet to be submitted");
        Assert.Equal(magnet, Mock.Submissions[1].Link);

        // No files invented while the provider doesn't know them.
        var early = await connection.AskAsync<RemoteFilesResultDto>(MessageTypes.RemoteFiles, new RemoteJobIdRequest { JobId = e.Id });
        Assert.True(early.Pending);
        Assert.Empty(early.Files);

        await UntilAsync(connection, e.Id, job => job.State == "awaitingSelection", "the choice of files in scenario E", checkNow: true);
        Assert.True((await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteSelect, new RemoteSelectRequest { JobId = e.Id, FileIds = [wanted.Id.ToString()] })).Ok);
        await UntilProviderJobsFinishAsync(connection, 5);
        AssertSameFile(host, wanted, "Pretty Name", "sub", "wanted.bin");
        Assert.False(File.Exists(Path.Combine(host.Destination, "Pretty Name", "other.bin")));

        // The other client watched it all as events.
        await Eventually(() => listener.Events.Any(message =>
            message.Type == MessageTypes.RemoteChanged && message.GetPayload<RemoteChangedDto>().Job?.Id == e.Id));
        Assert.Contains(listener.Events, message => message.Type == MessageTypes.JobEvent);
        Assert.Equal(0, Mock.CdnRequestsWithCredentials);

        await host.StopAsync();
        Assert.Equal(0, host.LastExitCode);

        // The search itself has to be able to find something, or passing means nothing.
        Assert.Contains(host.Frames, frame => frame.Contains("uncached.bin", StringComparison.Ordinal));
        Assert.True(File.ReadAllText(Path.Combine(host.DataDirectory, "remote", "account-torbox.json")).Contains("protectedKey", StringComparison.OrdinalIgnoreCase));

        var links = Mock.IssuedLinks;
        Assert.True(links.Count >= 5, "Each file should have asked for a link.");
        host.AssertNowhere(
        [
            Key,
            hosterLink,
            hosterLink[..hosterLink.IndexOf('#', StringComparison.Ordinal)],
            fragment,
            linkPassword,
            magnet,
            passkey,
            "tracker.invalid",
            "magnet:?",
            "/torbox-cdn/",
            "/hoster/",
            .. links.Select(link => link.Uuid),
        ]);
    }

    [Fact]
    public async Task ScenarioJ_AKilledHostPicksUpEverythingWithoutASecondSubmission()
    {
        await using var host = await StartHostAsync();
        var connection = await WithKeyAsync(host);
        var link = Mock.HosterUrl("file/survivor.bin");
        var file = NewFile("survivor/survivor.bin", 4 * 1024 * Kb, 41);
        Mock.ScriptSubmission(Md5(link), [file], [MockTorBoxState.Downloading(0.2, eta: 5), MockTorBoxState.Ready]);

        var job = (await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Input = link,
            Destination = host.Destination,
            Selection = "all",
            ApproveUncached = true,
        })).Job!;
        await UntilAsync(connection, job.Id, candidate => candidate.State == "waiting", "the submission");
        Assert.Single(Mock.Submissions);

        // Gone while the provider works on it.
        await host.KillAsync();
        await host.RestartAsync();
        connection = await host.ConnectAsync();

        var restored = (await connection.AskAsync<RemoteListResult>(MessageTypes.RemoteList, new { })).Jobs.Single();
        Assert.Equal(job.Id, restored.Id);
        Assert.True(Assert.Single((await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountGet, new { })).Accounts).HasKey);

        // Gone again in the middle of the local download.
        Mock.CdnBytesPerSecond = 512 * Kb;
        await UntilAsync(connection, job.Id, candidate => candidate.State == "localStarted", "ready after the restart", checkNow: true);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var child = (await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs.SingleOrDefault(candidate => candidate.Url.StartsWith("remote://", StringComparison.Ordinal));
            if (child is { ReceivedBytes: > 64 * Kb })
            {
                break;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, "The local download never got going.");
            await Task.Delay(25);
        }

        await host.KillAsync();
        Mock.CdnBytesPerSecond = null;
        await host.RestartAsync();
        connection = await host.ConnectAsync();

        await UntilAsync(connection, job.Id, candidate => candidate.State == "localStarted", "the record after the second restart");
        var kids = (await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs.Where(candidate => candidate.Url.StartsWith("remote://", StringComparison.Ordinal)).ToList();
        var kid = Assert.Single(kids);
        if (kid.State is "paused" or "failed" or "queued")
        {
            await connection.RequestAsync(MessageTypes.JobResume, new JobActionRequest { JobId = kid.Id });
        }

        await UntilProviderJobsFinishAsync(connection, 1);
        AssertSameFile(host, file, "survivor", "survivor.bin");
        Assert.Single(Mock.Submissions);
        await connection.DisposeAsync();
        await host.StopAsync();
        host.AssertNowhere(Key, link, "/torbox-cdn/");
    }

    // ---- The rest of the messages -------------------------------------------------------------

    [Fact]
    public async Task BrowsingReadsTheProvidersListByKindAndPageWithNoLinkInIt()
    {
        await using var host = await StartHostAsync();
        await using var connection = await WithKeyAsync(host);
        var one = Mock.AddItem(RemoteItemKind.Torrent, "One", [NewFile("One/a.bin", 2 * Kb, 1), NewFile("One/../../evil.bin", 2 * Kb, 2)]);
        Mock.AddItem(RemoteItemKind.Torrent, "Two", [NewFile("Two/a.bin", 2 * Kb, 3)]);
        Mock.AddItem(RemoteItemKind.Torrent, "Three", [NewFile("Three/a.bin", 2 * Kb, 4)]);
        var web = Mock.AddItem(RemoteItemKind.WebDownload, "Web", [NewFile("Web/a.bin", 2 * Kb, 5)]);
        web.OriginalUrl = Mock.HosterUrl("file/secret-original.bin#invented-original-key");
        var usenet = Mock.AddItem(RemoteItemKind.Usenet, "News", [NewFile("News/a.bin", 2 * Kb, 6)]);

        var page = await connection.AskAsync<RemoteItemResult>(MessageTypes.RemoteItemGet, new RemoteItemRequest { Kind = "torrents", Limit = 2 });
        Assert.True(page.Ok, page.Error);
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.All(page.Items, item => Assert.Null(item.Files));
        var rest = await connection.AskAsync<RemoteItemResult>(MessageTypes.RemoteItemGet, new RemoteItemRequest { Kind = "torrents", Limit = 2, Offset = 2 });
        Assert.Single(rest.Items);
        Assert.False(rest.HasMore);

        var single = await connection.AskAsync<RemoteItemResult>(MessageTypes.RemoteItemGet, new RemoteItemRequest { Kind = "torrents", ItemId = one.Id.ToString() });
        var item = Assert.Single(single.Items);
        Assert.Equal(2, item.FileCount);
        Assert.Equal("ready", item.Stage);
        Assert.Equal("torrents", item.Kind);
        Assert.Contains(item.Files!, file => file.Selectable && file.RelativePath == Path.Combine("One", "a.bin"));
        Assert.Contains(item.Files!, file => !file.Selectable && file.RelativePath is null);

        Assert.Equal("Web", Assert.Single((await connection.AskAsync<RemoteItemResult>(MessageTypes.RemoteItemGet, new RemoteItemRequest { Kind = "webdl" })).Items).Name);
        Assert.Equal("News", Assert.Single((await connection.AskAsync<RemoteItemResult>(MessageTypes.RemoteItemGet, new RemoteItemRequest { Kind = "usenet", IncludeFiles = true })).Items).Name);

        var missing = await connection.AskAsync<RemoteItemResult>(MessageTypes.RemoteItemGet, new RemoteItemRequest { Kind = "torrents", ItemId = "424242" });
        Assert.False(missing.Ok);
        Assert.Equal("notFound", missing.FailureKind);
        var wrongKind = await connection.AskAsync<RemoteItemResult>(MessageTypes.RemoteItemGet, new RemoteItemRequest { Kind = "movies" });
        Assert.False(wrongKind.Ok);
        Assert.Contains("torrents, webdl or usenet", wrongKind.Error);

        // An item from the list can be added by reference, and the input was never involved.
        var added = await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Provider = "torbox",
            ItemKind = "usenet",
            ItemId = usenet.Id.ToString(),
            Destination = host.Destination,
            Selection = "all",
        });
        Assert.True(added.Ok, added.Error);
        await UntilAsync(connection, added.Job!.Id, job => job.State == "localStarted", "the browsed item");

        Assert.Empty(Mock.Submissions);
        await host.StopAsync();
        host.AssertNowhere(Key, "secret-original", "invented-original-key", "/torbox-cdn/");
    }

    [Fact]
    public async Task RemovingHereLeavesTheProviderAloneAndDeletingThereNeedsConfirmation()
    {
        await using var host = await StartHostAsync();
        await using var connection = await WithKeyAsync(host);
        var one = Mock.AddItem(RemoteItemKind.Torrent, "One", [NewFile("One/a.bin", 2 * Kb, 1)]);
        var two = Mock.AddItem(RemoteItemKind.Torrent, "Two", [NewFile("Two/a.bin", 2 * Kb, 2)]);
        async Task<RemoteJobDto> AddAsync(MockTorBoxItem item)
            => (await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
            {
                Input = $"https://torbox.app/download?id={item.Id}&type=torrents",
                Destination = host.Destination,
            })).Job!;

        var kept = await AddAsync(one);
        await UntilAsync(connection, kept.Id, job => job.State == "awaitingSelection", "the first");
        var removed = await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteRemove, new RemoteJobIdRequest { JobId = kept.Id });
        Assert.True(removed.Ok, removed.Error);
        Assert.Empty((await connection.AskAsync<RemoteListResult>(MessageTypes.RemoteList, new { })).Jobs);
        Assert.Empty(Mock.Deleted);
        Assert.NotNull(Mock.FindItem(RemoteItemKind.Torrent, one.Hash));

        var doomed = await AddAsync(two);
        await UntilAsync(connection, doomed.Id, job => job.State == "awaitingSelection", "the second");
        var unconfirmed = await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteDeleteRemote, new RemoteDeleteRequest { JobId = doomed.Id });
        Assert.False(unconfirmed.Ok);
        Assert.Contains("confirmed", unconfirmed.Error);
        Assert.Empty(Mock.Deleted);

        var confirmed = await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteDeleteRemote, new RemoteDeleteRequest { JobId = doomed.Id, Confirmed = true });
        Assert.True(confirmed.Ok, confirmed.Error);
        Assert.Equal(two.Id, Assert.Single(Mock.Deleted).Id);
        Assert.Empty((await connection.AskAsync<RemoteListResult>(MessageTypes.RemoteList, new { })).Jobs);
    }

    [Fact]
    public async Task ASlowCallHoldsOnlyItsOwnConnectionAndEndsWithItsClient()
    {
        await using var host = await StartHostAsync();
        await using var slow = await WithKeyAsync(host);
        await using var other = await host.ConnectAsync();

        Mock.Fail("GET user/me", MockFault.Delay(TimeSpan.FromSeconds(8)), times: 1);
        var watch = Stopwatch.StartNew();
        var pending = slow.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountValidate, new { });

        // The other connection answers while that one waits, even for a command of the same family.
        var quick = await other.AskAsync<RemoteListResult>(MessageTypes.RemoteList, new { }, TimeSpan.FromSeconds(6));
        Assert.True(quick.Ok);
        Assert.False(pending.IsCompleted, "The slow call finished first, so this proved nothing.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), "The other connection had to wait.");
        var answer = await pending;
        Assert.True(answer.Ok, answer.Error);

        // A client that gives up doesn't leave the Host stuck, and the Host stays usable.
        Mock.Fail("GET user/me", MockFault.Delay(TimeSpan.FromSeconds(8)), times: 1);
        var gone = await host.ConnectAsync();
        _ = gone.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountValidate, new { }).ContinueWith(task => task.Exception, TaskScheduler.Default);
        await Task.Delay(100);
        await gone.DisposeAsync();
        Assert.True((await other.AskAsync<RemoteListResult>(MessageTypes.RemoteList, new { }, TimeSpan.FromSeconds(6))).Ok);
    }

    [Fact]
    public async Task ARefreshWhoseClientGaveUpLeavesTheAddressAsItWas()
    {
        await using var host = await StartHostAsync();
        await using var connection = await WithKeyAsync(host);
        var before = $"{_server.BaseAddress}/file/before.bin?size=1024&seed=51";
        var added = await connection.AskAsync<AddJobResponse>(MessageTypes.JobAdd, new AddJobRequest
        {
            Url = before,
            FileName = "before.bin",
            DestinationDirectory = host.Destination,
            StartMode = "later",
            Source = "cli",
        });
        Assert.True(added.Accepted, added.Error);

        // Checking the new address needs the hoster list, which is slow to come. The client stops waiting first.
        Mock.Fail("GET webdl/hosters", MockFault.Delay(TimeSpan.FromSeconds(4)), times: 1);
        var gone = await host.ConnectAsync();
        var request = new JobRefreshUrlRequest { JobId = added.Job!.Id, Url = $"{_server.BaseAddress}/file/after.bin?size=1024&seed=52" };
        _ = gone.AskAsync<JobActionResponse>(MessageTypes.JobRefreshUrl, request).ContinueWith(task => task.Exception, TaskScheduler.Default);
        await Task.Delay(500);
        await gone.DisposeAsync();

        // It would have been applied by now, if it were still going.
        await Task.Delay(TimeSpan.FromSeconds(6));
        var job = (await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs.Single(candidate => candidate.Id == added.Job.Id);
        Assert.Equal(before, job.Url);
        Assert.Equal("later", job.StartMode);
    }

    [Fact]
    public async Task BadRequestsGetPlainAnswersAndTheOldRulesStillHold()
    {
        await using var host = await StartHostAsync();
        await using var connection = await host.ConnectAsync();

        // No key yet: a folder link can't be looked up, and says what to do.
        var noKey = await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto { Input = "https://torbox.app/download?id=1&type=torrents", Destination = host.Destination });
        Assert.False(noKey.Ok);
        Assert.Contains("API key", noKey.Error);

        // With no key a hoster link stays ordinary, and an ordinary link isn't remote.add's business.
        var ordinary = await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto { Input = "https://example.invalid/a.bin", Destination = host.Destination });
        Assert.False(ordinary.Ok);
        Assert.Equal("ordinaryLink", ordinary.InputKind);

        var noSelection = await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto { Input = "x", Selection = "some" });
        Assert.False(noSelection.Ok);
        Assert.Contains("selection", noSelection.Error);

        var both = await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto { Input = "x", ItemKind = "torrents", ItemId = "1" });
        Assert.False(both.Ok);

        var absent = await connection.AskAsync<RemoteActionResultDto>(MessageTypes.RemoteRetry, new RemoteRetryRequest { JobId = Guid.NewGuid() });
        Assert.False(absent.Ok);
        Assert.Contains("not on the list", absent.Error);

        // A message with the right prefix and no meaning, and one with a payload it can't read.
        var unknown = await connection.RequestAsync(MessageTypes.RemoteList.Replace("list", "nonsense", StringComparison.Ordinal), new { });
        Assert.False(unknown.GetPayload<HostActionResult>().Ok);
        var unreadable = await connection.RequestAsync(MessageTypes.RemoteSelect, new { jobId = "not a guid" });
        Assert.Equal(MessageTypes.RemoteActionResult, unreadable.Type);
        Assert.False(unreadable.GetPayload<RemoteActionResultDto>().Ok);

        // Clients still can't forge a provider job.
        var forged = await connection.AskAsync<AddJobResponse>(MessageTypes.JobAdd, new AddJobRequest { Url = "remote://torbox/torrents/1/1", DestinationDirectory = host.Destination });
        Assert.False(forged.Accepted);
    }

    [Fact]
    public async Task WordsFromTheProviderAreScrubbedBeforeAnyClientSeesThem()
    {
        // A provider's text can quote what it was given. Nothing in this answer may get through.
        var quoted = $"https://cdn.invalid/dld/invented-uuid?token={Key}";
        var hostile = $"Could not fetch {quoted} or magnet:?xt=urn:btih:{new string('e', 40)}&tr=invented-tracker for key {Key.Replace("-", string.Empty, StringComparison.Ordinal)}";
        var failing = Mock.AddItem(RemoteItemKind.WebDownload, quoted, [NewFile("failing/a.bin", 2 * Kb, 1)], MockTorBoxState.Failed(hostile));
        failing.OriginalUrl = quoted;
        var leaky = Mock.AddItem(RemoteItemKind.Torrent, $"named {Key} here", [NewFile($"folder/{Key}.bin", 2 * Kb, 2)]);

        await using var host = await StartHostAsync();
        await using var connection = await WithKeyAsync(host);

        var page = await connection.AskAsync<RemoteItemResult>(MessageTypes.RemoteItemGet, new RemoteItemRequest { Kind = "webdl", IncludeFiles = true });
        Assert.True(page.Ok, page.Error);
        var failed = Assert.Single(page.Items);
        Assert.Equal("failed", failed.Stage);
        Assert.Contains("Could not fetch", failed.Error);

        var single = await connection.AskAsync<RemoteItemResult>(MessageTypes.RemoteItemGet, new RemoteItemRequest { Kind = "torrents", ItemId = leaky.Id.ToString() });
        Assert.Contains("here", Assert.Single(single.Items).Name);

        // The same through a job: the failure a job keeps, and shows, is clean as well.
        var job = (await connection.AskAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, new RemoteAddRequestDto
        {
            Input = $"https://torbox.app/download?id={failing.Id}&type=web_downloads",
            Destination = host.Destination,
        })).Job!;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        RemoteJobDto seen;
        do
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "It never failed.");
            await Task.Delay(50);
            seen = (await connection.AskAsync<RemoteListResult>(MessageTypes.RemoteList, new { })).Jobs.Single();
        }
        while (seen.State != "failed");
        Assert.Contains("Could not fetch", seen.FailureMessage);

        await host.StopAsync();
        host.AssertNowhere(Key, quoted, "cdn.invalid", "invented-uuid", "invented-tracker", "magnet:?");
    }

    [Fact]
    public async Task AnAnswerTooLargeForOneFrameGetsAPlainFailureAndTheConnectionLives()
    {
        var files = Enumerable.Range(0, 70_000).Select(i => new MockTorBoxFile
        {
            Id = i,
            Name = $"Huge/{i:D6}-{new string('x', 90)}.bin",
            Size = 1,
        });
        var huge = Mock.AddItem(RemoteItemKind.Torrent, "Huge", files);

        await using var host = await StartHostAsync();
        await using var connection = await WithKeyAsync(host);
        var answer = await connection.AskAsync<RemoteItemResult>(MessageTypes.RemoteItemGet, new RemoteItemRequest { Kind = "torrents", ItemId = huge.Id.ToString() });
        Assert.False(answer.Ok);
        Assert.Contains("too large", answer.Error);

        // A page of it, without the files, is fine, on the same connection.
        var page = await connection.AskAsync<RemoteItemResult>(MessageTypes.RemoteItemGet, new RemoteItemRequest { Kind = "torrents" });
        Assert.Equal(70_000, Assert.Single(page.Items).FileCount);
    }

    [Theory]
    [InlineData("remote/pacing-torbox.json")]
    [InlineData("remote/jobs/11111111111111111111111111111111.json")]
    public async Task ADamagedRemoteFileDoesNotStopTheHost(string relative)
    {
        await using var host = await StartHostAsync(sandbox =>
        {
            var path = Path.Combine(sandbox.DataDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "this is not what it should be");
        });
        await using var connection = await host.ConnectAsync();

        Assert.Contains("remote", connection.Hello.Capabilities);
        var list = await connection.AskAsync<RemoteListResult>(MessageTypes.RemoteList, new { });
        Assert.True(list.Ok);
        Assert.Empty(list.Jobs);
    }

    [Fact]
    public async Task TheSearchFindsASecretInEveryFormItLooksFor()
    {
        // If this search couldn't find one, every test relying on it would pass for nothing.
        await using var host = await StartHostAsync();
        foreach (var form in SandboxHost.FormsOf(Key).Distinct())
        {
            var planted = Path.Combine(host.DataDirectory, "remote", "planted.bin");
            await File.WriteAllBytesAsync(planted, Encoding.Latin1.GetBytes($"before{form}after"));
            Assert.Throws<Xunit.Sdk.FalseException>(() => host.AssertNowhere(Key));
            File.Delete(planted);
        }

        // Escaped the way JSON writes an ampersand, it is still found.
        var escaped = Path.Combine(host.DataDirectory, "remote", "planted.json");
        await File.WriteAllTextAsync(escaped, "{\"a\":\"x\u0026y=1\"}");
        Assert.Throws<Xunit.Sdk.FalseException>(() => host.AssertNowhere("x&y=1"));
        File.Delete(escaped);

        await using var connection = await host.ConnectAsync();
        host.AssertNowhere(Key);
    }

    // ---- The browser's side -------------------------------------------------------------------

    [Fact]
    public async Task TheNativeHostForwardsNoRemoteOrArchiveMessageInEitherDirection()
    {
        await using var host = await StartHostAsync();
        await using var connection = await host.ConnectAsync();
        using var native = host.StartNativeHost();
        try
        {
            var toExtension = native.StandardInput.BaseStream;
            var fromExtension = native.StandardOutput.BaseStream;
            async Task<IpcMessage> SendAsync(string type, object payload)
            {
                var request = IpcMessage.Create(type, payload);
                await IpcFraming.WriteAsync(toExtension, request);
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (true)
                {
                    var frame = await IpcFraming.ReadAsync(fromExtension, wait.Token) ?? throw new IOException("The native host closed.");
                    Assert.False(frame.Type.StartsWith("remote.", StringComparison.OrdinalIgnoreCase), $"Forwarded {frame.Type}");
                    if (frame.ReplyTo == request.Id) return frame;
                }
            }

            Assert.True((await SendAsync(MessageTypes.HostPing, new { })).GetPayload<HostActionResult>().Ok);

            foreach (var type in typeof(MessageTypes).GetFields().Select(field => (string)field.GetRawConstantValue()!).Where(type => type.StartsWith("remote.", StringComparison.Ordinal)).Append("archive.extract"))
            {
                var refused = await SendAsync(type, new RemoteSetKeyRequest { Key = Key });
                Assert.Equal(MessageTypes.HostActionResult, refused.Type);
                var result = refused.GetPayload<HostActionResult>();
                Assert.False(result.Ok);
                Assert.Contains("browser extension cannot send", result.Message);
            }

            // None of those reached the Host.
            Assert.False(Assert.Single((await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountGet, new { })).Accounts).HasKey);
            Assert.Equal(0, Mock.TotalApiCalls);

            // Something the Host does say to everyone: a key change, then a job. Only the job gets through.
            var saved = await connection.AskAsync<RemoteAccountResult>(MessageTypes.RemoteAccountSetKey, new RemoteSetKeyRequest { Key = Key });
            Assert.True(saved.Ok);
            await Eventually(() => connection.Events.Any(message => message.Type == MessageTypes.RemoteChanged));
            var job = await connection.AskAsync<AddJobResponse>(MessageTypes.JobAdd, new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/seen.bin?size=1024",
                DestinationDirectory = host.Destination,
                Source = "cli",
            });
            Assert.True(job.Accepted, job.Error);

            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var frame = await IpcFraming.ReadAsync(fromExtension, wait.Token) ?? throw new IOException("The native host closed.");
                Assert.False(frame.Type.StartsWith("remote.", StringComparison.OrdinalIgnoreCase), $"Forwarded {frame.Type}");
                if (frame.Type == MessageTypes.JobEvent) break;
            }
        }
        finally
        {
            if (!native.HasExited) native.Kill(entireProcessTree: true);
            await native.WaitForExitAsync();
        }
    }

    // ---- The command line ---------------------------------------------------------------------

    [Fact]
    public async Task AHosterLinkGoesThroughTheProviderWithNoChoiceAskedForAndDmAddWontTakeIt()
    {
        await using var host = await StartHostAsync();
        await using var connection = await WithKeyAsync(host);
        var link = Mock.HosterUrl("file/cli-hoster#invented-fragment");
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(link))).ToLowerInvariant();
        var file = MockTorBoxFile.Of(Mock.NextFileId(), "cli-hoster/a.bin", 4 * Kb, 41);
        Mock.ScriptSubmission(hash, [file], [MockTorBoxState.CachedReady]);
        Mock.AddCached(hash);

        // The command that means a direct request refuses a hoster link, and nothing reaches the hoster.
        var direct = await host.RunCliAsync(null, "add", link, "--dest", host.Destination);
        Assert.Equal(2, direct.ExitCode);
        Assert.Contains("dm remote add", direct.Error);
        Assert.DoesNotContain("invented-fragment", direct.All);
        Assert.Equal(0, Mock.HosterRequests);
        var none = await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { });
        Assert.Empty(none.Jobs);

        // An explicit option doesn't get round that, for something known to be a hoster.
        Assert.Equal(2, (await host.RunCliAsync(null, "add", link, "--direct", "--dest", host.Destination)).ExitCode);
        Assert.Equal(0, Mock.HosterRequests);

        // Nor does asking for several at once, or by a list. Each counts as failed, in the same words.
        var batch = await host.RunCliAsync(null, "add-batch", Mock.HosterUrl("file/cli-batch[1-2].bin"), "--direct", "--dest", host.Destination);
        Assert.Equal(1, batch.ExitCode);
        Assert.Contains("dm remote add", batch.Error);
        var listFile = Path.Combine(host.Root, "list.json");
        await File.WriteAllTextAsync(listFile, JobListFile.Serialize([new JobDto { Url = link }]));
        var imported = await host.RunCliAsync(null, "import", listFile, "--direct");
        Assert.Equal(1, imported.ExitCode);
        Assert.Contains("dm remote add", imported.Output);

        // A link to an item on the account is no more a direct download than a hoster's.
        var folder = await host.RunCliAsync(null, "add", "https://torbox.app/download?id=5&type=torrents", "--direct");
        Assert.Equal(2, folder.ExitCode);
        Assert.Contains("dm remote add", folder.Error);
        Assert.Equal(0, Mock.HosterRequests);
        Assert.Empty((await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs);

        // Other options of other commands aren't taken silently.
        var odd = await host.RunCliAsync(link, "remote", "add", "-", "--item", "5");
        Assert.Equal(2, odd.ExitCode);
        Assert.Contains("--item", odd.Error);

        // Through the provider, with nothing chosen: when it is ready it starts.
        var added = await host.RunCliAsync(link, "remote", "add", "-", "--dest", host.Destination);
        Assert.True(added.ExitCode == 0, added.Error);
        var id = added.Output.Split('\n').First(line => line.StartsWith("id:", StringComparison.Ordinal))["id:".Length..].Trim();
        await UntilProviderJobsFinishAsync(connection, 1);
        Assert.Equal(0, Mock.HosterRequests);
        AssertSameFile(host, file, "cli-hoster", "a.bin");

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (!(await host.RunCliAsync(null, "remote", "list")).Output.Contains("Completed", StringComparison.Ordinal))
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The job never said it was finished.");
            await Task.Delay(100);
        }

        // Added again with different wishes, it says what wasn't used.
        var again = await host.RunCliAsync(link, "remote", "add", "-", "--dest", Path.Combine(host.Destination, "other"));
        Assert.Equal(0, again.ExitCode);
        Assert.Contains("Already on the list", again.Output);
        Assert.Contains("destination", again.Output);
        Assert.Contains(id, again.Output);

        // job.add stays what it is, a direct request, because a browser handoff is made of those.
        var viaPipe = await connection.AskAsync<AddJobResponse>(MessageTypes.JobAdd, new AddJobRequest { Url = Mock.HosterUrl("file/job-add"), DestinationDirectory = host.Destination, StartMode = "later" });
        Assert.True(viaPipe.Accepted, viaPipe.Error);
    }

    [Fact]
    public async Task TheCliReadsALinkAndAPasswordFromOnePipeAsUtf8WithSpacesKept()
    {
        await using var host = await StartHostAsync();
        await using var connection = await WithKeyAsync(host);
        var link = Mock.HosterUrl("file/stdin-order");
        const string password = "  pä55ß жд 日本 ";

        // Both lines arrive in one write, so a second reader on the pipe would find its line already gone.
        var added = await host.RunCliAsync($"{link}\n{password}", "remote", "add", "-", "--archive-password-stdin", "--dest", host.Destination);
        Assert.True(added.ExitCode == 0, added.Error);
        Assert.DoesNotContain("pä55ß", added.All);

        var saved = Directory.EnumerateFiles(Path.Combine(host.DataDirectory, "remote", "jobs"), "*.json").Single();
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(saved));
        var sealedPassword = document.RootElement.EnumerateObject()
            .First(property => property.Name.Equals("protectedArchivePassword", StringComparison.OrdinalIgnoreCase)).Value.GetString();
        Assert.Equal(password, DownloadManager.Persistence.SecretProtector.UnprotectRemoteArchivePassword(sealedPassword));
    }

    [Fact]
    public async Task WithNoListOfSitesDmAddAsksAndRemoteAddGivesTheRealCause()
    {
        await using var host = await StartHostAsync();
        await using var connection = await WithKeyAsync(host);
        Mock.Fail("GET webdl/hosters", MockFault.Status5xx());
        var link = "https://hoster.invalid/file/x";

        var classified = await host.RunCliAsync(link, "remote", "classify", "-");
        Assert.Equal(0, classified.ExitCode);
        Assert.Contains("undetermined", classified.Output);

        var add = await host.RunCliAsync(link, "remote", "add", "-", "--dest", host.Destination);
        Assert.Equal(1, add.ExitCode);
        Assert.Contains("TorBox could not be reached", add.Error);
        Assert.DoesNotContain("ordinary", add.All);

        var direct = await host.RunCliAsync(null, "add", link, "--dest", host.Destination, "--start", "later");
        Assert.Equal(2, direct.ExitCode);
        Assert.Contains("--direct", direct.Error);
        Assert.Empty((await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs);

        var chosen = await host.RunCliAsync(null, "add", link, "--direct", "--dest", host.Destination, "--start", "later");
        Assert.True(chosen.ExitCode == 0, chosen.Error);
    }

    [Fact]
    public async Task WithoutAnAccountDmAddIsAsItEverWas()
    {
        await using var host = await StartHostAsync();
        var result = await host.RunCliAsync(null, "add", Mock.HosterUrl("file/no-account"), "--dest", host.Destination, "--start", "later");
        Assert.True(result.ExitCode == 0, result.Error);
    }

    [Fact]
    public async Task TheCommandLineShowsNoEscapeSequencesAndWarnsAboutASecretOnIt()
    {
        await using var host = await StartHostAsync();
        await using var connection = await WithKeyAsync(host);
        var file = MockTorBoxFile.Of(Mock.NextFileId(), "Pack/a.bin", 4 * Kb, 42);
        Mock.AddItem(RemoteItemKind.Torrent, "x\u001b[2K\u001b[1Afake line", [file], MockTorBoxState.Failed("bad\u001b]0;title\u0007 news"));

        var browse = await host.RunCliAsync(null, "remote", "browse", "torrents");
        Assert.Equal(0, browse.ExitCode);
        Assert.DoesNotContain('\u001b', browse.All);
        Assert.DoesNotContain('\u0007', browse.All);
        Assert.Contains("fake line", browse.Output);

        var items = await connection.AskAsync<RemoteItemResult>(MessageTypes.RemoteItemGet, new RemoteItemRequest { Kind = "torrents", Fresh = true });
        Assert.DoesNotContain('\u001b', Assert.Single(items.Items).Name ?? string.Empty);

        var magnet = "magnet:?xt=urn:btih:" + new string('a', 40) + "&tr=invented-passkey";
        var warned = await host.RunCliAsync(null, "remote", "classify", magnet);
        Assert.Contains("shell history", warned.Error);
        Assert.DoesNotContain("invented-passkey", warned.All);

        var quiet = await host.RunCliAsync(magnet, "remote", "classify", "-");
        Assert.DoesNotContain("shell history", quiet.Error);
        Assert.Contains("magnet", quiet.Output);

        var plain = await host.RunCliAsync(null, "remote", "classify", "https://example.invalid/file.zip");
        Assert.DoesNotContain("shell history", plain.Error);

        var network = await host.RunCliAsync(null, "remote", "add-torrent", @"\\attacker.invalid\share\a.torrent");
        Assert.Equal(2, network.ExitCode);
        Assert.Contains("network path", network.Error);
    }

    [Fact]
    public async Task TheCommandLineDoesTheWholeJobAndPrintsNoSecret()
    {
        await using var host = await StartHostAsync();
        var outputs = new List<string>();
        async Task<CliResult> RunAsync(string? input, params string[] arguments)
        {
            var result = await host.RunCliAsync(input, ["remote", .. arguments]);
            outputs.Add(result.All);
            return result;
        }

        var before = await RunAsync(null, "status");
        Assert.Equal(0, before.ExitCode);
        Assert.Contains("TorBox key: not set", before.Output);
        Assert.Contains("remote jobs: 0", before.Output);

        // The command line is no place for it: refused, not echoed, not used.
        var inArguments = await RunAsync(null, "key", "set", Key);
        Assert.Equal(2, inArguments.ExitCode);
        Assert.DoesNotContain(Key, inArguments.All);
        Assert.Contains("standard input", inArguments.Error);
        Assert.DoesNotContain("key: set", (await RunAsync(null, "status")).Output);

        var empty = await RunAsync(string.Empty, "key", "set");
        Assert.Equal(2, empty.ExitCode);

        var saved = await RunAsync(Key, "key", "set");
        Assert.True(saved.ExitCode == 0, saved.Error);
        Assert.Contains("Key saved.", saved.Output);
        Assert.Contains("plan: Pro", saved.Output);

        var status = await RunAsync(null, "status");
        Assert.Contains("TorBox key: set", status.Output);

        var file = NewFile("Pack/a.bin", 32 * Kb, 51);
        var second = NewFile("Pack/b.bin", 16 * Kb, 52);
        var item = Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file, second]);

        var browse = await RunAsync(null, "browse", "torrents");
        Assert.Contains("Pack", browse.Output);
        var files = await RunAsync(null, "browse", "torrents", "--item", item.Id.ToString());
        Assert.Contains(file.Id.ToString(), files.Output);
        var noSuchKind = await RunAsync(null, "browse", "movies");
        Assert.Equal(1, noSuchKind.ExitCode);
        Assert.Contains("torrents, webdl or usenet", noSuchKind.Error);

        var link = $"https://torbox.app/download?id={item.Id}&type=torrents";
        var classified = await RunAsync(null, "classify", link);
        Assert.Contains("kind:     folderLink", classified.Output);

        // A link can stay off the command line too.
        var added = await RunAsync(link, "add", "-", "--dest", host.Destination);
        Assert.True(added.ExitCode == 0, added.Error);
        Assert.Contains("Added:", added.Output);
        var id = added.Output.Split('\n').First(line => line.StartsWith("id:", StringComparison.Ordinal))["id:".Length..].Trim();

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (!(await RunAsync(null, "list")).Output.Contains("awaitingSelection", StringComparison.Ordinal))
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "It never got to the choice of files.");
            await Task.Delay(100);
        }

        var listed = await RunAsync(null, "files", id[..12]);
        Assert.Contains(Path.Combine("Pack", "a.bin"), listed.Output);
        var chosen = await RunAsync(null, "select", id, file.Id.ToString());
        Assert.True(chosen.ExitCode == 0, chosen.Error);

        await using (var connection = await host.ConnectAsync())
        {
            await UntilProviderJobsFinishAsync(connection, 1);
        }

        AssertSameFile(host, file, "Pack", "a.bin");
        Assert.False(File.Exists(Path.Combine(host.Destination, "Pack", "b.bin")));

        // A magnet and a torrent file, through their own commands, wait for the same question.
        var hash = new string('d', 40);
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn=Cli%20Magnet&tr=http%3A%2F%2Ftracker.invalid%2Finvented-cli-passkey";
        var magnetAdded = await RunAsync(magnet, "add", "-", "--dest", host.Destination, "--select", "all");
        Assert.True(magnetAdded.ExitCode == 0, magnetAdded.Error);
        var magnetId = magnetAdded.Output.Split('\n').First(line => line.StartsWith("id:", StringComparison.Ordinal))["id:".Length..].Trim();
        deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (!(await RunAsync(null, "list")).Output.Contains("Not cached on TorBox", StringComparison.Ordinal))
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "It never asked about the magnet.");
            await Task.Delay(100);
        }

        var torrentPath = Path.Combine(host.Root, "invented.torrent");
        await File.WriteAllBytesAsync(torrentPath, Encoding.ASCII.GetBytes("d4:infod6:lengthi1e4:name3:abc12:piece lengthi16384e6:pieces20:aaaaaaaaaaaaaaaaaaaaee"));
        var torrent = await RunAsync(null, "add-torrent", torrentPath, "--dest", host.Destination, "--select", "all");
        Assert.True(torrent.ExitCode == 0, torrent.Error);
        Assert.Contains("name:     abc", torrent.Output);
        Assert.Equal(1, (await RunAsync(null, "add-torrent", Path.Combine(host.Root, "missing.torrent"))).ExitCode);

        // Taking one off the list here touches nothing there, and deleting there asks first.
        Assert.Equal(0, (await RunAsync(null, "check", magnetId)).ExitCode);
        var noSuchJob = await RunAsync(null, "remove", "not-an-id");
        Assert.Equal(2, noSuchJob.ExitCode);
        Assert.Contains("No remote job has that id", noSuchJob.Error);
        Assert.Equal(2, (await RunAsync(null, "remove", id, "--delete-from-provider")).ExitCode);
        Assert.Contains("--yes", outputs[^1]);
        Assert.Empty(Mock.Deleted);
        var removed = await RunAsync(null, "remove", magnetId);
        Assert.True(removed.ExitCode == 0, removed.Error);
        Assert.Contains("as they were", removed.Output);

        var clear = await RunAsync(null, "key", "clear");
        Assert.Equal(0, clear.ExitCode);
        Assert.Contains("TorBox key: not set", (await RunAsync(null, "status")).Output);

        await host.StopAsync();
        foreach (var output in outputs)
        {
            foreach (var secret in new[] { Key, Key.Replace("-", string.Empty, StringComparison.Ordinal), magnet, "invented-cli-passkey", "tracker.invalid", "/torbox-cdn/" })
            {
                Assert.False(output.Contains(secret, StringComparison.Ordinal), "A secret turned up in the output of a command.");
            }
        }

        host.AssertNowhere(Key, magnet, "invented-cli-passkey", "/torbox-cdn/");
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "Waited too long.");
            await Task.Delay(20);
        }
    }
}
